using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AntDeploy.Core.Models;
using AntDeploy.Core.Util;
using AntDeployWeb.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace AntDeployWeb.Services
{
    /// <summary>
    /// 基于 EF Core 的数据库配置存储（单例）。支持 SQLite / SQL Server / MySQL，
    /// 由 appsettings.json 的 Storage:Provider 与 Storage:ConnectionString 决定。
    /// 凭据（Git 密码 / Agent Token / 镜像仓库密码）在落库时使用 CodingHelper.AES 加密。
    /// </summary>
    public class DbConfigStore : IConfigStore
    {
        private readonly IDbContextFactory<AntDeployDbContext> _dbFactory;
        private readonly StorageOptions _options;
        private readonly string _dataDir;
        private readonly object _lock = new object();

        public DbConfigStore(IDbContextFactory<AntDeployDbContext> dbFactory, IOptions<StorageOptions> options, IWebHostEnvironment env)
        {
            _dbFactory = dbFactory;
            _options = options.Value;
            _dataDir = Path.Combine(env.ContentRootPath, "App_Data");
        }

        /// <summary>解析后的工作区根目录（git 代码落地位置）</summary>
        public string GetWorkspaceRoot()
        {
            var s = GetSettings();
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

        public WebSettings GetSettings()
        {
            using var db = _dbFactory.CreateDbContext();
            var entity = db.WebSettings.AsNoTracking().FirstOrDefault(e => e.Id == 1);
            if (entity == null || string.IsNullOrWhiteSpace(entity.Data))
                return new WebSettings();
            try
            {
                return JsonConvert.DeserializeObject<WebSettings>(entity.Data) ?? new WebSettings();
            }
            catch
            {
                return new WebSettings();
            }
        }

        public void SaveSettings(WebSettings settings)
        {
            lock (_lock)
            {
                using var db = _dbFactory.CreateDbContext();
                var entity = db.WebSettings.FirstOrDefault(e => e.Id == 1);
                if (entity == null)
                {
                    entity = new WebSettingsEntity { Id = 1 };
                    db.WebSettings.Add(entity);
                }
                entity.Data = JsonConvert.SerializeObject(settings ?? new WebSettings());
                entity.UpdatedAt = DateTime.UtcNow;
                db.SaveChanges();
            }
        }

        /// <summary>
        /// 解析 jib.exe 所在目录：优先用设置的 JibPath，其次探测程序目录/DLL 目录。
        /// </summary>
        public string GetJibPath()
        {
            var s = GetSettings();
            if (!string.IsNullOrWhiteSpace(s.JibPath))
            {
                if (File.Exists(Path.Combine(s.JibPath, "jib.exe")) || Directory.Exists(s.JibPath))
                    return s.JibPath;
            }
            var baseDir = AppContext.BaseDirectory;
            if (File.Exists(Path.Combine(baseDir, "jib.exe"))) return baseDir;
            var dllDir = Path.Combine(baseDir, "DLL");
            if (File.Exists(Path.Combine(dllDir, "jib.exe"))) return dllDir;
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

        public List<DeployProfile> GetProfiles()
        {
            using var db = _dbFactory.CreateDbContext();
            var entities = db.DeployProfiles.AsNoTracking().OrderBy(e => e.CreatedAt).ToList();
            var result = new List<DeployProfile>();
            foreach (var e in entities)
            {
                var p = DeserializeProfile(e.Data);
                if (p != null) result.Add(p);
            }
            return result;
        }

        public DeployProfile GetProfile(string id)
        {
            using var db = _dbFactory.CreateDbContext();
            var entity = db.DeployProfiles.AsNoTracking().FirstOrDefault(e => e.Id == id);
            if (entity == null) return null;
            return DeserializeProfile(entity.Data);
        }

        public void AddProfile(DeployProfile profile)
        {
            lock (_lock)
            {
                using var db = _dbFactory.CreateDbContext();
                if (string.IsNullOrEmpty(profile.Id)) profile.Id = Guid.NewGuid().ToString("N");
                var entity = new DeployProfileEntity
                {
                    Id = profile.Id,
                    Name = profile.Name ?? string.Empty,
                    Data = SerializeProfile(profile),
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                db.DeployProfiles.Add(entity);
                db.SaveChanges();
            }
        }

        public void UpdateProfile(DeployProfile profile)
        {
            lock (_lock)
            {
                using var db = _dbFactory.CreateDbContext();
                var entity = db.DeployProfiles.FirstOrDefault(e => e.Id == profile.Id);
                if (entity == null)
                {
                    entity = new DeployProfileEntity
                    {
                        Id = profile.Id,
                        CreatedAt = DateTime.UtcNow
                    };
                    db.DeployProfiles.Add(entity);
                }
                entity.Name = profile.Name ?? string.Empty;
                entity.Data = SerializeProfile(profile);
                entity.UpdatedAt = DateTime.UtcNow;
                db.SaveChanges();
            }
        }

        public void DeleteProfile(string id)
        {
            lock (_lock)
            {
                using var db = _dbFactory.CreateDbContext();
                var entity = db.DeployProfiles.FirstOrDefault(e => e.Id == id);
                if (entity != null)
                {
                    db.DeployProfiles.Remove(entity);
                    db.SaveChanges();
                }
            }
        }

        // ---------------- 序列化 / 加密 ----------------

        private static string SerializeProfile(DeployProfile profile)
        {
            // 克隆一份，对凭据字段加密后再序列化
            var clone = JsonConvert.DeserializeObject<DeployProfile>(JsonConvert.SerializeObject(profile)) ?? new DeployProfile();
            EncryptSecrets(clone);
            return JsonConvert.SerializeObject(clone);
        }

        private static DeployProfile DeserializeProfile(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var p = JsonConvert.DeserializeObject<DeployProfile>(json);
                if (p != null) DecryptSecrets(p);
                return p;
            }
            catch
            {
                return null;
            }
        }

        private static void EncryptSecrets(DeployProfile p)
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

        private static void DecryptSecrets(DeployProfile p)
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

        private static string Enc(string v) => string.IsNullOrEmpty(v) ? v : CodingHelper.AESEncrypt(v);
        private static string Dec(string v)
        {
            if (string.IsNullOrEmpty(v)) return v;
            try { return CodingHelper.AESDecrypt(v); } catch { return v; }
        }
    }
}
