using System;
using System.Collections.Generic;
using AntDeploy.Core.Models;
using AntDeployWeb.Data;

namespace AntDeployWeb.Models
{
    /// <summary>
    /// Agent 客户端：一套 Agent 配置 + 多台目标服务器（在线安装 / 管理的最小持久化单元）。
    /// <para>序列化后存入 <see cref="AgentClientEntity.Data"/>；其中服务器密码 / SSH 私钥 / Agent Token
    /// 由 <see cref="Services.AgentService"/> 在落库前 AES 加密、读取后解密。</para>
    /// </summary>
    public class AgentClient
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;

        /// <summary>所属团队 Id（为空=未分配，仅超级管理员可见）。</summary>
        public string? TeamId { get; set; }

        /// <summary>操作系统类型：windows / linux（决定上传的发布包与安装脚本）。</summary>
        public string OsType { get; set; } = "windows";

        /// <summary>目标服务器列表（一套配置多台）。</summary>
        public List<AgentServerItem> Servers { get; set; } = new List<AgentServerItem>();

        /// <summary>Agent 运行配置（写入目标机 agentsettings.json；字段与 Core.AgentConfigDto 一致）。</summary>
        public AgentConfigDto Config { get; set; } = new AgentConfigDto();

        /// <summary>最近一次安装 / 连接验证的每台结果（✓/✗），用于列表展示。</summary>
        public List<HostResult> LastResults { get; set; } = new List<HostResult>();

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>是否 Windows（OsType 判定，大小写无关）。</summary>
        public bool IsWindows => string.Equals(OsType, "windows", StringComparison.OrdinalIgnoreCase);

        /// <summary>服务器台数。</summary>
        public int ServerCount => Servers?.Count ?? 0;

        /// <summary>默认安装目录：Windows C:\AntDeploy\Agent，Linux /opt/antdeploy/agent。</summary>
        public static string GetDefaultInstallDir(string? osType) =>
            string.Equals(osType, "windows", StringComparison.OrdinalIgnoreCase)
                ? @"C:\AntDeploy\Agent"
                : "/opt/antdeploy/agent";
    }

    /// <summary>
    /// 单台目标服务器的连接与凭据信息。Pwd / PrivateKey 落库前 AES 加密，界面回显脱敏。
    /// </summary>
    public class AgentServerItem
    {
        /// <summary>IP 或主机名。</summary>
        public string Host { get; set; } = string.Empty;

        /// <summary>远程通道：ssh / winrm（winrm 仅 Windows 服务器 + Windows 宿主可用）。</summary>
        public string Transport { get; set; } = "ssh";

        /// <summary>通道端口：SSH 默认 22；WinRM 默认 5985(HTTP)/5986(HTTPS)。0=按通道默认。</summary>
        public int Port { get; set; }

        /// <summary>登录账号（本地或域账号）。</summary>
        public string UserName { get; set; } = string.Empty;

        /// <summary>登录密码（AES 加密存储）。</summary>
        public string Pwd { get; set; } = string.Empty;

        /// <summary>SSH 私钥（可选，AES 加密存储；与密码二选一或并存）。</summary>
        public string? PrivateKey { get; set; }

        /// <summary>AD 域（仅 WinRM 通道使用，如 DOMAIN 或 domain.com；可空）。</summary>
        public string? Domain { get; set; }

        /// <summary>Agent 安装目录（留空用 OS 默认）。</summary>
        public string InstallDir { get; set; } = string.Empty;

        /// <summary>是否走 WinRM 通道。</summary>
        public bool IsWinRm => string.Equals(Transport, "winrm", StringComparison.OrdinalIgnoreCase);

        /// <summary>通道默认端口（未显式指定 Port 时使用）。</summary>
        public int EffectivePort => Port > 0 ? Port : (IsWinRm ? 5985 : 22);

        /// <summary>实际安装目录（留空回退 OS 默认；osType 由所属 AgentClient 提供）。</summary>
        public string EffectiveInstallDir(string? osType) =>
            string.IsNullOrWhiteSpace(InstallDir) ? AgentClient.GetDefaultInstallDir(osType) : InstallDir.Trim();

        /// <summary>列表展示用：主机:端口/通道。</summary>
        public string Display => string.IsNullOrWhiteSpace(Host)
            ? string.Empty
            : $"{Host}:{EffectivePort}/{(IsWinRm ? "winrm" : "ssh")}";
    }
}
