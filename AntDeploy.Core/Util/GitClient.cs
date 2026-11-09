using System;
using System.IO;
using System.Linq;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;

namespace AntDeploy.Core.Util
{
    /// <summary>
    /// 基于 LibGit2Sharp 的 Git 客户端，支持 clone / pull 并携带 http 凭据。
    /// 从 AntDeployWinform.Util.GitClient 提取公共能力，去掉 NLog 依赖改用 ILog。
    /// </summary>
    public class GitClient : IDisposable
    {
        private readonly ILog _logger;
        private string _userName;
        private string _password;

        // 进度回调节流状态（每次操作都 new 一个 GitClient 实例，故用实例字段即可）。
        // LibGit2Sharp 的 transfer/checkout 回调触发极其频繁，若每次都写日志会刷屏并拖慢 Blazor 电路，
        // 因此按“百分比变化 + 时间间隔”节流。
        private int _lastTransferPercent = -1;
        private int _lastCheckoutPercent = -1;
        private DateTime _lastProgressLog = DateTime.MinValue;

        public GitClient(ILog logger)
        {
            _logger = logger;
        }

        private Credentials CredentialsProvider(string url, string user, SupportedCredentialTypes types)
        {
            return new UsernamePasswordCredentials
            {
                Username = string.IsNullOrEmpty(_userName) ? "token" : _userName,
                Password = _password ?? string.Empty
            };
        }

        /// <summary>
        /// 拉取代码（按用户需求的确定行为）：
        /// ① 本地目录已存在且是 Git 仓库 → 增量 Pull（绝不删除目录；克隆被中断的半成品仓库也走这里续传恢复）；
        /// ② 本地目录已存在但为空 → 直接克隆进去；
        /// ③ 本地目录已存在、非空、又不是 Git 仓库 → 不删除，给出清晰提示交由用户处理；
        /// ④ 本地目录不存在 → 创建并克隆。
        /// checkCancel 返回 true 时中止下载（经 LibGit2Sharp 传输回调），避免大仓库/网络异常时“一直卡着”。
        /// </summary>
        public bool CloneOrUpdate(string repoUrl, string localPath, string branch, string userName, string password, Func<bool> checkCancel = null)
        {
            _userName = userName;
            _password = password;
            if (string.IsNullOrEmpty(localPath)) { _logger.Error("【git】local path is empty"); return false; }
            if (string.IsNullOrEmpty(repoUrl)) { _logger.Error("【git】repository url is empty"); return false; }

            try
            {
                if (Directory.Exists(localPath))
                {
                    // 存在即“增量拉取”，绝不删除用户目录。
                    // IsLocalGitRepo 严格校验 .git/HEAD 存在且工作树根 == localPath，
                    // 杜绝“无效 .git → LibGit2Sharp 向上发现父仓库 → Pull 误操作真实源码”的危险。
                    if (IsLocalGitRepo(localPath))
                    {
                        _logger.Info($"【git】目录已存在，执行增量拉取（不删除）：{localPath}");
                        return Pull(localPath, branch, checkCancel);
                    }
                    if (IsDirectoryEmpty(localPath))
                    {
                        _logger.Info($"【git】目录已存在但为空，执行克隆：{localPath}");
                        return CloneRepo(repoUrl, localPath, branch, checkCancel);
                    }
                    // 非空且不是仓库：按需求不删除，交由用户手动处理，避免误删用户数据。
                    _logger.Error($"【git】目录已存在，但既不是有效的 Git 仓库也不为空。按你的要求不会删除该目录，请手动检查或清空后重试：{localPath}");
                    return false;
                }

                // 不存在即“创建文件夹并克隆”。
                _logger.Info($"【git】目录不存在，创建并克隆：{localPath}");
                return CloneRepo(repoUrl, localPath, branch, checkCancel);
            }
            catch (Exception ex)
            {
                _logger.Error($"【git】clone/update fail: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 执行一次克隆：带下载/检出进度回调（解决“卡着没有动态更新”）与取消点（回调返回 false 即中止传输）。
        /// 不在 CloneOptions 里写死 BranchName：分支名写错（含大小写不符，GitLab/Git 区分大小写）会让整个 clone
        /// 直接失败、一行都拉不下来；改为先克隆拿到全部远程引用，再本地解析并检出目标分支（大小写不敏感回退）。
        /// 失败时不删除目录（按需求）；残留的半成品仓库下次点击会走 Pull 续传恢复。
        /// </summary>
        private bool CloneRepo(string repoUrl, string localPath, string branch, Func<bool> checkCancel)
        {
            // 确保父目录存在；目标目录交给 Clone 创建（Clone 要求目标不存在或为空目录）。
            var parent = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

            _logger.Info($"【git】cloning {repoUrl} -> {localPath} (branch:{branch})");
            ResetProgress();

            var cloneOptions = new CloneOptions { Checkout = true };
            cloneOptions.FetchOptions.CredentialsProvider = (url, usernameFromUrl, types) =>
                new UsernamePasswordCredentials
                {
                    Username = string.IsNullOrEmpty(_userName) ? "token" : _userName,
                    Password = _password ?? string.Empty
                };
            // 下载进度：既做“动态更新”反馈，又作为取消点——checkCancel 为真时返回 false 立即中止传输。
            cloneOptions.FetchOptions.OnTransferProgress = p =>
            {
                if (checkCancel != null && checkCancel()) { _logger.Warn("【git】已请求停止，正在中止克隆下载..."); return false; }
                ReportTransferProgress(p);
                return true;
            };
            cloneOptions.OnCheckoutProgress = (path, completed, total) => ReportCheckoutProgress(completed, total);

            try
            {
                Repository.Clone(repoUrl, localPath, cloneOptions);
            }
            catch (Exception cloneEx)
            {
                _logger.Error("【git】clone fail: " + EnhanceAuthError(cloneEx.Message));
                return false;
            }

            // 克隆成功后检出目标分支（大小写不敏感回退 + 设置上游跟踪）。
            using (var repo = new Repository(localPath))
            {
                if (!CheckoutTrackingBranch(repo, branch))
                {
                    _logger.Error($"【git】克隆完成但检出目标分支失败（branch:{branch}）；目录已保留，请修正分支名后重试");
                    return false;
                }
            }
            _logger.Info($"【git】clone success (branch:{branch})");
            return true;
        }

        /// <summary>
        /// 列举远程分支名（不克隆，通过 ls-remote 协议）。
        /// 返回如 main / develop / feature-x 的短分支名列表；失败时通过 error 输出原因。
        /// </summary>
        public System.Collections.Generic.List<string> ListRemoteBranches(string repoUrl, string userName, string password, out string error)
        {
            error = null;
            var branches = new System.Collections.Generic.List<string>();
            if (string.IsNullOrEmpty(repoUrl)) { error = "仓库地址为空"; return branches; }
            _userName = userName;
            _password = password;

            // 尝试 1：标准凭据回调方式
            if (TryListRemoteRefs(repoUrl, true, branches, out error))
            {
                branches.Sort(StringComparer.OrdinalIgnoreCase);
                _logger.Info($"【git】list remote branches ok: {branches.Count}");
                return branches;
            }

            // 尝试 2：把凭据内嵌进 URL 再 ls-remote。
            // 规避旧版 libgit2（LibGit2Sharp 0.26.x）在私有仓库 401 后再回调凭据时
            // 连接状态未标记、导致抛 "This remote has never connected" 的 bug。
            if (!string.IsNullOrEmpty(password))
            {
                branches.Clear();
                var embedded = EmbedCredentials(repoUrl, userName, password);
                if (TryListRemoteRefs(embedded, false, branches, out error))
                {
                    branches.Sort(StringComparer.OrdinalIgnoreCase);
                    _logger.Info($"【git】list remote branches ok (embedded auth): {branches.Count}");
                    return branches;
                }

                // 尝试 3：用户名改用 oauth2（GitLab 用 PAT 认证的标准占位用户名）。
                // 部分自建 GitLab 不接受邮箱/账号名作用户名，用 oauth2:<PAT> 才能通过。
                if (!string.Equals(userName, "oauth2", StringComparison.OrdinalIgnoreCase))
                {
                    branches.Clear();
                    var oauth2Url = EmbedCredentials(repoUrl, "oauth2", password);
                    if (TryListRemoteRefs(oauth2Url, false, branches, out error))
                    {
                        branches.Sort(StringComparer.OrdinalIgnoreCase);
                        _logger.Info($"【git】list remote branches ok (oauth2 auth): {branches.Count}");
                        return branches;
                    }
                }
            }

            error = EnhanceAuthError(error);
            _logger.Error("【git】list remote branches fail: " + error);
            return branches;
        }

        /// <summary>
        /// 把底层 HTTP 状态码错误翻译成可操作的中文诊断提示。
        /// </summary>
        private static string EnhanceAuthError(string error)
        {
            if (string.IsNullOrEmpty(error)) return error;
            if (error.Contains("403"))
                return error + "（403 Forbidden：凭据已送达服务器但被拒绝授权。请检查：PAT 是否勾选 read_repository 权限、是否已过期、该账号对此仓库是否有访问权限；GitLab 也可尝试把用户名留空由程序用 oauth2）";
            if (error.Contains("401"))
                return error + "（401 Unauthorized：用户名或 PAT 不正确，或开启 2FA 时密码位置必须填 PAT 而非登录密码）";
            return error;
        }

        /// <summary>
        /// 执行一次 ls-remote 并把 refs/heads/* 的短分支名收集进 branches。
        /// useHandler=true 时走凭据回调；false 时假定 URL 已内嵌凭据。
        /// 立即 ToList() 强制枚举，避免延迟求值引发的连接状态问题。
        /// </summary>
        private bool TryListRemoteRefs(string url, bool useHandler, System.Collections.Generic.List<string> branches, out string error)
        {
            error = null;
            try
            {
                var refs = useHandler
                    ? Repository.ListRemoteReferences(url, CredentialsProvider)
                    : Repository.ListRemoteReferences(url);
                foreach (var reference in refs.ToList())
                {
                    var canonical = reference.CanonicalName;
                    if (canonical != null && canonical.StartsWith("refs/heads/", StringComparison.Ordinal))
                    {
                        var name = canonical.Substring("refs/heads/".Length);
                        if (!string.IsNullOrEmpty(name) && !branches.Contains(name)) branches.Add(name);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 把用户名/密码(或 PAT)内嵌进 git URL：scheme://user:pass@rest。
        /// 用户名与密码均做 URL 转义（用户名含 @ 时必须转义为 %40）。
        /// 用户名为空时用 oauth2（GitLab PAT 的通用占位用户名）。
        /// </summary>
        private static string EmbedCredentials(string url, string user, string pass)
        {
            if (string.IsNullOrEmpty(pass)) return url;
            var idx = url.IndexOf("://", StringComparison.Ordinal);
            if (idx < 0) return url;
            var scheme = url.Substring(0, idx + 3);
            var rest = url.Substring(idx + 3);
            var u = Uri.EscapeDataString(string.IsNullOrEmpty(user) ? "oauth2" : user);
            var p = Uri.EscapeDataString(pass);
            return scheme + u + ":" + p + "@" + rest;
        }

        /// <summary>
        /// 测试 Git 连接与凭据是否有效（通过 ls-remote）。成功返回 true，失败通过 error 输出原因。
        /// </summary>
        public bool TestConnection(string repoUrl, string userName, string password, out string error)
        {
            var branches = ListRemoteBranches(repoUrl, userName, password, out error);
            if (error != null) return false;
            _logger.Info($"【git】connection ok, remote branches: {branches.Count}");
            return true;
        }

        /// <summary>
        /// 对已存在的本地仓库执行增量拉取：fetch → 检出目标分支（大小写不敏感回退）→ pull。
        /// 带传输进度回调（动态更新）与取消点（checkCancel 为真时返回 false 中止下载）。
        /// </summary>
        public bool Pull(string localPath, string branch, Func<bool> checkCancel = null)
        {
            try
            {
                ResetProgress();
                using (var repo = new Repository(localPath))
                {
                    var fetchOptions = new FetchOptions { CredentialsProvider = CredentialsProvider, Prune = true };
                    fetchOptions.OnTransferProgress = p =>
                    {
                        if (checkCancel != null && checkCancel()) { _logger.Warn("【git】已请求停止，正在中止拉取下载..."); return false; }
                        ReportTransferProgress(p);
                        return true;
                    };

                    // 先 fetch 一次，确保拿到最新的远程跟踪分支 origin/<branch>，
                    // 也覆盖"本地缺失 remote-tracking 引用"或"克隆被中断的半成品仓库"的情况（相当于续传恢复）。
                    _logger.Info("【git】fetching origin ...");
                    Commands.Fetch(repo, "origin", new[] { "+refs/heads/*:refs/remotes/origin/*" }, fetchOptions, "AntDeploy fetch");

                    // 解析并检出目标分支（大小写不敏感回退）+ 设置上游跟踪，
                    // 避免 Commands.Pull 抛 "There is no tracking information for the current branch"。
                    if (!CheckoutTrackingBranch(repo, branch)) return false;

                    var signature = new Signature("antdeploy", "antdeploy@email.com", DateTimeOffset.Now);
                    Commands.Pull(repo, signature, new PullOptions { FetchOptions = fetchOptions });
                    _logger.Info($"【git】pull success (branch:{branch})");
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"【git】pull fail: {ex.Message}");
                return false;
            }
        }

        // ---------------- 进度反馈（节流） ----------------

        private void ResetProgress()
        {
            _lastTransferPercent = -1;
            _lastCheckoutPercent = -1;
            _lastProgressLog = DateTime.MinValue;
        }

        /// <summary>下载进度：节流输出（每变化 &gt;=5% 或 &gt;=1 秒一行），避免高频回调刷屏并拖慢 Blazor 电路。</summary>
        private void ReportTransferProgress(TransferProgress p)
        {
            if (p == null || p.TotalObjects <= 0) return;
            var percent = (int)(p.ReceivedObjects * 100L / p.TotalObjects);
            var now = DateTime.Now;
            if (percent - _lastTransferPercent < 5 && (now - _lastProgressLog).TotalMilliseconds < 1000) return;
            _lastTransferPercent = percent;
            _lastProgressLog = now;
            _logger.Info($"【git】下载进度 {percent}%（{p.ReceivedObjects}/{p.TotalObjects} 对象，{p.ReceivedBytes / 1024} KB）");
        }

        /// <summary>检出进度：节流输出（每变化 &gt;=10% 一行，末尾 100% 必出）。</summary>
        private void ReportCheckoutProgress(int completed, int total)
        {
            if (total <= 0) return;
            var percent = (int)(completed * 100L / total);
            if (percent - _lastCheckoutPercent < 10 && percent < 100) return;
            _lastCheckoutPercent = percent;
            _logger.Info($"【git】检出进度 {percent}%（{completed}/{total}）");
        }

        /// <summary>
        /// 在已 fetch/clone 的仓库中解析并检出目标分支，并设置上游跟踪。
        /// GitLab/Git 分支名区分大小写，用户常把 master 写成 Master：
        /// 先精确匹配 origin/&lt;branch&gt;，失败再做大小写不敏感回退；
        /// 都找不到时输出含"远程可用分支清单"的清晰错误。成功返回 true。
        /// </summary>
        private bool CheckoutTrackingBranch(Repository repo, string branch)
        {
            // 关键：从"实际枚举"的远程分支里解析，而不是用 repo.Branches["origin/"+branch] 索引器。
            // Windows 文件系统大小写不敏感，索引器会把 "origin/Master" 命中到真实的 "origin/master"，
            // 但其 CanonicalName 却按请求的大写返回，导致建立错误大小写的上游跟踪引用
            // （refs/heads/Master），后续 pull 报 "reference was not fetched"。
            var remoteBranches = repo.Branches
                .Where(b => b.IsRemote && !b.FriendlyName.EndsWith("/HEAD", StringComparison.Ordinal))
                .ToList();

            // 先精确匹配（区分大小写），再大小写不敏感回退；均基于真实枚举名，杜绝索引器的大小写陷阱。
            var remoteBranch = remoteBranches.FirstOrDefault(b => b.FriendlyName == "origin/" + branch)
                ?? remoteBranches.FirstOrDefault(b => string.Equals(b.FriendlyName, "origin/" + branch, StringComparison.OrdinalIgnoreCase));

            if (remoteBranch == null)
            {
                var available = string.Join(", ", remoteBranches
                    .Select(b => b.FriendlyName.StartsWith("origin/") ? b.FriendlyName.Substring("origin/".Length) : b.FriendlyName)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
                _logger.Error($"【git】无法定位分支 '{branch}'。远程可用分支：[{available}]。请修正分支名（GitLab 区分大小写）");
                return false;
            }

            if (!string.Equals(remoteBranch.FriendlyName, "origin/" + branch, StringComparison.Ordinal))
                _logger.Warn($"【git】未找到远程分支 'origin/{branch}'，按大小写不敏感匹配到 '{remoteBranch.FriendlyName}'");

            // 用远程分支的"真实"名字（大小写正确）作为本地分支名与上游跟踪引用，
            // 而不是沿用用户可能写错大小写的 branch。
            var resolvedName = remoteBranch.CanonicalName != null && remoteBranch.CanonicalName.StartsWith("refs/remotes/origin/")
                ? remoteBranch.CanonicalName.Substring("refs/remotes/origin/".Length)
                : (remoteBranch.FriendlyName.StartsWith("origin/") ? remoteBranch.FriendlyName.Substring("origin/".Length) : remoteBranch.FriendlyName);

            var localBranch = repo.Branches.FirstOrDefault(b => !b.IsRemote && b.FriendlyName == resolvedName);
            if (localBranch == null)
                localBranch = repo.CreateBranch(resolvedName, remoteBranch.Tip);

            // 设置上游跟踪（用局部变量固化，避免闭包捕获问题）
            var trackedCanonical = remoteBranch.CanonicalName;
            if (localBranch.TrackedBranch == null || localBranch.TrackedBranch.CanonicalName != trackedCanonical)
                repo.Branches.Update(localBranch, b => b.TrackedBranch = trackedCanonical);

            Commands.Checkout(repo, localBranch);
            return true;
        }

        /// <summary>
        /// 判断 localPath "本身"是否为本地 Git 仓库（可安全地增量拉取 / 续传恢复）：
        /// ① .git/HEAD 必须存在（排除无 .git 的普通目录，同时避免打开时向上发现父仓库）；
        /// ② 打开后工作树根必须就是 localPath 本身（再次防 LibGit2Sharp 向上发现到父仓库，
        ///    workspace 目录位于 AntDeploy 源码仓库工作树内时尤其危险）。
        /// 不再要求"已有提交"：克隆被中断的半成品仓库也据此走 Pull 续传恢复，从而无需删除目录。
        /// </summary>
        private bool IsLocalGitRepo(string localPath)
        {
            try
            {
                // ① 无 .git/HEAD 直接排除——同时避免打开时向上发现父仓库
                if (!File.Exists(Path.Combine(localPath, ".git", "HEAD"))) return false;

                using (var repo = new Repository(localPath))
                {
                    // ② 防向上发现：工作树根必须等于 localPath 本身
                    var workDir = repo.Info.WorkingDirectory;
                    if (string.IsNullOrEmpty(workDir)) return false;   // bare 仓库等异常情形
                    if (!string.Equals(NormalizeDir(workDir), NormalizeDir(localPath), StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.Warn($"【git】目录 {localPath} 被发现为父仓库 {workDir}，判定不可用（避免误操作父仓库）");
                        return false;
                    }
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>目录是否为空（无任何文件/子目录）。用于"已存在的空目录可直接克隆进去"。</summary>
        private static bool IsDirectoryEmpty(string path)
        {
            try { return !Directory.EnumerateFileSystemEntries(path).Any(); }
            catch { return false; }
        }

        /// <summary>规范化目录路径：转绝对路径并去掉结尾分隔符，便于比较。</summary>
        private static string NormalizeDir(string path)
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        public void Dispose()
        {
        }
    }
}
