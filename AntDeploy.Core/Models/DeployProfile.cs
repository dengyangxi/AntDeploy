using System.Collections.Generic;

namespace AntDeploy.Core.Models
{
    /// <summary>
    /// 一个部署项目的完整配置（共享数据契约）。
    /// Web 端表单绑定此模型，部署编排服务直接消费它。
    /// 凭据（Git.Password、Server.Token、镜像仓库密码）在落盘时由 CodingHelper 做 AES 加密。
    /// </summary>
    public class DeployProfile
    {
        public string Id { get; set; } = System.Guid.NewGuid().ToString("N");
        public string Name { get; set; }
        /// <summary>本地代码目录（位于 Web 服务的工作区根目录下，或绝对路径）</summary>
        public string ProjectPath { get; set; }

        /// <summary>最近一次发布（部署）的时间，用于项目列表展示</summary>
        public System.DateTime? LastPublishTime { get; set; }
        /// <summary>最近一次发布所使用的分支名称，用于项目列表展示</summary>
        public string LastPublishBranch { get; set; }
        /// <summary>最近一次发布所使用的部署类型（iis/windowservice/linux/docker），一键发布默认沿用</summary>
        public string LastDeployType { get; set; }

        public GitConfig Git { get; set; } = new GitConfig();
        public BuildConfig Build { get; set; } = new BuildConfig();
        public PluginConfig Plugin { get; set; } = new PluginConfig();

        public List<Env> Envs { get; set; } = new List<Env>();

        public IIsConfig Iis { get; set; } = new IIsConfig();
        public WindowsServiceConfig WindowsService { get; set; } = new WindowsServiceConfig();
        public LinuxServiceConfig Linux { get; set; } = new LinuxServiceConfig();
        public DockerConfig Docker { get; set; } = new DockerConfig();
    }

    public class GitConfig
    {
        public string RepositoryUrl { get; set; }
        public string UserName { get; set; }
        /// <summary>明文仅在内存中使用；持久化时由存储层加密</summary>
        public string Password { get; set; }
        public string Branch { get; set; } = "main";
        /// <summary>true 时直接使用已存在的本地目录，不执行 clone/pull</summary>
        public bool UseLocalPath { get; set; }
    }

    public class BuildConfig
    {
        /// <summary>netcore | framework</summary>
        public string SdkType { get; set; } = "netcore";
        /// <summary>
        /// 要编译发布的项目/解决方案文件（相对仓库根的相对路径，或绝对路径），如
        /// "Homeinns.PMS.WebUI/Homeinns.PMS.WebUI.csproj"。留空则自动探测。
        /// 多项目仓库（根目录只有 .sln、子目录含多个 .csproj）务必显式指定要发布的 Web 主项目，
        /// 否则自动探测可能选中无关的类库项目导致编译失败。
        /// </summary>
        public string ProjectFile { get; set; }
        public bool IsWeb { get; set; } = true;
        public string Configuration { get; set; } = "Release";
        public string Framework { get; set; }
        public string Runtime { get; set; }
        public string ExtraArgs { get; set; }
        /// <summary>发布输出目录（相对工作区或绝对路径），为空时默认 {workspace}/{projectId}/publish</summary>
        public string PublishPath { get; set; }
    }

    public class Env
    {
        public string Name { get; set; }
        /// <summary>打包忽略列表（zip 时跳过匹配项）</summary>
        public List<string> IgnoreList { get; set; } = new List<string>();
        /// <summary>备份忽略列表（Agent 端备份旧版本时跳过匹配项）</summary>
        public List<string> WindowsBackUpIgnoreList { get; set; } = new List<string>();
        /// <summary>Agent 服务器（HTTP 协议，host + token）</summary>
        public List<Server> ServerList { get; set; } = new List<Server>();
        /// <summary>SSH 服务器（直连 Linux/Docker，host + 用户名 + 密码）</summary>
        public List<LinuxServer> LinuxServerList { get; set; } = new List<LinuxServer>();
    }

    public class Server
    {
        /// <summary>Agent 地址，例如 http://192.168.1.10:5000 或 192.168.1.10:5000</summary>
        public string Host { get; set; }
        /// <summary>服务器昵称（便于识别）</summary>
        public string NickName { get; set; }
        /// <summary>Agent 令牌，持久化时加密</summary>
        public string Token { get; set; }
        public string IIsFireUrl { get; set; }
        public string LinuxServiceFireUrl { get; set; }
        public string WindowsServiceFireUrl { get; set; }
        public string DockerFireUrl { get; set; }
        public bool Enable { get; set; } = true;
    }

    /// <summary>
    /// SSH 直连服务器（用于 Linux 上的 Docker 部署：上传 zip → 远程 docker build/run）。
    /// 对应 WinForm 的 LinuxServer（host + 用户名 + 密码）。
    /// </summary>
    public class LinuxServer
    {
        /// <summary>SSH 地址，例如 192.168.1.20 或 192.168.1.20:22</summary>
        public string Host { get; set; }
        public string NickName { get; set; }
        public string UserName { get; set; }
        /// <summary>SSH 密码，持久化时加密</summary>
        public string Pwd { get; set; }
        public string DockerFireUrl { get; set; }
        public bool Enable { get; set; } = true;
    }

    public class EnvPairConfig
    {
        public string EnvName { get; set; }
        public string ConfigName { get; set; }
        public string LinuxEnvParam { get; set; }
        public string DockerPort { get; set; }
        public string DockerVolume { get; set; }
        public string DockerOther { get; set; }
    }

    public class IIsConfig
    {
        public string SdkType { get; set; } = "netcore";
        public string WebSiteName { get; set; }
        public string PoolName { get; set; }
        public string PhysicalPath { get; set; }
        /// <summary>站点绑定端口（Agent 校验/预热用，可空）</summary>
        public string Port { get; set; }
        public bool PoolAlwaysRunning { get; set; }
        public bool UseOfflineHtm { get; set; }
        public bool UseTempPhysicalPath { get; set; }
        public bool NotStopSiteDeploy { get; set; }
        /// <summary>增量部署：仅上传自上次以来变更的文件</summary>
        public bool EnableIncrement { get; set; }
        /// <summary>选择文件部署：手动勾选要上传的文件</summary>
        public bool EnableSelectDeploy { get; set; }
        public List<EnvPairConfig> EnvPairList { get; set; } = new List<EnvPairConfig>();
        public string LastEnvName { get; set; }
    }

    public class WindowsServiceConfig
    {
        public string ServiceName { get; set; }
        public bool IsProjectInstallService { get; set; }
        public string SdkType { get; set; } = "netcore";
        public string StartBat { get; set; }
        /// <summary>服务可执行文件相对路径（项目自安装服务时用）</summary>
        public string ExecFilePath { get; set; }
        /// <summary>服务启动类型（Automatic / Manual ...）</summary>
        public string StartType { get; set; }
        /// <summary>服务描述</summary>
        public string Description { get; set; }
        /// <summary>是否使用 nssm 安装</summary>
        public string UseNssm { get; set; }
        /// <summary>服务启动参数</summary>
        public string Param { get; set; }
        public bool EnableIncrement { get; set; }
        public bool EnableSelectDeploy { get; set; }
        public List<EnvPairConfig> EnvPairList { get; set; } = new List<EnvPairConfig>();
        public string LastEnvName { get; set; }
    }

    public class LinuxServiceConfig
    {
        public string ServiceName { get; set; }
        public string EnvParam { get; set; }
        public string SdkType { get; set; } = "netcore";
        public bool UseDotnet { get; set; } = true;
        /// <summary>部署后通知 systemd 重启服务</summary>
        public bool NotifySystemd { get; set; }
        public bool EnableIncrement { get; set; }
        public bool EnableSelectDeploy { get; set; }
        public List<EnvPairConfig> EnvPairList { get; set; } = new List<EnvPairConfig>();
        public string LastEnvName { get; set; }
    }

    public class DockerConfig
    {
        public string BaseImage { get; set; }
        public ImageCredential BaseImageCredential { get; set; } = new ImageCredential();
        public string TargetImage { get; set; }
        public ImageCredential TargetImageCredential { get; set; } = new ImageCredential();
        public string ImageFormat { get; set; } = "docker";
        public List<string> IgnoreList { get; set; } = new List<string>();
        public List<EnvPairConfig> EnvPairList { get; set; } = new List<EnvPairConfig>();
        public string LastEnvName { get; set; }
        public string Port { get; set; }
        public string Volume { get; set; }
        public string Other { get; set; }
        /// <summary>是否用 sudo 执行 docker 命令</summary>
        public bool EnableSudo { get; set; }
        /// <summary>仅构建镜像，不启动容器</summary>
        public bool BuildImageOnly { get; set; }
        /// <summary>容器内 ASPNETCORE_ENVIRONMENT</summary>
        public string AspNetCoreEnv { get; set; }
        /// <summary>删除 N 天前的旧版本（0 表示不删除）</summary>
        public int RemoveDaysFromPublished { get; set; }
        /// <summary>远程工作目录（为空则用 antdeploy）</summary>
        public string WorkDir { get; set; }
        /// <summary>是否上传（关闭时仅本地构建镜像 tar）</summary>
        public bool EnableUpload { get; set; } = true;
        /// <summary>镜像仓库地址</summary>
        public string RepositoryUrl { get; set; }
        public string RepositoryUserName { get; set; }
        /// <summary>镜像仓库密码，持久化时加密</summary>
        public string RepositoryUserPwd { get; set; }
        public string RepositoryNameSpace { get; set; }
        public string RepositoryImageName { get; set; }
        /// <summary>增量部署：仅上传自上次以来变更的文件</summary>
        public bool EnableIncrement { get; set; }
        /// <summary>选择文件部署：手动勾选要上传的文件</summary>
        public bool EnableSelectDeploy { get; set; }
        /// <summary>jib 基础镜像 HTTP 代理</summary>
        public string BaseHttpProxy { get; set; }
        /// <summary>jib 目标镜像 HTTP 代理</summary>
        public string TargetHttpProxy { get; set; }
        /// <summary>jib 目标镜像 tag（多个用英文逗号分隔）</summary>
        public string TargetTags { get; set; }
        /// <summary>jib Entrypoint（多个用英文逗号分隔）</summary>
        public string Entrypoint { get; set; }
        /// <summary>jib Cmd（多个用英文逗号分隔）</summary>
        public string Cmd { get; set; }
    }

    /// <summary>
    /// SSH 部署时写入远程的 args.json（对应 WinForm 的 LinuxArgModel）。
    /// 用于服务器端识别部署来源与备注。
    /// </summary>
    public class LinuxArgModel
    {
        public string Remark { get; set; }
        public string Mac { get; set; }
        public string Ip { get; set; }
        public string Pc { get; set; }
    }

    /// <summary>
    /// jib 镜像构建配置 DTO（对应 WinForm 的 DockerImageConfig）。
    /// </summary>
    public class DockerImageConfig
    {
        public string BaseHttpProxy { get; set; }
        public string BaseImage { get; set; }
        public ImageCredential BaseImageCredential { get; set; } = new ImageCredential();
        public string TargetImage { get; set; }
        public string TargetHttpProxy { get; set; }
        public string[] TargetTags { get; set; }
        public ImageCredential TargetImageCredential { get; set; } = new ImageCredential();
        public string ImageFormat { get; set; } = "docker";
        public string[] Entrypoint { get; set; }
        public string[] Cmd { get; set; }
        public List<string> IgnoreList { get; set; } = new List<string>();
    }

    /// <summary>
    /// jib.exe 实际读取的序列化配置（对应 WinForm 的 DockerImageConfigGo）。
    /// </summary>
    public class DockerImageConfigGo : DockerImageConfig
    {
        public string ImageLayersFolder { get; set; }
        public string ImageWorkingDirectory { get; set; } = "/publish";
        public string ApplicationLayersCacheDirectory { get; set; }

        public static DockerImageConfigGo get(DockerImageConfig config)
        {
            var go = new DockerImageConfigGo
            {
                BaseHttpProxy = config.BaseHttpProxy,
                BaseImage = config.BaseImage,
                BaseImageCredential = config.BaseImageCredential,
                TargetImage = config.TargetImage,
                TargetHttpProxy = config.TargetHttpProxy,
                TargetTags = config.TargetTags,
                TargetImageCredential = config.TargetImageCredential,
                ImageFormat = config.ImageFormat,
                Entrypoint = config.Entrypoint,
                Cmd = config.Cmd,
                IgnoreList = config.IgnoreList
            };
            return go;
        }
    }

    public class ImageCredential
    {
        public string UserName { get; set; }
        public string Password { get; set; }
    }

    public class PluginConfig
    {
        public bool EnableIncrement { get; set; }
        public bool EnableSelectDeploy { get; set; }
        public string DeployFolderPath { get; set; }
        public string DeployHttpProxy { get; set; }
        /// <summary>
        /// .NET Core 发布模式："FDD"（框架依赖）或 "xxx(runtime)"（自包含，括号内为 runtime 标识）。
        /// </summary>
        public string NetCorePublishMode { get; set; }

        /// <summary>
        /// 根据 NetCorePublishMode 计算 dotnet publish 的 --runtime 参数。
        /// FDD 返回空字符串；"xxx(runtime)" 提取 runtime 返回 " --runtime {runtime}"。
        /// </summary>
        public string GetNetCorePublishRuntimeArg()
        {
            var mode = NetCorePublishMode;
            if (string.IsNullOrWhiteSpace(mode)) return string.Empty;
            if (mode.IndexOf("(runtime)", System.StringComparison.OrdinalIgnoreCase) < 0)
            {
                return string.Empty;
            }
            var runtime = mode.Replace("(runtime)", string.Empty).Trim();
            if (string.IsNullOrEmpty(runtime)) return string.Empty;
            return " --runtime " + runtime;
        }
    }
}
