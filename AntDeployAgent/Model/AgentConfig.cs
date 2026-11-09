using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace AntDeployAgentWindows.Model
{
    /// <summary>
    /// Agent 运行配置（对应 agentsettings.json）。取代旧的 *.config appSettings，支持热加载：
    /// Token / 备份开关 / 保留个数 / 白名单 / DeployDir 变更后立即生效；
    /// Port 仅在服务启动时读取一次（变更需重启服务重新绑定 OWIN 监听）。
    /// <para>字段与旧 appSettings key 一一对应，缺失项先回退旧 *.config，再回退默认值。</para>
    /// </summary>
    public class AgentConfig
    {
        /// <summary>Agent 授权 Token（所有 HTTP 接口鉴权用）。旧 key: Token</summary>
        public string Token { get; set; } = "AntDeploy@publish#";

        /// <summary>Agent 通讯端口（变更需重启服务生效）。旧 key: Port</summary>
        public int Port { get; set; } = 8088;

        /// <summary>发布根目录，空=程序所在目录。旧 key: DeployDir</summary>
        public string DeployDir { get; set; } = string.Empty;

        /// <summary>是否启用备份。旧 key: NeedBackUp（空/true=启用，false=不启用）</summary>
        public bool NeedBackUp { get; set; } = true;

        /// <summary>备份保留个数，&lt;=0 表示与发布历史保留个数一致。旧 key: BackUpLimit</summary>
        public int BackUpLimit { get; set; } = 0;

        /// <summary>Mac 白名单，多个用半角逗号分隔；空=不限制。旧 key: MacWhiteList</summary>
        public string MacWhiteList { get; set; } = string.Empty;

        /// <summary>发布历史最多保留个数（最小 1）。旧 key: OldPulishLimit（历史拼写）</summary>
        public int OldPublishLimit { get; set; } = 10;

        /// <summary>超过该天数的旧发布文件夹会被清理；0=不按日期清理。旧 key: ClearOldPublishFolderOverDays</summary>
        public int ClearOldPublishFolderOverDays { get; set; } = 10;

        /// <summary>生效的备份保留个数：BackUpLimit&lt;=0 时取发布历史保留个数。</summary>
        [JsonIgnore]
        public int EffectiveBackUpLimit => BackUpLimit > 0 ? BackUpLimit : EffectiveOldPublishLimit;

        /// <summary>生效的发布历史保留个数（最小 1）。</summary>
        [JsonIgnore]
        public int EffectiveOldPublishLimit => OldPublishLimit < 1 ? 1 : OldPublishLimit;

        /// <summary>Mac 白名单条目（去空、去重、Trim）。</summary>
        [JsonIgnore]
        public List<string> MacWhiteListItems =>
            string.IsNullOrEmpty(MacWhiteList)
                ? new List<string>()
                : MacWhiteList.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().ToList();

        /// <summary>深拷贝一份配置快照（用于原子替换/比较）。</summary>
        public AgentConfig Clone()
        {
            return new AgentConfig
            {
                Token = Token,
                Port = Port,
                DeployDir = DeployDir,
                NeedBackUp = NeedBackUp,
                BackUpLimit = BackUpLimit,
                MacWhiteList = MacWhiteList,
                OldPublishLimit = OldPublishLimit,
                ClearOldPublishFolderOverDays = ClearOldPublishFolderOverDays
            };
        }
    }
}
