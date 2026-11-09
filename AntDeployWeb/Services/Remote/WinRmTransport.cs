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
            onLog?.Invoke(ok ? $"[winrm] connect success: {_userName}@{_host}:{_port}" : $"[winrm] connect fail: {res.Reason}");
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
                File.WriteAllText(tempPs1, wrapper, new UTF8Encoding(true));

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
                return new RemoteResult
                {
                    Success = exit == 0,
                    ExitCode = exit,
                    Output = output.ToString(),
                    Error = error.ToString(),
                    Message = exit == 0 ? string.Empty : "WinRM 执行失败"
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

        public void Dispose()
        {
            // WinRM 会话随每次 powershell.exe 进程结束而释放，无常驻句柄需清理。
            GC.SuppressFinalize(this);
        }
    }
}
