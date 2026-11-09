using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace AntDeploy.Core.Models
{
    /// <summary>
    /// 与 AntDeployAgent 端完全相同的日志模型（/logger 轮询返回）。
    /// </summary>
    public class LoggerModel
    {
        public string Msg { get; set; }
        public DateTime Date { get; set; }
        public bool IsActive { get; set; }
    }

    /// <summary>
    /// Agent 通用返回结果（/publish、/rollback 等）。
    /// </summary>
    public class DeployResult
    {
        public bool Success { get; set; }
        public string Msg { get; set; }
    }

    public class DeployResult<T> : DeployResult
    {
        public T Data { get; set; }
    }

    /// <summary>
    /// /version 预检返回（以 IIS 为例）。
    /// </summary>
    public class GetVersionResult
    {
        public string LastDeployTime { get; set; }
        public string LastDeployUser { get; set; }
        public List<string> SitePaths { get; set; } = new List<string>();
        public List<string> History { get; set; } = new List<string>();
        public bool Success { get; set; }
        public string Msg { get; set; }
    }

    /// <summary>
    /// /version checkiis 的站点校验返回。
    /// </summary>
    public class IIsSiteCheck
    {
        public bool IsSuccess { get; set; }
        public string Msg { get; set; }
        public string WebSiteName { get; set; }
        public string PoolName { get; set; }
        public string PhysicalPath { get; set; }
    }

    /// <summary>
    /// Agent 能力探测：基于 /version 返回的版本号判定是否支持新特性。
    /// 采用版本号阈值而非新增协议字段，向后兼容（旧 Agent 版本号低即视为不支持）。
    /// </summary>
    public static class AgentCapability
    {
        /// <summary>支持“两阶段发布 + 更新前后回调”的最低 Agent 版本。</summary>
        public const double MinTwoPhaseVersion = 7.2;

        /// <summary>支持“/config 在线读取与热同步配置”的最低 Agent 版本。</summary>
        public const double MinConfigSyncVersion = 7.3;

        /// <summary>解析版本号并判断是否支持两阶段/回调。versionText 为空或不可解析时返回 false。</summary>
        public static bool SupportsTwoPhase(string versionText)
        {
            return AtLeast(versionText, MinTwoPhaseVersion);
        }

        /// <summary>解析版本号并判断是否支持 /config 在线热同步。versionText 为空或不可解析时返回 false。</summary>
        public static bool SupportsConfigSync(string versionText)
        {
            return AtLeast(versionText, MinConfigSyncVersion);
        }

        /// <summary>版本是否 &gt;= 阈值：取开头可解析的 double（形如 "7.3" 或带后缀）。</summary>
        private static bool AtLeast(string versionText, double min)
        {
            if (string.IsNullOrWhiteSpace(versionText)) return false;
            var s = versionText.Trim();
            int end = 0;
            while (end < s.Length && (char.IsDigit(s[end]) || s[end] == '.')) end++;
            if (end == 0) return false;
            if (double.TryParse(s.Substring(0, end), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v))
            {
                return v >= min;
            }
            return false;
        }
    }

    /// <summary>
    /// Agent 运行配置 DTO（与 Agent 端 AntDeployAgentWindows.Model.AgentConfig 字段一一对应，用于 /config 在线同步）。
    /// </summary>
    public class AgentConfigDto
    {
        /// <summary>授权 Token（获取时脱敏为 ******）。</summary>
        public string Token { get; set; } = string.Empty;
        /// <summary>通讯端口（变更需重启 Agent 服务）。</summary>
        public int Port { get; set; } = 8088;
        /// <summary>发布根目录，空=程序所在目录。</summary>
        public string DeployDir { get; set; } = string.Empty;
        /// <summary>是否启用备份。</summary>
        public bool NeedBackUp { get; set; } = true;
        /// <summary>备份保留个数，&lt;=0 表示与发布历史保留个数一致。</summary>
        public int BackUpLimit { get; set; } = 0;
        /// <summary>Mac 白名单，多个用半角逗号分隔；空=不限制。</summary>
        public string MacWhiteList { get; set; } = string.Empty;
        /// <summary>发布历史最多保留个数。</summary>
        public int OldPublishLimit { get; set; } = 10;
        /// <summary>超过该天数的旧发布文件夹会被清理；0=不按日期清理。</summary>
        public int ClearOldPublishFolderOverDays { get; set; } = 10;
    }

    /// <summary>
    /// /config 端点返回。RestartNeeded=true 表示 Port 变更需重启 Agent 服务重新绑定监听；
    /// Config 为脱敏后的当前配置（Token 以 ****** 呈现）。
    /// </summary>
    public class AgentConfigResult
    {
        public bool Success { get; set; }
        public string Msg { get; set; } = string.Empty;
        public bool RestartNeeded { get; set; }
        public AgentConfigDto Config { get; set; }
    }

    /// <summary>
    /// 发布类型枚举，与 Agent FormHandler 的 publishType 字段保持一致。
    /// </summary>
    public enum PublishType
    {
        iis,
        windowservice,
        linux,
        docker,
        iis_rollback,
        windowservice_rollback,
        linux_rollback,
        docker_rollback
    }
}
