using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using AntDeploy.Core.Models;
using AntDeployWeb.Data;
using AntDeployWeb.Models;
using Microsoft.AspNetCore.Components.Authorization;

namespace AntDeployWeb.Services
{
    /// <summary>
    /// 当前登录用户上下文（Scoped，每个电路一个）。
    /// 从 Blazor 鉴权状态读取用户身份，并集中提供角色/团队权限判定。
    /// 使用前需先 await LoadAsync()。
    /// </summary>
    public class CurrentUserService
    {
        public const string TeamClaimType = "antdeploy_team";
        public const string DisplayNameClaimType = "antdeploy_displayname";

        private readonly AuthenticationStateProvider _authProvider;
        private readonly ProjectTeamService _projectTeams;

        public CurrentUserService(AuthenticationStateProvider authProvider, ProjectTeamService projectTeams)
        {
            _authProvider = authProvider;
            _projectTeams = projectTeams;
        }

        public bool IsAuthenticated { get; private set; }
        public string UserId { get; private set; } = string.Empty;
        public string UserName { get; private set; } = string.Empty;
        public string DisplayName { get; private set; } = string.Empty;
        public UserRole Role { get; private set; } = UserRole.User;
        public string? TeamId { get; private set; }

        public bool IsSuperAdmin => Role == UserRole.SuperAdmin;
        public bool IsTeamAdmin => Role == UserRole.TeamAdmin;
        public bool IsNormalUser => Role == UserRole.User;

        public string RoleText => Role switch
        {
            UserRole.SuperAdmin => "超级管理员",
            UserRole.TeamAdmin => "团队管理员",
            _ => "普通用户"
        };

        /// <summary>从鉴权状态加载当前用户身份。</summary>
        public async Task LoadAsync()
        {
            var state = await _authProvider.GetAuthenticationStateAsync();
            var user = state.User;
            if (user?.Identity?.IsAuthenticated != true)
            {
                IsAuthenticated = false;
                return;
            }
            IsAuthenticated = true;
            UserId = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
            UserName = user.FindFirstValue(ClaimTypes.Name) ?? string.Empty;
            DisplayName = user.FindFirstValue(DisplayNameClaimType) ?? UserName;
            TeamId = user.FindFirstValue(TeamClaimType);
            var roleStr = user.FindFirstValue(ClaimTypes.Role);
            Role = System.Enum.TryParse<UserRole>(roleStr, true, out var r) ? r : UserRole.User;
        }

        // ---------------- 权限判定 ----------------

        /// <summary>是否可访问（可见）某项目。</summary>
        public bool CanAccessProject(string? profileId)
        {
            if (!IsAuthenticated) return false;
            if (IsSuperAdmin) return true;
            var team = _projectTeams.GetTeamId(profileId ?? string.Empty);
            // 未分配团队的项目仅超级管理员可见
            if (string.IsNullOrEmpty(team)) return false;
            return team == TeamId;
        }

        /// <summary>是否可编辑某项目配置（超管全部；团队管理员本团队；普通用户否）。</summary>
        public bool CanEditProject(string? profileId)
        {
            if (!IsAuthenticated) return false;
            if (IsSuperAdmin) return true;
            if (IsTeamAdmin) return CanAccessProject(profileId);
            return false;
        }

        /// <summary>是否可删除某项目（同编辑权限）。</summary>
        public bool CanDeleteProject(string? profileId) => CanEditProject(profileId);

        /// <summary>是否可发布某项目（在可见范围内即可发布）。</summary>
        public bool CanPublishProject(string? profileId) => CanAccessProject(profileId);

        /// <summary>是否可新增项目（超管、团队管理员）。</summary>
        public bool CanAddProject => IsAuthenticated && (IsSuperAdmin || IsTeamAdmin);

        /// <summary>是否可访问全局设置（仅超管）。</summary>
        public bool CanAccessSettings => IsAuthenticated && IsSuperAdmin;

        /// <summary>是否可访问用户/团队管理（超管、团队管理员）。</summary>
        public bool CanManageUsers => IsAuthenticated && (IsSuperAdmin || IsTeamAdmin);

        /// <summary>是否可访问 Agent 管理（超管、团队管理员）。</summary>
        public bool CanManageAgents => IsAuthenticated && (IsSuperAdmin || IsTeamAdmin);

        /// <summary>过滤出当前用户可见的项目列表。</summary>
        public List<DeployProfile> FilterVisible(IEnumerable<DeployProfile> profiles)
        {
            if (profiles == null) return new List<DeployProfile>();
            if (IsSuperAdmin) return profiles.ToList();
            var map = _projectTeams.GetTeamMap();
            return profiles.Where(p =>
            {
                map.TryGetValue(p.Id, out var team);
                return !string.IsNullOrEmpty(team) && team == TeamId;
            }).ToList();
        }

        /// <summary>过滤出当前用户可见的 Agent 客户端（超管全部；团队管理员仅本团队；未分配团队的仅超管可见）。</summary>
        public List<AgentClient> FilterVisibleAgents(IEnumerable<AgentClient> agents)
        {
            if (agents == null) return new List<AgentClient>();
            if (IsSuperAdmin) return agents.ToList();
            return agents.Where(a => !string.IsNullOrEmpty(a.TeamId) && a.TeamId == TeamId).ToList();
        }

        /// <summary>是否可编辑某 Agent 客户端（超管全部；团队管理员本团队；普通用户否）。</summary>
        public bool CanEditAgent(AgentClient? agent)
        {
            if (!IsAuthenticated || agent == null) return false;
            if (IsSuperAdmin) return true;
            if (IsTeamAdmin) return !string.IsNullOrEmpty(agent.TeamId) && agent.TeamId == TeamId;
            return false;
        }
    }
}
