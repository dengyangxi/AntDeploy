using System;
using System.Collections.Generic;
using System.Linq;
using AntDeployWeb.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AntDeployWeb.Services
{
    /// <summary>
    /// 项目↔团队映射服务（单例，走 IDbContextFactory）。
    /// 映射独立于配置存储位置，Json / DB 模式均可用，避免改动 Core 的 DeployProfile。
    /// </summary>
    public class ProjectTeamService
    {
        private readonly IDbContextFactory<AntDeployDbContext> _dbFactory;
        private readonly ILogger<ProjectTeamService> _logger;

        public ProjectTeamService(IDbContextFactory<AntDeployDbContext> dbFactory, ILogger<ProjectTeamService> logger)
        {
            _dbFactory = dbFactory;
            _logger = logger;
        }

        /// <summary>获取项目所属团队 Id（未分配返回 null）。</summary>
        public string? GetTeamId(string profileId)
        {
            if (string.IsNullOrEmpty(profileId)) return null;
            try
            {
                using var db = _dbFactory.CreateDbContext();
                return db.ProjectTeams.AsNoTracking().FirstOrDefault(p => p.ProfileId == profileId)?.TeamId;
            }
            catch { return null; }
        }

        /// <summary>获取全部项目→团队映射。</summary>
        public Dictionary<string, string?> GetTeamMap()
        {
            try
            {
                using var db = _dbFactory.CreateDbContext();
                return db.ProjectTeams.AsNoTracking().ToDictionary(p => p.ProfileId, p => p.TeamId);
            }
            catch { return new Dictionary<string, string?>(); }
        }

        /// <summary>设置项目所属团队。</summary>
        public void SetTeamId(string profileId, string? teamId)
        {
            if (string.IsNullOrEmpty(profileId)) return;
            try
            {
                using var db = _dbFactory.CreateDbContext();
                var map = db.ProjectTeams.FirstOrDefault(p => p.ProfileId == profileId);
                if (map == null)
                {
                    map = new ProjectTeamEntity { ProfileId = profileId };
                    db.ProjectTeams.Add(map);
                }
                map.TeamId = teamId;
                map.UpdatedAt = DateTime.UtcNow;
                db.SaveChanges();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "设置项目团队失败: {ProfileId}", profileId);
            }
        }

        /// <summary>删除项目映射（删除项目时调用）。</summary>
        public void DeleteMapping(string profileId)
        {
            if (string.IsNullOrEmpty(profileId)) return;
            try
            {
                using var db = _dbFactory.CreateDbContext();
                var map = db.ProjectTeams.FirstOrDefault(p => p.ProfileId == profileId);
                if (map != null)
                {
                    db.ProjectTeams.Remove(map);
                    db.SaveChanges();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除项目团队映射失败: {ProfileId}", profileId);
            }
        }
    }
}
