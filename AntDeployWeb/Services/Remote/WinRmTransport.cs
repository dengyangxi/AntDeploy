using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AntDeployWeb.Models;

namespace AntDeployWeb.Services.Remote
{
    /// <summary>
    /// WinRM 远程通道（PowerShell Remoting）：<c>New-PSSession</c> + <c>Invoke-Command</c> 执行、
    /// <c>Copy-Item -ToSession</c> 传输；支持 AD 域（Negotiate/Kerberos）。
    /// <para><b>要求 AntDeployWeb 宿主为 Windows</b>：非 Windows 宿主时所有操作快速返回“通道不支持”，
    /// 对 Windows 服务器建议回退 SSH（OpenSSH）。通过 <c>powershell.exe</c> 子进程执行，
    /// 主机 / 端口 / 账号 / 密码 / 远程脚本经环境变量传入（避免命令行转义与凭据泄露），无需引入 PowerShell SDK 包。</para>
    /// </summary>
    public class WinRmTransport : IRemoteTransport
    {
        private readonly string _host;
        private readonly int _port;
        private readonly string _userName;
        private readonly string _password;
        private readonly string _auth = "Negotiate";

        /// <summary>强制 PowerShell 以 UTF-8 写 stdout/stderr 的前置语句：Windows PowerShell 5.1 默认按控制台活动代码页
        /// （中文系统 GBK/936）输出，而宿主按 UTF-8 读取，本地化异常消息（含中文）会乱码；无控制台时置编码可能抛错，故 try/catch 兜底。</summary>
        private const string Utf8Prologue = "try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}\r\n";

        /// <summary>执行远程命令的 PowerShell 包装脚本（会话建立 + 运行 $env:ANT_WR_CODE）。
        /// 成功=脚本未抛出（exit 0）；失败=抛出终止性错误（catch → exit 1）。业务成功另由输出中的 INSTALL_OK 哨兵判定。</summary>
        private const string RunWrapper = @"$ErrorActionPreference = 'Stop'
$session = $null
try {
    $sec = ConvertTo-SecureString $env:ANT_WR_PWD -AsPlainText -Force
    $cred = New-Object System.Management.Automation.PSCredential($env:ANT_WR_USER, $sec)
    $so = New-PSSessionOption -OperationTimeout 180000 -OpenTimeout 30000
    $session = New-PSSession -ComputerName $env:ANT_WR_HOST -Port ([int]$env:ANT_WR_PORT) -Credential $cred -Authentication $env:ANT_WR_AUTH -SessionOption $so
    $sb = [scriptblock]::Create($env:ANT_WR_CODE)
    Invoke-Command -Session $session -ScriptBlock $sb
    Remove-PSSession $session; $session = $null
    exit 0
} catch {
    Write-Output ('[winrm][error] ' + $_.Exception.Message)
    if ($session) { try { Remove-PSSession $session } catch {} }
    exit 1
}";

        /// <summary>上传的 PowerShell 包装脚本（会话建立 + 远程建目录 + Copy-Item -ToSession 递归）。</summary>
        private const string UploadWrapper = @"$ErrorActionPreference = 'Stop'
$session = $null
try {
    $sec = ConvertTo-SecureString $env:ANT_WR_PWD -AsPlainText -Force
    $cred = New-Object System.Management.Automation.PSCredential($env:ANT_WR_USER, $sec)
    $so = New-PSSessionOption -OperationTimeout 600000 -OpenTimeout 30000
    $session = New-PSSession -ComputerName $env:ANT_WR_HOST -Port ([int]$env:ANT_WR_PORT) -Credential $cred -Authentication $env:ANT_WR_AUTH -SessionOption $so
    Invoke-Command -Session $session -ScriptBlock { param($d) if (-not (Test-Path -LiteralPath $d)) { New-Item -ItemType Directory -Force -Path $d | Out-Null } } -ArgumentList $env:ANT_WR_REMDIR
    Copy-Item -Path $env:ANT_WR_LOCAL -Destination $env:ANT_WR_REMDIR -ToSession $session -Recurse -Force
    Remove-PSSession $session; $session = $null
    exit 0
} catch {
    Write-Output ('[winrm][error] ' + $_.Exception.Message)
    if ($session) { try { Remove-PSSession $session } catch {} }
    exit 1
}";

        public string ChannelName => "winrm";
        public string Display => $"{_host}:{_port}/winrm";

        /// <summary>当前 AntDeployWeb 宿主是否 Windows（WinRM 通道前提）。</summary>
        public static bool HostSupportsWinRm => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        public WinRmTransport(AgentServerItem server)
        {
            _host = (server.Host ?? string.Empty).Trim();
            _port = server.EffectivePort;
            _password = server.Pwd ?? string.Empty;
            _userName = BuildDomainUser((server.UserName ?? string.Empty).Trim(), server.Domain);
        }

        /// <summary>组合 AD 域账号：Domain 非空且用户名未含 \ 或 @ 时前缀 "Domain\"。</summary>
        private static string BuildDomainUser(string user, string? domain)
        {
            if (string.IsNullOrWhiteSpace(domain)) return user;
            if (user.Contains('\\') || user.Contains('@')) return user;
            return domain.Trim() + "\\" + user;
        }

        public async Task<bool> TestConnectAsync(Action<string>? onLog, CancellationToken ct = default)
        {
            if (!HostSupportsWinRm)
            {
                onLog?.Invoke("[winrm] 当前 Web 宿主非 Windows，WinRM 通道不可用；请对 Windows 服务器改用 SSH 通道");
                return false;
            }
            onLog?.Invoke($"[winrm] connecting {_host}:{_port} as {_userName} (auth={_auth}) ...");
            var res = await RunPowerShellAsync(RunWrapper,
                code: "Write-Output 'antdeploy-winrm-ok'",
                onLog: onLog, ct: ct,
                extraEnv: null).ConfigureAwait(false);
            var ok = res.ExitCode == 0;
            if (ok)
            {
                onLog?.Invoke($"[winrm] connect success: {_userName}@{_host}:{_port}");
            }
            else
            {
                onLog?.Invoke($"[winrm] connect fail: {res.Reason}");
                var hint = HintFor(res.Reason, _host);
                if (!string.IsNullOrEmpty(hint)) onLog?.Invoke("[winrm][hint] " + hint);
            }
            return ok;
        }

        public async Task<RemoteResult> RunAsync(string script, Action<string>? onLog, CancellationToken ct = default)
        {
            if (!HostSupportsWinRm)
            {
                var msg = "当前 Web 宿主非 Windows，WinRM 通道不可用；请对 Windows 服务器改用 SSH 通道";
                onLog?.Invoke("[winrm] " + msg);
                return RemoteResult.Fail(msg);
            }
            return await RunPowerShellAsync(RunWrapper, code: script, onLog: onLog, ct: ct, extraEnv: null).ConfigureAwait(false);
        }

        public async Task<bool> UploadAsync(string localPath, string remotePath, Action<string>? onLog, CancellationToken ct = default)
        {
            if (!HostSupportsWinRm)
            {
                onLog?.Invoke("[winrm] 当前 Web 宿主非 Windows，WinRM 通道不可用；请改用 SSH 通道上传");
                return false;
            }
            if (!Directory.Exists(localPath) && !File.Exists(localPath))
            {
                onLog?.Invoke($"[winrm] upload fail: local path not found {localPath}");
                return false;
            }

            // remotePath 语义为“目标目录”（与 SshTransport 一致）：
            // 目录源用 "<dir>\*" 复制其内容并入目标目录；文件源复制进目标目录并保留文件名。
            var isDir = Directory.Exists(localPath);
            var srcForCopy = isDir
                ? localPath.TrimEnd('\\', '/') + "\\*"
                : localPath;

            onLog?.Invoke($"[winrm] upload {(isDir ? "dir " : "file ")}{localPath} -> {remotePath}");
            var env = new Dictionary<string, string?>
            {
                ["ANT_WR_LOCAL"] = srcForCopy,
                ["ANT_WR_REMDIR"] = remotePath
            };
            var res = await RunPowerShellAsync(UploadWrapper, code: null, onLog: onLog, ct: ct, extraEnv: env).ConfigureAwait(false);
            if (res.ExitCode == 0)
            {
                onLog?.Invoke("[winrm] upload success");
                return true;
            }
            onLog?.Invoke($"[winrm] upload fail: {res.Reason}");
            return false;
        }

        /// <summary>
        /// 写临时 .ps1 并以 <c>powershell.exe -File</c> 执行；凭据 / 主机 / 远程脚本经环境变量传入，
        /// stdout / stderr 流式回传 onLog。返回进程退出码与聚合输出。
        /// </summary>
        private async Task<RemoteResult> RunPowerShellAsync(
            string wrapper, string? code, Action<string>? onLog, CancellationToken ct,
            Dictionary<string, string?>? extraEnv)
        {
            var output = new StringBuilder();
            var error = new StringBuilder();
            var tempPs1 = Path.Combine(Path.GetTempPath(), $"ant_wr_{Guid.NewGuid():N}.ps1");
            try
            {
                // UTF-8 with BOM：Windows PowerShell 5.1 无 BOM 时按 ANSI 读取，含非 ASCII 会乱码。
                // 前置 Utf8Prologue 强制 PowerShell 以 UTF-8 输出，避免本地化异常消息回传宿主后乱码。
                File.WriteAllText(tempPs1, Utf8Prologue + wrapper, new UTF8Encoding(true));

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{tempPs1}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                psi.Environment["ANT_WR_HOST"] = _host;
                psi.Environment["ANT_WR_PORT"] = _port.ToString();
                psi.Environment["ANT_WR_USER"] = _userName;
                psi.Environment["ANT_WR_PWD"] = _password;
                psi.Environment["ANT_WR_AUTH"] = _auth;
                if (code != null) psi.Environment["ANT_WR_CODE"] = code;
                if (extraEnv != null)
                {
                    foreach (var kv in extraEnv) psi.Environment[kv.Key] = kv.Value;
                }

                using var proc = new Process { StartInfo = psi };
                proc.OutputDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;
                    output.AppendLine(e.Data);
                    onLog?.Invoke(e.Data);
                };
                proc.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;
                    error.AppendLine(e.Data);
                    onLog?.Invoke("[winrm][err] " + e.Data);
                };

                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                await proc.WaitForExitAsync(ct).ConfigureAwait(false);

                var exit = proc.ExitCode;
                var outStr = output.ToString();
                var errStr = error.ToString();
                // 非 0 退出时优先提取包装脚本 catch 写出的 [winrm][error] 真实原因，
                // 避免被通用文案 "WinRM 执行失败" 覆盖（RemoteResult.Reason 优先取 Message）。
                var msg = exit == 0
                    ? string.Empty
                    : ExtractWinRmError(outStr) ?? ExtractWinRmError(errStr) ?? "WinRM 执行失败";
                return new RemoteResult
                {
                    Success = exit == 0,
                    ExitCode = exit,
                    Output = outStr,
                    Error = errStr,
                    Message = msg
                };
            }
            catch (OperationCanceledException)
            {
                onLog?.Invoke("[winrm] cancelled");
                return RemoteResult.Fail("已取消", output.ToString());
            }
            catch (Exception ex)
            {
                onLog?.Invoke($"[winrm] exec fail: {ex.Message}");
                return RemoteResult.Fail(ex.Message, error.ToString());
            }
            finally
            {
                try { if (File.Exists(tempPs1)) File.Delete(tempPs1); } catch { }
            }
        }

        /// <summary>依据 WinRM 失败原因匹配已知错误，返回在 Web 宿主 / 目标机上可执行的修复提示（无匹配返回空）。</summary>
        private static string HintFor(string reason, string host)
        {
            if (string.IsNullOrWhiteSpace(reason)) return string.Empty;
            // 0x8009030e / TrustedHosts / 服务器身份验证失败：Negotiate 经 HTTP + IP 连工作组机需在“客户端(Web宿主)”加信任主机。
            if (reason.Contains("0x8009030e", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("TrustedHosts", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("logon session", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("无法验证服务器身份", StringComparison.Ordinal)
                || reason.Contains("服务器身份", StringComparison.Ordinal))
            {
                return $"在【AntDeployWeb 宿主机】以管理员运行：winrm set winrm/config/client @{{TrustedHosts=\"{host}\"}}" +
                       "（多台用逗号分隔，或 \"*\" 全信任但更安全差）；并确认宿主 WinRM 服务已启动：winrm quickconfig。" +
                       " 该错误多为经 HTTP(5985) + IP 连接工作组机器时客户端未信任目标所致。";
            }
            // 认证失败 / 拒绝访问：账号密码或权限问题。
            if (reason.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("0x80070005", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("拒绝访问", StringComparison.Ordinal)
                || reason.Contains("用户名或密码", StringComparison.Ordinal)
                || reason.Contains("logon failure", StringComparison.OrdinalIgnoreCase))
            {
                return "账号或密码无效 / 无远程管理权限：确认密码正确；工作组机器账号用 目标机名\\用户；域账号用 DOMAIN\\用户 或填写“AD 域”。" +
                       " 目标机需允许该账号远程管理（本地 Administrators，且非空密码）。";
            }
            // 连不上 / 端口 / 服务未启用。
            if (reason.Contains("cannot connect", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("无法连接", StringComparison.Ordinal)
                || reason.Contains("The client cannot connect", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("0x80338012", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("0x80338102", StringComparison.OrdinalIgnoreCase))
            {
                return $"在【目标机 {host}】以管理员运行：winrm quickconfig 启用 WinRM 并放行防火墙；确认监听器已开启（HTTP 5985 / HTTPS 5986）：" +
                       "winrm enumerate winrm/config/listener；若跨网段检查 5985/5986 是否被防火墙放行。";
            }
            // HTTPS / 证书身份验证问题。
            if (reason.Contains("certificate", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("证书", StringComparison.Ordinal)
                || reason.Contains("SSL", StringComparison.OrdinalIgnoreCase))
            {
                return "使用 HTTPS(5986) 时证书不被信任：改用 HTTP(5985) 并将目标加入 TrustedHosts，或在宿主安装目标服务器证书链。";
            }
            return string.Empty;
        }

        /// <summary>从聚合输出中提取包装脚本 catch 写出的 <c>[winrm][error] ...</c> 真实原因（首个非空者）。</summary>
        private static string? ExtractWinRmError(string? text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            const string tag = "[winrm][error]";
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (!line.StartsWith(tag, StringComparison.OrdinalIgnoreCase)) continue;
                var m = line.Substring(tag.Length).Trim();
                if (m.Length > 0) return m;
            }
            return null;
        }

        public void Dispose()
        {
            // WinRM 会话随每次 powershell.exe 进程结束而释放，无常驻句柄需清理。
            GC.SuppressFinalize(this);
        }
    }
}
