namespace AntDeployWeb.Data
{
    /// <summary>
    /// appsettings.json 中的鉴权配置段（Auth）。
    /// </summary>
    public class AuthOptions
    {
        public const string SectionName = "Auth";

        /// <summary>鉴权 Cookie 名称</summary>
        public string CookieName { get; set; } = "antdeploy_auth";

        /// <summary>登录有效期（天）。默认 3650 天（约 10 年），配合滑动过期实现"永不过期，除非注销"。</summary>
        public int ExpireDays { get; set; } = 3650;

        /// <summary>首次运行（库中无用户）时种子创建的超级管理员账号。</summary>
        public DefaultAdminOptions DefaultAdmin { get; set; } = new();
    }

    /// <summary>初始超级管理员账号配置。</summary>
    public class DefaultAdminOptions
    {
        public string UserName { get; set; } = "admin";
        public string Password { get; set; } = "admin123";
        public string DisplayName { get; set; } = "超级管理员";
    }
}
