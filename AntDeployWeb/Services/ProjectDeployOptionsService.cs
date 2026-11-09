using System;
using AntDeployWeb.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AntDeployWeb.Services
{
    /// <summary>
    /// 项目级并发发布设置服务（单例，走 IDbContextFactory）。
    /// 设置独立于配置存储位置，Json / DB 模式均可用，避免改动 Core 的 DeployProfile（对齐 ProjectTeamService 写法）。
    /// </summary>
    public class ProjectDeployOptionsService
    {
        /// <summary>并发模式：按数量</summary>
        public const string ModeCount = "count";
        /// <summary>并发模式：按百分比</summary>
        public const string ModePercent = "percent";

        private readonly IDbContextFactory<AntDeployDbContext> _dbFactory;
        private readonly ILogger<ProjectDeployOptionsService> _logger;

        public ProjectDeployOptionsService(IDbContextFactory<AntDeployDbContext> dbFactory, ILogger<ProjectDeployOptionsService> logger)
        {
            _dbFactory = dbFactory;
            _logger = logger;
        }

        /// <summary>获取项目并发设置；无记录时返回默认（上传/部署均为 count=1，即逐台顺序发布）。</summary>
        public DeployOptionsEntity GetOptions(string profileId)
        {
            if (string.IsNullOrEmpty(profileId)) return new DeployOptionsEntity();
            try
            {
                using var db = _dbFactory.CreateDbContext();
                return db.DeployOptions.AsNoTracking().FirstOrDefault(o => o.ProfileId == profileId)
                       ?? new DeployOptionsEntity { ProfileId = profileId };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "读取项目并发设置失败: {ProfileId}", profileId);
                return new DeployOptionsEntity { ProfileId = profileId };
            }
        }

        /// <summary>保存项目并发与零掉包设置（不存在则新增）。回调/两阶段参数为选填，缺省即不回调、不启用两阶段。</summary>
        public void SaveOptions(string profileId, string uploadMode, int uploadValue, string deployMode, int deployValue,
            string beforeCallbackUrl = null, string afterCallbackUrl = null, int drainWaitSeconds = 0,
            int callbackTimeoutSeconds = 10, bool enableTwoPhase = false)
        {
            if (string.IsNullOrEmpty(profileId)) return;
            try
            {
                using var db = _dbFactory.CreateDbContext();
                var opt = db.DeployOptions.FirstOrDefault(o => o.ProfileId == profileId);
                if (opt == null)
                {
                    opt = new DeployOptionsEntity { ProfileId = profileId };
                    db.DeployOptions.Add(opt);
                }
                opt.UploadMode = NormalizeMode(uploadMode);
                opt.UploadValue = NormalizeValue(uploadValue, opt.UploadMode);
                opt.DeployMode = NormalizeMode(deployMode);
                opt.DeployValue = NormalizeValue(deployValue, opt.DeployMode);
                opt.BeforeCallbackUrl = beforeCallbackUrl?.Trim() ?? string.Empty;
                opt.AfterCallbackUrl = afterCallbackUrl?.Trim() ?? string.Empty;
                opt.DrainWaitSeconds = drainWaitSeconds < 0 ? 0 : drainWaitSeconds;
                opt.CallbackTimeoutSeconds = callbackTimeoutSeconds <= 0 ? 10 : callbackTimeoutSeconds;
                opt.EnableTwoPhase = enableTwoPhase;
                opt.UpdatedAt = DateTime.UtcNow;
                db.SaveChanges();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存项目并发设置失败: {ProfileId}", profileId);
            }
        }

        /// <summary>删除项目并发设置（删除项目时调用）。</summary>
        public void DeleteMapping(string profileId)
        {
            if (string.IsNullOrEmpty(profileId)) return;
            try
            {
                using var db = _dbFactory.CreateDbContext();
                var opt = db.DeployOptions.FirstOrDefault(o => o.ProfileId == profileId);
                if (opt != null)
                {
                    db.DeployOptions.Remove(opt);
                    db.SaveChanges();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除项目并发设置失败: {ProfileId}", profileId);
            }
        }

        /// <summary>
        /// 计算实际并发上限：分别解析上传/部署并发（百分比按 serverCount 向上取整），取两者较小值，钳制到 [1, serverCount]。
        /// serverCount&lt;=0 时返回 1。
        /// </summary>
        public static int ResolveEffectiveParallel(DeployOptionsEntity opt, int serverCount)
        {
            if (serverCount <= 0) return 1;
            if (opt == null) return 1;
            var upload = ResolveOne(opt.UploadMode, opt.UploadValue, serverCount);
            var deploy = ResolveOne(opt.DeployMode, opt.DeployValue, serverCount);
            var eff = Math.Min(upload, deploy);
            if (eff < 1) eff = 1;
            if (eff > serverCount) eff = serverCount;
            return eff;
        }

        /// <summary>上传阶段实际并发（百分比按 serverCount 向上取整，钳制 [1, serverCount]）。</summary>
        public static int ResolveUploadParallel(DeployOptionsEntity opt, int serverCount)
        {
            if (serverCount <= 0) return 1;
            if (opt == null) return 1;
            return Clamp(ResolveOne(opt.UploadMode, opt.UploadValue, serverCount), 1, serverCount);
        }

        /// <summary>部署阶段实际并发（百分比按 serverCount 向上取整，钳制 [1, serverCount]）。</summary>
        public static int ResolveDeployParallel(DeployOptionsEntity opt, int serverCount)
        {
            if (serverCount <= 0) return 1;
            if (opt == null) return 1;
            return Clamp(ResolveOne(opt.DeployMode, opt.DeployValue, serverCount), 1, serverCount);
        }

        private static int ResolveOne(string mode, int value, int serverCount)
        {
            if (string.Equals(mode, ModePercent, StringComparison.OrdinalIgnoreCase))
            {
                var pct = value < 0 ? 0 : (value > 100 ? 100 : value);
                var n = (int)Math.Ceiling(serverCount * pct / 100.0);
                return Clamp(n, 1, serverCount);
            }
            return Clamp(value, 1, serverCount);
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        private static string NormalizeMode(string mode) =>
            string.Equals(mode, ModePercent, StringComparison.OrdinalIgnoreCase) ? ModePercent : ModeCount;

        private static int NormalizeValue(int value, string mode)
        {
            if (mode == ModePercent) return value < 1 ? 1 : (value > 100 ? 100 : value);
            return value < 1 ? 1 : value;
        }
    }
}
