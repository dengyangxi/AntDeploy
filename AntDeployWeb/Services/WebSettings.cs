using System.Collections.Generic;
using AntDeploy.Core.Models;

namespace AntDeployWeb.Services
{
    /// <summary>
    /// Web 端全局设置（对应 WinForm 的 GlobalConfig）。
    /// </summary>
    public class WebSettings
    {
        /// <summary>代码工作区根目录：git clone/pull 的落地位置。为空时使用 {ContentRoot}/App_Data/workspace</summary>
        public string WorkspaceRoot { get; set; } = string.Empty;

        /// <summary>msbuild.exe 路径（编译 net framework 项目用）。为空时自动通过 vswhere 探测。</summary>
        public string MsBuildPath { get; set; } = string.Empty;

        /// <summary>是否保存日志到文件</summary>
        public bool SaveLogs { get; set; }

        /// <summary>Docker 容器内是否使用 Asia/Shanghai 时区</summary>
        public bool UseAsiaShanghai { get; set; }

        /// <summary>部署/SSH 使用的 HTTP 代理，格式 host:port（为空不使用）</summary>
        public string DeployHttpProxy { get; set; } = string.Empty;

        /// <summary>jib.exe 所在目录（镜像构建用）。为空时自动探测程序目录/DLL 目录。</summary>
        public string JibPath { get; set; } = string.Empty;

        /// <summary>是否把项目配置同时保存到项目目录下的 AntDeploy.json</summary>
        public bool EnableAntDeployJson { get; set; }

        /// <summary>界面语言：zh / en（i18n）</summary>
        public string Language { get; set; } = "zh";
    }

    /// <summary>
    /// 落盘的数据结构：全局设置 + 项目配置列表。
    /// </summary>
    public class AppData
    {
        public WebSettings Settings { get; set; } = new WebSettings();
        public List<DeployProfile> Profiles { get; set; } = new List<DeployProfile>();
    }
}
