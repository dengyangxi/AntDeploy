using AntDeploy.Core;
using AntDeploy.Core.Models;
using AntDeploy.Core.Util;
using AntDeployWeb.Data;
using AntDeployWeb.Models;
using AntDeployWeb.Services.Remote;

namespace AntDeployWeb.Services
{
    /// <summary>
    /// Agent 连接验证（W1.8）：安装并启动后，Web 端对每台服务器探测 <c>http://{host}:{port}/version</c>（复用 <see cref="DeployClient.GetAgentVersionAsync"/>，空 body 无需 token）。
    /// <para>成功 → ✓ 显示 Agent 版本，并据 <see cref="AgentCapability.SupportsConfigSync"/> 标注是否支持在线配置同步；
    /// 失败 → ✗，再经远程通道在“服务器本机”做一次 <c>/version</c> 自检对比，据此区分
    /// “服务未起（本机亦不通）”与“网络/端口不通（本机通、外机不通）”，Msg 附排错手册指引。</para>
    /// <para>可独立用于列表页“验证连接”，也可在安装向导中安装后调用。外机探测使用短超时（避免被防火墙静默丢包时长时间挂起）。</para>
    /// </summary>
    public class AgentVerifier
    {
        /// <summary>外机 /version 探测超时：DeployClient 内部 HttpClient 默认 30 分钟，过长；此处用链接令牌缩短，防火墙丢包时快速失败。</summary>
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(12);

        private readonly ILogger<AgentVerifier> _logger;

        public AgentVerifier(ILogger<AgentVerifier> logger)
        {
            _logger = logger;
        }

        /// <summary>Agent HTTP 端口（与 <see cref="AgentInstaller"/> 一致）：Config.Port&gt;0 用之，否则默认端口。</summary>
        public static int ResolveAgentPort(AgentClient client) =>
            client?.Config != null && client.Config.Port > 0 ? client.Config.Port : AgentInstaller.DefaultPort;

        /// <summary>
        /// 对 <see cref="AgentClient.Servers"/> 逐台做连接验证（顺序）。onLog 收到带主机分隔的实时日志。返回每台 ✓/✗ + 原因。
        /// </summary>
        public async Task<List<HostResult>> VerifyAsync(AgentClient client, Action<string>? onLog, CancellationToken ct = default)
        {
            var results = new List<HostResult>();
            if (client == null) return results;

            var servers = client.Servers ?? new List<AgentServerItem>();
            if (servers.Count == 0)
            {
                onLog?.Invoke("没有可验证的服务器。");
                return results;
            }

            var port = ResolveAgentPort(client);
            for (var i = 0; i < servers.Count; i++)
            {
                if (ct.IsCancellationRequested)
                {
                    onLog?.Invoke("验证已取消。");
                    break;
                }
                var server = servers[i];
                onLog?.Invoke(string.Empty);
                onLog?.Invoke($"==================== [验证 {i + 1}/{servers.Count}] {server.Host} ====================");
                var r = await VerifyOneAsync(client, server, port, onLog, ct).ConfigureAwait(false);
                results.Add(r);
                onLog?.Invoke(r.Success
                    ? $"==================== ✓ {server.Host} 连接正常：{r.Msg} ===================="
                    : $"==================== ✗ {server.Host} 连接失败：{r.Msg} ====================");
            }
            return results;
        }

        /// <summary>
        /// 验证单台服务器：外机 /version 探测；成功即 ✓（含版本与能力）；失败则经远程通道本机自检对比以定位原因，返回 ✗（含排错指引）。
        /// </summary>
        public async Task<HostResult> VerifyOneAsync(AgentClient client, AgentServerItem server, int port, Action<string>? onLog, CancellationToken ct = default)
        {
            var agentUrl = $"{server.Host}:{port}"; // DeployClient 内部 NormalizeAgentUrl 会补 http://
            onLog?.Invoke($"[verify] 外机探测 http://{agentUrl}/version ...");

            string? raw = null;
            try
            {
                using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                probeCts.CancelAfter(ProbeTimeout);
                var log = new ActionLog((lvl, msg) => onLog?.Invoke($"[{lvl}] {msg}"));
                raw = await new DeployClient(log).GetAgentVersionAsync(agentUrl, probeCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                onLog?.Invoke($"[verify] 外机探测异常：{ex.Message}");
            }

            var version = raw?.Trim();
            // 有效版本：非空且以数字开头（Agent /version 返回纯文本版本号，如 7.3）
            if (!string.IsNullOrEmpty(version) && char.IsDigit(version[0]))
            {
                if (version.Length > 40) version = version.Substring(0, 40); // 防御异常响应
                var canSync = AgentCapability.SupportsConfigSync(version);
                var msg = $"外机 /version 成功，Agent 版本 {version}"
                    + (canSync ? "（支持在线配置同步）" : $"（低于 {AgentCapability.MinConfigSyncVersion}，暂不支持在线同步，建议升级）");
                onLog?.Invoke($"[verify] {msg}");
                return Ok(server, msg);
            }

            // 外机不通（或返回非预期内容）→ 经远程通道在服务器本机自检对比，定位“服务未起”还是“网络/端口不通”
            if (!string.IsNullOrEmpty(version))
                onLog?.Invoke($"[verify] 端口可连通但返回非预期内容（非 Agent /version 响应），继续本机诊断");
            else
                onLog?.Invoke("[verify] 外机 /version 不通，尝试经远程通道在服务器本机自检以定位原因 ...");

            var local = await ProbeLocalAsync(client, server, port, onLog, ct).ConfigureAwait(false);
            var failMsg = local switch
            {
                LocalProbeResult.Ok =>
                    $"本机 /version 正常但 Web 侧不通：多为防火墙/安全组未放行端口 {port} 或网络隔离。请在目标机放行 TCP {port} 入站、并在云控制台安全组放行同一端口，确认 Web 主机可路由到该地址。详见「手工安装教程 / 排错手册 → 排错手册」。",
                LocalProbeResult.Fail =>
                    $"Agent 服务未正常监听端口 {port}（本机自检亦失败）：请确认服务已启动、Port 配置与实际一致（改 Port 后需重启服务）、端口未被其他进程占用。详见「手工安装教程 / 排错手册 → 排错手册」。",
                _ =>
                    $"外机 /version 不通，且无法经远程通道做本机诊断（通道不可用或连接失败）。请检查 Agent 服务状态、端口 {port} 放行情况与远程凭据。详见「手工安装教程 / 排错手册 → 排错手册」。"
            };
            onLog?.Invoke($"[verify] {failMsg}");
            return Fail(server, failMsg);
        }

        /// <summary>本机自检结果分类：Ok=本机 /version 通；Fail=本机亦不通（服务未起/端口错）；Unknown=通道不可用无法诊断。</summary>
        private enum LocalProbeResult { Ok, Fail, Unknown }

        /// <summary>
        /// 经远程通道在“服务器本机”执行 /version 自检（best-effort）：通道不可用或连接失败返回 <see cref="LocalProbeResult.Unknown"/>；
        /// 解析输出中的 <c>LOCAL_VERSION=</c>（有版本=Ok）与 <c>LOCAL_VERSION_ERR=</c>/空（=Fail）。
        /// </summary>
        private async Task<LocalProbeResult> ProbeLocalAsync(AgentClient client, AgentServerItem server, int port, Action<string>? onLog, CancellationToken ct)
        {
            var (chanOk, chanReason) = RemoteTransportFactory.CheckChannelAvailable(server);
            if (!chanOk)
            {
                onLog?.Invoke($"[verify] 本机诊断跳过：{chanReason}");
                return LocalProbeResult.Unknown;
            }

            try
            {
                using var transport = RemoteTransportFactory.Create(server);
                if (!await transport.TestConnectAsync(onLog, ct).ConfigureAwait(false))
                {
                    onLog?.Invoke("[verify] 本机诊断跳过：远程通道连接失败");
                    return LocalProbeResult.Unknown;
                }

                var cmd = BuildLocalProbeCommand(client, server, port);
                onLog?.Invoke($"[verify] 本机自检命令：{cmd}");
                var res = await transport.RunAsync(cmd, onLog, ct).ConfigureAwait(false);
                return ClassifyLocalOutput(res.Output, onLog);
            }
            catch (OperationCanceledException)
            {
                onLog?.Invoke("[verify] 本机诊断已取消");
                return LocalProbeResult.Unknown;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agent 本机诊断异常 {Host}", server.Host);
                onLog?.Invoke($"[verify] 本机诊断异常：{ex.Message}");
                return LocalProbeResult.Unknown;
            }
        }

        /// <summary>解析本机自检输出：遇 <c>LOCAL_VERSION_ERR=</c> 或空版本判 Fail；<c>LOCAL_VERSION=</c> 带版本判 Ok。</summary>
        private static LocalProbeResult ClassifyLocalOutput(string? output, Action<string>? onLog)
        {
            if (string.IsNullOrEmpty(output))
            {
                onLog?.Invoke("[verify] 本机自检无输出（命令未执行或服务无响应）");
                return LocalProbeResult.Fail;
            }
            foreach (var rawLine in output.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.StartsWith("LOCAL_VERSION_ERR=", StringComparison.OrdinalIgnoreCase))
                {
                    onLog?.Invoke($"[verify] 本机自检失败：{line.Substring("LOCAL_VERSION_ERR=".Length).Trim()}");
                    return LocalProbeResult.Fail;
                }
                if (line.StartsWith("LOCAL_VERSION=", StringComparison.OrdinalIgnoreCase))
                {
                    var v = line.Substring("LOCAL_VERSION=".Length).Trim();
                    if (!string.IsNullOrEmpty(v))
                    {
                        onLog?.Invoke($"[verify] 本机自检成功：/version={v}");
                        return LocalProbeResult.Ok;
                    }
                }
            }
            onLog?.Invoke("[verify] 本机自检未返回版本（服务未监听或响应为空）");
            return LocalProbeResult.Fail;
        }

        /// <summary>
        /// 构造“服务器本机”/version 自检命令（单行，适配 OS 与通道，输出统一 <c>LOCAL_VERSION=</c>/<c>LOCAL_VERSION_ERR=</c>）：
        /// <list type="bullet">
        /// <item>Windows + WinRM：原始 PowerShell（<see cref="WinRmTransport.RunAsync"/> 以 scriptblock 执行）；</item>
        /// <item>Windows + SSH：默认 shell 为 cmd，用 <c>powershell.exe -Command</c> 包裹（内部仅单引号，无需转义）；</item>
        /// <item>Linux：curl 优先、回退 wget，均缺失则报 no_curl_wget。</item>
        /// </list>
        /// </summary>
        private static string BuildLocalProbeCommand(AgentClient client, AgentServerItem server, int port)
        {
            if (client.IsWindows)
            {
                var ps = "try { $r = Invoke-WebRequest -Uri 'http://127.0.0.1:" + port
                    + "/version' -Method Post -Body '' -UseBasicParsing -TimeoutSec 8; Write-Output ('LOCAL_VERSION=' + $r.Content.Trim()) }"
                    + " catch { Write-Output ('LOCAL_VERSION_ERR=' + $_.Exception.Message) }";
                return server.IsWinRm ? ps : "powershell.exe -NoProfile -Command \"" + ps + "\"";
            }

            return "if command -v curl >/dev/null 2>&1; then V=$(curl -s -m 8 -X POST http://127.0.0.1:" + port + "/version 2>/dev/null);"
                + " elif command -v wget >/dev/null 2>&1; then V=$(wget -qO- --timeout=8 --post-data='' http://127.0.0.1:" + port + "/version 2>/dev/null);"
                + " else echo LOCAL_VERSION_ERR=no_curl_wget; exit 0; fi;"
                + " if [ -n \"$V\" ]; then echo LOCAL_VERSION=$V; else echo LOCAL_VERSION_ERR=empty_response; fi";
        }

        private static HostResult Ok(AgentServerItem s, string msg) =>
            new HostResult { Host = s.Host, Display = s.Display, Success = true, Msg = msg };

        private static HostResult Fail(AgentServerItem s, string msg) =>
            new HostResult { Host = s.Host, Display = s.Display, Success = false, Msg = msg };
    }
}
