using Microsoft.EntityFrameworkCore;

namespace AntDeployWeb.Data
{
    /// <summary>
    /// AntDeploy Web 数据库上下文。支持 SQLite / SQL Server / MySQL 三种提供程序，
    /// 由 Program.cs 根据 appsettings.json 的 Storage:Provider 配置选择。
    /// </summary>
    public class AntDeployDbContext : DbContext
    {
        public AntDeployDbContext(DbContextOptions<AntDeployDbContext> options)
            : base(options)
        {
        }

        /// <summary>全局设置（单行）</summary>
        public DbSet<WebSettingsEntity> WebSettings => Set<WebSettingsEntity>();

        /// <summary>部署项目配置</summary>
        public DbSet<DeployProfileEntity> DeployProfiles => Set<DeployProfileEntity>();

        /// <summary>部署会话/历史</summary>
        public DbSet<DeploySessionEntity> DeploySessions => Set<DeploySessionEntity>();

        /// <summary>系统用户</summary>
        public DbSet<UserEntity> Users => Set<UserEntity>();

        /// <summary>团队</summary>
        public DbSet<TeamEntity> Teams => Set<TeamEntity>();

        /// <summary>项目↔团队映射</summary>
        public DbSet<ProjectTeamEntity> ProjectTeams => Set<ProjectTeamEntity>();

        /// <summary>项目级并发发布设置</summary>
        public DbSet<DeployOptionsEntity> DeployOptions => Set<DeployOptionsEntity>();

        /// <summary>Agent 客户端（在线管理）</summary>
        public DbSet<AgentClientEntity> AgentClients => Set<AgentClientEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // WebSettings 始终只有一行（Id=1）
            modelBuilder.Entity<WebSettingsEntity>().HasData(new WebSettingsEntity { Id = 1, Data = "{}" });

            // DeploySessions 索引：按 ProfileId + IsRunning 快速查询运行中的会话
            modelBuilder.Entity<DeploySessionEntity>()
                .HasIndex(e => new { e.ProfileId, e.IsRunning });

            // DeploySessions 索引：按 StartTime 排序查询历史
            modelBuilder.Entity<DeploySessionEntity>()
                .HasIndex(e => e.StartTime);

            // 用户名唯一索引
            modelBuilder.Entity<UserEntity>()
                .HasIndex(e => e.UserName)
                .IsUnique();

            // AgentClients 索引：按 TeamId 过滤（团队管理员仅见本团队）
            modelBuilder.Entity<AgentClientEntity>()
                .HasIndex(e => e.TeamId);
        }
    }
}
