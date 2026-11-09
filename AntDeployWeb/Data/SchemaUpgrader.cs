using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;

namespace AntDeployWeb.Data
{
    /// <summary>
    /// 轻量、幂等、保留数据的 Schema 演进器。
    /// <para>
    /// EF Core 的 <c>EnsureCreated()</c> 只在数据库不存在时建库建表，对“已存在的旧库”不会补新表/新列。
    /// 本项目未引入 Migrations，为让旧库（含既有项目配置数据）平滑升级到含 用户/团队/会话新字段 的模型，
    /// 这里对默认的 SQLite 提供程序做幂等补丁：新表用 <c>CREATE TABLE IF NOT EXISTS</c>，
    /// 新列先查 <c>PRAGMA table_info</c> 再 <c>ALTER TABLE ADD COLUMN</c>。绝不删除既有表或数据。
    /// </para>
    /// <para>SqlServer / MySql 的生产环境请改用 EF Migrations；此处仅对 SQLite 兜底。</para>
    /// </summary>
    public static class SchemaUpgrader
    {
        public static void EnsureLatestSchema(AntDeployDbContext db, StorageProvider provider)
        {
            if (provider != StorageProvider.Sqlite) return; // 仅对默认 SQLite 兜底
            try
            {
                // ---- 新表（幂等）----
                db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS ""Users"" (
                    ""Id"" TEXT NOT NULL CONSTRAINT ""PK_Users"" PRIMARY KEY,
                    ""UserName"" TEXT NOT NULL,
                    ""DisplayName"" TEXT NOT NULL,
                    ""PasswordHash"" TEXT NOT NULL,
                    ""PasswordSalt"" TEXT NOT NULL,
                    ""Role"" TEXT NOT NULL,
                    ""TeamId"" TEXT NULL,
                    ""IsDisabled"" INTEGER NOT NULL,
                    ""CreatedAt"" TEXT NOT NULL
                );");
                db.Database.ExecuteSqlRaw(@"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Users_UserName"" ON ""Users"" (""UserName"");");

                db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS ""Teams"" (
                    ""Id"" TEXT NOT NULL CONSTRAINT ""PK_Teams"" PRIMARY KEY,
                    ""Name"" TEXT NOT NULL,
                    ""Description"" TEXT NOT NULL,
                    ""CreatedAt"" TEXT NOT NULL
                );");

                db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS ""ProjectTeams"" (
                    ""ProfileId"" TEXT NOT NULL CONSTRAINT ""PK_ProjectTeams"" PRIMARY KEY,
                    ""TeamId"" TEXT NULL,
                    ""UpdatedAt"" TEXT NOT NULL
                );");

                db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS ""DeployOptions"" (
                    ""ProfileId"" TEXT NOT NULL CONSTRAINT ""PK_DeployOptions"" PRIMARY KEY,
                    ""UploadMode"" TEXT NOT NULL,
                    ""UploadValue"" INTEGER NOT NULL,
                    ""DeployMode"" TEXT NOT NULL,
                    ""DeployValue"" INTEGER NOT NULL,
                    ""BeforeCallbackUrl"" TEXT NOT NULL DEFAULT '',
                    ""AfterCallbackUrl"" TEXT NOT NULL DEFAULT '',
                    ""DrainWaitSeconds"" INTEGER NOT NULL DEFAULT 0,
                    ""CallbackTimeoutSeconds"" INTEGER NOT NULL DEFAULT 10,
                    ""EnableTwoPhase"" INTEGER NOT NULL DEFAULT 0,
                    ""UpdatedAt"" TEXT NOT NULL
                );");

                db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS ""AgentClients"" (
                    ""Id"" TEXT NOT NULL CONSTRAINT ""PK_AgentClients"" PRIMARY KEY,
                    ""Name"" TEXT NOT NULL,
                    ""TeamId"" TEXT NULL,
                    ""OsType"" TEXT NOT NULL DEFAULT 'windows',
                    ""Data"" TEXT NOT NULL DEFAULT '',
                    ""CreatedAt"" TEXT NOT NULL,
                    ""UpdatedAt"" TEXT NOT NULL
                );");
                db.Database.ExecuteSqlRaw(@"CREATE INDEX IF NOT EXISTS ""IX_AgentClients_TeamId"" ON ""AgentClients"" (""TeamId"");");

                // ---- DeployOptions 新列（既有库幂等补齐）----
                var optCols = GetColumns(db, "DeployOptions");
                AddColumnIfMissing(db, "DeployOptions", optCols, "BeforeCallbackUrl", "TEXT NOT NULL DEFAULT ''");
                AddColumnIfMissing(db, "DeployOptions", optCols, "AfterCallbackUrl", "TEXT NOT NULL DEFAULT ''");
                AddColumnIfMissing(db, "DeployOptions", optCols, "DrainWaitSeconds", "INTEGER NOT NULL DEFAULT 0");
                AddColumnIfMissing(db, "DeployOptions", optCols, "CallbackTimeoutSeconds", "INTEGER NOT NULL DEFAULT 10");
                AddColumnIfMissing(db, "DeployOptions", optCols, "EnableTwoPhase", "INTEGER NOT NULL DEFAULT 0");

                // ---- DeploySessions 新列（存在则跳过）----
                var sessionCols = GetColumns(db, "DeploySessions");
                AddColumnIfMissing(db, "DeploySessions", sessionCols, "Operation", "TEXT NOT NULL DEFAULT 'quick_deploy'");
                AddColumnIfMissing(db, "DeploySessions", sessionCols, "RollbackVersion", "TEXT NOT NULL DEFAULT ''");
                AddColumnIfMissing(db, "DeploySessions", sessionCols, "DockerUseSsh", "INTEGER NOT NULL DEFAULT 0");
                AddColumnIfMissing(db, "DeploySessions", sessionCols, "SelectedFilesJson", "TEXT NOT NULL DEFAULT ''");
                AddColumnIfMissing(db, "DeploySessions", sessionCols, "PullMs", "INTEGER NOT NULL DEFAULT 0");
                AddColumnIfMissing(db, "DeploySessions", sessionCols, "BuildMs", "INTEGER NOT NULL DEFAULT 0");
                AddColumnIfMissing(db, "DeploySessions", sessionCols, "PackageMs", "INTEGER NOT NULL DEFAULT 0");
                AddColumnIfMissing(db, "DeploySessions", sessionCols, "UploadMs", "INTEGER NOT NULL DEFAULT 0");
                AddColumnIfMissing(db, "DeploySessions", sessionCols, "DeployMs", "INTEGER NOT NULL DEFAULT 0");
            }
            catch
            {
                // 兜底补丁失败不应阻断启动；EnsureCreated 已保证全新库结构完整。
            }
        }

        private static HashSet<string> GetColumns(AntDeployDbContext db, string table)
        {
            var conn = db.Database.GetDbConnection();
            var needClose = false;
            if (conn.State != System.Data.ConnectionState.Open) { conn.Open(); needClose = true; }
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"PRAGMA table_info(\"{table}\");";
                using var reader = cmd.ExecuteReader();
                var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (reader.Read())
                {
                    // PRAGMA table_info 第 2 列（索引 1）为列名
                    cols.Add(reader.GetValue(1)?.ToString() ?? string.Empty);
                }
                return cols;
            }
            finally
            {
                if (needClose) conn.Close();
            }
        }

        private static void AddColumnIfMissing(AntDeployDbContext db, string table, HashSet<string> cols, string column, string ddlType)
        {
            if (cols.Contains(column)) return;
            db.Database.ExecuteSqlRaw($"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {ddlType};");
            cols.Add(column);
        }
    }
}
