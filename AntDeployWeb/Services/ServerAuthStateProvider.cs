using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AntDeployWeb.Services
{
    /// <summary>
    /// 基于 Cookie 鉴权的 Blazor Server 鉴权状态提供器。
    /// 初始状态由框架在电路创建时从 HttpContext.User 注入；
    /// 周期性重新校验用户是否仍存在且未被禁用（被禁用后最多 20 分钟内失效）。
    /// </summary>
    public class ServerAuthStateProvider : RevalidatingServerAuthenticationStateProvider
    {
        private readonly IServiceScopeFactory _scopeFactory;

        public ServerAuthStateProvider(ILoggerFactory loggerFactory, IServiceScopeFactory scopeFactory)
            : base(loggerFactory)
        {
            _scopeFactory = scopeFactory;
        }

        protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(20);

        protected override Task<bool> ValidateAuthenticationStateAsync(
            AuthenticationState authenticationState, CancellationToken cancellationToken)
        {
            var user = authenticationState?.User;
            if (user?.Identity?.IsAuthenticated != true) return Task.FromResult(false);
            var id = user.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(id)) return Task.FromResult(false);

            var scope = _scopeFactory.CreateScope();
            try
            {
                var users = scope.ServiceProvider.GetRequiredService<UserService>();
                var dbUser = users.GetUser(id);
                return Task.FromResult(dbUser != null && !dbUser.IsDisabled);
            }
            finally
            {
                scope.Dispose();
            }
        }
    }
}
