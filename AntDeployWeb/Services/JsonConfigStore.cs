using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AntDeploy.Core.Models;
using AntDeploy.Core.Util;
using Newtonsoft.Json;

namespace AntDeployWeb.Services
{
    /// <summary>
    /// 本地 JSON 配置存储（单例）。负责项目配置与全局设置的读写，
    /// 凭据（Git 密码 / Agent Token / 镜像仓库密码）在落盘时使用 CodingHelper.AES 加密。
    /// 实现 IConfigStore 接口，作为数据库存储的向后兼容回退方案。
    /// </summary>
    public class JsonConfigStore : IConfigStore
    {
        private readonly string _dataDir;
        private readonly string _dataFile;
        private readonly string _contentRoot;
        private readonly object _lock = new object();
        private AppData _cache;

        public JsonConfigStore(IWebHostEnvironment env)
        {
            _contentRoot = env.ContentRootPath;
            _dataDir = Path.Combine(_contentRoot, "App_Data");
            _dataFile = Path.Combine(_dataDir, "antdeploy.json");
        }

        /// <summary>解析后的工作区根目录（git 代码落地位置）</summary>
        public string GetWorkspaceRoot()
        {
            var s = Load().Settings;
            if (!string.IsNullOrWhiteSpace(s.WorkspaceRoot)) return s.WorkspaceRoot;
            return Path.Combine(_dataDir, "workspace");
        }

        /// <summary>某个项目在本地的代码目录</summary>
        public string GetProjectPath(DeployProfile profile)
        {
            if (!string.IsNullOrWhiteSpace(profile.ProjectPath) && Path.IsPathRooted(profile.ProjectPath))
                return profile.ProjectPath;
            var sub = string.IsNullOrWhiteSpace(profile.ProjectPath) ? profile.Id : profile.ProjectPath;
            return Path.Combine(GetWorkspaceRoot(), sub);
        }

        /// <summary>某个项目的编译发布输出目录</summary>
        public string GetPublishPath(DeployProfile profile)
        {
            if (!string.IsNullOrWhiteSpace(profile.Build?.PublishPath))
            {
                return Path.IsPathRooted(profile.Build.PublishPath)
                    ? profile.Build.PublishPath
                    : Path.Combine(GetWorkspaceRoot(), profile.Build.PublishPath);
            }
            return Path.Combine(GetProjectPath(profile), "..", profile.Id + "_publish");
        }

        public WebSettings GetSettings() => Load().Settings;

        /// <summary>
        /// 解析 jib.exe 所在目录：优先用设置的 JibPath，其次探测程序目录/DLL 目录。
        /// 返回包含 jib.exe 的目录（若都找不到则返回设置值或程序目录，由调用方校验存在性）。
        /// </summary>
        public string GetJibPath()
        {
            var s = Load().Settings;
            if (!string.IsNullOrWhiteSpace(s.JibPath))
            {
                if (File.Exists(Path.Combine(s.JibPath, "jib.exe")) || Directory.Exists(s.JibPath))
                    return s.JibPath;
            }
            var baseDir = AppContext.BaseDirectory;
            if (File.Exists(Path.Combine(baseDir, "jib.exe"))) return baseDir;
            var dllDir = Path.Combine(baseDir, "DLL");
            if (File.Exists(Path.Combine(dllDir, "jib.exe"))) return dllDir;
            // 向上回溯查找仓库根下的 DLL/jib.exe
            try
            {
                var dir = new DirectoryInfo(baseDir);
                for (int i = 0; i < 6 && dir != null; i++)
                {
                    var cand = Path.Combine(dir.FullName, "DLL", "jib.exe");
                    if (File.Exists(cand)) return Path.Combine(dir.FullName, "DLL");
                    dir = dir.Parent;
                }
            }
            catch { }
            return string.IsNullOrWhiteSpace(s.JibPath) ? baseDir : s.JibPath;
        }

        public void SaveSettings(WebSettings settings)
        {
            lock (_lock)
            {
                var data = Load();
                data.Settings = settings ?? new WebSettings();
                WriteToDisk(data);
            }
        }

        public List<DeployProfile> GetProfiles() => Load().Profiles;

        public DeployProfile GetProfile(string id) => Load().Profiles.FirstOrDefault(p => p.Id == id);

        public void AddProfile(DeployProfile profile)
        {
            lock (_lock)
            {
                var data = Load();
                if (string.IsNullOrEmpty(profile.Id)) profile.Id = Guid.NewGuid().ToString("N");
                data.Profiles.Add(profile);
                WriteToDisk(data);
            }
        }

        public void UpdateProfile(DeployProfile profile)
        {
            lock (_lock)
            {
                var data = Load();
                var idx = data.Profiles.FindIndex(p => p.Id == profile.Id);
                if (idx >= 0) data.Profiles[idx] = profile;
                else data.Profiles.Add(profile);
                WriteToDisk(data);
            }
        }

        public void DeleteProfile(string id)
        {
            lock (_lock)
            {
                var data = Load();
                data.Profiles.RemoveAll(p => p.Id == id);
                WriteToDisk(data);
            }
        }

        private AppData Load()
        {
            if (_cache != null) return _cache;
            lock (_lock)
            {
                if (_cache != null) return _cache;
                try
                {
                    if (File.Exists(_dataFile))
                    {
                        var json = File.ReadAllText(_dataFile);
                        var data = JsonConvert.DeserializeObject<AppData>(json) ?? new AppData();
                        DecryptSecrets(data);
                        _cache = data;
                    }
                    else
                    {
                        _cache = new AppData();
                    }
                }
                catch
                {
                    _cache = new AppData();
                }
                return _cache;
            }
        }

        private void WriteToDisk(AppData data)
        {
            _cache = data;
            Directory.CreateDirectory(_dataDir);
            var clone = JsonConvert.DeserializeObject<AppData>(JsonConvert.SerializeObject(data)) ?? new AppData();
            EncryptSecrets(clone);
            var json = JsonConvert.SerializeObject(clone, Formatting.Indented);
            File.WriteAllText(_dataFile, json);
        }

        private static void EncryptSecrets(AppData data)
        {
            foreach (var p in data.Profiles)
            {
                if (p.Git != null) p.Git.Password = Enc(p.Git.Password);
                if (p.Envs != null)
                {
                    foreach (var env in p.Envs)
                    {
                        foreach (var srv in env.ServerList ?? new List<Server>())
                            srv.Token = Enc(srv.Token);
                        foreach (var ls in env.LinuxServerList ?? new List<LinuxServer>())
                            ls.Pwd = Enc(ls.Pwd);
                    }
                }
                if (p.Docker != null)
                {
                    if (p.Docker.BaseImageCredential != null) p.Docker.BaseImageCredential.Password = Enc(p.Docker.BaseImageCredential.Password);
                    if (p.Docker.TargetImageCredential != null) p.Docker.TargetImageCredential.Password = Enc(p.Docker.TargetImageCredential.Password);
                    p.Docker.RepositoryUserPwd = Enc(p.Docker.RepositoryUserPwd);
                }
            }
        }

        private static void DecryptSecrets(AppData data)
        {
            foreach (var p in data.Profiles)
            {
                if (p.Git != null) p.Git.Password = Dec(p.Git.Password);
                if (p.Envs != null)
                {
                    foreach (var env in p.Envs)
                    {
                        foreach (var srv in env.ServerList ?? new List<Server>())
                            srv.Token = Dec(srv.Token);
                        foreach (var ls in env.LinuxServerList ?? new List<LinuxServer>())
                            ls.Pwd = Dec(ls.Pwd);
                    }
                }
                if (p.Docker != null)
                {
                    if (p.Docker.BaseImageCredential != null) p.Docker.BaseImageCredential.Password = Dec(p.Docker.BaseImageCredential.Password);
                    if (p.Docker.TargetImageCredential != null) p.Docker.TargetImageCredential.Password = Dec(p.Docker.TargetImageCredential.Password);
                    p.Docker.RepositoryUserPwd = Dec(p.Docker.RepositoryUserPwd);
                }
            }
        }

        private static string Enc(string v) => string.IsNullOrEmpty(v) ? v : CodingHelper.AESEncrypt(v);
        private static string Dec(string v)
        {
            if (string.IsNullOrEmpty(v)) return v;
            try { return CodingHelper.AESDecrypt(v); } catch { return v; }
        }
    }
}
