using AntDeployWeb.Models;

namespace AntDeployWeb.Services.Remote
{
    /// <summary>
    /// 远程传输通道工厂：按每台服务器选择的通道（<see cref="AgentServerItem.IsWinRm"/>）+ 凭据构造对应 <see cref="IRemoteTransport"/>。
    /// 调用方负责 Dispose 返回的实例。
    /// </summary>
    public static class RemoteTransportFactory
    {
        /// <summary>依据服务器条目创建通道实例（winrm → WinRmTransport，其余 → SshTransport）。</summary>
        public static IRemoteTransport Create(AgentServerItem server)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            return server.IsWinRm
                ? new WinRmTransport(server)
                : new SshTransport(server);
        }

        /// <summary>
        /// 预检某服务器所选通道在当前宿主是否可用：WinRM 要求 Web 宿主为 Windows；SSH 恒可用。
        /// 返回 (是否可用, 不可用原因)，供安装前快速失败与 UI 提示。
        /// </summary>
        public static (bool Ok, string Reason) CheckChannelAvailable(AgentServerItem server)
        {
            if (server == null) return (false, "服务器条目为空");
            if (string.IsNullOrWhiteSpace(server.Host)) return (false, "主机 / IP 未填写");
            if (server.IsWinRm && !WinRmTransport.HostSupportsWinRm)
            {
                return (false, "WinRM 通道要求 AntDeployWeb 宿主为 Windows；当前宿主非 Windows，请对该 Windows 服务器改用 SSH 通道");
            }
            if (!server.IsWinRm
                && string.IsNullOrWhiteSpace(server.Pwd)
                && string.IsNullOrWhiteSpace(server.PrivateKey))
            {
                return (false, "SSH 通道需要密码或私钥之一");
            }
            return (true, string.Empty);
        }
    }
}
