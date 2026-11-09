using System;
using System.Collections.Generic;
using System.Linq;
using AntDeployWeb.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AntDeployWeb.Services
{
    /// <summary>
    /// 用户与团队管理服务（单例，走 IDbContextFactory）。
    /// </summary>
    public class UserService
    {
        private readonly IDbContextFactory<AntDeployDbContext> _dbFactory;
        private readonly AuthOptions _authOptions;
        private readonly ILogger<UserService> _logger;

        public UserService(IDbContextFactory<AntDeployDbContext> dbFactory, IOptions<AuthOptions> authOptions, ILogger<UserService> logger)
        {
            _dbFactory = dbFactory;
            _authOptions = authOptions.Value;
            _logger = logger;
        }

        /// <summary>校验登录，成功返回用户实体，失败返回 null。</summary>
        public UserEntity? ValidateLogin(string userName, string password)
        {
            if (string.IsNullOrWhiteSpace(userName)) return null;
            try
            {
                using var db = _dbFactory.CreateDbContext();
                var user = db.Users.AsNoTracking().FirstOrDefault(u => u.UserName == userName);
                if (user == null || user.IsDisabled) return null;
                return PasswordHasher.Verify(password, user.PasswordSalt, user.PasswordHash) ? user : null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "登录校验失败: {UserName}", userName);
                return null;
            }
        }

        public UserEntity? GetUser(string id)
        {
            try
            {
                using var db = _dbFactory.CreateDbContext();
                return db.Users.AsNoTracking().FirstOrDefault(u => u.Id == id);
            }
            catch { return null; }
        }

        public List<UserEntity> ListUsers(string? teamIdFilter = null)
        {
            try
            {
                using var db = _dbFactory.CreateDbContext();
                var q = db.Users.AsNoTracking().AsQueryable();
                if (!string.IsNullOrEmpty(teamIdFilter)) q = q.Where(u => u.TeamId == teamIdFilter);
                return q.OrderBy(u => u.UserName).ToList();
            }
            catch { return new List<UserEntity>(); }
        }

        public List<TeamEntity> ListTeams()
        {
            try
            {
                using var db = _dbFactory.CreateDbContext();
                return db.Teams.AsNoTracking().OrderBy(t => t.Name).ToList();
            }
            catch { return new List<TeamEntity>(); }
        }

        /// <summary>创建用户。返回 (成功, 错误信息)。</summary>
        public (bool ok, string error) CreateUser(string userName, string password, string displayName, UserRole role, string? teamId)
        {
            if (string.IsNullOrWhiteSpace(userName)) return (false, "用户名不能为空");
            if (string.IsNullOrWhiteSpace(password)) return (false, "密码不能为空");
            try
            {
                using var db = _dbFactory.CreateDbContext();
                if (db.Users.Any(u => u.UserName == userName)) return (false, "用户名已存在");
                var salt = PasswordHasher.CreateSalt();
                var user = new UserEntity
                {
                    Id = Guid.NewGuid().ToString("N"),
                    UserName = userName.Trim(),
                    DisplayName = displayName ?? string.Empty,
                    PasswordSalt = salt,
                    PasswordHash = PasswordHasher.Hash(password, salt),
                    Role = role.ToString(),
                    TeamId = role == UserRole.SuperAdmin ? null : teamId,
                    CreatedAt = DateTime.UtcNow
                };
                db.Users.Add(user);
                db.SaveChanges();
                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建用户失败");
                return (false, ex.Message);
            }
        }

        /// <summary>更新用户（不含密码）。password 非空时同时改密。</summary>
        public (bool ok, string error) UpdateUser(string id, string displayName, UserRole role, string? teamId, bool isDisabled, string? password = null)
        {
            try
            {
                using var db = _dbFactory.CreateDbContext();
                var user = db.Users.FirstOrDefault(u => u.Id == id);
                if (user == null) return (false, "用户不存在");
                user.DisplayName = displayName ?? string.Empty;
                user.Role = role.ToString();
                user.TeamId = role == UserRole.SuperAdmin ? null : teamId;
                user.IsDisabled = isDisabled;
                if (!string.IsNullOrWhiteSpace(password))
                {
                    user.PasswordSalt = PasswordHasher.CreateSalt();
                    user.PasswordHash = PasswordHasher.Hash(password, user.PasswordSalt);
                }
                db.SaveChanges();
                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新用户失败");
                return (false, ex.Message);
            }
        }

        public (bool ok, string error) DeleteUser(string id)
        {
            try
            {
                using var db = _dbFactory.CreateDbContext();
                var user = db.Users.FirstOrDefault(u => u.Id == id);
                if (user == null) return (false, "用户不存在");
                // 禁止删除最后一个超级管理员
                if (user.GetRole() == UserRole.SuperAdmin && db.Users.Count(u => u.Role == nameof(UserRole.SuperAdmin)) <= 1)
                    return (false, "不能删除唯一的超级管理员");
                db.Users.Remove(user);
                db.SaveChanges();
                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除用户失败");
                return (false, ex.Message);
            }
        }

        public (bool ok, string error) CreateTeam(string name, string description)
        {
            if (string.IsNullOrWhiteSpace(name)) return (false, "团队名称不能为空");
            try
            {
                using var db = _dbFactory.CreateDbContext();
                if (db.Teams.Any(t => t.Name == name)) return (false, "团队名称已存在");
                db.Teams.Add(new TeamEntity
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = name.Trim(),
                    Description = description ?? string.Empty,
                    CreatedAt = DateTime.UtcNow
                });
                db.SaveChanges();
                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建团队失败");
                return (false, ex.Message);
            }
        }

        public (bool ok, string error) UpdateTeam(string id, string name, string description)
        {
            try
            {
                using var db = _dbFactory.CreateDbContext();
                var team = db.Teams.FirstOrDefault(t => t.Id == id);
                if (team == null) return (false, "团队不存在");
                team.Name = name ?? team.Name;
                team.Description = description ?? string.Empty;
                db.SaveChanges();
                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新团队失败");
                return (false, ex.Message);
            }
        }

        public (bool ok, string error) DeleteTeam(string id)
        {
            try
            {
                using var db = _dbFactory.CreateDbContext();
                var team = db.Teams.FirstOrDefault(t => t.Id == id);
                if (team == null) return (false, "团队不存在");
                if (db.Users.Any(u => u.TeamId == id)) return (false, "团队下仍有用户，无法删除");
                db.Teams.Remove(team);
                // 解除项目归属
                var maps = db.ProjectTeams.Where(p => p.TeamId == id).ToList();
                if (maps.Count > 0) db.ProjectTeams.RemoveRange(maps);
                db.SaveChanges();
                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除团队失败");
                return (false, ex.Message);
            }
        }

        /// <summary>首次运行（库中无用户）时按 appsettings 的 Auth:DefaultAdmin 种子创建超级管理员。</summary>
        public void EnsureSeedAdmin()
        {
            try
            {
                using var db = _dbFactory.CreateDbContext();
                if (db.Users.Any()) return;
                var admin = _authOptions.DefaultAdmin;
                var userName = string.IsNullOrWhiteSpace(admin.UserName) ? "admin" : admin.UserName.Trim();
                var password = string.IsNullOrWhiteSpace(admin.Password) ? "admin123" : admin.Password;
                var salt = PasswordHasher.CreateSalt();
                db.Users.Add(new UserEntity
                {
                    Id = Guid.NewGuid().ToString("N"),
                    UserName = userName,
                    DisplayName = string.IsNullOrWhiteSpace(admin.DisplayName) ? "超级管理员" : admin.DisplayName,
                    PasswordSalt = salt,
                    PasswordHash = PasswordHasher.Hash(password, salt),
                    Role = UserRole.SuperAdmin.ToString(),
                    TeamId = null,
                    CreatedAt = DateTime.UtcNow
                });
                db.SaveChanges();
                _logger.LogInformation("已种子创建初始超级管理员：{UserName}", userName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "种子创建超级管理员失败");
            }
        }
    }
}
