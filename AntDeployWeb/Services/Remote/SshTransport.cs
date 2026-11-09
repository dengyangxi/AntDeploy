using System.Text;
using AntDeployWeb.Models;
using Renci.SshNet;
using Renci.SshNet.Sftp;
// Web SDK 隐式 using 引入了 Microsoft.AspNetCore.Http.ConnectionInfo，与 Renci.SshNet.ConnectionInfo 冲突（CS0104），此处显式取别名。
using SshConnectionInfo = Renci.SshNet.ConnectionInfo;

namespace AntDeployWeb.Services.Remote
{
    /// <summary>
    /// SSH 远程通道（Renci.SshNet，复用 AntDeploy.Core 传递依赖）：
    /// 支持密码或私钥认证；<see cref="SshClient"/> 流式执行命令、<see cref="SftpClient"/> 上传文件 / 目录。
    /// <para>跨平台，是 Web 宿主跑在 Linux 时对 Windows 服务器（OpenSSH）的推荐通道；
    /// Windows Server 2019+ 自带 OpenSSH 亦可用。Renci.SshNet 为同步 API，故所有阻塞调用包在 <see cref="Task.Run(Action)"/> 内。</para>
    /// </summary>
    public class SshTransport : IRemoteTransport
    {
        private const int ConnectTimeoutMs = 20000;

        private readonly string _host;
        private readonly int _port;
        private readonly string _userName;
        private readonly string _password;
        private readonly string? _privateKey;

        private SshConnectionInfo? _connInfo;
        private SshClient? _ssh;
        private SftpClient? _sftp;
        private bool _disposed;

        public string ChannelName => "ssh";
        public string Display => $"{_host}:{_port}/ssh";

        public SshTransport(AgentServerItem server)
        {
            _host = (server.Host ?? string.Empty).Trim();
            _port = server.EffectivePort;
            _userName = (server.UserName ?? string.Empty).Trim();
            _password = server.Pwd ?? string.Empty;
            _privateKey = server.PrivateKey;
        }

        /// <summary>依据凭据构造 ConnectionInfo：优先私钥，其次密码；两者皆无则抛异常。</summary>
        private SshConnectionInfo BuildConnectionInfo()
        {
            if (_connInfo != null) return _connInfo;

            var methods = new List<AuthenticationMethod>();
            if (!string.IsNullOrWhiteSpace(_privateKey))
            {
                try
                {
                    using var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(_privateKey));
                    var keyFile = string.IsNullOrEmpty(_password)
                        ? new PrivateKeyFile(keyStream)
                        : new PrivateKeyFile(keyStream, _password);
                    methods.Add(new PrivateKeyAuthenticationMethod(_userName, keyFile));
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"SSH 私钥解析失败：{ex.Message}", ex);
                }
            }
            if (!string.IsNullOrEmpty(_password))
            {
                methods.Add(new PasswordAuthenticationMethod(_userName, _password));
            }
            if (methods.Count == 0)
            {
                throw new InvalidOperationException("SSH 通道需要密码或私钥之一");
            }

            _connInfo = new SshConnectionInfo(_host, _port, _userName, methods.ToArray())
            {
                Timeout = TimeSpan.FromMilliseconds(ConnectTimeoutMs)
            };
            return _connInfo;
        }

        private SshClient EnsureSsh()
        {
            if (_ssh == null) _ssh = new SshClient(BuildConnectionInfo());
            if (!_ssh.IsConnected) _ssh.Connect();
            return _ssh;
        }

        private SftpClient EnsureSftp()
        {
            if (_sftp == null)
            {
                _sftp = new SftpClient(BuildConnectionInfo());
                _sftp.BufferSize = 6 * 1024; // bypass Payload error on large files（与 SshDockerClient 一致）
            }
            if (!_sftp.IsConnected) _sftp.Connect();
            return _sftp;
        }

        public Task<bool> TestConnectAsync(Action<string>? onLog, CancellationToken ct = default)
            => Task.Run(() =>
            {
                try
                {
                    onLog?.Invoke($"[ssh] connecting {_host}:{_port} as {_userName} ...");
                    ct.ThrowIfCancellationRequested();
                    var ssh = EnsureSsh();
                    var cmd = ssh.RunCommand("echo antdeploy-ssh-ok");
                    var ok = cmd.ExitStatus == 0;
                    onLog?.Invoke(ok
                        ? $"[ssh] connect success: {_userName}@{_host}:{_port}"
                        : $"[ssh] connect check failed: exit={cmd.ExitStatus} {cmd.Error}");
                    return ok;
                }
                catch (OperationCanceledException)
                {
                    onLog?.Invoke("[ssh] cancelled");
                    return false;
                }
                catch (Exception ex)
                {
                    onLog?.Invoke($"[ssh] connect fail: {ex.Message}");
                    return false;
                }
            }, ct);

        public Task<RemoteResult> RunAsync(string script, Action<string>? onLog, CancellationToken ct = default)
            => Task.Run(() =>
            {
                var output = new StringBuilder();
                try
                {
                    ct.ThrowIfCancellationRequested();
                    var ssh = EnsureSsh();
                    var cmd = ssh.CreateCommand(script);
                    var ar = cmd.BeginExecute();

                    // 流式读取 stdout（命令结束时 OutputStream 关闭，ReadLine 返回 null）
                    using (var reader = new StreamReader(cmd.OutputStream, Encoding.UTF8, true, 1024, true))
                    {
                        string? line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (line.Length > 0)
                            {
                                output.AppendLine(line);
                                onLog?.Invoke(line);
                            }
                            if (ct.IsCancellationRequested) break;
                        }
                    }

                    cmd.EndExecute(ar);

                    var err = cmd.Error ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(err))
                    {
                        onLog?.Invoke($"[ssh][stderr] {err.Trim()}");
                    }

                    var exit = cmd.ExitStatus ?? -1;
                    return new RemoteResult
                    {
                        Success = exit == 0 && !ct.IsCancellationRequested,
                        ExitCode = exit,
                        Output = output.ToString(),
                        Error = err,
                        Message = exit == 0 ? string.Empty : $"命令退出码 {exit}"
                    };
                }
                catch (OperationCanceledException)
                {
                    return RemoteResult.Fail("已取消", output.ToString());
                }
                catch (Exception ex)
                {
                    onLog?.Invoke($"[ssh] run fail: {ex.Message}");
                    return RemoteResult.Fail(ex.Message, output.ToString());
                }
            }, ct);

        public Task<bool> UploadAsync(string localPath, string remotePath, Action<string>? onLog, CancellationToken ct = default)
            => Task.Run(() =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    var sftp = EnsureSftp();

                    if (Directory.Exists(localPath))
                    {
                        onLog?.Invoke($"[ssh] upload dir {localPath} -> {remotePath}");
                        EnsureRemoteDir(sftp, remotePath);
                        UploadDirectory(sftp, localPath, remotePath, onLog, ct);
                    }
                    else if (File.Exists(localPath))
                    {
                        // remotePath 语义为“目标目录”：文件落入该目录并保留原文件名（与 WinRmTransport 一致）
                        EnsureRemoteDir(sftp, remotePath);
                        var remoteFile = NormalizeRemoteDir(remotePath) + "/" + Path.GetFileName(localPath);
                        onLog?.Invoke($"[ssh] upload file {Path.GetFileName(localPath)} -> {remoteFile}");
                        using var fs = File.OpenRead(localPath);
                        sftp.UploadFile(fs, remoteFile, true);
                    }
                    else
                    {
                        onLog?.Invoke($"[ssh] upload fail: local path not found {localPath}");
                        return false;
                    }

                    onLog?.Invoke("[ssh] upload success");
                    return true;
                }
                catch (OperationCanceledException)
                {
                    onLog?.Invoke("[ssh] upload cancelled");
                    return false;
                }
                catch (Exception ex)
                {
                    onLog?.Invoke($"[ssh] upload fail: {ex.Message}");
                    return false;
                }
            }, ct);

        /// <summary>递归上传本地目录内容到远程目录（远程目录已存在）。</summary>
        private void UploadDirectory(SftpClient sftp, string localDir, string remoteDir, Action<string>? onLog, CancellationToken ct)
        {
            remoteDir = NormalizeRemoteDir(remoteDir);
            foreach (var file in Directory.GetFiles(localDir))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(file);
                var remotePath = remoteDir + "/" + name;
                using var fs = File.OpenRead(file);
                sftp.UploadFile(fs, remotePath, true);
                onLog?.Invoke($"[ssh]   + {name}");
            }
            foreach (var sub in Directory.GetDirectories(localDir))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(sub);
                var remoteSub = remoteDir + "/" + name;
                EnsureRemoteDir(sftp, remoteSub);
                UploadDirectory(sftp, sub, remoteSub, onLog, ct);
            }
        }

        /// <summary>逐级创建远程目录（沿用 SshDockerClient.CreateServerDirectoryIfItDoesntExist 思路）。
        /// 兼容 Unix 绝对路径（/opt/...）与 Windows 盘符路径（C:/... 经 SFTP 用正斜杠，不尝试创建盘符本身）。</summary>
        private void EnsureRemoteDir(SftpClient sftp, string remoteDir)
        {
            remoteDir = NormalizeRemoteDir(remoteDir);
            if (remoteDir.Length == 0 || sftp.Exists(remoteDir)) return;

            var parts = remoteDir.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;

            var unixAbs = remoteDir.StartsWith("/");
            string current;
            int start;
            // Windows 盘符（形如 "C:"）：以盘符为根，从下一级开始创建
            if (parts[0].Length >= 2 && parts[0][1] == ':')
            {
                current = parts[0];
                start = 1;
            }
            else
            {
                current = string.Empty;
                start = 0;
            }

            for (var i = start; i < parts.Length; i++)
            {
                current = current.Length == 0 ? (unixAbs ? "/" + parts[i] : parts[i]) : current + "/" + parts[i];
                if (!sftp.Exists(current))
                {
                    sftp.CreateDirectory(current);
                }
            }
        }

        private static string NormalizeRemoteDir(string dir)
        {
            dir = (dir ?? string.Empty).Replace('\\', '/').TrimEnd('/');
            return dir;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { if (_ssh != null) { if (_ssh.IsConnected) _ssh.Disconnect(); _ssh.Dispose(); } } catch { }
            try { if (_sftp != null) { if (_sftp.IsConnected) _sftp.Disconnect(); _sftp.Dispose(); } } catch { }
            _ssh = null;
            _sftp = null;
            GC.SuppressFinalize(this);
        }
    }
}
