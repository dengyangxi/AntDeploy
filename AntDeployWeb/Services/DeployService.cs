using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AntDeploy.Core;
using AntDeploy.Core.Models;
using AntDeploy.Core.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AntDeployWeb.Services
{
    /// <summary>
    /// 部署编排服务（每个 Blazor 电路一个实例）。
    /// 串联：git 拉取 → 本地编译(msbuild/dotnet) → 打包 zip（全量/增量/选择文件）→
    /// 上传 Agent(HTTP) 或 SSH 直连 Docker 部署 → 实时日志；并支持 jib 镜像构建、回滚、历史、服务器测试。
    /// 所有 UI 实时输出通过 onLog 回调返回，页面据此刷新日志面板。
    /// </summary>
    public class DeployService
    {
        private readonly IConfigStore _store;

        public DeployService(IConfigStore store)
        {
            _store = store;
        }

        private static ILog Log(Action<string> onLog) => new ActionLog((lvl, msg) => onLog?.Invoke($"[{lvl}] {msg}"));

        /// <summary>测试 Git 连接与凭据是否有效（含二要素场景：密码位置填 Personal Access Token）</summary>
        public bool TestGitConnection(DeployProfile profile, out string error)
        {
            using (var git = new GitClient(new NullLog()))
            {
                return git.TestConnection(profile.Git.RepositoryUrl, profile.Git.UserName, profile.Git.Password, out error);
            }
        }

        /// <summary>列举远程分支</summary>
        public List<string> ListBranches(DeployProfile profile, out string error)
        {
            using (var git = new GitClient(new NullLog()))
            {
                return git.ListRemoteBranches(profile.Git.RepositoryUrl, profile.Git.UserName, profile.Git.Password, out error);
            }
        }

        /// <summary>按仓库地址+凭据列出全部远程分支（供分支选择器复用，忽略 out error，失败返回空列表）。</summary>
        public List<string> ListBranches(string repositoryUrl, string userName, string password)
        {
            if (string.IsNullOrWhiteSpace(repositoryUrl)) return new List<string>();
            using (var git = new GitClient(new NullLog()))
            {
                return git.ListRemoteBranches(repositoryUrl, userName, password, out _) ?? new List<string>();
            }
        }

        /// <summary>拉取代码：本地已有仓库则增量 pull，不存在则创建并 clone（绝不删除已存在目录）。checkCancel 用于中止下载。branchOverride 非空时优先使用该分支（一键发布用）。</summary>
        public Task<bool> PullAsync(DeployProfile profile, Action<string> onLog, Func<bool> checkCancel = null, string branchOverride = null)
        {
            return Task.Run(() =>
            {
                var localPath = _store.GetProjectPath(profile);
                var log = Log(onLog);
                if (profile.Git.UseLocalPath)
                {
                    log.Info($"【git】使用本地目录，不执行拉取：{localPath}");
                    return Directory.Exists(localPath);
                }
                var branch = string.IsNullOrWhiteSpace(branchOverride) ? profile.Git.Branch : branchOverride;
                log.Info($"【git】目标本地目录：{localPath}，分支：{branch}");
                using (var git = new GitClient(log))
                {
                    return git.CloneOrUpdate(profile.Git.RepositoryUrl, localPath, branch,
                        profile.Git.UserName, profile.Git.Password, checkCancel);
                }
            });
        }

        /// <summary>编译发布：netcore 走 dotnet publish，framework 走 msbuild。</summary>
        public Task<bool> BuildAsync(DeployProfile profile, Action<string> onLog, Func<bool> checkCancel = null)
        {
            return Task.Run(() =>
            {
                var log = Log(onLog);
                try
                {
                    var projectPath = _store.GetProjectPath(profile);
                    var publishPath = _store.GetPublishPath(profile);
                    Directory.CreateDirectory(publishPath);

                    var settings = _store.GetSettings();
                    if (!string.IsNullOrWhiteSpace(settings.MsBuildPath))
                        CommandHelper.MsBuildPath = settings.MsBuildPath;

                    var build = profile.Build ?? new BuildConfig();
                    var projFile = ResolveProjectFile(projectPath, build, profile.Name, log);
                    if (projFile == null)
                    {
                        log.Error($"【build】在 {projectPath} 下未找到 .csproj 工程文件");
                        return false;
                    }

                    log.Info($"【build】开始编译：{projFile} -> {publishPath}");
                    bool ok;
                    if (string.Equals(build.SdkType, "framework", StringComparison.OrdinalIgnoreCase))
                    {
                        ok = CommandHelper.RunMsbuild(projFile, publishPath, log, build.IsWeb, checkCancel);
                    }
                    else
                    {
                        var extra = BuildDotnetArgs(build) + (profile.Plugin?.GetNetCorePublishRuntimeArg() ?? string.Empty);
                        ok = CommandHelper.RunDotnetExe(projFile, publishPath, extra, log, checkCancel);
                    }
                    log.Info(ok ? "【build】编译成功" : "【build】编译失败");
                    return ok;
                }
                catch (Exception ex)
                {
                    // 兜底：任何未预期异常都转为失败结果并写入日志，绝不让其冒泡打断 Blazor 电路
                    log.Error($"【build】编译过程发生未预期异常：{ex.GetType().Name}: {ex.Message}");
                    return false;
                }
            });
        }

        /// <summary>
        /// 列出仓库下所有可作为发布目标的项目/解决方案文件（返回相对仓库根、以 / 分隔的路径），
        /// 排除 obj/bin/packages/.git/.vs/node_modules。供 UI “项目文件” 下拉选择。
        /// </summary>
        public List<string> ListProjectFiles(DeployProfile profile)
        {
            var result = new List<string>();
            try
            {
                var root = _store.GetProjectPath(profile);
                if (!Directory.Exists(root)) return result;
                var prefix = root.TrimEnd('\\', '/') + "\\";
                result = Directory.GetFiles(root, "*.csproj", SearchOption.AllDirectories)
                    .Concat(Directory.GetFiles(root, "*.sln", SearchOption.AllDirectories))
                    .Where(f => !IsJunkPath(f))
                    .Select(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? f.Substring(prefix.Length) : f)
                    .Select(f => f.Replace('\\', '/'))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch { }
            return result;
        }

        /// <summary>
        /// 发布：打包 publish 目录“一次”，再把同一个包上传到选中的所有 Agent 服务器（HTTP 协议）。
        /// 打包只做一次很关键：增量/选择文件模式打包后会提交本地增量基线，若每台服务器各打一次，
        /// 第二台起将算出“无变更”而上传空包。
        /// </summary>
        public async Task<DeployResult> DeployAsync(DeployProfile profile, Env env, List<Server> servers, PublishType type,
            Action<string> onLog, string remark = "", List<string> selectedFiles = null, Func<bool> checkCancel = null,
            CancellationToken cancellationToken = default, DeployRunOptions run = null, Action<DeployPhaseTimings> onTimings = null)
        {
            var log = Log(onLog);
            if (servers == null || servers.Count == 0)
                return new DeployResult { Success = false, Msg = "no server selected" };

            // IIS 发布必填项预检：站点名(WebSiteName)为空时，即便上传成功，Agent 端 IIsPublisher 也无法定位/创建站点，
            // 会在解压后才失败。这里在打包/上传上百 MB 之前“快速失败”，避免用户长时间等待后才看到报错。
            if (type == PublishType.iis && string.IsNullOrWhiteSpace(profile.Iis?.WebSiteName))
            {
                log.Error("【deploy】IIS 发布缺少必填项「网站名称(WebSiteName)」，请先在部署配置中填写站点名后再发布");
                return new DeployResult { Success = false, Msg = "IIS WebSiteName is required" };
            }

            var publishPath = _store.GetPublishPath(profile);
            if (!Directory.Exists(publishPath))
            {
                log.Error($"【deploy】发布目录不存在，请先编译：{publishPath}");
                return new DeployResult { Success = false, Msg = "publish folder not found" };
            }

            byte[] zip;
            var pkgSw = Stopwatch.StartNew();
            try
            {
                zip = PackageCore(profile, env, type, selectedFiles, log);
            }
            catch (Exception ex)
            {
                log.Error("【deploy】打包失败：" + ex.Message);
                return new DeployResult { Success = false, Msg = ex.Message };
            }
            pkgSw.Stop();

            log.Info($"【deploy】打包完成，大小 {zip.Length / 1024} KB，将部署到 {servers.Count} 台服务器");

            // 打包一次后交给统一编排（原子 / 两阶段），复用 UploadAndDeployAsync；
            // 结果映射回 DeployResult，PackageMs 用本地打包耗时补上（UploadAndDeployAsync 不负责打包）。
            var ctx = new QuickDeployContext { Profile = profile, Env = env, Type = type, Remark = remark, Zip = zip };
            var outcome = await UploadAndDeployAsync(ctx, servers, onLog, null, checkCancel, cancellationToken, run,
                t => onTimings?.Invoke(new DeployPhaseTimings
                {
                    PackageMs = pkgSw.ElapsedMilliseconds,
                    UploadMs = t.UploadMs,
                    DeployMs = t.DeployMs
                }));
            return new DeployResult
            {
                Success = outcome.Success,
                Msg = outcome.Cancelled ? "cancelled" : (outcome.Success ? "ok" : outcome.Message)
            };
        }

        // ---------------- 一键发布（项目列表页）：拉取 → 编译 → 打包 → 上传部署，带阶段进度 ----------------

        /// <summary>
        /// 一键发布：默认拉取所选分支最新代码 → 本地编译 → 打包一次 → 循环上传部署到选中环境的所有 Agent 服务器。
        /// 通过 onProgress 回推阶段进度（pull/build/package/upload/deploy），供项目列表行内进度条实时刷新。
        /// 打包只做一次（见 PackageCore 副作用说明），失败服务器可由 RetryDeployAsync 复用同一个包重试。
        /// </summary>
        public async Task<QuickDeployOutcome> QuickDeployAsync(DeployProfile profile, Env env, List<Server> servers,
            PublishType type, string branch, string remark, Action<string> onLog, Action<DeployProgress> onProgress,
            Func<bool> checkCancel = null, CancellationToken cancellationToken = default, DeployRunOptions run = null,
            Action<DeployPhaseTimings> onTimings = null)
        {
            var log = Log(onLog);
            var outcome = new QuickDeployOutcome();
            var timings = new DeployPhaseTimings();

            if (servers == null || servers.Count == 0)
            {
                outcome.Message = "当前环境没有可发布的 Agent 服务器";
                onProgress?.Invoke(DeployProgress.Fail(outcome.Message));
                return outcome;
            }

            // IIS 必填项预检：与 DeployAsync 一致，打包/上传前快速失败
            if (type == PublishType.iis && string.IsNullOrWhiteSpace(profile.Iis?.WebSiteName))
            {
                outcome.Message = "IIS 发布缺少必填项「网站名称(WebSiteName)」，请先到项目详情页填写";
                log.Error("【deploy】" + outcome.Message);
                onProgress?.Invoke(DeployProgress.Fail(outcome.Message));
                return outcome;
            }

            try
            {
                // ① 拉取所选分支最新代码
                onProgress?.Invoke(new DeployProgress { Phase = "pull", Percent = -1, Text = "Pull 拉取代码中" });
                var pullSw = Stopwatch.StartNew();
                var pulled = await PullAsync(profile, onLog, checkCancel, branch);
                pullSw.Stop();
                timings.PullMs = pullSw.ElapsedMilliseconds;
                if (!pulled)
                {
                    outcome.Message = "拉取代码失败";
                    onProgress?.Invoke(DeployProgress.Fail(outcome.Message));
                    return outcome;
                }
                if (checkCancel != null && checkCancel()) { outcome.Cancelled = true; outcome.Message = "已取消"; onProgress?.Invoke(DeployProgress.Fail("已取消")); return outcome; }

                // ② 编译发布
                onProgress?.Invoke(new DeployProgress { Phase = "build", Percent = -1, Text = "Build 编译中" });
                var buildSw = Stopwatch.StartNew();
                var built = await BuildAsync(profile, onLog, checkCancel);
                buildSw.Stop();
                timings.BuildMs = buildSw.ElapsedMilliseconds;
                if (!built)
                {
                    outcome.Message = "编译失败";
                    onProgress?.Invoke(DeployProgress.Fail(outcome.Message));
                    return outcome;
                }
                if (checkCancel != null && checkCancel()) { outcome.Cancelled = true; outcome.Message = "已取消"; onProgress?.Invoke(DeployProgress.Fail("已取消")); return outcome; }

                // ③ 打包（只做一次）
                var publishPath = _store.GetPublishPath(profile);
                if (!Directory.Exists(publishPath))
                {
                    outcome.Message = $"发布目录不存在：{publishPath}";
                    log.Error("【deploy】" + outcome.Message);
                    onProgress?.Invoke(DeployProgress.Fail(outcome.Message));
                    return outcome;
                }
                onProgress?.Invoke(new DeployProgress { Phase = "package", Percent = -1, Text = "Package 打包中" });
                byte[] zip;
                var pkgSw = Stopwatch.StartNew();
                try
                {
                    zip = PackageCore(profile, env, type, null, log);
                }
                catch (Exception ex)
                {
                    outcome.Message = "打包失败：" + ex.Message;
                    log.Error("【deploy】" + outcome.Message);
                    onProgress?.Invoke(DeployProgress.Fail(outcome.Message));
                    return outcome;
                }
                pkgSw.Stop();
                timings.PackageMs = pkgSw.ElapsedMilliseconds;
                onProgress?.Invoke(new DeployProgress { Phase = "package", Percent = 100, Text = "Package 打包完成" });
                log.Info($"【deploy】打包完成，大小 {zip.Length / 1024} KB");

                // ④ 上传部署到所有服务器（受限并行，maxParallel<=1 时等价顺序执行）
                var ctx = new QuickDeployContext { Profile = profile, Env = env, Type = type, Remark = remark, Zip = zip };
                var result = await UploadAndDeployAsync(ctx, servers, onLog, onProgress, checkCancel, cancellationToken, run);
                result.Timings.PullMs = timings.PullMs;
                result.Timings.BuildMs = timings.BuildMs;
                result.Timings.PackageMs = timings.PackageMs;
                onTimings?.Invoke(result.Timings);
                return result;
            }
            catch (Exception ex)
            {
                outcome.Message = $"发布异常：{ex.GetType().Name}: {ex.Message}";
                log.Error("【deploy】" + outcome.Message);
                onProgress?.Invoke(DeployProgress.Fail(outcome.Message));
                return outcome;
            }
        }

        /// <summary>
        /// 失败重试：复用上一次已打好的包（QuickDeployContext.Zip），仅重新上传部署到指定的失败服务器，
        /// 不重新拉取/编译/打包（避免增量模式重打包得到空包）。
        /// </summary>
        public Task<QuickDeployOutcome> RetryDeployAsync(QuickDeployContext ctx, List<Server> servers,
            Action<string> onLog, Action<DeployProgress> onProgress, Func<bool> checkCancel = null, CancellationToken cancellationToken = default,
            DeployRunOptions run = null, Action<DeployPhaseTimings> onTimings = null)
        {
            if (ctx == null || ctx.Zip == null)
            {
                var empty = new QuickDeployOutcome { Message = "没有可复用的发布包，请重新发布" };
                onProgress?.Invoke(DeployProgress.Fail(empty.Message));
                return Task.FromResult(empty);
            }
            return UploadAndDeployAsync(ctx, servers, onLog, onProgress, checkCancel, cancellationToken, run, onTimings);
        }

        /// <summary>
        /// 把已打好的包上传部署到每台服务器（SemaphoreSlim 受限并行，maxParallel&lt;=1 时等价顺序执行），
        /// 聚合每台结果（成功/失败/部分成功），回推 upload/deploy 阶段进度，并记录分段耗时（wall-clock）。
        /// </summary>
        private async Task<QuickDeployOutcome> UploadAndDeployAsync(QuickDeployContext ctx, List<Server> servers,
            Action<string> onLog, Action<DeployProgress> onProgress, Func<bool> checkCancel, CancellationToken cancellationToken,
            DeployRunOptions run = null, Action<DeployPhaseTimings> onTimings = null)
        {
            var log = Log(onLog);
            var outcome = new QuickDeployOutcome { Context = ctx };
            if (servers == null || servers.Count == 0)
            {
                outcome.Message = "没有可发布的服务器";
                onProgress?.Invoke(DeployProgress.Fail(outcome.Message));
                return outcome;
            }

            run = run ?? new DeployRunOptions();
            var folderName = DateTime.Now.ToString("yyyyMMddHHmmss");
            var extra = BuildExtraFields(ctx.Profile, ctx.Env, ctx.Type, ctx.Remark, folderName, ctx.Zip.Length, run);

            // 两阶段：先把包并行上传解压到所有服务器（无害），再逐台部署（默认滚动，有害部分串行）
            if (run.TwoPhase)
                return await TwoPhaseDeployAsync(ctx, servers, extra, onLog, onProgress, checkCancel, cancellationToken, run, onTimings);

            var total = servers.Count;
            var parallelism = run.AtomicParallel;
            var completed = 0;      // 已完成台数（Interlocked 递增）
            var upload100 = 0;      // 上传达 100% 的台数（用于 T1 打点）
            var t1Ms = 0L;          // 全部台上传达 100% 的时刻（相对 T0，毫秒）；0 表示未达成
            var cancelled = false;
            var results = new HostOutcome[total];
            var reached100 = new bool[total]; // 每台仅记一次“上传达 100%”，槽位互不重叠、线程安全
            log.Info($"【deploy】开始上传部署 {total} 台服务器（并发 {parallelism}）");

            var sw = Stopwatch.StartNew();
            using (var gate = new SemaphoreSlim(parallelism))
            {
                var tasks = servers.Select((server, i) => Task.Run(async () =>
                {
                    var idx = i;
                    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        if (checkCancel != null && checkCancel() || cancellationToken.IsCancellationRequested)
                        {
                            Interlocked.CompareExchange(ref cancelled, true, false);
                            return;
                        }
                        var baseCompleted = completed; // 启动时已完成台数，作为本台整体进度的基数（近似）
                        var display = string.IsNullOrWhiteSpace(server.NickName) ? server.Host : $"{server.NickName}({server.Host})";
                        log.Info($"【deploy】===== 上传到 {server.Host} ({idx + 1}/{total}) =====");
                        onProgress?.Invoke(new DeployProgress
                        {
                            Phase = "upload",
                            Percent = (int)(baseCompleted * 100.0 / total),
                            ServerIndex = idx + 1,
                            ServerCount = total,
                            Text = $"Upload 上传中 ({idx + 1}/{total}) {display}",
                            ServerHost = server.Host,
                            ServerDisplay = display,
                            ServerUploadPercent = 0,
                            ServerStepText = "上传中 0%"
                        });

                        var loggerId = Guid.NewGuid().ToString("N");
                        DeployResult r;
                        using (var ms = new MemoryStream(ctx.Zip))
                        {
                            var client = new DeployClient(log);
                            r = await client.PublishAsync(server.Host, server.Token, ctx.Type, extra, ms, loggerId,
                                m =>
                                {
                                    onLog?.Invoke(m);
                                    if (ReportUploadProgress(m, idx + 1, total, display, baseCompleted, onProgress, server.Host)
                                        && !reached100[idx])
                                    {
                                        reached100[idx] = true; // 每台仅计一次，避免重复报 100% 干扰 T1
                                        if (Interlocked.Increment(ref upload100) == total)
                                        {
                                            // T1：全部台上传达 100%
                                            Interlocked.CompareExchange(ref t1Ms, sw.ElapsedMilliseconds, 0);
                                        }
                                    }
                                }, cancellationToken).ConfigureAwait(false);
                        }

                        results[idx] = new HostOutcome { Host = server.Host, Display = display, Success = r.Success, Msg = r.Msg };
                        var done = Interlocked.Increment(ref completed);
                        if (r.Success)
                        {
                            log.Info($"【deploy】{server.Host} 部署成功");
                            onProgress?.Invoke(new DeployProgress
                            {
                                Phase = "deploy",
                                Percent = (int)(done * 100.0 / total),
                                ServerIndex = done,
                                ServerCount = total,
                                Text = $"Deploy 部署中 ({done}/{total}) {display} 完成",
                                ServerHost = server.Host,
                                ServerDisplay = display,
                                ServerUploadPercent = 100,
                                ServerStepText = "完成"
                            });
                        }
                        else
                        {
                            log.Error($"【deploy】{server.Host} 部署失败：{r.Msg}");
                            onProgress?.Invoke(new DeployProgress
                            {
                                Phase = "deploy",
                                Percent = (int)(done * 100.0 / total),
                                ServerIndex = done,
                                ServerCount = total,
                                Text = $"Deploy ({done}/{total}) {display} 失败",
                                ServerHost = server.Host,
                                ServerDisplay = display,
                                ServerUploadPercent = 100,
                                ServerStepText = "失败：" + (r.Msg ?? "未知错误")
                            });
                        }
                    }
                    finally { gate.Release(); }
                })).ToList();
                await Task.WhenAll(tasks);
            }
            sw.Stop();

            // 按 servers 原始顺序收集结果，保持列表展示顺序稳定（取消时未启动的台为 null）
            foreach (var h in results.Where(h => h != null)) outcome.Hosts.Add(h);

            // 分段耗时：UploadMs = T1-T0（全部字节发出），DeployMs = T2-T1（全部站点更新完成）；T1 未达成时整段计入上传
            var uploadMs = t1Ms > 0 ? t1Ms : sw.ElapsedMilliseconds;
            outcome.Timings.UploadMs = uploadMs;
            outcome.Timings.DeployMs = Math.Max(0, sw.ElapsedMilliseconds - uploadMs);
            onTimings?.Invoke(outcome.Timings);

            outcome.Cancelled = cancelled;
            var okCount = outcome.Hosts.Count(h => h.Success);
            var attempted = outcome.Hosts.Count;
            outcome.SuccessCount = okCount;
            outcome.FailCount = attempted - okCount;
            outcome.Success = !cancelled && attempted == total && okCount == total && total > 0;
            outcome.Partial = okCount > 0 && okCount < total;
            if (outcome.Success) outcome.Message = $"发布成功（{okCount}/{total} 台）";
            else if (cancelled) outcome.Message = "已取消";
            else if (outcome.Partial) outcome.Message = $"部分成功：成功 {okCount} 台，失败 {total - okCount} 台";
            else outcome.Message = $"发布失败（0/{total} 台成功）";
            return outcome;
        }

        /// <summary>
        /// 两阶段发布：阶段1 把同一个包并行上传解压到所有服务器（deployPhase=upload，站点保持运行、无害）；
        /// 阶段2 逐台触发部署（deployPhase=deploy，不带 zip，Agent 用 deployFolderName 定位已解压目录）。
        /// 回调（下/上负载）与 drainWait 在阶段2 由 Agent 执行；默认 DeployParallel=1 即滚动逐台，做到零掉包。
        /// 严守"打包一次"不变式：两阶段复用同一 byte[] zip（阶段2 不再传 zip）。进度映射：阶段1→0-50%，阶段2→50-100%。
        /// </summary>
        private async Task<QuickDeployOutcome> TwoPhaseDeployAsync(QuickDeployContext ctx, List<Server> servers,
            Dictionary<string, string> baseExtra,
            Action<string> onLog, Action<DeployProgress> onProgress, Func<bool> checkCancel, CancellationToken cancellationToken,
            DeployRunOptions run, Action<DeployPhaseTimings> onTimings = null)
        {
            var log = Log(onLog);
            var outcome = new QuickDeployOutcome { Context = ctx };
            var total = servers.Count;
            var results = new HostOutcome[total];
            var uploadOk = new bool[total];
            var cancelled = false;

            // ---- 阶段1：上传解压（并行 UploadParallel，deployPhase=upload，带 zip，不触发回调/不动站点）----
            var uploadExtra = new Dictionary<string, string>(baseExtra) { ["deployPhase"] = "upload" };
            var uploadParallelism = Math.Max(1, run.UploadParallel);
            var uploadDone = 0;
            log.Info($"【twophase】阶段1：上传解压到 {total} 台服务器（并发 {uploadParallelism}），此阶段站点保持运行");
            onProgress?.Invoke(new DeployProgress { Phase = "upload", Percent = 0, ServerCount = total, Text = $"Upload 上传中 (0/{total})" });

            var upSw = Stopwatch.StartNew();
            using (var gate = new SemaphoreSlim(uploadParallelism))
            {
                var tasks = servers.Select((server, i) => Task.Run(async () =>
                {
                    var idx = i;
                    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        if (checkCancel != null && checkCancel() || cancellationToken.IsCancellationRequested)
                        {
                            Interlocked.CompareExchange(ref cancelled, true, false);
                            return;
                        }
                        var display = string.IsNullOrWhiteSpace(server.NickName) ? server.Host : $"{server.NickName}({server.Host})";
                        var baseDone = uploadDone; // 启动时已完成台数，作为本台上传进度的基数（近似）
                        log.Info($"【twophase】===== 上传到 {server.Host} ({idx + 1}/{total}) =====");
                        var loggerId = Guid.NewGuid().ToString("N");
                        DeployResult r;
                        using (var ms = new MemoryStream(ctx.Zip))
                        {
                            var client = new DeployClient(log);
                            r = await client.PublishAsync(server.Host, server.Token, ctx.Type, uploadExtra, ms, loggerId,
                                m =>
                                {
                                    onLog?.Invoke(m);
                                    var mm = Regex.Match(m ?? string.Empty, @"上传进度\s*(\d+)%");
                                    if (mm.Success && int.TryParse(mm.Groups[1].Value, out var pct))
                                    {
                                        if (pct < 0) pct = 0; if (pct > 100) pct = 100;
                                        var overall = (int)((baseDone + pct / 100.0) * 50.0 / total); // 阶段1 映射到 0-50%
                                        if (overall > 50) overall = 50;
                                        onProgress?.Invoke(new DeployProgress { Phase = "upload", Percent = overall, ServerIndex = idx + 1, ServerCount = total, Text = $"Upload 上传中 ({idx + 1}/{total}) {display} {pct}%", ServerHost = server.Host, ServerDisplay = display, ServerUploadPercent = pct, ServerStepText = $"上传中 {pct}%" });
                                    }
                                }, cancellationToken).ConfigureAwait(false);
                        }
                        uploadOk[idx] = r.Success;
                        if (r.Success) log.Info($"【twophase】{server.Host} 上传解压完成");
                        else log.Error($"【twophase】{server.Host} 上传失败：{r.Msg}");
                        Interlocked.Increment(ref uploadDone);
                    }
                    finally { gate.Release(); }
                })).ToList();
                await Task.WhenAll(tasks);
            }
            upSw.Stop();
            outcome.Timings.UploadMs = upSw.ElapsedMilliseconds;

            if (cancelled)
            {
                log.Warn("【twophase】上传阶段已取消，未启动的服务器被跳过，不进入部署阶段");
                outcome.Cancelled = true;
                outcome.Message = "已取消";
                onProgress?.Invoke(DeployProgress.Fail("已取消"));
                onTimings?.Invoke(outcome.Timings);
                return outcome;
            }

            // ---- 阶段2：逐台部署（DeployParallel，默认1=滚动；deployPhase=deploy，不带 zip，回调+drainWait 由 Agent 执行）----
            var deployExtra = new Dictionary<string, string>(baseExtra) { ["deployPhase"] = "deploy" };
            var deployParallelism = Math.Max(1, run.DeployParallel);
            var deployDone = 0;
            var uploadedCount = uploadOk.Count(b => b);
            log.Info($"【twophase】阶段2：部署 {uploadedCount}/{total} 台（并发 {deployParallelism}{(deployParallelism == 1 ? "，滚动逐台" : "")}）");
            onProgress?.Invoke(new DeployProgress { Phase = "deploy", Percent = 50, ServerCount = total, Text = $"Deploy 部署中 (0/{total})" });

            var dpSw = Stopwatch.StartNew();
            using (var gate = new SemaphoreSlim(deployParallelism))
            {
                var tasks = servers.Select((server, i) => Task.Run(async () =>
                {
                    var idx = i;
                    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        if (checkCancel != null && checkCancel() || cancellationToken.IsCancellationRequested)
                        {
                            Interlocked.CompareExchange(ref cancelled, true, false);
                            return;
                        }
                        var display = string.IsNullOrWhiteSpace(server.NickName) ? server.Host : $"{server.NickName}({server.Host})";
                        if (!uploadOk[idx])
                        {
                            // 上传阶段失败的台，部署阶段直接跳过并记为失败（避免对未就绪目录执行部署）
                            results[idx] = new HostOutcome { Host = server.Host, Display = display, Success = false, Msg = "上传阶段失败，已跳过部署" };
                            var skipped = Interlocked.Increment(ref deployDone);
                            log.Error($"【twophase】{server.Host} 上传阶段失败，跳过部署");
                            onProgress?.Invoke(new DeployProgress { Phase = "deploy", Percent = (int)(50 + skipped * 50.0 / total), ServerIndex = skipped, ServerCount = total, Text = $"Deploy 部署中 ({skipped}/{total}) {display} 跳过", ServerHost = server.Host, ServerDisplay = display, ServerUploadPercent = 100, ServerStepText = "失败：上传阶段失败" });
                            return;
                        }
                        log.Info($"【twophase】===== 部署 {server.Host} ({idx + 1}/{total}) =====");
                        var loggerId = Guid.NewGuid().ToString("N");
                        var client = new DeployClient(log);
                        var r = await client.DeployCommitAsync(server.Host, server.Token, ctx.Type, deployExtra, loggerId,
                            m => onLog?.Invoke(m), cancellationToken).ConfigureAwait(false);
                        results[idx] = new HostOutcome { Host = server.Host, Display = display, Success = r.Success, Msg = r.Msg };
                        var done = Interlocked.Increment(ref deployDone);
                        var overall = (int)(50 + done * 50.0 / total); // 阶段2 映射到 50-100%
                        if (overall > 100) overall = 100;
                        if (r.Success) log.Info($"【twophase】{server.Host} 部署成功");
                        else log.Error($"【twophase】{server.Host} 部署失败：{r.Msg}");
                        onProgress?.Invoke(new DeployProgress { Phase = "deploy", Percent = overall, ServerIndex = done, ServerCount = total, Text = $"Deploy 部署中 ({done}/{total}) {display} {(r.Success ? "完成" : "失败")}", ServerHost = server.Host, ServerDisplay = display, ServerUploadPercent = 100, ServerStepText = r.Success ? "完成" : "失败：" + (r.Msg ?? "未知错误") });
                    }
                    finally { gate.Release(); }
                })).ToList();
                await Task.WhenAll(tasks);
            }
            dpSw.Stop();
            outcome.Timings.DeployMs = dpSw.ElapsedMilliseconds;
            onTimings?.Invoke(outcome.Timings);

            // 按 servers 原始顺序收集结果，保持列表展示顺序稳定（取消时未启动的台为 null）
            foreach (var h in results.Where(h => h != null)) outcome.Hosts.Add(h);
            outcome.Cancelled = cancelled;
            var okCount = outcome.Hosts.Count(h => h.Success);
            var attempted = outcome.Hosts.Count;
            outcome.SuccessCount = okCount;
            outcome.FailCount = attempted - okCount;
            outcome.Success = !cancelled && attempted == total && okCount == total && total > 0;
            outcome.Partial = okCount > 0 && okCount < total;
            if (outcome.Success) outcome.Message = $"发布成功（{okCount}/{total} 台）";
            else if (cancelled) outcome.Message = "已取消";
            else if (outcome.Partial) outcome.Message = $"部分成功：成功 {okCount} 台，失败 {total - okCount} 台";
            else outcome.Message = $"发布失败（0/{total} 台成功）";
            return outcome;
        }

        /// <summary>
        /// 解析 DeployClient 上传进度日志（"上传进度 X%"），换算为跨服务器的整体百分比后回推。
        /// 返回本条日志是否为"上传达 100%"事件（并行下由调用方做 T1 打点计数，去重靠日志每到达 100% 只报一次）。
        /// </summary>
        private static bool ReportUploadProgress(string msg, int cur, int total, string display, int baseCompleted, Action<DeployProgress> onProgress, string serverHost = "")
        {
            if (string.IsNullOrEmpty(msg)) return false;
            var m = Regex.Match(msg, @"上传进度\s*(\d+)%");
            if (!m.Success) return false;
            int pct;
            if (!int.TryParse(m.Groups[1].Value, out pct)) return false;
            if (pct < 0) pct = 0; if (pct > 100) pct = 100;
            if (onProgress != null)
            {
                var overall = (int)((baseCompleted + pct / 100.0) * 100.0 / total);
                if (overall > 100) overall = 100;
                var phase = pct >= 100 ? "deploy" : "upload";
                var text = pct >= 100
                    ? $"Deploy 部署中 ({cur}/{total}) {display}"
                    : $"Upload 上传中 ({cur}/{total}) {display} {pct}%";
                var stepText = pct >= 100 ? "部署中" : $"上传中 {pct}%";
                onProgress(new DeployProgress
                {
                    Phase = phase,
                    Percent = overall,
                    ServerIndex = cur,
                    ServerCount = total,
                    Text = text,
                    ServerHost = serverHost,
                    ServerDisplay = display,
                    ServerUploadPercent = pct,
                    ServerStepText = stepText
                });
            }
            return pct >= 100;
        }

        /// <summary>回滚到指定版本（Agent 协议），对选中的所有服务器回滚（SemaphoreSlim 受限并行，run 为空/并发&lt;=1 时逐台顺序）。
        /// 回滚不启用两阶段（回滚包体小、逻辑简单），但保留零掉包回调（下/上负载）。</summary>
        public async Task<DeployResult> RollbackAsync(DeployProfile profile, Env env, List<Server> servers, PublishType type, string version, Action<string> onLog,
            DeployRunOptions run = null, Action<DeployPhaseTimings> onTimings = null)
        {
            var log = Log(onLog);
            if (servers == null || servers.Count == 0)
                return new DeployResult { Success = false, Msg = "no server selected" };

            run = run ?? new DeployRunOptions();
            var extra = BuildExtraFields(profile, env, type, string.Empty, version, 0, run);
            var failHosts = new ConcurrentBag<string>();
            var sw = Stopwatch.StartNew();
            using (var gate = new SemaphoreSlim(run.AtomicParallel))
            {
                var tasks = servers.Select(async server =>
                {
                    await gate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        var loggerId = Guid.NewGuid().ToString("N");
                        log.Info($"【rollback】===== {server.Host} 回滚到 {version} =====");
                        var client = new DeployClient(log);
                        var r = await client.RollbackAsync(server.Host, server.Token, type, version, extra, loggerId, m => onLog?.Invoke(m)).ConfigureAwait(false);
                        if (r.Success) log.Info($"【rollback】{server.Host} 回滚成功");
                        else { failHosts.Add(server.Host); log.Error($"【rollback】{server.Host} 回滚失败：{r.Msg}"); }
                    }
                    finally { gate.Release(); }
                }).ToList();
                await Task.WhenAll(tasks);
            }
            sw.Stop();
            onTimings?.Invoke(new DeployPhaseTimings { DeployMs = sw.ElapsedMilliseconds });
            var allOk = failHosts.IsEmpty;
            return new DeployResult { Success = allOk, Msg = allOk ? "ok" : ("fail: " + string.Join(", ", failHosts)) };
        }

        /// <summary>预检：拉取目标服务器上的历史版本（用于回滚选择）。</summary>
        public async Task<GetVersionResult> CheckVersionAsync(Server server, string type, string name, Action<string> onLog)
        {
            var log = Log(onLog);
            var client = new DeployClient(log);
            return await client.CheckVersionAsync<GetVersionResult>(server.Host, server.Token, type, name);
        }

        /// <summary>测试 Agent 服务器连通性（以 /version 预检为准，能返回即视为可达）。</summary>
        public async Task<bool> TestAgentServerAsync(Server server, string checkType, string name, Action<string> onLog)
        {
            var res = await CheckVersionAsync(server, checkType, name, onLog);
            onLog?.Invoke(res != null ? $"【test】Agent 服务器可达：{server.Host}" : $"【test】Agent 服务器不可达或返回空：{server.Host}");
            return res != null;
        }

        /// <summary>探测目标 Agent 版本号（/version 返回纯文本），用于两阶段/回调能力判定；失败返回 null。</summary>
        public async Task<string> GetAgentVersionAsync(Server server, Action<string> onLog)
        {
            var log = Log(onLog);
            var client = new DeployClient(log);
            return await client.GetAgentVersionAsync(server.Host);
        }

        /// <summary>测试 SSH 服务器连通性。</summary>
        public bool TestSshServer(LinuxServer server, Action<string> onLog)
        {
            var log = Log(onLog);
            var settings = _store.GetSettings();
            try
            {
                using (var ssh = new SshDockerClient(server.Host, server.UserName, server.Pwd, settings.DeployHttpProxy, log))
                {
                    var ok = ssh.TestConnection(out var msg);
                    onLog?.Invoke("【test】" + msg);
                    return ok;
                }
            }
            catch (Exception ex)
            {
                log.Error("【test】SSH 测试异常：" + ex.Message);
                return false;
            }
        }

        // ---------------- SSH Docker 部署 ----------------

        /// <summary>
        /// SSH 直连部署 Docker：打包“一次”（全量/增量/选择文件）→ 上传选中的 SSH 服务器（受限并行）→ 远程 docker build/run。
        /// </summary>
        public Task<DeployResult> SshDockerDeployAsync(DeployProfile profile, Env env, List<LinuxServer> servers,
            List<string> selectedFiles, string remark, Action<string> onLog, Func<bool> checkCancel = null, Action<int> onProgress = null,
            int maxParallel = 1, Action<DeployPhaseTimings> onTimings = null)
        {
            return Task.Run(() =>
            {
                var tlog = new ErrorTrackingLog(onLog);
                if (servers == null || servers.Count == 0)
                    return new DeployResult { Success = false, Msg = "no server selected" };

                var settings = _store.GetSettings();
                var docker = profile.Docker ?? new DockerConfig();
                var publishPath = _store.GetPublishPath(profile);
                if (!Directory.Exists(publishPath))
                {
                    tlog.Error($"【deploy】发布目录不存在，请先编译：{publishPath}");
                    return new DeployResult { Success = false, Msg = "publish folder not found" };
                }

                DetectEntryPoint(publishPath, out var entryPoint, out var sdkVersion);
                if (string.IsNullOrEmpty(entryPoint))
                {
                    tlog.Error("【deploy】未能探测到入口 dll（缺少 *.runtimeconfig.json），请确认已编译 .NET Core 项目");
                    return new DeployResult { Success = false, Msg = "entrypoint not found" };
                }

                byte[] zip;
                var pkgSw = Stopwatch.StartNew();
                try
                {
                    zip = PackageCore(profile, env, PublishType.docker, selectedFiles, tlog);
                }
                catch (Exception ex)
                {
                    tlog.Error("【deploy】打包失败：" + ex.Message);
                    return new DeployResult { Success = false, Msg = ex.Message };
                }
                pkgSw.Stop();

                var pair = env != null ? FindPair(profile, env, PublishType.docker) : null;
                var clientFolder = DateTime.Now.ToString("yyyyMMddHHmmss");
                var increment = docker.EnableIncrement || docker.EnableSelectDeploy;
                onLog?.Invoke($"【deploy】打包完成，将 SSH 部署到 {servers.Count} 台服务器（并发 {Math.Max(1, maxParallel)}）");

                // 受限并行 SSH 部署：每台独立 SshDockerClient，槽位化收集失败主机，maxParallel<=1 时等价顺序执行
                var failHosts = new string[servers.Count];
                var deploySw = Stopwatch.StartNew();
                using (var gate = new SemaphoreSlim(Math.Max(1, maxParallel)))
                {
                    var tasks = servers.Select((server, i) => Task.Run(async () =>
                    {
                        var idx = i;
                        await gate.WaitAsync().ConfigureAwait(false);
                        try
                        {
                            if (checkCancel != null && checkCancel()) return;
                            onLog?.Invoke($"【deploy】===== SSH 部署到 {server.Host} =====");
                            using (var ssh = new SshDockerClient(server.Host, server.UserName, server.Pwd, settings.DeployHttpProxy, tlog, onProgress, checkCancel))
                            {
                                ApplyDockerProps(ssh, docker, pair, settings, entryPoint, sdkVersion, remark, clientFolder, increment);
                                if (!ssh.Connect())
                                {
                                    failHosts[idx] = server.Host;
                                    tlog.Error($"【deploy】{server.Host} SSH 连接失败");
                                    return;
                                }
                                try
                                {
                                    using (var ms = new MemoryStream(zip))
                                    {
                                        ssh.PublishZip(ms, "publish.zip", () => checkCancel == null || !checkCancel(), null);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    failHosts[idx] = server.Host;
                                    tlog.Error($"【deploy】{server.Host} SSH 部署异常：{ex.Message}");
                                }
                            }
                        }
                        finally { gate.Release(); }
                    })).ToArray();
                    Task.WaitAll(tasks);
                }
                deploySw.Stop();
                onTimings?.Invoke(new DeployPhaseTimings { PackageMs = pkgSw.ElapsedMilliseconds, DeployMs = deploySw.ElapsedMilliseconds });

                var fails = failHosts.Where(h => h != null).ToList();
                var allOk = fails.Count == 0 && !tlog.HasError;
                return new DeployResult { Success = allOk, Msg = allOk ? "ok" : ("deploy fail: " + string.Join(", ", fails)) };
            });
        }

        /// <summary>SSH 直连回滚 Docker 到指定版本，对选中的所有 SSH 服务器逐台回滚。</summary>
        public Task<DeployResult> SshDockerRollbackAsync(DeployProfile profile, List<LinuxServer> servers, string version, Action<string> onLog, Func<bool> checkCancel = null)
        {
            return Task.Run(() =>
            {
                var tlog = new ErrorTrackingLog(onLog);
                if (servers == null || servers.Count == 0)
                    return new DeployResult { Success = false, Msg = "no server selected" };

                var settings = _store.GetSettings();
                var docker = profile.Docker ?? new DockerConfig();
                var publishPath = _store.GetPublishPath(profile);
                DetectEntryPoint(publishPath, out var entryPoint, out var sdkVersion);

                var failHosts = new List<string>();
                foreach (var server in servers)
                {
                    if (checkCancel != null && checkCancel()) { tlog.Warn("【rollback】已取消，跳过剩余 SSH 服务器"); break; }
                    onLog?.Invoke($"【rollback】===== SSH 回滚 {server.Host} -> {version} =====");
                    using (var ssh = new SshDockerClient(server.Host, server.UserName, server.Pwd, settings.DeployHttpProxy, tlog, null, checkCancel))
                    {
                        ApplyDockerProps(ssh, docker, null, settings, entryPoint, sdkVersion, string.Empty, version, docker.EnableIncrement || docker.EnableSelectDeploy);
                        if (!ssh.Connect())
                        {
                            failHosts.Add(server.Host);
                            tlog.Error($"【rollback】{server.Host} SSH 连接失败");
                            continue;
                        }
                        try
                        {
                            ssh.RollBack(version);
                        }
                        catch (Exception ex)
                        {
                            failHosts.Add(server.Host);
                            tlog.Error($"【rollback】{server.Host} SSH 回滚异常：{ex.Message}");
                        }
                    }
                }
                var allOk = failHosts.Count == 0 && !tlog.HasError;
                return new DeployResult { Success = allOk, Msg = allOk ? "ok" : ("rollback fail: " + string.Join(", ", failHosts)) };
            });
        }

        /// <summary>SSH 直连查询 Docker 部署历史（返回当前版本 + 历史版本列表带备注）。</summary>
        public Task<Tuple<string, List<Tuple<string, string>>>> SshDockerHistoryAsync(DeployProfile profile, LinuxServer server, Action<string> onLog)
        {
            return Task.Run(() =>
            {
                var tlog = new ErrorTrackingLog(onLog);
                var settings = _store.GetSettings();
                var docker = profile.Docker ?? new DockerConfig();
                var publishPath = _store.GetPublishPath(profile);
                DetectEntryPoint(publishPath, out var entryPoint, out _);

                using (var ssh = new SshDockerClient(server.Host, server.UserName, server.Pwd, settings.DeployHttpProxy, tlog))
                {
                    ssh.NetCoreENTRYPOINT = entryPoint;
                    ssh.WorkDir = docker.WorkDir;
                    ssh.NetCorePort = docker.Port;
                    ssh.Volume = docker.Volume;
                    ssh.Other = docker.Other;
                    if (!ssh.Connect(true))
                    {
                        return new Tuple<string, List<Tuple<string, string>>>(string.Empty, new List<Tuple<string, string>>());
                    }
                    return ssh.GetDeployHistory();
                }
            });
        }

        private static void ApplyDockerProps(SshDockerClient ssh, DockerConfig docker, EnvPairConfig pair, WebSettings settings,
            string entryPoint, string sdkVersion, string remark, string clientFolder, bool increment)
        {
            ssh.NetCoreENTRYPOINT = entryPoint;
            ssh.NetCoreVersion = sdkVersion;
            ssh.NetCorePort = pair?.DockerPort ?? docker.Port;
            ssh.NetCoreEnvironment = docker.AspNetCoreEnv;
            ssh.ClientDateTimeFolderName = clientFolder;
            ssh.RemoveDaysFromPublished = docker.RemoveDaysFromPublished.ToString();
            ssh.WorkDir = docker.WorkDir;
            ssh.Volume = pair?.DockerVolume ?? docker.Volume;
            ssh.Other = pair?.DockerOther ?? docker.Other;
            ssh.Remark = remark;
            ssh.UseAsiaShanghai = settings.UseAsiaShanghai;
            ssh.Increment = increment;
            ssh.IsSelect = docker.EnableSelectDeploy;
            ssh.Sudo = docker.EnableSudo ? "sudo" : string.Empty;
            ssh.DockerServiceEnableUpload = docker.EnableUpload && !string.IsNullOrEmpty(docker.RepositoryImageName);
            ssh.DockerServiceBuildImageOnly = docker.BuildImageOnly;
            ssh.RepositoryUrl = docker.RepositoryUrl;
            ssh.RepositoryUserName = docker.RepositoryUserName;
            ssh.RepositoryUserPwd = docker.RepositoryUserPwd;
            ssh.RepositoryNameSpace = docker.RepositoryNameSpace;
            ssh.RepositoryImageName = docker.RepositoryImageName;
        }

        // ---------------- jib 镜像构建 ----------------

        /// <summary>调用 jib.exe 构建 Docker 镜像（tar 本地打包或推送远程仓库）。</summary>
        public Task<bool> BuildImageAsync(DeployProfile profile, Action<string> onLog, Func<bool> checkCancel = null)
        {
            return Task.Run(() =>
            {
                var log = Log(onLog);
                var publishPath = _store.GetPublishPath(profile);
                if (!Directory.Exists(publishPath))
                {
                    log.Error("【image】发布目录不存在，请先编译");
                    return false;
                }
                var docker = profile.Docker ?? new DockerConfig();
                if (string.IsNullOrWhiteSpace(docker.TargetImage))
                {
                    log.Error("【image】未配置目标镜像 TargetImage");
                    return false;
                }
                var rootPath = _store.GetJibPath();
                var cfg = new DockerImageConfig
                {
                    BaseHttpProxy = docker.BaseHttpProxy,
                    BaseImage = docker.BaseImage,
                    BaseImageCredential = docker.BaseImageCredential ?? new ImageCredential(),
                    TargetImage = docker.TargetImage,
                    TargetHttpProxy = docker.TargetHttpProxy,
                    TargetTags = SplitCsv(docker.TargetTags),
                    TargetImageCredential = docker.TargetImageCredential ?? new ImageCredential(),
                    ImageFormat = string.IsNullOrWhiteSpace(docker.ImageFormat) ? "docker" : docker.ImageFormat,
                    Entrypoint = SplitCsv(docker.Entrypoint),
                    Cmd = SplitCsv(docker.Cmd),
                    IgnoreList = docker.IgnoreList
                };
                log.Info($"【image】开始构建镜像，jib 目录：{rootPath}");
                var ok = CommandHelper.RunJibExe(publishPath, rootPath, cfg, log, checkCancel);
                log.Info(ok ? "【image】镜像构建成功" : "【image】镜像构建失败");
                return ok;
            });
        }

        // ---------------- 打包 / 增量 / 选择文件 ----------------

        /// <summary>列举发布目录下的所有文件（相对路径，供“选择文件部署”勾选）。</summary>
        public List<string> GetPublishFiles(DeployProfile profile)
        {
            var publishPath = _store.GetPublishPath(profile);
            var files = new List<string>();
            if (!Directory.Exists(publishPath)) return files;
            foreach (var f in Directory.GetFiles(publishPath, "*", SearchOption.AllDirectories))
            {
                var rel = f.Substring(publishPath.Length).TrimStart('\\', '/').Replace('\\', '/');
                files.Add(rel);
            }
            return files;
        }

        /// <summary>基于发布目录本地 git 仓库，列举自上次以来变更的文件（相对路径，供增量预览）。</summary>
        public List<string> GetChangedFiles(DeployProfile profile, Action<string> onLog)
        {
            var log = Log(onLog);
            var publishPath = _store.GetPublishPath(profile);
            if (!Directory.Exists(publishPath)) return new List<string>();
            using (var g = new IncrementGit(publishPath, log))
            {
                return g.GetChanges();
            }
        }

        private byte[] PackageCore(DeployProfile profile, Env env, PublishType type, List<string> selectedFiles, ILog log)
        {
            var publishPath = _store.GetPublishPath(profile);
            var ignore = env?.IgnoreList;
            // Agent 端解压后期望「单一顶层目录」：IIsPublisher / WindowServicePublisher 先找 publish 子目录，
            // 找不到时若解压目录恰好只有一个子目录则用它，否则报 "Path not found"。
            // 与原 Winform 客户端保持一致：iis/windowservice/linux 打包时包含基目录（顶层单一文件夹），docker 不包含。
            bool includeBase = type != PublishType.docker && type != PublishType.docker_rollback;
            var increment = false;
            var select = false;
            switch (type)
            {
                case PublishType.iis:
                case PublishType.iis_rollback: increment = profile.Iis?.EnableIncrement == true; select = profile.Iis?.EnableSelectDeploy == true; break;
                case PublishType.windowservice:
                case PublishType.windowservice_rollback: increment = profile.WindowsService?.EnableIncrement == true; select = profile.WindowsService?.EnableSelectDeploy == true; break;
                case PublishType.linux:
                case PublishType.linux_rollback: increment = profile.Linux?.EnableIncrement == true; select = profile.Linux?.EnableSelectDeploy == true; break;
                case PublishType.docker:
                case PublishType.docker_rollback: increment = profile.Docker?.EnableIncrement == true; select = profile.Docker?.EnableSelectDeploy == true; break;
            }

            using (var ms = new MemoryStream())
            {
                if (select && selectedFiles != null && selectedFiles.Count > 0)
                {
                    log.Info($"【package】选择文件部署，共 {selectedFiles.Count} 个文件");
                    ZipHelper.CreateFromFiles(selectedFiles, publishPath, ms, ignore, includeBase);
                    var abs = selectedFiles.Select(f => Path.IsPathRooted(f) ? f : Path.Combine(publishPath, f)).ToList();
                    using (var g = new IncrementGit(publishPath, log)) { g.SubmitSelectedChanges(abs, publishPath); }
                }
                else if (increment)
                {
                    List<string> changes;
                    using (var g = new IncrementGit(publishPath, log)) { changes = g.GetChanges(); }
                    if (changes == null) changes = new List<string>();
                    log.Info($"【package】增量部署，变更文件 {changes.Count} 个");
                    if (changes.Count == 0)
                    {
                        log.Warn("【package】增量：未检测到变更文件，本次将上传空包");
                    }
                    ZipHelper.CreateFromFiles(changes, publishPath, ms, ignore, includeBase);
                    using (var g = new IncrementGit(publishPath, log)) { g.SubmitChanges(changes.Count); }
                }
                else
                {
                    log.Info($"【package】全量打包发布目录：{publishPath}");
                    ZipHelper.CreateFromDirectory(publishPath, ms, ignore, includeBase);
                }
                return ms.ToArray();
            }
        }

        /// <summary>探测发布目录的入口 dll 与 .NET 版本（读取 *.runtimeconfig.json）。</summary>
        public static bool DetectEntryPoint(string publishPath, out string entryPoint, out string sdkVersion)
        {
            entryPoint = null;
            sdkVersion = null;
            try
            {
                if (!Directory.Exists(publishPath)) return false;
                var rc = Directory.GetFiles(publishPath, "*.runtimeconfig.json", SearchOption.TopDirectoryOnly);
                if (rc.Length == 0) return false;
                var file = rc[0];
                var name = Path.GetFileNameWithoutExtension(file); // xxx.runtimeconfig
                if (name.EndsWith(".runtimeconfig", StringComparison.OrdinalIgnoreCase))
                    name = name.Substring(0, name.Length - ".runtimeconfig".Length);
                entryPoint = name + ".dll";
                try
                {
                    var obj = JObject.Parse(File.ReadAllText(file));
                    var tfm = obj["runtimeOptions"]?["tfm"]?.ToString();
                    if (!string.IsNullOrEmpty(tfm)) sdkVersion = TfmToVersion(tfm);
                    var fwv = obj["runtimeOptions"]?["framework"]?["version"]?.ToString();
                    if (string.IsNullOrEmpty(sdkVersion) && !string.IsNullOrEmpty(fwv))
                    {
                        var parts = fwv.Split('.');
                        if (parts.Length >= 2) sdkVersion = parts[0] + "." + parts[1];
                    }
                }
                catch { }
                return true;
            }
            catch { return false; }
        }

        private static string TfmToVersion(string tfm)
        {
            var m = Regex.Match(tfm, @"(\d+)\.(\d+)");
            return m.Success ? m.Groups[1].Value + "." + m.Groups[2].Value : tfm;
        }

        private static string[] SplitCsv(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return new string[0];
            return s.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToArray();
        }

        private static string BuildDotnetArgs(BuildConfig build)
        {
            var args = $"-c {build.Configuration}";
            if (!string.IsNullOrWhiteSpace(build.Framework)) args += $" -f {build.Framework}";
            if (!string.IsNullOrWhiteSpace(build.Runtime)) args += $" -r {build.Runtime}";
            if (!string.IsNullOrWhiteSpace(build.ExtraArgs)) args += " " + build.ExtraArgs;
            return args;
        }

        private static string ResolveProjectFile(string projectPath, BuildConfig build, string preferredName, ILog log)
        {
            // ① 用户显式指定的项目/解决方案文件优先（支持相对仓库根或绝对路径）
            if (!string.IsNullOrWhiteSpace(build.ProjectFile))
            {
                var specified = Path.IsPathRooted(build.ProjectFile)
                    ? build.ProjectFile
                    : Path.Combine(projectPath, build.ProjectFile);
                if (File.Exists(specified))
                {
                    log.Info($"【build】使用指定的项目文件：{specified}");
                    return specified;
                }
                log.Warn($"【build】指定的项目文件不存在，转为自动探测：{build.ProjectFile}");
            }

            if (!Directory.Exists(projectPath)) return null;

            // ② 仓库根目录下的 csproj
            var top = Directory.GetFiles(projectPath, "*.csproj", SearchOption.TopDirectoryOnly);
            if (top.Length == 1)
            {
                log.Info($"【build】自动选择根目录项目：{top[0]}");
                return top[0];
            }
            if (top.Length > 1)
            {
                var pickTop = PickBestProject(top, build, preferredName);
                log.Info($"【build】根目录有 {top.Length} 个项目，自动选择：{pickTop}（建议在“项目文件”下拉中显式指定）");
                return pickTop;
            }

            // ③ 根目录无 csproj：递归子目录（排除 obj/bin/packages/.git/.vs/node_modules），优先 Web/可执行主项目
            string[] all;
            try
            {
                all = Directory.GetFiles(projectPath, "*.csproj", SearchOption.AllDirectories)
                    .Where(f => !IsJunkPath(f))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception ex)
            {
                // 克隆仓库常含 .git 深层长路径或无权限目录，递归枚举可能抛异常；降级为顶层探测，绝不冒泡打断电路
                log.Warn($"【build】递归枚举项目文件失败（可能存在超长路径/无权限目录），转为顶层解决方案探测：{ex.Message}");
                all = new string[0];
            }
            if (all.Length > 0)
            {
                var pick = PickBestProject(all, build, preferredName);
                var matchedByName = !string.IsNullOrWhiteSpace(preferredName)
                    && string.Equals(Path.GetFileNameWithoutExtension(pick), preferredName, StringComparison.OrdinalIgnoreCase);
                if (matchedByName)
                {
                    log.Info($"【build】按部署配置名“{preferredName}”精确匹配到项目：{pick}");
                }
                else
                {
                    log.Warn($"【build】多项目仓库（根目录无项目文件），子目录发现 {all.Length} 个项目，自动选择：{pick}");
                    var webApps = all.Where(IsWebAppProject).Select(Path.GetFileNameWithoutExtension).ToArray();
                    if (webApps.Length > 1)
                        log.Warn($"【build】仓库中有 {webApps.Length} 个 Web 项目，仅凭自动探测可能选错：{string.Join("、", webApps)}");
                    log.Warn($"【build】请在“项目文件”下拉中显式选择要发布的主项目并保存后重试（配置名“{preferredName}”未匹配到同名项目）");
                }
                return pick;
            }

            // ④ 仅有解决方案文件
            var sln = Directory.GetFiles(projectPath, "*.sln", SearchOption.TopDirectoryOnly);
            if (sln.Length > 0)
            {
                log.Info($"【build】未发现 csproj，使用解决方案文件：{sln[0]}");
                return sln[0];
            }
            return null;
        }

        /// <summary>从候选项目中挑选最可能的发布目标：配置名精确匹配 &gt; Framework 名匹配 &gt; Web 主项目 &gt; 可执行项目 &gt; 非测试项目 &gt; 第一个。</summary>
        private static string PickBestProject(string[] candidates, BuildConfig build, string preferredName)
        {
            // ⓪ 最强信号：项目文件名（不含扩展名）与部署配置名精确匹配。
            // 用户通常以“要发布的目标项目名”命名部署配置（如配置名=Homeinns.PMS.WebUI）。
            // 在多 Web 项目仓库里（Backups/SignalR/SignIn/Test/WebUI 都含 ASP.NET Web GUID），
            // 仅凭字母序会误选 Backups，导致发布一个无法独立发布的辅助项目而报“句柄无效”。
            if (!string.IsNullOrWhiteSpace(preferredName))
            {
                var byName = candidates.FirstOrDefault(f =>
                    string.Equals(Path.GetFileNameWithoutExtension(f), preferredName, StringComparison.OrdinalIgnoreCase));
                if (byName != null) return byName;
            }
            if (!string.IsNullOrWhiteSpace(build.Framework))
            {
                var byFw = candidates.FirstOrDefault(f => f.IndexOf(build.Framework, StringComparison.OrdinalIgnoreCase) >= 0);
                if (byFw != null) return byFw;
            }
            // 优先真正的 Web 主项目（ASP.NET Web Application GUID / Microsoft.NET.Sdk.Web）；
            // 其次可执行项目(Exe/WinExe)；再次非测试项目；最后第一个。
            // 注意：绝不能把“控制台 Exe”当作 Web 主项目——多项目仓库里 ConsoleApplication 常按路径排在 WebUI 之前，
            // 若把 Exe 视为 Web 会误选控制台项目，导致 isWeb 编译走 /t:WebPublish 报 MSB4057。
            var webApp = candidates.FirstOrDefault(IsWebAppProject);
            if (webApp != null) return webApp;
            var exe = candidates.FirstOrDefault(IsExeProject);
            if (exe != null) return exe;
            var nonTest = candidates.FirstOrDefault(f => !IsTestProject(f));
            return nonTest ?? candidates[0];
        }

        /// <summary>是否为真正的 Web 主项目：ASP.NET Web Application GUID 或 Microsoft.NET.Sdk.Web。</summary>
        private static bool IsWebAppProject(string csproj)
        {
            try
            {
                var content = File.ReadAllText(csproj);
                if (content.IndexOf("349c5851-65df-11da-9384-00065b846f21", StringComparison.OrdinalIgnoreCase) >= 0) return true; // ASP.NET Web Application
                if (content.IndexOf("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase) >= 0) return true;                 // .NET Core/5+ Web
                return false;
            }
            catch { return false; }
        }

        /// <summary>是否为可执行项目（控制台 Exe / WinExe）——弱于 Web 主项目的候选信号，仅在无 Web 项目时兜底。</summary>
        private static bool IsExeProject(string csproj)
        {
            try
            {
                var content = File.ReadAllText(csproj);
                if (content.IndexOf("<OutputType>Exe</OutputType>", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (content.IndexOf("<OutputType>WinExe</OutputType>", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                return false;
            }
            catch { return false; }
        }

        private static bool IsTestProject(string csproj)
        {
            var name = Path.GetFileNameWithoutExtension(csproj);
            return name.IndexOf("test", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsJunkPath(string path)
        {
            var p = path.Replace('/', '\\');
            return p.IndexOf("\\obj\\", StringComparison.OrdinalIgnoreCase) >= 0
                || p.IndexOf("\\bin\\", StringComparison.OrdinalIgnoreCase) >= 0
                || p.IndexOf("\\packages\\", StringComparison.OrdinalIgnoreCase) >= 0
                || p.IndexOf("\\.git\\", StringComparison.OrdinalIgnoreCase) >= 0
                || p.IndexOf("\\.vs\\", StringComparison.OrdinalIgnoreCase) >= 0
                || p.IndexOf("\\node_modules\\", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>组装上传 Agent 的业务字段（与 WinForm SetFieldValue 协议对齐）。
        /// run 非空时附带零掉包回调字段（beforeCallback/afterCallback/drainWait/callbackTimeout），旧 Agent 忽略多余字段。</summary>
        private Dictionary<string, string> BuildExtraFields(DeployProfile profile, Env env, PublishType type, string remark, string folderName, long zipSize, DeployRunOptions run = null)
        {
            var settings = _store.GetSettings();
            var fields = new Dictionary<string, string>();
            var pair = env != null ? FindPair(profile, env, type) : null;
            var backUpIgnore = env?.WindowsBackUpIgnoreList;

            // 公共字段
            fields["remark"] = remark ?? string.Empty;
            fields["mac"] = CodingHelper.GetMacAddress();
            fields["pc"] = Environment.MachineName;
            fields["localIp"] = CodingHelper.GetLocalIPAddress();
            fields["deployFolderName"] = folderName ?? string.Empty;
            fields["backUpIgnore"] = (backUpIgnore != null && backUpIgnore.Any()) ? string.Join("@_@", backUpIgnore) : string.Empty;

            // 零掉包回调字段（选填，缺省即不回调）；deployPhase 由两阶段编排按阶段追加
            if (run != null)
            {
                fields["beforeCallback"] = run.BeforeCallback ?? string.Empty;
                fields["afterCallback"] = run.AfterCallback ?? string.Empty;
                fields["drainWait"] = run.DrainWaitSeconds > 0 ? run.DrainWaitSeconds.ToString() : string.Empty;
                fields["callbackTimeout"] = run.CallbackTimeout > 0 ? run.CallbackTimeout.ToString() : string.Empty;
            }

            switch (type)
            {
                case PublishType.iis:
                case PublishType.iis_rollback:
                    {
                        var iis = profile.Iis ?? new IIsConfig();
                        fields["isIncrement"] = iis.EnableIncrement ? "true" : string.Empty;
                        fields["sdkType"] = iis.SdkType ?? "netcore";
                        fields["port"] = iis.Port ?? string.Empty;
                        fields["poolName"] = iis.PoolName ?? string.Empty;
                        fields["poolAlwaysRunning"] = iis.PoolAlwaysRunning ? "true" : string.Empty;
                        fields["physicalPath"] = iis.PhysicalPath ?? string.Empty;
                        fields["webSiteName"] = iis.WebSiteName ?? string.Empty;
                        fields["useTempPhysicalPath"] = iis.NotStopSiteDeploy ? "true" : string.Empty;
                        fields["useOfflineHtm"] = iis.UseOfflineHtm ? "true" : string.Empty;
                        break;
                    }
                case PublishType.windowservice:
                case PublishType.windowservice_rollback:
                    {
                        var ws = profile.WindowsService ?? new WindowsServiceConfig();
                        fields["isIncrement"] = ws.EnableIncrement ? "true" : string.Empty;
                        fields["serviceName"] = ws.ServiceName ?? string.Empty;
                        fields["sdkType"] = ws.SdkType ?? "netcore";
                        fields["isProjectInstallService"] = ws.IsProjectInstallService ? "yes" : "no";
                        fields["execFilePath"] = ws.ExecFilePath ?? string.Empty;
                        fields["physicalPath"] = string.Empty;
                        fields["startType"] = ws.StartType ?? string.Empty;
                        fields["desc"] = ws.Description ?? string.Empty;
                        fields["useNssm"] = ws.UseNssm ?? string.Empty;
                        fields["param"] = ws.Param ?? string.Empty;
                        break;
                    }
                case PublishType.linux:
                case PublishType.linux_rollback:
                    {
                        var lx = profile.Linux ?? new LinuxServiceConfig();
                        fields["isIncrement"] = lx.EnableIncrement ? "true" : string.Empty;
                        fields["serviceName"] = lx.ServiceName ?? string.Empty;
                        fields["execFilePath"] = string.Empty;
                        fields["notify"] = lx.NotifySystemd ? "true" : string.Empty;
                        fields["physicalPath"] = string.Empty;
                        fields["env"] = pair?.LinuxEnvParam ?? lx.EnvParam ?? string.Empty;
                        fields["useDotnet"] = lx.UseDotnet ? "true" : string.Empty;
                        break;
                    }
                case PublishType.docker:
                case PublishType.docker_rollback:
                    {
                        var dk = profile.Docker ?? new DockerConfig();
                        var publishPath = _store.GetPublishPath(profile);
                        DetectEntryPoint(publishPath, out var entryPoint, out var sdkVersion);
                        var serviceName = string.IsNullOrEmpty(entryPoint) ? (dk.TargetImage ?? string.Empty) : entryPoint.Replace(".dll", "");
                        fields["isIncrement"] = dk.EnableIncrement ? "true" : string.Empty;
                        fields["serviceName"] = serviceName;
                        var param = new
                        {
                            NetCoreENTRYPOINT = entryPoint,
                            NetCoreVersion = sdkVersion,
                            NetCorePort = pair?.DockerPort ?? dk.Port,
                            NetCoreEnvironment = dk.AspNetCoreEnv,
                            ClientDateTimeFolderName = folderName,
                            RemoveDaysFromPublished = dk.RemoveDaysFromPublished.ToString(),
                            WorkDir = dk.WorkDir,
                            Volume = pair?.DockerVolume ?? dk.Volume,
                            Other = pair?.DockerOther ?? dk.Other,
                            Remark = remark,
                            UseAsiaShanghai = settings.UseAsiaShanghai,
                            Increment = dk.EnableIncrement || dk.EnableSelectDeploy,
                            Sudo = dk.EnableSudo ? "sudo" : string.Empty,
                            IsSelect = dk.EnableSelectDeploy,
                            DockerServiceEnableUpload = dk.EnableUpload,
                            DockerServiceBuildImageOnly = dk.BuildImageOnly,
                            RepositoryUrl = dk.RepositoryUrl,
                            RepositoryUserName = dk.RepositoryUserName,
                            RepositoryUserPwd = dk.RepositoryUserPwd,
                            RepositoryNameSpace = dk.RepositoryNameSpace,
                            RepositoryImageName = dk.RepositoryImageName
                        };
                        fields["param"] = JsonConvert.SerializeObject(param);
                        break;
                    }
            }
            return fields;
        }

        private static EnvPairConfig FindPair(DeployProfile profile, Env env, PublishType type)
        {
            List<EnvPairConfig> list = null;
            switch (type)
            {
                case PublishType.iis:
                case PublishType.iis_rollback: list = profile.Iis?.EnvPairList; break;
                case PublishType.windowservice:
                case PublishType.windowservice_rollback: list = profile.WindowsService?.EnvPairList; break;
                case PublishType.linux:
                case PublishType.linux_rollback: list = profile.Linux?.EnvPairList; break;
                case PublishType.docker:
                case PublishType.docker_rollback: list = profile.Docker?.EnvPairList; break;
            }
            if (list == null || env == null) return null;
            foreach (var p in list) if (p.EnvName == env.Name) return p;
            return null;
        }

        /// <summary>包装 onLog，并跟踪是否出现过 Error（用于判定 SSH 部署成败，对齐 WinForm 的 hasError）。</summary>
        private class ErrorTrackingLog : ILog
        {
            private readonly Action<string> _sink;
            public bool HasError;
            public ErrorTrackingLog(Action<string> sink) { _sink = sink; }
            public void Info(string msg) => _sink?.Invoke("[Info] " + msg);
            public void Debug(string msg) => _sink?.Invoke("[Debug] " + msg);
            public void Warn(string msg) => _sink?.Invoke("[Warn] " + msg);
            public void Error(string msg) { HasError = true; _sink?.Invoke("[Error] " + msg); }
        }
    }

    /// <summary>一键发布的阶段进度（供项目列表行内进度条实时刷新）。</summary>
    public class DeployProgress
    {
        /// <summary>阶段标识：pull / build / package / upload / deploy / fail</summary>
        public string Phase { get; set; } = string.Empty;
        /// <summary>百分比 0-100；-1 表示该阶段无精确百分比（拉取/编译/打包），UI 显示为进行中动画。</summary>
        public int Percent { get; set; } = -1;
        /// <summary>展示文案</summary>
        public string Text { get; set; } = string.Empty;
        /// <summary>当前第几台服务器（1 基）</summary>
        public int ServerIndex { get; set; }
        /// <summary>服务器总数</summary>
        public int ServerCount { get; set; }
        /// <summary>是否为失败终态</summary>
        public bool IsFail => Phase == "fail";

        /// <summary>本条进度对应的服务器 Host（空表示共享阶段）。</summary>
        public string ServerHost { get; set; } = string.Empty;
        /// <summary>本条进度对应的服务器展示名。</summary>
        public string ServerDisplay { get; set; } = string.Empty;
        /// <summary>该服务器的上传百分比（0-100，-1 表示不适用）。</summary>
        public int ServerUploadPercent { get; set; } = -1;
        /// <summary>该服务器当前步骤状态文案（待上传/上传中/待部署/部署中/延迟等待中/完成/失败）。</summary>
        public string ServerStepText { get; set; } = string.Empty;

        public static DeployProgress Fail(string msg) => new DeployProgress { Phase = "fail", Percent = -1, Text = msg };
    }

    /// <summary>单台服务器的发布结果。</summary>
    public class HostOutcome
    {
        public string Host { get; set; } = string.Empty;
        public string Display { get; set; } = string.Empty;
        public bool Success { get; set; }
        public string Msg { get; set; } = string.Empty;
    }

    /// <summary>一次发布已打好的包与上下文，供失败重试复用（避免重打包得到增量空包）。</summary>
    public class QuickDeployContext
    {
        public DeployProfile Profile { get; set; }
        public Env Env { get; set; }
        public PublishType Type { get; set; }
        public string Remark { get; set; } = string.Empty;
        public byte[] Zip { get; set; }
    }

    /// <summary>一次发布各阶段的 wall-clock 耗时（毫秒）。并行发布时上传/部署为整体近似：
    /// UploadMs=全部台上传达 100% 的时刻(T1-T0)，DeployMs=全部站点更新完成(T2-T1)。</summary>
    public class DeployPhaseTimings
    {
        public long PullMs { get; set; }
        public long BuildMs { get; set; }
        public long PackageMs { get; set; }
        public long UploadMs { get; set; }
        public long DeployMs { get; set; }
    }

    /// <summary>
    /// 一次发布的运行期选项（由 DeploySessionManager 从项目并发设置 + 能力探测解析而来）：
    /// 上传/部署并发、是否两阶段（先全部上传再滚动部署）、以及零掉包回调字段。
    /// 传 null 时各发布方法退化为原顺序行为（并发 1、原子发布、不回调），保证向后兼容。
    /// </summary>
    public class DeployRunOptions
    {
        /// <summary>上传阶段并发（两阶段时生效；原子发布取 AtomicParallel）。</summary>
        public int UploadParallel { get; set; } = 1;
        /// <summary>部署阶段并发（两阶段时生效，默认 1=滚动逐台，零掉包）。</summary>
        public int DeployParallel { get; set; } = 1;
        /// <summary>是否启用两阶段发布（先把包上传解压到所有服务器，再逐台部署）。</summary>
        public bool TwoPhase { get; set; }
        /// <summary>更新前回调（下负载），空=不回调。</summary>
        public string BeforeCallback { get; set; } = string.Empty;
        /// <summary>更新成功后回调（上负载），空=不回调。</summary>
        public string AfterCallback { get; set; } = string.Empty;
        /// <summary>下负载后等待秒数，让在途请求处理完成。</summary>
        public int DrainWaitSeconds { get; set; }
        /// <summary>回调超时秒数（默认 10）。</summary>
        public int CallbackTimeout { get; set; } = 10;

        /// <summary>原子发布/回滚时使用的并发：取上传、部署并发的较小值并钳制到 &gt;=1，
        /// 与旧 maxParallel&lt;=1 时顺序执行等价（回归安全）。</summary>
        public int AtomicParallel => Math.Max(1, Math.Min(UploadParallel < 1 ? 1 : UploadParallel, DeployParallel < 1 ? 1 : DeployParallel));
    }

    /// <summary>一键发布的聚合结果（成功 / 部分成功 / 失败 / 取消 + 每台明细）。</summary>
    public class QuickDeployOutcome
    {
        public bool Success { get; set; }
        public bool Partial { get; set; }
        public bool Cancelled { get; set; }
        public string Message { get; set; } = string.Empty;
        public int SuccessCount { get; set; }
        public int FailCount { get; set; }
        public List<HostOutcome> Hosts { get; set; } = new List<HostOutcome>();
        /// <summary>成功打包后的上下文，非空表示可对失败服务器重试</summary>
        public QuickDeployContext Context { get; set; }
        /// <summary>本次发布各阶段耗时（未涉及的阶段为 0）</summary>
        public DeployPhaseTimings Timings { get; set; } = new DeployPhaseTimings();

        public List<HostOutcome> FailedHosts => Hosts.Where(h => !h.Success).ToList();
    }
}
