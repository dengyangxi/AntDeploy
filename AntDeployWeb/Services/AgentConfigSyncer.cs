using AntDeploy.Core;
using AntDeploy.Core.Models;
using AntDeploy.Core.Util;
using AntDeployWeb.Data;
using AntDeployWeb.Models;

namespace AntDeployWeb.Services
{
    /// <summary>
    /// Agent 配置在线同步（W1.10）：把 Web 端编辑后的 Agent 运行配置经 <c>/config</c> 端点推送到每台在线服务器，
    /// Agent 本机原子写 <c>agentsettings.json</c> 并热加载即时生效（<c>Port</c> 变更需重启服务才会重新监听）。
    /// <para>推送前先用 <c>/version</c> 探测能力：不可达、或版本 &lt; <see cref="AgentCapability.MinConfigSyncVersion"/>(7.3) 的服务器跳过并记录原因，
    /// 从而“列出未同步服务器与原因”。凭据不在本类处理（Token 由调用方以明文传入，仅用于本次 HTTP 鉴权）。</para>
    /// </summary>
    public class AgentConfigSyncer
    {
        /// <summary>/version 探测超时（DeployClient 内部 HttpClient 默认 30 分钟过长，用链接令牌缩短）。</summary>
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(12);

        /// <summary>/config 推送超时。</summary>
        private static readonly TimeSpan PushTimeout = TimeSpan.FromSeconds(20);

        private readonly ILogger<AgentConfigSyncer> _logger;

        public AgentConfigSyncer(ILogger<AgentConfigSyncer> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// 对 <see cref="AgentClient.Servers"/> 逐台推送 <see cref="AgentClient.Config"/>（顺序）。onLog 收到实时日志。
        /// 返回每台 ✓/✗ + 原因（成功项标注是否需重启）。
        /// </summary>
        public async Task<List<HostResult>> SyncAsync(AgentClient client, Action<string>? onLog, CancellationToken ct = default)
        {
            var results = new List<HostResult>();
            if (client == null) return results;

            var servers = client.Servers ?? new List<AgentServerItem>();
            if (servers.Count == 0)
            {
                onLog?.Invoke("没有可同步的服务器。");
                return results;
            }

            var config = client.Config ?? new AgentConfigDto();
            var token = config.Token ?? string.Empty;
            if (string.IsNullOrWhiteSpace(token))
            {
                var msg = "Agent Token 为空，无法进行在线同步（/config 需 Token 鉴权）。";
                onLog?.Invoke(msg);
                foreach (var s in servers) results.Add(Fail(s, msg));
                return results;
            }

            var port = AgentVerifier.ResolveAgentPort(client);
            for (var i = 0; i < servers.Count; i++)
            {
                if (ct.IsCancellationRequested)
                {
                    onLog?.Invoke("同步已取消。");
                    break;
                }
                var server = servers[i];
                onLog?.Invoke(string.Empty);
                onLog?.Invoke($"==================== [同步 {i + 1}/{servers.Count}] {server.Host} ====================");
                var r = await SyncOneAsync(server, port, token, config, onLog, ct).ConfigureAwait(false);
                results.Add(r);
                onLog?.Invoke(r.Success
                    ? $"==================== ✓ {server.Host} 同步成功：{r.Msg} ===================="
                    : $"==================== ✗ {server.Host} 同步失败：{r.Msg} ====================");
            }
            return results;
        }

        private async Task<HostResult> SyncOneAsync(AgentServerItem server, int port, string token, AgentConfigDto config, Action<string>? onLog, CancellationToken ct)
        {
            var agentUrl = $"{server.Host}:{port}"; // DeployClient 内部 NormalizeAgentUrl 会补 http://
            var log = new ActionLog((lvl, msg) => onLog?.Invoke($"[{lvl}] {msg}"));
            var dc = new DeployClient(log);

            // 1. 探测版本 + 能力（/version 空 body 无需 token）
            string? version = null;
            try
            {
                using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                probeCts.CancelAfter(ProbeTimeout);
                onLog?.Invoke($"[sync] 探测 http://{agentUrl}/version ...");
                version = (await dc.GetAgentVersionAsync(agentUrl, probeCts.Token).ConfigureAwait(false))?.Trim();
            }
            catch (Exception ex)
            {
                onLog?.Invoke($"[sync] 版本探测异常：{ex.Message}");
            }

            if (string.IsNullOrEmpty(version) || !char.IsDigit(version[0]))
            {
                return Fail(server, $"不可达或响应异常（/version 未返回有效版本）：请确认 Agent 已启动、端口 {port} 已放行。详见「手工安装教程 / 排错手册」。");
            }

            if (!AgentCapability.SupportsConfigSync(version))
            {
                return Fail(server, $"Agent 版本 {version} 低于 {AgentCapability.MinConfigSyncVersion}，不支持在线配置同步；请升级 Agent 后重试，或改用「重装」下发新配置。");
            }

            // 2. 推送配置（/config，Action=set）
            onLog?.Invoke($"[sync] 版本 {version} 支持在线同步，推送配置到 http://{agentUrl}/config ...");
            AgentConfigResult res;
            try
            {
                using var pushCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                pushCts.CancelAfter(PushTimeout);
                res = await dc.PushAgentConfigAsync(agentUrl, token, config, pushCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Fail(server, "推送已取消");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "推送配置异常 {Host}", server.Host);
                return Fail(server, "推送配置异常：" + ex.Message);
            }

            if (res != null && res.Success)
            {
                var msg = res.RestartNeeded
                    ? $"配置已同步（版本 {version}）；Port 变更需重启 Agent 服务才会重新监听。"
                    : $"配置已同步并热生效（版本 {version}）。";
                onLog?.Invoke($"[sync] {msg}");
                return Ok(server, msg);
            }

            var reason = string.IsNullOrWhiteSpace(res?.Msg) ? "未知错误（可能 Token 不匹配，两端 Token 须完全一致）" : res!.Msg;
            return Fail(server, "同步失败：" + reason);
        }

        private static HostResult Ok(AgentServerItem s, string msg) =>
            new HostResult { Host = s.Host, Display = s.Display, Success = true, Msg = msg };

        private static HostResult Fail(AgentServerItem s, string msg) =>
            new HostResult { Host = s.Host, Display = s.Display, Success = false, Msg = msg };
    }
}
