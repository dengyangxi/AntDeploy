using System;
using System.Configuration;
using System.IO;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AntDeployAgentWindows.Model
{
    /// <summary>
    /// Agent 配置加载器（进程内单例，线程安全）。
    /// <para>优先级：agentsettings.json（最高） → 旧 *.config 的 appSettings（向后兼容） → 内置默认值。</para>
    /// <para>通过 <see cref="FileSystemWatcher"/> 监听 agentsettings.json，变更后防抖重载并原子替换内存快照：
    /// Token / 保留个数 / 白名单 / 备份开关 / DeployDir 立即生效；Port 仅在服务启动时读取一次（变更需重启）。</para>
    /// <para>对外统一通过 <see cref="Current"/> 读取；<see cref="Save"/> 供 /config 端点在线同步写盘。</para>
    /// </summary>
    public static class AgentConfigLoader
    {
        /// <summary>配置文件名（与 exe 同目录）。</summary>
        public const string JsonFileName = "agentsettings.json";

        private static readonly object _sync = new object();
        private static AgentConfig _current = new AgentConfig();
        private static string _dir;
        private static FileSystemWatcher _watcher;
        private static Timer _debounce;
        private static bool _initialized;

        /// <summary>配置变更事件（旧值, 新值）。DeployDir 变更时可用于重建发布根目录。</summary>
        public static event Action<AgentConfig, AgentConfig> Changed;

        /// <summary>当前生效配置快照（永不为 null；首次访问会自动定位并加载）。</summary>
        public static AgentConfig Current
        {
            get
            {
                if (!_initialized) Initialize(null);
                return _current;
            }
        }

        /// <summary>agentsettings.json 的完整路径。</summary>
        public static string JsonPath
        {
            get { return Path.Combine(string.IsNullOrEmpty(_dir) ? BaseDir() : _dir, JsonFileName); }
        }

        /// <summary>
        /// 显式初始化：宿主启动早期用 exe 所在目录调用；dir 为空则回退 <see cref="AppContext.BaseDirectory"/>。
        /// 幂等——重复调用会重载配置，但只安装一次文件监听。
        /// </summary>
        public static void Initialize(string dir)
        {
            lock (_sync)
            {
                _dir = string.IsNullOrEmpty(dir) ? BaseDir() : dir;
                _current = Load();
                if (!_initialized)
                {
                    _initialized = true;
                    EnsureJsonFile(); // 首次运行依据旧 *.config/默认值生成 agentsettings.json，便于可视化编辑与热加载
                    StartWatcher();
                }
            }
        }

        /// <summary>重新从磁盘加载配置并原子替换快照，随后触发 <see cref="Changed"/>。</summary>
        public static void Reload()
        {
            AgentConfig old;
            AgentConfig neu;
            lock (_sync)
            {
                if (string.IsNullOrEmpty(_dir)) _dir = BaseDir();
                old = _current;
                neu = Load();
                _current = neu;
            }
            try { var h = Changed; if (h != null) h(old, neu); }
            catch { /* 忽略订阅方异常，避免影响加载 */ }
        }

        /// <summary>
        /// 保存配置到 agentsettings.json（原子写：临时文件 + 覆盖复制），随后重载生效。
        /// 供 /config 端点在线同步使用。返回是否写盘成功。
        /// </summary>
        public static bool Save(AgentConfig cfg)
        {
            if (cfg == null) return false;
            try
            {
                lock (_sync)
                {
                    if (string.IsNullOrEmpty(_dir)) _dir = BaseDir();
                }
                var json = JsonConvert.SerializeObject(cfg, Formatting.Indented);
                var tmp = Path.Combine(_dir, JsonFileName + ".tmp");
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                File.Copy(tmp, JsonPath, true);
                try { File.Delete(tmp); } catch { /* 临时文件删除失败可忽略 */ }
                Reload();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string BaseDir()
        {
            try
            {
                var d = AppContext.BaseDirectory;
                if (!string.IsNullOrEmpty(d)) return d;
            }
            catch { /* 某些宿主环境下可能不可用，回退当前目录 */ }
            try { return Directory.GetCurrentDirectory(); }
            catch { return "."; }
        }

        private static AgentConfig Load()
        {
            var cfg = new AgentConfig();
            ApplyLegacyAppSettings(cfg); // 向后兼容旧 *.config
            ApplyJson(cfg);              // JSON 覆盖（最高优先）
            return cfg;
        }

        private static void ApplyLegacyAppSettings(AgentConfig cfg)
        {
            try
            {
                var token = ConfigurationManager.AppSettings["Token"];
                if (!string.IsNullOrEmpty(token)) cfg.Token = token;

                var port = ConfigurationManager.AppSettings["Port"];
                int p;
                if (int.TryParse(port, out p) && p > 0) cfg.Port = p;

                var deployDir = ConfigurationManager.AppSettings["DeployDir"];
                if (deployDir != null) cfg.DeployDir = deployDir;

                var needBackUp = ConfigurationManager.AppSettings["NeedBackUp"];
                if (!string.IsNullOrEmpty(needBackUp))
                    cfg.NeedBackUp = !needBackUp.Trim().ToLower().Equals("false");

                var backUpLimit = ConfigurationManager.AppSettings["BackUpLimit"];
                int bl;
                if (int.TryParse(backUpLimit, out bl)) cfg.BackUpLimit = bl;

                var mac = ConfigurationManager.AppSettings["MacWhiteList"];
                if (mac != null) cfg.MacWhiteList = mac;

                var oldLimit = ConfigurationManager.AppSettings["OldPulishLimit"]; // 历史拼写
                int ol;
                if (int.TryParse(oldLimit, out ol)) cfg.OldPublishLimit = ol;

                var clearDays = ConfigurationManager.AppSettings["ClearOldPublishFolderOverDays"];
                int cd;
                if (int.TryParse(clearDays, out cd)) cfg.ClearOldPublishFolderOverDays = cd;
            }
            catch { /* 旧配置不可用时静默回退默认值 */ }
        }

        private static void ApplyJson(AgentConfig cfg)
        {
            try
            {
                var path = JsonPath;
                if (!File.Exists(path)) return;
                string text;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                {
                    text = sr.ReadToEnd();
                }
                if (string.IsNullOrWhiteSpace(text)) return;

                // 仅覆盖 JSON 中“显式出现”的字段，避免类型默认值抹掉 legacy 配置
                var jo = JObject.Parse(text);
                if (jo.ContainsKey("Token"))
                {
                    var t = (string)jo["Token"];
                    if (t != null) cfg.Token = t;
                }
                if (jo.ContainsKey("Port")) cfg.Port = jo.Value<int>("Port");
                if (jo.ContainsKey("DeployDir"))
                {
                    var d = (string)jo["DeployDir"];
                    cfg.DeployDir = d ?? string.Empty;
                }
                if (jo.ContainsKey("NeedBackUp")) cfg.NeedBackUp = jo.Value<bool>("NeedBackUp");
                if (jo.ContainsKey("BackUpLimit")) cfg.BackUpLimit = jo.Value<int>("BackUpLimit");
                if (jo.ContainsKey("MacWhiteList"))
                {
                    var m = (string)jo["MacWhiteList"];
                    cfg.MacWhiteList = m ?? string.Empty;
                }
                if (jo.ContainsKey("OldPublishLimit")) cfg.OldPublishLimit = jo.Value<int>("OldPublishLimit");
                if (jo.ContainsKey("ClearOldPublishFolderOverDays")) cfg.ClearOldPublishFolderOverDays = jo.Value<int>("ClearOldPublishFolderOverDays");
            }
            catch { /* JSON 缺失或损坏时保留 legacy/默认配置 */ }
        }

        /// <summary>
        /// 首次运行若无 agentsettings.json，则依据当前（旧 *.config/默认值合并后的）配置生成一份。
        /// 只读目录等写失败场景静默跳过——仍可用 legacy/内存配置正常运行，只是暂不支持热加载。
        /// </summary>
        private static void EnsureJsonFile()
        {
            try
            {
                if (string.IsNullOrEmpty(_dir) || !Directory.Exists(_dir)) return;
                if (File.Exists(JsonPath)) return;
                var json = JsonConvert.SerializeObject(_current, Formatting.Indented);
                File.WriteAllText(JsonPath, json, new UTF8Encoding(false));
            }
            catch { /* 无写权限时忽略 */ }
        }

        private static void StartWatcher()
        {
            try
            {
                if (string.IsNullOrEmpty(_dir) || !Directory.Exists(_dir)) return;
                _watcher = new FileSystemWatcher(_dir, JsonFileName);
                _watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime | NotifyFilters.FileName;
                _watcher.Changed += OnFileEvent;
                _watcher.Created += OnFileEvent;
                _watcher.Renamed += OnFileEvent;
                _watcher.EnableRaisingEvents = true;
            }
            catch { /* 监听不可用时仅失去热加载能力，不影响启动 */ }
        }

        private static void OnFileEvent(object sender, FileSystemEventArgs e)
        {
            // 防抖：300ms 内的多次文件系统事件合并为一次重载
            lock (_sync)
            {
                if (_debounce != null) _debounce.Dispose();
                _debounce = new Timer(delegate(object s) { try { Reload(); } catch { } }, null, 300, Timeout.Infinite);
            }
        }
    }
}
