using System.Text;
using AntDeploy.Core.Models;
using AntDeployWeb.Data;
using AntDeployWeb.Models;
using AntDeployWeb.Services.Remote;
using Newtonsoft.Json;

namespace AntDeployWeb.Services
{
    /// <summary>
    /// Agent 在线安装编排（W1.7）：对每台目标服务器顺序执行 连接测试 → 上传产物 → 生成并上传配置/安装脚本 →
    /// 安装为系统服务并启动 → 本机 /version 自检；全程 <c>onLog</c> 流式回传命令与输出，逐台产出 <see cref="HostResult"/>（✓/✗）。
    /// <para>安装脚本模板化（占位安装目录/服务名/端口），所有外部命令前做 <c>command -v</c>/<c>Get-Command</c> 存在性判断，
    /// 兼容多发行版与多 Windows 版本；成功以输出中的 <c>INSTALL_OK</c> 哨兵判定（跨 SSH/WinRM 通道一致，不受退出码差异影响）。</para>
    /// </summary>
    public class AgentInstaller
    {
        public const string WindowsServiceName = "AntDeployAgentWindowsService";
        public const string WindowsExeName = "AntDeployAgentWindowsService.exe";
        public const string LinuxServiceName = "AntAgent";
        public const string LinuxBinName = "AntDeployAgentLinuxService";
        public const string JsonFileName = "agentsettings.json";
        public const int DefaultPort = 8091;

        /// <summary>安装脚本成功哨兵：脚本运行到末尾输出该行，代表关键步骤全部完成。</summary>
        private const string OkSentinel = "INSTALL_OK";

        private readonly string _contentRoot;
        private readonly ILogger<AgentInstaller> _logger;

        public AgentInstaller(IWebHostEnvironment env, ILogger<AgentInstaller> logger)
        {
            _contentRoot = env.ContentRootPath;
            _logger = logger;
        }

        /// <summary>本地 Agent 发布产物目录：ContentRootPath/AgentService/{Windows|Linux}（由 publish-agents.ps1 生成）。</summary>
        public string GetProductsDir(string? osType) =>
            Path.Combine(_contentRoot, "AgentService",
                string.Equals(osType, "windows", StringComparison.OrdinalIgnoreCase) ? "Windows" : "Linux");

        /// <summary>
        /// 对 <see cref="AgentClient.Servers"/> 逐台安装（顺序执行）。onLog 收到带主机前缀的实时日志行。
        /// 返回每台结果（✓/✗ + 原因），供 UI 展示与入库。
        /// </summary>
        public async Task<List<HostResult>> InstallAsync(AgentClient client, Action<string>? onLog, CancellationToken ct = default)
        {
            var results = new List<HostResult>();
            if (client == null) return results;

            var servers = client.Servers ?? new List<AgentServerItem>();
            if (servers.Count == 0)
            {
                onLog?.Invoke("没有可安装的服务器，请先在向导中添加至少一台。");
                return results;
            }

            var products = GetProductsDir(client.OsType);
            if (!Directory.Exists(products) || !Directory.EnumerateFileSystemEntries(products).Any())
            {
                var msg = $"未找到 Agent 发布产物目录：{products}（请先在 AntDeployWeb 目录运行 publish-agents.ps1）";
                onLog?.Invoke(msg);
                foreach (var s in servers) results.Add(Fail(s, msg));
                return results;
            }

            // 生成一份所有服务器共用的 agentsettings.json（含用户填写的 Token/Port/limits/白名单）
            var settingsJson = BuildAgentSettingsJson(client);

            for (var i = 0; i < servers.Count; i++)
            {
                if (ct.IsCancellationRequested)
                {
                    onLog?.Invoke("安装已取消。");
                    break;
                }
                var server = servers[i];
                onLog?.Invoke(string.Empty);
                onLog?.Invoke($"==================== [{i + 1}/{servers.Count}] {server.Display} ====================");
                var r = await InstallOneAsync(client, server, products, settingsJson, onLog, ct).ConfigureAwait(false);
                results.Add(r);
                onLog?.Invoke(r.Success
                    ? $"==================== ✓ {server.Host} 安装成功：{r.Msg} ===================="
                    : $"==================== ✗ {server.Host} 安装失败：{r.Msg} ====================");
            }
            return results;
        }

        private async Task<HostResult> InstallOneAsync(
            AgentClient client, AgentServerItem server, string products, string settingsJson,
            Action<string>? onLog, CancellationToken ct)
        {
            try
            {
                // 0. 通道可用性预检（WinRM 要求 Web 宿主 Windows；SSH 需密码或私钥）
                var (chanOk, chanReason) = RemoteTransportFactory.CheckChannelAvailable(server);
                if (!chanOk)
                {
                    onLog?.Invoke($"[skip] {chanReason}");
                    return Fail(server, chanReason);
                }

                var installDir = server.EffectiveInstallDir(client.OsType);
                var port = client.Config != null && client.Config.Port > 0 ? client.Config.Port : DefaultPort;

                using var transport = RemoteTransportFactory.Create(server);

                // 1. 连接测试（失败即快速返回：网络/认证/通道）
                if (!await transport.TestConnectAsync(onLog, ct).ConfigureAwait(false))
                {
                    return Fail(server, "连接测试失败（网络不通 / 认证失败 / 通道不支持），详见日志");
                }

                // 2. 上传发布产物（目录内容合并进安装目录）
                onLog?.Invoke($"[upload] 产物 {products} -> {installDir}");
                if (!await transport.UploadAsync(products, installDir, onLog, ct).ConfigureAwait(false))
                {
                    return Fail(server, "上传 Agent 产物失败，详见日志");
                }

                // 3. 上传生成的 agentsettings.json（覆盖模板），用临时目录承载以保留精确文件名
                var tempDir = Path.Combine(Path.GetTempPath(), "antagent_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(tempDir);
                    var jsonPath = Path.Combine(tempDir, JsonFileName);
                    File.WriteAllText(jsonPath, settingsJson, new UTF8Encoding(false));
                    onLog?.Invoke($"[upload] 配置 {JsonFileName} -> {installDir}");
                    if (!await transport.UploadAsync(jsonPath, installDir, onLog, ct).ConfigureAwait(false))
                    {
                        return Fail(server, "上传 agentsettings.json 失败，详见日志");
                    }

                    // 4. 生成安装脚本，上传（SSH）或直接下发（WinRM），执行：安装服务 + 启动 + 防火墙 + 本机自检
                    var isWin = client.IsWindows;
                    var scriptName = isWin ? "install-agent.ps1" : "install-agent.sh";
                    var script = isWin
                        ? BuildWindowsInstallScript(installDir, port)
                        : BuildLinuxInstallScript(installDir, port);

                    onLog?.Invoke("[script] 生成的安装脚本：");
                    foreach (var line in script.Split('\n')) onLog?.Invoke("    | " + line.TrimEnd('\r'));

                    var runRes = await RunScriptAsync(client, server, transport, script, scriptName, installDir, tempDir, onLog, ct).ConfigureAwait(false);
                    var ok = (runRes.Output ?? string.Empty).Contains(OkSentinel);
                    if (!ok)
                    {
                        return Fail(server, "安装/启动服务未成功：" + Shorten(runRes.Reason));
                    }

                    var localVer = ParseLocalVersion(runRes.Output);
                    var msg = "服务已安装并启动";
                    if (localVer != null) msg += $"，本机 /version={localVer}";
                    else msg += "（本机 /version 暂未就绪，稍后于连接验证阶段确认）";
                    return new HostResult { Host = server.Host, Display = server.Display, Success = true, Msg = msg };
                }
                finally
                {
                    try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
                }
            }
            catch (OperationCanceledException)
            {
                return Fail(server, "已取消");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agent 安装异常 {Host}", server.Host);
                onLog?.Invoke($"[error] {ex.Message}");
                return Fail(server, ex.Message);
            }
        }

        /// <summary>
        /// 执行安装脚本：WinRM 直接以 scriptblock 下发脚本内容（不落盘、不受执行策略限制）；
        /// SSH 则落盘为脚本文件后用对应解释器执行（<c>powershell.exe -File</c> / <c>bash</c>），规避多行与引号转义。
        /// </summary>
        private async Task<RemoteResult> RunScriptAsync(
            AgentClient client, AgentServerItem server, IRemoteTransport transport,
            string script, string scriptName, string installDir, string tempDir,
            Action<string>? onLog, CancellationToken ct)
        {
            if (server.IsWinRm)
            {
                return await transport.RunAsync(script, onLog, ct).ConfigureAwait(false);
            }

            // SSH：写临时脚本文件（.ps1 带 BOM 以兼容 Windows PowerShell 5.1 中文；.sh 无 BOM）
            var localScript = Path.Combine(tempDir, scriptName);
            var isWin = client.IsWindows;
            File.WriteAllText(localScript, script, new UTF8Encoding(isWin));

            if (!await transport.UploadAsync(localScript, installDir, onLog, ct).ConfigureAwait(false))
            {
                return RemoteResult.Fail("上传安装脚本失败");
            }

            string remoteScript, exec;
            if (isWin)
            {
                remoteScript = installDir.TrimEnd('\\', '/') + "\\" + scriptName;
                exec = $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{remoteScript}\"";
            }
            else
            {
                remoteScript = installDir.TrimEnd('/') + "/" + scriptName;
                exec = $"bash '{remoteScript}'";
            }
            onLog?.Invoke($"[exec] {exec}");
            return await transport.RunAsync(exec, onLog, ct).ConfigureAwait(false);
        }

        /// <summary>由 <see cref="AgentConfigDto"/> 序列化 agentsettings.json（属性名与 Agent 端 AgentConfig 一一对应）。</summary>
        public static string BuildAgentSettingsJson(AgentClient client)
        {
            var cfg = client.Config ?? new AgentConfigDto();
            return JsonConvert.SerializeObject(cfg, Formatting.Indented);
        }

        private static string? ParseLocalVersion(string? output)
        {
            if (string.IsNullOrEmpty(output)) return null;
            foreach (var raw in output.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("LOCAL_VERSION_ERR=", StringComparison.OrdinalIgnoreCase)) return null;
                if (line.StartsWith("LOCAL_VERSION=", StringComparison.OrdinalIgnoreCase))
                {
                    var v = line.Substring("LOCAL_VERSION=".Length).Trim();
                    return string.IsNullOrEmpty(v) ? null : v;
                }
            }
            return null;
        }

        private static string Shorten(string s, int max = 400)
        {
            if (string.IsNullOrEmpty(s)) return "未知错误";
            s = s.Trim();
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        private static HostResult Fail(AgentServerItem s, string msg) =>
            new HostResult { Host = s.Host, Display = s.Display, Success = false, Msg = msg };

        // ============================ 安装脚本模板 ============================

        /// <summary>Windows 安装脚本（PowerShell）：幂等重装为原生 Windows 服务（LocalSystem/管理员权限），放行防火墙，本机自检 /version。</summary>
        private string BuildWindowsInstallScript(string installDir, int port)
        {
            var tpl = @"$ErrorActionPreference = 'Stop'
$installDir = '__INSTALLDIR__'
$svc = '__SVCNAME__'
$port = __PORT__
$exe = Join-Path $installDir '__EXENAME__'

Write-Output '[install] === AntDeploy Agent 安装开始 (Windows) ==='
Write-Output ('[install] OS=' + [System.Environment]::OSVersion.VersionString)
Write-Output ('[install] PowerShell=' + $PSVersionTable.PSVersion.ToString())
Write-Output ('[install] installDir=' + $installDir + '  port=' + $port)

if (-not (Test-Path -LiteralPath $installDir)) { New-Item -ItemType Directory -Force -Path $installDir | Out-Null }
if (-not (Test-Path -LiteralPath $exe)) { throw ('未找到 Agent 可执行文件: ' + $exe + '（上传可能失败）') }

$existing = Get-Service -Name $svc -ErrorAction SilentlyContinue
if ($existing) {
    Write-Output '[install] 已存在同名服务，先停止并删除（幂等重装）'
    try { Stop-Service -Name $svc -Force -ErrorAction SilentlyContinue } catch { }
    Start-Sleep -Milliseconds 800
    & sc.exe delete $svc | Out-Null
    Start-Sleep -Seconds 2
}

$q = [char]34
$bin = $q + $exe + $q
New-Service -Name $svc -BinaryPathName $bin -DisplayName 'AntDeploy Agent' -StartupType Automatic -Description 'AntDeploy 部署 Agent' | Out-Null
Write-Output '[install] 服务已创建（LocalSystem/管理员权限、自动启动）'
& sc.exe failure $svc reset= 86400 actions= restart/5000 | Out-Null

try {
    if (Get-Command New-NetFirewallRule -ErrorAction SilentlyContinue) {
        if (-not (Get-NetFirewallRule -DisplayName 'AntDeployAgent' -ErrorAction SilentlyContinue)) {
            New-NetFirewallRule -DisplayName 'AntDeployAgent' -Direction Inbound -Action Allow -Protocol TCP -LocalPort $port | Out-Null
            Write-Output ('[install] 已放行防火墙入站端口 ' + $port)
        } else {
            Write-Output '[install] 防火墙规则 AntDeployAgent 已存在'
        }
    }
} catch { Write-Output ('[install] 防火墙放行跳过: ' + $_.Exception.Message) }

Start-Service -Name $svc
Start-Sleep -Seconds 2
$status = (Get-Service -Name $svc).Status
Write-Output ('[install] 服务状态: ' + $status)

try {
    $resp = Invoke-WebRequest -Uri ('http://127.0.0.1:' + $port + '/version') -Method Post -Body '' -UseBasicParsing -TimeoutSec 8
    Write-Output ('LOCAL_VERSION=' + $resp.Content.Trim())
} catch {
    Write-Output ('LOCAL_VERSION_ERR=' + $_.Exception.Message)
}
Write-Output '__OK__'
Write-Output '[install] === Windows 安装流程结束 ==='";

            return tpl
                .Replace("__INSTALLDIR__", installDir.Replace("'", "''"))
                .Replace("__SVCNAME__", WindowsServiceName)
                .Replace("__EXENAME__", WindowsExeName)
                .Replace("__PORT__", port.ToString())
                .Replace("__OK__", OkSentinel);
        }

        /// <summary>Linux 安装脚本（bash）：探测 systemd（有则装 AntAgent.service，无则 nohup 回退），best-effort 依赖与防火墙，本机自检 /version。</summary>
        private string BuildLinuxInstallScript(string installDir, int port)
        {
            var tpl = @"#!/usr/bin/env bash
set -e
INSTALL_DIR='__INSTALLDIR__'
SVC='__SVCNAME__'
PORT=__PORT__
BIN=""$INSTALL_DIR/__BINNAME__""

if [ ""$(id -u)"" -ne 0 ]; then SUDO=""sudo -n""; else SUDO=""""; fi

echo ""[install] === AntDeploy Agent 安装开始 (Linux) ===""
echo ""[install] OS=$(uname -s) $(uname -r)  user=$(whoami)""
echo ""[install] installDir=$INSTALL_DIR  port=$PORT""
if [ -n ""$SUDO"" ]; then echo ""[install] WARN: 非 root 用户，使用 sudo -n（需配置免密 sudo）""; fi

$SUDO mkdir -p ""$INSTALL_DIR""
if [ ! -f ""$BIN"" ]; then echo ""[install] ERROR: 未找到可执行文件 $BIN（上传可能失败）""; exit 1; fi
$SUDO chmod +x ""$BIN"" || true

HAS_SYSTEMD=0
if command -v systemctl >/dev/null 2>&1 && [ -d /run/systemd/system ]; then HAS_SYSTEMD=1; fi
echo ""[install] systemd=$HAS_SYSTEMD""

if ! command -v curl >/dev/null 2>&1 && ! command -v wget >/dev/null 2>&1; then
    echo ""[install] curl/wget 均缺失，best-effort 安装 curl（不阻断安装）""
    if command -v apt-get >/dev/null 2>&1; then $SUDO apt-get install -y curl >/dev/null 2>&1 || true
    elif command -v dnf >/dev/null 2>&1; then $SUDO dnf install -y curl >/dev/null 2>&1 || true
    elif command -v yum >/dev/null 2>&1; then $SUDO yum install -y curl >/dev/null 2>&1 || true
    elif command -v apk >/dev/null 2>&1; then $SUDO apk add --no-cache curl >/dev/null 2>&1 || true
    elif command -v zypper >/dev/null 2>&1; then $SUDO zypper --non-interactive install curl >/dev/null 2>&1 || true; fi
fi

if [ ""$HAS_SYSTEMD"" = ""1"" ]; then
    echo ""[install] 写入 systemd 单元 $SVC.service""
    $SUDO tee /etc/systemd/system/$SVC.service >/dev/null <<UNIT
[Unit]
Description=AntAgent

[Service]
Type=notify
WorkingDirectory=$INSTALL_DIR
ExecStart=$BIN
SyslogIdentifier=AntAgent
Restart=always
RestartSec=5
KillSignal=SIGINT

[Install]
WantedBy=multi-user.target
UNIT
    $SUDO systemctl daemon-reload
    $SUDO systemctl enable ""$SVC"" >/dev/null 2>&1 || true
    $SUDO systemctl restart ""$SVC""
    sleep 2
    $SUDO systemctl --no-pager --lines=5 status ""$SVC"" || true
else
    echo ""[install] 无 systemd，回退 nohup 后台运行""
    $SUDO pkill -f ""$BIN"" >/dev/null 2>&1 || true
    sleep 1
    cd ""$INSTALL_DIR""
    $SUDO nohup ""$BIN"" >/var/log/antagent.log 2>&1 &
    sleep 2
    echo ""[install] 已通过 nohup 启动，日志 /var/log/antagent.log（重启不自启，建议配置 rc.local/init.d）""
fi

if command -v firewall-cmd >/dev/null 2>&1; then
    $SUDO firewall-cmd --add-port=$PORT/tcp --permanent >/dev/null 2>&1 || true
    $SUDO firewall-cmd --reload >/dev/null 2>&1 || true
    echo ""[install] firewalld 放行 $PORT/tcp""
elif command -v ufw >/dev/null 2>&1; then
    $SUDO ufw allow $PORT/tcp >/dev/null 2>&1 || true
    echo ""[install] ufw 放行 $PORT/tcp""
fi

sleep 1
if command -v curl >/dev/null 2>&1; then
    V=$(curl -s -m 8 -X POST ""http://127.0.0.1:$PORT/version"" || true)
    echo ""LOCAL_VERSION=$V""
elif command -v wget >/dev/null 2>&1; then
    V=$(wget -qO- --timeout=8 --post-data='' ""http://127.0.0.1:$PORT/version"" || true)
    echo ""LOCAL_VERSION=$V""
fi
echo ""__OK__""
echo ""[install] === Linux 安装流程结束 ===""";

            return tpl
                .Replace("__INSTALLDIR__", installDir.Replace("'", "'\\''"))
                .Replace("__SVCNAME__", LinuxServiceName)
                .Replace("__BINNAME__", LinuxBinName)
                .Replace("__PORT__", port.ToString())
                .Replace("__OK__", OkSentinel);
        }
    }
}
