using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AntDeployWeb.Data
{
    /// <summary>
    /// 存储提供程序类型。
    /// </summary>
    public enum StorageProvider
    {
        /// <summary>本地 JSON 文件（默认，向后兼容）</summary>
        Json = 0,
        /// <summary>SQLite 数据库</summary>
        Sqlite = 1,
        /// <summary>SQL Server 数据库</summary>
        SqlServer = 2,
        /// <summary>MySQL 数据库</summary>
        MySql = 3
    }

    /// <summary>
    /// appsettings.json 中的存储配置段。
    /// </summary>
    public class StorageOptions
    {
        public const string SectionName = "Storage";

        /// <summary>存储提供程序：Json / Sqlite / SqlServer / MySql</summary>
        public string Provider { get; set; } = "Sqlite";

        /// <summary>数据库连接字符串（Json 模式下忽略）</summary>
        public string ConnectionString { get; set; } = string.Empty;

        /// <summary>发布历史最大保留条数，超出自动删除最旧记录。默认 100。</summary>
        public int HistoryMaxRecords { get; set; } = 100;

        public StorageProvider GetProvider()
        {
            if (Enum.TryParse<StorageProvider>(Provider, true, out var p)) return p;
            return StorageProvider.Sqlite;
        }
    }

    /// <summary>
    /// 全局设置（单行表）。对应原 JsonConfigStore 中的 WebSettings。
    /// </summary>
    public class WebSettingsEntity
    {
        [Key]
        public int Id { get; set; } = 1; // 始终只有一行

        /// <summary>序列化的 WebSettings JSON</summary>
        [Column(TypeName = "TEXT")]
        public string Data { get; set; } = string.Empty;

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// 部署项目配置。对应原 JsonConfigStore 中的 DeployProfile。
    /// </summary>
    public class DeployProfileEntity
    {
        [Key]
        [MaxLength(64)]
        public string Id { get; set; } = string.Empty;

        [MaxLength(256)]
        public string Name { get; set; } = string.Empty;

        /// <summary>序列化的 DeployProfile JSON（凭据已 AES 加密）</summary>
        [Column(TypeName = "TEXT")]
        public string Data { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Agent 客户端（一套配置 + 多台服务器）。仿 <see cref="DeployProfileEntity"/>：Data 为序列化 JSON。
    /// <para>Data 内的服务器密码 / SSH 私钥 / Agent Token 落库时 AES 加密（与 DbConfigStore 一致）。</para>
    /// <para>TeamId 为直接列（无需映射表）：为空表示未分配，仅超级管理员可见；团队管理员仅见本团队。</para>
    /// </summary>
    public class AgentClientEntity
    {
        [Key]
        [MaxLength(64)]
        public string Id { get; set; } = string.Empty;

        [MaxLength(256)]
        public string Name { get; set; } = string.Empty;

        /// <summary>所属团队 Id（为空表示未分配，仅超级管理员可见）</summary>
        [MaxLength(64)]
        public string? TeamId { get; set; }

        /// <summary>操作系统类型：windows / linux</summary>
        [MaxLength(16)]
        public string OsType { get; set; } = "windows";

        /// <summary>序列化的 AgentClient JSON（服务器凭据已 AES 加密）</summary>
        [Column(TypeName = "TEXT")]
        public string Data { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// 部署会话/历史记录。既用于运行中的实时状态共享，也用于已完成的历史查询。
    /// </summary>
    public class DeploySessionEntity
    {
        [Key]
        [MaxLength(64)]
        public string Id { get; set; } = string.Empty;

        /// <summary>关联的项目 Id</summary>
        [MaxLength(64)]
        public string ProfileId { get; set; } = string.Empty;

        [MaxLength(256)]
        public string ProfileName { get; set; } = string.Empty;

        [MaxLength(128)]
        public string EnvName { get; set; } = string.Empty;

        /// <summary>部署类型：iis / windowservice / linux / docker</summary>
        [MaxLength(32)]
        public string DeployType { get; set; } = string.Empty;

        [MaxLength(256)]
        public string Branch { get; set; } = string.Empty;

        [MaxLength(512)]
        public string Remark { get; set; } = string.Empty;

        /// <summary>状态：Running / Success / Failed / Cancelled / Partial</summary>
        [MaxLength(32)]
        public string Status { get; set; } = "Running";

        /// <summary>当前阶段：pull / build / package / upload / deploy / fail / done</summary>
        [MaxLength(32)]
        public string Phase { get; set; } = string.Empty;

        /// <summary>进度百分比 0-100；-1 表示无精确百分比</summary>
        public int Percent { get; set; } = -1;

        /// <summary>进度文案</summary>
        [MaxLength(512)]
        public string ProgressText { get; set; } = string.Empty;

        public int ServerIndex { get; set; }
        public int ServerCount { get; set; }

        /// <summary>成功台数</summary>
        public int SuccessCount { get; set; }
        /// <summary>失败台数</summary>
        public int FailCount { get; set; }

        /// <summary>最后一条错误信息</summary>
        [Column(TypeName = "TEXT")]
        public string LastError { get; set; } = string.Empty;

        /// <summary>结果摘要消息</summary>
        [MaxLength(1024)]
        public string Message { get; set; } = string.Empty;

        /// <summary>每台服务器的结果 JSON（List&lt;HostOutcome&gt;）</summary>
        [Column(TypeName = "TEXT")]
        public string HostResults { get; set; } = string.Empty;

        /// <summary>实时日志 JSON（List&lt;LogEntry&gt;），运行中持续追加</summary>
        [Column(TypeName = "TEXT")]
        public string Logs { get; set; } = string.Empty;

        /// <summary>触发发布的用户标识（可选，如 IP / 用户名）</summary>
        [MaxLength(256)]
        public string TriggeredBy { get; set; } = string.Empty;

        public DateTime StartTime { get; set; } = DateTime.UtcNow;
        public DateTime? EndTime { get; set; }

        /// <summary>是否为运行中的会话（用于快速筛选）</summary>
        public bool IsRunning { get; set; } = true;

        /// <summary>操作类型：quick_deploy / deploy / pull / build / pull_build / rollback / build_image</summary>
        [MaxLength(32)]
        public string Operation { get; set; } = "quick_deploy";

        /// <summary>回滚目标版本（rollback 操作重试上下文）</summary>
        [MaxLength(128)]
        public string RollbackVersion { get; set; } = string.Empty;

        /// <summary>Docker 是否走 SSH 直连通道（重试上下文）</summary>
        public bool DockerUseSsh { get; set; }

        /// <summary>选择文件部署的文件列表 JSON（重试上下文）</summary>
        [Column(TypeName = "TEXT")]
        public string SelectedFilesJson { get; set; } = string.Empty;

        /// <summary>拉取代码阶段耗时（毫秒）</summary>
        public long PullMs { get; set; }
        /// <summary>编译阶段耗时（毫秒）</summary>
        public long BuildMs { get; set; }
        /// <summary>打包阶段耗时（毫秒）</summary>
        public long PackageMs { get; set; }
        /// <summary>上传阶段耗时（毫秒，并行下为 wall-clock：全部台上传达 100% 的时刻）</summary>
        public long UploadMs { get; set; }
        /// <summary>部署阶段耗时（毫秒，并行下为 wall-clock：全部站点更新完成）</summary>
        public long DeployMs { get; set; }
    }

    /// <summary>
    /// 项目级并发发布与零掉包设置（独立于配置存储位置，Json/DB 模式均可用，避免改动 Core 的 DeployProfile）。
    /// <para>
    /// UploadXxx / DeployXxx 分别控制"上传阶段"与"部署阶段"的并发；未启用两阶段时为原子发布，
    /// 实际并发上限取两者较小值。启用 EnableTwoPhase 后（Agent >= 7.2，仅 IIS）：上传阶段并行、部署阶段可滚动逐台。
    /// BeforeCallbackUrl/AfterCallbackUrl/DrainWaitSeconds 用于负载均衡摘除/挂回，实现发布期间零掉包。
    /// </para>
    /// </summary>
    public class DeployOptionsEntity
    {
        [Key]
        [MaxLength(64)]
        public string ProfileId { get; set; } = string.Empty;

        /// <summary>上传并发模式：count（数量）/ percent（百分比）</summary>
        [MaxLength(32)]
        public string UploadMode { get; set; } = "count";

        /// <summary>上传并发值：count 模式为台数，percent 模式为百分比（1-100）</summary>
        public int UploadValue { get; set; } = 1;

        /// <summary>部署并发模式：count / percent</summary>
        [MaxLength(32)]
        public string DeployMode { get; set; } = "count";

        /// <summary>部署并发值：count 模式为台数，percent 模式为百分比（1-100）</summary>
        public int DeployValue { get; set; } = 1;

        /// <summary>更新前回调地址（下负载）：完整 http(s):// 或以 / 开头的相对路径。空=不回调。</summary>
        [MaxLength(512)]
        public string BeforeCallbackUrl { get; set; } = string.Empty;

        /// <summary>更新成功后回调地址（上负载）。空=不回调。</summary>
        [MaxLength(512)]
        public string AfterCallbackUrl { get; set; } = string.Empty;

        /// <summary>更新前回调后等待秒数，让在途请求处理完成再停站更新。0=不等待。</summary>
        public int DrainWaitSeconds { get; set; } = 0;

        /// <summary>回调 HTTP 超时秒数（默认 10）。</summary>
        public int CallbackTimeoutSeconds { get; set; } = 10;

        /// <summary>是否启用两阶段发布：先全部上传到所有服务器，再滚动部署（仅 IIS 生效）。</summary>
        public bool EnableTwoPhase { get; set; } = false;

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>用户角色。</summary>
    public enum UserRole
    {
        /// <summary>超级管理员：管控所有团队与项目、全局设置、用户/团队管理</summary>
        SuperAdmin = 0,
        /// <summary>团队管理员：管控本团队项目的所有设置，管理本团队普通用户</summary>
        TeamAdmin = 1,
        /// <summary>普通用户：仅有本团队项目的发版权限，无配置/删除/新增权限</summary>
        User = 2
    }

    /// <summary>系统用户。</summary>
    public class UserEntity
    {
        [Key]
        [MaxLength(64)]
        public string Id { get; set; } = string.Empty;

        [MaxLength(128)]
        public string UserName { get; set; } = string.Empty;

        /// <summary>显示名（可选）</summary>
        [MaxLength(128)]
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>PBKDF2 密码哈希（Base64）</summary>
        [MaxLength(512)]
        public string PasswordHash { get; set; } = string.Empty;

        /// <summary>PBKDF2 盐（Base64）</summary>
        [MaxLength(128)]
        public string PasswordSalt { get; set; } = string.Empty;

        /// <summary>角色：SuperAdmin / TeamAdmin / User</summary>
        [MaxLength(32)]
        public string Role { get; set; } = "User";

        /// <summary>所属团队 Id（超级管理员可为空，表示不限团队）</summary>
        [MaxLength(64)]
        public string? TeamId { get; set; }

        /// <summary>是否禁用</summary>
        public bool IsDisabled { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public UserRole GetRole() =>
            Enum.TryParse<UserRole>(Role, true, out var r) ? r : UserRole.User;
    }

    /// <summary>团队。</summary>
    public class TeamEntity
    {
        [Key]
        [MaxLength(64)]
        public string Id { get; set; } = string.Empty;

        [MaxLength(128)]
        public string Name { get; set; } = string.Empty;

        [MaxLength(512)]
        public string Description { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>项目↔团队映射（独立于配置存储位置，Json/DB 模式均可用，避免改动 Core 的 DeployProfile）。</summary>
    public class ProjectTeamEntity
    {
        [Key]
        [MaxLength(64)]
        public string ProfileId { get; set; } = string.Empty;

        /// <summary>所属团队 Id（为空表示未分配，仅超级管理员可见）</summary>
        [MaxLength(64)]
        public string? TeamId { get; set; }

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// 日志条目（序列化到 DeploySessionEntity.Logs）。
    /// </summary>
    public class LogEntry
    {
        public string Time { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        /// <summary>日志级别 CSS 类：log-error / log-warn / 空</summary>
        public string Css { get; set; } = string.Empty;
    }

    /// <summary>
    /// 单台服务器发布结果（序列化到 DeploySessionEntity.HostResults）。
    /// </summary>
    public class HostResult
    {
        public string Host { get; set; } = string.Empty;
        public string Display { get; set; } = string.Empty;
        public bool Success { get; set; }
        public string Msg { get; set; } = string.Empty;
    }
}
