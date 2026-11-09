using System;
using System.Collections.Generic;
using System.Linq;
using AntDeploy.Core.Util;
using AntDeployWeb.Data;
using AntDeployWeb.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AntDeployWeb.Services
{
    /// <summary>
    /// Agent 客户端存储服务（单例，走 IDbContextFactory；仿 <see cref="ProjectTeamService"/> / <see cref="DbConfigStore"/>）。
    /// <para>职责：CRUD + 序列化。服务器密码 / SSH 私钥 / Agent Token 落库前 AES 加密、读取后解密（与 DbConfigStore 一致，复用 CodingHelper）。</para>
    /// <para>团队可见性过滤由 <see cref="CurrentUserService"/> 统一判定，本服务只负责持久化，保持职责单一。</para>
    /// </summary>
    public class AgentService
    {
        private readonly IDbContextFactory<AntDeployDbContext> _dbFactory;
        private readonly ILogger<AgentService> _logger;
        private readonly object _lock = new object();

        public AgentService(IDbContextFactory<AntDeployDbContext> dbFactory, ILogger<AgentService> logger)
        {
            _dbFactory = dbFactory;
            _logger = logger;
        }

        /// <summary>获取全部 Agent 客户端（凭据已解密；可见性过滤交由调用方）。</summary>
        public List<AgentClient> GetAll()
        {
            try
            {
                using var db = _dbFactory.CreateDbContext();
                var entities = db.AgentClients.AsNoTracking().OrderBy(e => e.CreatedAt).ToList();
                var result = new List<AgentClient>();
                foreach (var e in entities)
                {
                    var c = Deserialize(e);
                    if (c != null) result.Add(c);
                }
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "读取 Agent 客户端列表失败");
                return new List<AgentClient>();
            }
        }

        /// <summary>按 Id 获取单个（凭据已解密）；不存在返回 null。</summary>
        public AgentClient? Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            try
            {
                using var db = _dbFactory.CreateDbContext();
                var e = db.AgentClients.AsNoTracking().FirstOrDefault(x => x.Id == id);
                return e == null ? null : Deserialize(e);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "读取 Agent 客户端失败: {Id}", id);
                return null;
            }
        }

        /// <summary>新增或更新（凭据落库前加密）。Id 为空则生成；Name/TeamId/OsType 同步到实体列。</summary>
        public void Save(AgentClient client)
        {
            if (client == null) return;
            lock (_lock)
            {
                try
                {
                    using var db = _dbFactory.CreateDbContext();
                    if (string.IsNullOrEmpty(client.Id)) client.Id = Guid.NewGuid().ToString("N");
                    var entity = db.AgentClients.FirstOrDefault(x => x.Id == client.Id);
                    var now = DateTime.UtcNow;
                    if (entity == null)
                    {
                        entity = new AgentClientEntity { Id = client.Id, CreatedAt = now };
                        db.AgentClients.Add(entity);
                        client.CreatedAt = now;
                    }
                    entity.Name = client.Name ?? string.Empty;
                    entity.TeamId = client.TeamId;
                    entity.OsType = string.IsNullOrWhiteSpace(client.OsType) ? "windows" : client.OsType.Trim().ToLowerInvariant();
                    client.OsType = entity.OsType;
                    entity.Data = Serialize(client);
                    entity.UpdatedAt = now;
                    client.UpdatedAt = now;
                    db.SaveChanges();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "保存 Agent 客户端失败: {Id}", client.Id);
                }
            }
        }

        /// <summary>删除。</summary>
        public void Delete(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            lock (_lock)
            {
                try
                {
                    using var db = _dbFactory.CreateDbContext();
                    var entity = db.AgentClients.FirstOrDefault(x => x.Id == id);
                    if (entity != null)
                    {
                        db.AgentClients.Remove(entity);
                        db.SaveChanges();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "删除 Agent 客户端失败: {Id}", id);
                }
            }
        }

        // ---------------- 序列化 / 加密 ----------------

        /// <summary>克隆后对凭据字段加密再序列化，避免污染调用方持有的明文对象。</summary>
        private static string Serialize(AgentClient client)
        {
            var clone = JsonConvert.DeserializeObject<AgentClient>(JsonConvert.SerializeObject(client)) ?? new AgentClient();
            foreach (var s in clone.Servers ?? new List<AgentServerItem>())
            {
                s.Pwd = Enc(s.Pwd) ?? string.Empty;
                s.PrivateKey = Enc(s.PrivateKey);
            }
            if (clone.Config != null) clone.Config.Token = Enc(clone.Config.Token) ?? string.Empty;
            return JsonConvert.SerializeObject(clone);
        }

        /// <summary>反序列化并解密凭据；以实体列为准回填标识字段（列是权威来源，避免 JSON 与列不一致）。</summary>
        private static AgentClient? Deserialize(AgentClientEntity entity)
        {
            if (entity == null || string.IsNullOrWhiteSpace(entity.Data)) return null;
            try
            {
                var c = JsonConvert.DeserializeObject<AgentClient>(entity.Data);
                if (c == null) return null;
                c.Id = entity.Id;
                c.Name = entity.Name;
                c.TeamId = entity.TeamId;
                c.OsType = entity.OsType;
                c.CreatedAt = entity.CreatedAt;
                c.UpdatedAt = entity.UpdatedAt;
                foreach (var s in c.Servers ?? new List<AgentServerItem>())
                {
                    s.Pwd = Dec(s.Pwd) ?? string.Empty;
                    s.PrivateKey = Dec(s.PrivateKey);
                }
                if (c.Config != null) c.Config.Token = Dec(c.Config.Token) ?? string.Empty;
                return c;
            }
            catch
            {
                return null;
            }
        }

        private static string? Enc(string? v) => string.IsNullOrEmpty(v) ? v : CodingHelper.AESEncrypt(v);
        private static string? Dec(string? v)
        {
            if (string.IsNullOrEmpty(v)) return v;
            try { return CodingHelper.AESDecrypt(v); } catch { return v; }
        }
    }
}
