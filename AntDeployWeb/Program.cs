using System.Security.Claims;
using AntDeployWeb.Components;
using AntDeployWeb.Data;
using AntDeployWeb.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Ant Design Blazor
builder.Services.AddAntDesign();

// ---------------- 存储配置 ----------------
var storageSection = builder.Configuration.GetSection(StorageOptions.SectionName);
builder.Services.Configure<StorageOptions>(storageSection);
var storageOptions = storageSection.Get<StorageOptions>() ?? new StorageOptions();
var provider = storageOptions.GetProvider();

// ---------------- 鉴权配置 ----------------
var authSection = builder.Configuration.GetSection(AuthOptions.SectionName);
builder.Services.Configure<AuthOptions>(authSection);
var authOptions = authSection.Get<AuthOptions>() ?? new AuthOptions();

// ---------------- 数据库（始终注册：鉴权/团队/会话/历史均依赖数据库） ----------------
// Json 配置模式下，profiles/settings 走文件，但 users/teams/sessions/项目团队映射仍走数据库。
var dataDir = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
Directory.CreateDirectory(dataDir);

StorageProvider dbProvider;
string connStr;
if (provider == StorageProvider.Json)
{
    // 配置存文件，鉴权等仍用默认 SQLite
    dbProvider = StorageProvider.Sqlite;
    connStr = $"Data Source={Path.Combine(dataDir, "antdeploy.db")}";
}
else
{
    dbProvider = provider;
    connStr = storageOptions.ConnectionString;
    if (string.IsNullOrWhiteSpace(connStr))
        connStr = "Data Source=App_Data/antdeploy.db";

    if (dbProvider == StorageProvider.Sqlite
        && !Path.IsPathRooted(connStr)
        && connStr.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
    {
        var relativePath = connStr.Substring("Data Source=".Length).Trim();
        if (!Path.IsPathRooted(relativePath))
            connStr = $"Data Source={Path.Combine(dataDir, Path.GetFileName(relativePath))}";
    }
}

builder.Services.AddDbContextFactory<AntDeployDbContext>(options =>
{
    switch (dbProvider)
    {
        case StorageProvider.SqlServer:
            options.UseSqlServer(connStr);
            break;
        case StorageProvider.MySql:
            var serverVersion = new MySqlServerVersion(new Version(8, 0, 0));
            options.UseMySql(connStr, serverVersion);
            break;
        default:
            options.UseSqlite(connStr);
            break;
    }
});

// 根据配置选择配置存储实现
if (provider == StorageProvider.Json)
    builder.Services.AddSingleton<IConfigStore, JsonConfigStore>();
else
    builder.Services.AddSingleton<IConfigStore, DbConfigStore>();

// ---------------- 鉴权（Cookie） ----------------
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = string.IsNullOrWhiteSpace(authOptions.CookieName) ? "antdeploy_auth" : authOptions.CookieName;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.LoginPath = "/login";
        options.LogoutPath = "/account/logout";
        options.AccessDeniedPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromDays(authOptions.ExpireDays <= 0 ? 3650 : authOptions.ExpireDays);
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();
builder.Services.AddScoped<AuthenticationStateProvider, ServerAuthStateProvider>();

// ---------------- 业务服务 ----------------
builder.Services.AddSingleton<DeploySessionManager>();   // 单例，跨用户共享发布状态
builder.Services.AddSingleton<UserService>();            // 用户/团队管理
builder.Services.AddSingleton<ProjectTeamService>();     // 项目↔团队映射
builder.Services.AddSingleton<ProjectDeployOptionsService>(); // 项目级并发发布设置
builder.Services.AddSingleton<AgentService>();             // Agent 客户端存储（在线管理）
builder.Services.AddSingleton<AgentInstaller>();           // Agent 在线安装编排（SSH/WinRM 远程装服务）
builder.Services.AddSingleton<AgentVerifier>();            // Agent 连接验证（外机 /version + 本机自检对比定位）
builder.Services.AddSingleton<AgentConfigSyncer>();        // Agent 配置在线同步（/config 推送 + 热生效，低版本/不可达列出原因）
builder.Services.AddScoped<CurrentUserService>();        // 当前用户上下文（每电路一个）
builder.Services.AddScoped<DeployService>();             // 部署编排（每电路一个）

var app = builder.Build();

// ---------------- 数据库初始化 + 种子超级管理员 ----------------
using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AntDeployDbContext>>();
    using (var db = dbFactory.CreateDbContext())
    {
        // 开发环境使用 EnsureCreated（生产建议使用 Migrations）
        db.Database.EnsureCreated();
        // 对已存在的旧库做幂等 Schema 补丁（补 用户/团队/会话 新表与新列），保留既有数据
        SchemaUpgrader.EnsureLatestSchema(db, dbProvider);
    }
    scope.ServiceProvider.GetRequiredService<UserService>().EnsureSeedAdmin();
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// ---------------- 登录 / 注销端点（原生表单 POST，规避 Blazor 电路设置 Cookie 的问题） ----------------
app.MapPost("/account/login", async (HttpContext http, UserService users) =>
{
    var form = await http.Request.ReadFormAsync();
    var username = form["username"].ToString();
    var password = form["password"].ToString();
    var user = users.ValidateLogin(username, password);
    if (user == null)
        return Results.Redirect("/login?error=1");

    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, user.Id),
        new(ClaimTypes.Name, user.UserName),
        new(ClaimTypes.Role, user.Role),
        new(CurrentUserService.DisplayNameClaimType, string.IsNullOrWhiteSpace(user.DisplayName) ? user.UserName : user.DisplayName)
    };
    if (!string.IsNullOrEmpty(user.TeamId))
        claims.Add(new Claim(CurrentUserService.TeamClaimType, user.TeamId));

    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    var principal = new ClaimsPrincipal(identity);
    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties
    {
        IsPersistent = true,
        ExpiresUtc = DateTimeOffset.UtcNow.AddDays(authOptions.ExpireDays <= 0 ? 3650 : authOptions.ExpireDays)
    });
    return Results.Redirect("/");
}).DisableAntiforgery();

app.MapPost("/account/logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
}).DisableAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
