using System;
using System.Collections.Generic;
using AntDeploy.Core.Models;

namespace AntDeployWeb.Services
{
    /// <summary>
    /// 配置存储抽象。支持 JSON 文件与数据库（SQLite / SQL Server / MySQL）两种实现，
    /// 由 appsettings.json 的 Storage:Provider 决定注册哪一个。
    /// </summary>
    public interface IConfigStore
    {
        /// <summary>解析后的工作区根目录（git 代码落地位置）</summary>
        string GetWorkspaceRoot();

        /// <summary>某个项目在本地的代码目录</summary>
        string GetProjectPath(DeployProfile profile);

        /// <summary>某个项目的编译发布输出目录</summary>
        string GetPublishPath(DeployProfile profile);

        /// <summary>获取全局设置</summary>
        WebSettings GetSettings();

        /// <summary>保存全局设置</summary>
        void SaveSettings(WebSettings settings);

        /// <summary>解析 jib.exe 所在目录</summary>
        string GetJibPath();

        /// <summary>获取所有项目配置</summary>
        List<DeployProfile> GetProfiles();

        /// <summary>按 Id 获取项目配置</summary>
        DeployProfile GetProfile(string id);

        /// <summary>新增项目配置</summary>
        void AddProfile(DeployProfile profile);

        /// <summary>更新项目配置</summary>
        void UpdateProfile(DeployProfile profile);

        /// <summary>删除项目配置</summary>
        void DeleteProfile(string id);
    }
}
