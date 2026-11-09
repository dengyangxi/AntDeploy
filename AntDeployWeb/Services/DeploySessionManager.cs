using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AntDeploy.Core.Models;
using AntDeployWeb.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace AntDeployWeb.Services
{
    /// <summary>
    /// 部署会话管理器（单例）。负责：
    /// 1. 在后台线程执行发布任务（不依赖 Blazor 电路，刷新页面不中断）；
    /// 2. 将会话状态持久化到数据库，供所有用户共享查看；
    /// 3. 通过事件通知所有订阅的组件实时刷新 UI。
    /// </summary>
    public class DeploySessionManager : IDisposable
    {
        private readonly IDbContextFactory<AntDeployDbContext>? _dbFactory;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConfigStore _configStore;
        private readonly StorageOptions _options;
        private readonly ILogger<DeploySessionManager> _logger;
        private readonly ProjectDeployOptionsService? _deployOptions;
        private readonly bool _useDb;

        /// <summary>运行中的会话（key = sessionId）</summary>
        private readonly ConcurrentDictionary<string, DeploySession> _activeSessions = new();

        /// <summary>会话状态变更事件（sessionId, session）</summary>
        public event Action<string, DeploySession>? OnSessionUpdated;

        public DeploySessionManager(
            IServiceScopeFactory scopeFactory,
            IConfigStore configStore,
            IOptions<StorageOptions> options,
            ILogger<DeploySessionManager> logger,
            IDbContextFactory<AntDeployDbContext>? dbFactory = null,
            ProjectDeployOptionsService? deployOptions = null)
        {
            _dbFactory = dbFactory;
            _scopeFactory = scopeFactory;
            _configStore = configStore;
            _options = options.Value;
            _logger = logger;
            _deployOptions = deployOptions;
            _useDb = dbFactory != null;
        }

        /// <summary>
        /// 计算本次发布的实际并发数：读取项目级并发设置，
        /// 实际生效 = min(上传并发解析值, 部署并发解析值)，钳制到 [1, 本次选中服务器数]。
        /// 未配置或服务不可用时返回 1（等价现状逐台顺序发布）。
        /// </summary>
        private int ResolveParallel(string profileId, int serverCount)
        {
            if (serverCount <= 0) return 1;
            if (_deployOptions == null) return 1;
            try
            {
                var opt = _deployOptions.GetOptions(profileId);
                return ProjectDeployOptionsService.ResolveEffectiveParallel(opt, serverCount);
            }
            catch
            {
                return 1;
            }
        }

        /// <summary>
        /// 解析本次发布的运行选项：上传/部署并发分别解析、两阶段开关、零掉包回调字段；
        /// 并对目标 Agent 做能力探测（版本 &gt;= 7.2 才支持两阶段/回调），不满足则降级为原子发布并告警。
        /// 未启用两阶段且未配置回调时不做探测（零额外延迟，完全等价旧顺序/并行行为）。
        /// </summary>
        private async Task<DeployRunOptions> ResolveRunOptionsAsync(DeployService svc, string profileId, int serverCount,
            PublishType type, List<Server> servers, Action<string> onLog)
        {
            var run = new DeployRunOptions();
            if (serverCount <= 0 || _deployOptions == null) return run;

            DeployOptionsEntity opt;
            try { opt = _deployOptions.GetOptions(profileId); }
            catch { return run; }
            if (opt == null) return run;

            run.UploadParallel = ProjectDeployOptionsService.ResolveUploadParallel(opt, serverCount);
            run.DeployParallel = ProjectDeployOptionsService.ResolveDeployParallel(opt, serverCount);
            run.BeforeCallback = opt.BeforeCallbackUrl ?? string.Empty;
            run.AfterCallback = opt.AfterCallbackUrl ?? string.Empty;
            run.DrainWaitSeconds = opt.DrainWaitSeconds;
            run.CallbackTimeout = opt.CallbackTimeoutSeconds > 0 ? opt.CallbackTimeoutSeconds : 10;
            // 两阶段本次仅 IIS 类型支持（其它类型 Agent 的 PrepareUpload 走基类默认=原子）
            run.TwoPhase = opt.EnableTwoPhase && type == PublishType.iis;

            var wantCallback = !string.IsNullOrWhiteSpace(run.BeforeCallback) || !string.IsNullOrWhiteSpace(run.AfterCallback);
            if (!run.TwoPhase && !wantCallback) return run; // 无新特性，免探测

            // 能力探测：取首台服务器版本作为代表（通常同构）；探测失败按"不支持"保守降级
            var probeServer = servers?.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Host));
            string version = null;
            if (probeServer != null && svc != null)
            {
                try { version = await svc.GetAgentVersionAsync(probeServer, onLog); }
                catch { version = null; }
            }
            var verText = string.IsNullOrWhiteSpace(version) ? "未知" : version;
            var supports = AgentCapability.SupportsTwoPhase(version);
            if (run.TwoPhase && !supports)
            {
                run.TwoPhase = false;
                onLog?.Invoke($"[Warn] 【capability】Agent 版本 {verText} 低于 {AgentCapability.MinTwoPhaseVersion}，两阶段已降级为原子发布；如需零掉包两阶段，请升级目标服务器的 Agent");
            }
            if (wantCallback && !supports)
            {
                onLog?.Invoke($"[Warn] 【capability】Agent 版本 {verText} 可能不支持回调，回调字段仍会下发但可能不生效；如需下/上负载回调，请升级 Agent");
            }
            return run;
        }

        /// <summary>把分段耗时写入会话（发布完成持久化前调用）。</summary>
        private static void ApplyTimings(DeploySession session, DeployPhaseTimings t)
        {
            if (t == null) return;
            session.PullMs = t.PullMs;
            session.BuildMs = t.BuildMs;
            session.PackageMs = t.PackageMs;
            session.UploadMs = t.UploadMs;
            session.DeployMs = t.DeployMs;
        }

        /// <summary>
        /// 备注为空时生成默认备注：发布时间 + 分支名称。
        /// 格式：<c>yyyy-MM-dd HH:mm:ss 发布:分支名称</c>（分支为空则仅保留时间前缀）。
        /// 用户已填写的备注原样返回，不覆盖。
        /// </summary>
        private static string ApplyDefaultRemark(string? remark, string? branch)
        {
            if (!string.IsNullOrWhiteSpace(remark)) return remark;
            var br = string.IsNullOrWhiteSpace(branch) ? string.Empty : branch.Trim();
            return $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} 发布:{br}";
        }

        /// <summary>
        /// 启动一次一键发布（后台执行，立即返回 sessionId）。
        /// </summary>
        public string StartQuickDeploy(DeployProfile profile, Env env, List<Server> servers,
            PublishType type, string branch, string remark, string triggeredBy = "")
        {
            var sessionId = Guid.NewGuid().ToString("N");
            var session = new DeploySession
            {
                Id = sessionId,
                ProfileId = profile.Id,
                ProfileName = profile.Name,
                EnvName = env?.Name ?? string.Empty,
                DeployType = type.ToString(),
                Branch = branch ?? string.Empty,
                Remark = ApplyDefaultRemark(remark, branch),
                Status = SessionStatus.Running,
                Phase = "pull",
                Percent = -1,
                ProgressText = "Pull 拉取代码…",
                ServerCount = servers?.Count ?? 0,
                TriggeredBy = triggeredBy ?? string.Empty,
                StartTime = DateTime.UtcNow,
                IsRunning = true,
                ServerProgresses = BuildServerProgresses(servers)
            };

            _activeSessions[sessionId] = session;
            PersistSession(session);
            NotifyUpdate(session);

            // 后台执行发布（不阻塞 UI，不依赖电路）
            _ = Task.Run(() => RunQuickDeployAsync(sessionId, profile, env, servers, type, branch, remark));

            return sessionId;
        }

        /// <summary>
        /// 启动一次详情页发布（后台执行，立即返回 sessionId）。
        /// </summary>
        public string StartDeploy(DeployProfile profile, Env env, List<Server> servers,
            PublishType type, string remark, List<string> selectedFiles, string triggeredBy = "")
        {
            var sessionId = Guid.NewGuid().ToString("N");
            var session = new DeploySession
            {
                Id = sessionId,
                ProfileId = profile.Id,
                ProfileName = profile.Name,
                EnvName = env?.Name ?? string.Empty,
                DeployType = type.ToString(),
                Branch = profile.Git?.Branch ?? string.Empty,
                Remark = ApplyDefaultRemark(remark, profile.Git?.Branch),
                Status = SessionStatus.Running,
                Phase = "deploy",
                Percent = -1,
                ProgressText = "Deploy 部署中…",
                ServerCount = servers?.Count ?? 0,
                TriggeredBy = triggeredBy ?? string.Empty,
                StartTime = DateTime.UtcNow,
                IsRunning = true,
                ServerProgresses = BuildServerProgresses(servers)
            };

            _activeSessions[sessionId] = session;
            PersistSession(session);
            NotifyUpdate(session);

            _ = Task.Run(() => RunDeployAsync(sessionId, profile, env, servers, type, remark, selectedFiles));

            return sessionId;
        }

        /// <summary>获取指定项目的运行中会话（若无则返回 null）</summary>
        public DeploySession GetRunningSession(string profileId)
        {
            return _activeSessions.Values.FirstOrDefault(s => s.ProfileId == profileId && s.IsRunning);
        }

        /// <summary>获取所有运行中的会话</summary>
        public List<DeploySession> GetAllRunningSessions()
        {
            return _activeSessions.Values.Where(s => s.IsRunning).ToList();
        }

        /// <summary>按 sessionId 获取会话（内存优先，回退数据库）</summary>
        public DeploySession? GetSession(string sessionId)
        {
            if (_activeSessions.TryGetValue(sessionId, out var s)) return s;
            return LoadSessionFromDb(sessionId);
        }

        /// <summary>请求取消指定会话</summary>
        public bool RequestCancel(string sessionId)
        {
            if (_activeSessions.TryGetValue(sessionId, out var session))
            {
                session.Cts?.Cancel();
                return true;
            }
            return false;
        }

        /// <summary>按项目 Id 请求取消</summary>
        public bool RequestCancelByProfile(string profileId)
        {
            var session = GetRunningSession(profileId);
            if (session == null) return false;
            session.Cts?.Cancel();
            return true;
        }

        // ---------------- 后台执行 ----------------

        private async Task RunQuickDeployAsync(string sessionId, DeployProfile profile, Env env,
            List<Server> servers, PublishType type, string branch, string remark)
        {
            var session = _activeSessions[sessionId];
            var cts = new CancellationTokenSource();
            session.Cts = cts;

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var deployService = scope.ServiceProvider.GetRequiredService<DeployService>();

                var run = await ResolveRunOptionsAsync(deployService, profile.Id, servers?.Count ?? 0, type, servers, msg => AppendLog(session, msg));
                var outcome = await deployService.QuickDeployAsync(profile, env, servers, type, branch, remark,
                    msg => AppendLog(session, msg),
                    progress => ApplyProgress(session, progress),
                    () => cts.IsCancellationRequested,
                    cts.Token,
                    run,
                    t => ApplyTimings(session, t));

                // 完成：写入结果
                session.IsRunning = false;
                session.EndTime = DateTime.UtcNow;
                session.SuccessCount = outcome.SuccessCount;
                session.FailCount = outcome.FailCount;
                session.Message = outcome.Message ?? string.Empty;
                session.LastError = outcome.Hosts?.FirstOrDefault(h => !h.Success)?.Msg ?? string.Empty;
                session.HostResults = outcome.Hosts?.Select(h => new HostResult
                {
                    Host = h.Host,
                    Display = h.Display,
                    Success = h.Success,
                    Msg = h.Msg
                }).ToList() ?? new List<HostResult>();

                if (outcome.Cancelled) session.Status = SessionStatus.Cancelled;
                else if (outcome.Success) session.Status = SessionStatus.Success;
                else if (outcome.Partial) session.Status = SessionStatus.Partial;
                else session.Status = SessionStatus.Failed;

                session.Phase = "done";
                session.Percent = 100;
                session.ProgressText = session.Message;

                // 更新项目的最后发布信息
                UpdateProfileLastDeploy(profile, branch, type);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "QuickDeploy 异常: {SessionId}", sessionId);
                session.IsRunning = false;
                session.EndTime = DateTime.UtcNow;
                session.Status = SessionStatus.Failed;
                session.Message = $"发布异常：{ex.GetType().Name}: {ex.Message}";
                session.LastError = ex.Message;
                session.Phase = "fail";
                session.ProgressText = session.Message;
                AppendLog(session, $"[Error] {session.Message}");
            }
            finally
            {
                cts.Dispose();
                session.Cts = null;
                PersistSession(session);
                NotifyUpdate(session);
                // 从活跃列表移除（保留在数据库供历史查询）
                _activeSessions.TryRemove(sessionId, out _);
                // 清理超出上限的历史记录
                _ = Task.Run(() => TrimHistory());
            }
        }

        private async Task RunDeployAsync(string sessionId, DeployProfile profile, Env env,
            List<Server> servers, PublishType type, string remark, List<string> selectedFiles)
        {
            var session = _activeSessions[sessionId];
            var cts = new CancellationTokenSource();
            session.Cts = cts;

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var deployService = scope.ServiceProvider.GetRequiredService<DeployService>();

                var run = await ResolveRunOptionsAsync(deployService, profile.Id, servers?.Count ?? 0, type, servers, msg => AppendLog(session, msg));
                var result = await deployService.DeployAsync(profile, env, servers, type,
                    msg => AppendLog(session, msg), remark, selectedFiles,
                    () => cts.IsCancellationRequested, cts.Token,
                    run, t => ApplyTimings(session, t));

                session.IsRunning = false;
                session.EndTime = DateTime.UtcNow;
                session.Message = result.Msg ?? string.Empty;
                session.Status = result.Success ? SessionStatus.Success : SessionStatus.Failed;
                session.Phase = "done";
                session.Percent = 100;
                session.ProgressText = session.Message;
                session.SuccessCount = result.Success ? (servers?.Count ?? 0) : 0;
                session.FailCount = result.Success ? 0 : (servers?.Count ?? 0);

                UpdateProfileLastDeploy(profile, profile.Git?.Branch, type);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Deploy 异常: {SessionId}", sessionId);
                session.IsRunning = false;
                session.EndTime = DateTime.UtcNow;
                session.Status = SessionStatus.Failed;
                session.Message = $"部署异常：{ex.GetType().Name}: {ex.Message}";
                session.LastError = ex.Message;
                session.Phase = "fail";
                session.ProgressText = session.Message;
                AppendLog(session, $"[Error] {session.Message}");
            }
            finally
            {
                cts.Dispose();
                session.Cts = null;
                PersistSession(session);
                NotifyUpdate(session);
                _activeSessions.TryRemove(sessionId, out _);
                _ = Task.Run(() => TrimHistory());
            }
        }

        // ---------------- 详情页操作（全部经会话管理器后台执行、持久化、跨用户共享） ----------------

        /// <summary>创建一个新的会话对象（未启动）。</summary>
        private DeploySession NewSession(DeployProfile profile, string operation, string triggeredBy,
            string deployType = "", string envName = "")
        {
            return new DeploySession
            {
                Id = Guid.NewGuid().ToString("N"),
                ProfileId = profile.Id,
                ProfileName = profile.Name,
                EnvName = envName ?? string.Empty,
                DeployType = deployType ?? string.Empty,
                Branch = profile.Git?.Branch ?? string.Empty,
                Operation = operation,
                Status = SessionStatus.Running,
                Phase = operation,
                Percent = -1,
                ProgressText = OperationLabel(operation) + "…",
                TriggeredBy = triggeredBy ?? string.Empty,
                StartTime = DateTime.UtcNow,
                IsRunning = true
            };
        }

        private static string OperationLabel(string op) => op switch
        {
            "pull" => "Pull 拉取代码",
            "build" => "Build 编译",
            "pull_build" => "拉取并编译",
            "deploy" => "Deploy 部署",
            "rollback" => "Rollback 回滚",
            "build_image" => "构建镜像",
            _ => "发布"
        };

        /// <summary>通用后台执行器：登记会话 → 后台 scope 解析 DeployService → 执行 work → 写结果 → 持久化/广播/清理。</summary>
        private string Launch(DeploySession session, Func<DeployService, CancellationToken, Func<bool>, Task<OpOutcome>> work)
        {
            var sessionId = session.Id;
            _activeSessions[sessionId] = session;
            PersistSession(session);
            NotifyUpdate(session);
            _ = Task.Run(() => RunOperationAsync(sessionId, work));
            return sessionId;
        }

        private async Task RunOperationAsync(string sessionId, Func<DeployService, CancellationToken, Func<bool>, Task<OpOutcome>> work)
        {
            var session = _activeSessions[sessionId];
            var cts = new CancellationTokenSource();
            session.Cts = cts;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var deployService = scope.ServiceProvider.GetRequiredService<DeployService>();
                var outcome = await work(deployService, cts.Token, () => cts.IsCancellationRequested);

                session.IsRunning = false;
                session.EndTime = DateTime.UtcNow;
                session.SuccessCount = outcome.SuccessCount;
                session.FailCount = outcome.FailCount;
                session.Message = outcome.Message ?? string.Empty;
                session.LastError = outcome.LastError ?? string.Empty;
                session.HostResults = outcome.Hosts ?? new List<HostResult>();

                if (cts.IsCancellationRequested || outcome.Cancelled) session.Status = SessionStatus.Cancelled;
                else if (outcome.Success) session.Status = SessionStatus.Success;
                else if (outcome.Partial) session.Status = SessionStatus.Partial;
                else session.Status = SessionStatus.Failed;

                session.Phase = "done";
                session.Percent = 100;
                session.ProgressText = session.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "操作 {Operation} 异常: {SessionId}", session.Operation, sessionId);
                session.IsRunning = false;
                session.EndTime = DateTime.UtcNow;
                session.Status = SessionStatus.Failed;
                session.Message = $"{OperationLabel(session.Operation)}异常：{ex.GetType().Name}: {ex.Message}";
                session.LastError = ex.Message;
                session.Phase = "fail";
                session.ProgressText = session.Message;
                AppendLog(session, $"[Error] {session.Message}");
            }
            finally
            {
                cts.Dispose();
                session.Cts = null;
                PersistSession(session);
                NotifyUpdate(session);
                _activeSessions.TryRemove(sessionId, out _);
                _ = Task.Run(() => TrimHistory());
            }
        }

        /// <summary>拉取代码。</summary>
        public string StartPull(DeployProfile profile, string triggeredBy = "")
        {
            var session = NewSession(profile, "pull", triggeredBy);
            return Launch(session, async (svc, token, isCancelled) =>
            {
                var ok = await svc.PullAsync(profile, msg => AppendLog(session, msg), isCancelled);
                return BoolOutcome(ok, "拉取完成", "拉取失败");
            });
        }

        /// <summary>编译发布。</summary>
        public string StartBuild(DeployProfile profile, string triggeredBy = "")
        {
            var session = NewSession(profile, "build", triggeredBy);
            return Launch(session, async (svc, token, isCancelled) =>
            {
                var ok = await svc.BuildAsync(profile, msg => AppendLog(session, msg), isCancelled);
                return BoolOutcome(ok, "编译完成", "编译失败");
            });
        }

        /// <summary>拉取并编译。</summary>
        public string StartPullAndBuild(DeployProfile profile, string triggeredBy = "")
        {
            var session = NewSession(profile, "pull_build", triggeredBy);
            return Launch(session, async (svc, token, isCancelled) =>
            {
                var pulled = await svc.PullAsync(profile, msg => AppendLog(session, msg), isCancelled);
                if (!pulled) return BoolOutcome(false, "拉取失败，已中止编译", "拉取失败");
                if (isCancelled()) return new OpOutcome { Cancelled = true, Message = "已取消" };
                var built = await svc.BuildAsync(profile, msg => AppendLog(session, msg), isCancelled);
                return BoolOutcome(built, "拉取并编译完成", "编译失败");
            });
        }

        /// <summary>构建镜像（jib）。</summary>
        public string StartBuildImage(DeployProfile profile, string triggeredBy = "")
        {
            var session = NewSession(profile, "build_image", triggeredBy, "docker");
            return Launch(session, async (svc, token, isCancelled) =>
            {
                var ok = await svc.BuildImageAsync(profile, msg => AppendLog(session, msg), isCancelled);
                return BoolOutcome(ok, "镜像构建成功", "镜像构建失败");
            });
        }

        /// <summary>详情页发布（Agent 或 SSH Docker 通道）。</summary>
        public string StartDetailDeploy(DeployProfile profile, Env env, List<Server> servers, List<LinuxServer> sshServers,
            string deployType, bool dockerUseSsh, List<string>? selectedFiles, string remark, string triggeredBy = "")
        {
            var type = ParsePublishType(deployType, false);
            var session = NewSession(profile, "deploy", triggeredBy, deployType, env?.Name ?? string.Empty);
            session.Remark = ApplyDefaultRemark(remark, profile.Git?.Branch);
            session.DockerUseSsh = dockerUseSsh;
            session.SelectedFiles = selectedFiles ?? new List<string>();
            session.ServerCount = dockerUseSsh ? (sshServers?.Count ?? 0) : (servers?.Count ?? 0);

            return Launch(session, async (svc, token, isCancelled) =>
            {
                if (dockerUseSsh && deployType == "docker")
                {
                    if (sshServers == null || sshServers.Count == 0)
                        return new OpOutcome { Success = false, Message = "未选择 SSH 服务器", LastError = "未选择 SSH 服务器" };
                    var parallel = ResolveParallel(profile.Id, sshServers.Count);
                    var r = await svc.SshDockerDeployAsync(profile, env, sshServers, selectedFiles, remark,
                        msg => AppendLog(session, msg), isCancelled, null, parallel, t => ApplyTimings(session, t));
                    if (r.Success) TryRecordLastDeploy(profile, deployType);
                    return ResultOutcome(r, sshServers.Count);
                }
                else
                {
                    if (servers == null || servers.Count == 0)
                        return new OpOutcome { Success = false, Message = "未选择目标服务器", LastError = "未选择目标服务器" };
                    var run = await ResolveRunOptionsAsync(svc, profile.Id, servers.Count, type, servers, msg => AppendLog(session, msg));
                    var r = await svc.DeployAsync(profile, env, servers, type, msg => AppendLog(session, msg),
                        remark, selectedFiles, isCancelled, token, run, t => ApplyTimings(session, t));
                    if (r.Success) TryRecordLastDeploy(profile, deployType);
                    return ResultOutcome(r, servers.Count);
                }
            });
        }

        /// <summary>详情页回滚（Agent 或 SSH Docker 通道）。</summary>
        public string StartRollback(DeployProfile profile, Env env, List<Server> servers, List<LinuxServer> sshServers,
            string deployType, bool dockerUseSsh, string version, string triggeredBy = "")
        {
            var type = ParsePublishType(deployType, true);
            var session = NewSession(profile, "rollback", triggeredBy, deployType, env?.Name ?? string.Empty);
            session.RollbackVersion = version ?? string.Empty;
            session.DockerUseSsh = dockerUseSsh;
            session.ServerCount = dockerUseSsh ? (sshServers?.Count ?? 0) : (servers?.Count ?? 0);

            return Launch(session, async (svc, token, isCancelled) =>
            {
                if (dockerUseSsh && deployType == "docker")
                {
                    if (sshServers == null || sshServers.Count == 0)
                        return new OpOutcome { Success = false, Message = "未选择 SSH 服务器", LastError = "未选择 SSH 服务器" };
                    var r = await svc.SshDockerRollbackAsync(profile, sshServers, version, msg => AppendLog(session, msg), isCancelled);
                    return ResultOutcome(r, sshServers.Count);
                }
                else
                {
                    if (servers == null || servers.Count == 0)
                        return new OpOutcome { Success = false, Message = "未选择目标服务器", LastError = "未选择目标服务器" };
                    var run = await ResolveRunOptionsAsync(svc, profile.Id, servers.Count, type, servers, msg => AppendLog(session, msg));
                    var r = await svc.RollbackAsync(profile, env, servers, type, version, msg => AppendLog(session, msg),
                        run, t => ApplyTimings(session, t));
                    return ResultOutcome(r, servers.Count);
                }
            });
        }

        /// <summary>重试指定会话的操作（依据会话保存的 Operation + 上下文重新执行）。</summary>
        public string? Retry(string sessionId, string triggeredBy = "")
        {
            var src = GetSession(sessionId);
            if (src == null) return null;
            if (GetRunningSession(src.ProfileId) != null) return null; // 已有运行中会话

            var profile = _configStore.GetProfile(src.ProfileId);
            if (profile == null) return null;

            var env = string.IsNullOrEmpty(src.EnvName)
                ? profile.Envs?.FirstOrDefault()
                : profile.Envs?.FirstOrDefault(e => e.Name == src.EnvName);
            var servers = env?.ServerList?.Where(s => !string.IsNullOrWhiteSpace(s.Host)).ToList() ?? new List<Server>();
            var sshServers = env?.LinuxServerList?.Where(s => !string.IsNullOrWhiteSpace(s.Host)).ToList() ?? new List<LinuxServer>();
            var deployType = string.IsNullOrEmpty(src.DeployType) ? "iis" : src.DeployType;

            return src.Operation switch
            {
                "pull" => StartPull(profile, triggeredBy),
                "build" => StartBuild(profile, triggeredBy),
                "pull_build" => StartPullAndBuild(profile, triggeredBy),
                "build_image" => StartBuildImage(profile, triggeredBy),
                "deploy" => StartDetailDeploy(profile, env, servers, sshServers, deployType, src.DockerUseSsh,
                    src.SelectedFiles, src.Remark, triggeredBy),
                "rollback" => StartRollback(profile, env, servers, sshServers, deployType, src.DockerUseSsh,
                    src.RollbackVersion, triggeredBy),
                _ => null
            };
        }

        /// <summary>获取指定项目最近一条会话（运行中优先，其次数据库最新完成记录），供详情页初始化按钮态与日志。</summary>
        public DeploySession? GetLatestSession(string profileId)
        {
            var running = GetRunningSession(profileId);
            if (running != null) return running;
            if (!_useDb) return null;
            try
            {
                using var db = _dbFactory!.CreateDbContext();
                var entity = db.DeploySessions.AsNoTracking()
                    .Where(e => e.ProfileId == profileId)
                    .OrderByDescending(e => e.StartTime)
                    .FirstOrDefault();
                return entity == null ? null : MapToSession(entity);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载项目最近会话失败: {ProfileId}", profileId);
                return null;
            }
        }

        private void TryRecordLastDeploy(DeployProfile profile, string deployType)
        {
            try
            {
                profile.LastPublishTime = DateTime.Now;
                profile.LastPublishBranch = profile.Git?.Branch ?? string.Empty;
                profile.LastDeployType = deployType;
                _configStore.UpdateProfile(profile);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "更新项目最后发布信息失败: {ProfileId}", profile.Id);
            }
        }

        private static PublishType ParsePublishType(string deployType, bool rollback) => deployType switch
        {
            "windowservice" => rollback ? PublishType.windowservice_rollback : PublishType.windowservice,
            "linux" => rollback ? PublishType.linux_rollback : PublishType.linux,
            "docker" => rollback ? PublishType.docker_rollback : PublishType.docker,
            _ => rollback ? PublishType.iis_rollback : PublishType.iis
        };

        /// <summary>
        /// 将部署类型规范化为四个基础值之一（iis / windowservice / linux / docker）。
        /// <para>
        /// 兼容两类脏数据：回滚操作写入的带后缀值（如 <c>iis_rollback</c>），以及历史遗留的大小写差异（如 <c>IIS</c>）。
        /// 空值或无法识别时回退 <c>iis</c>。UI 层的 <c>&lt;select&gt;</c>、DeployTypeConfig 的 <c>@if(Type=="iis")</c>
        /// 与发布前 WebSiteName 拦截均严格匹配这四个小写值，故读写两端都必须经此规范化，否则会出现
        /// “部署类型显示 iis 但配置区空白 / 必填告警不弹”的问题。
        /// </para>
        /// </summary>
        public static string NormalizeDeployType(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "iis";
            var t = raw.Trim();
            var idx = t.IndexOf("_rollback", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) t = t.Substring(0, idx);
            return t.ToLowerInvariant() switch
            {
                "windowservice" => "windowservice",
                "linux" => "linux",
                "docker" => "docker",
                _ => "iis"
            };
        }

        private static OpOutcome BoolOutcome(bool ok, string okMsg, string failMsg) =>
            new OpOutcome { Success = ok, Message = ok ? okMsg : failMsg, LastError = ok ? string.Empty : failMsg };

        private static OpOutcome ResultOutcome(DeployResult r, int serverCount)
        {
            var msg = r.Msg ?? string.Empty;
            if (r.Success)
                return new OpOutcome { Success = true, Message = string.IsNullOrEmpty(msg) || msg == "ok" ? "操作成功" : msg, SuccessCount = serverCount };
            return new OpOutcome { Success = false, Message = "操作失败", LastError = msg, FailCount = serverCount };
        }

        /// <summary>通用操作结果（把 bool / DeployResult / QuickDeployOutcome 归一）。</summary>
        private class OpOutcome
        {
            public bool Success { get; set; }
            public bool Partial { get; set; }
            public bool Cancelled { get; set; }
            public string Message { get; set; } = string.Empty;
            public string LastError { get; set; } = string.Empty;
            public int SuccessCount { get; set; }
            public int FailCount { get; set; }
            public List<HostResult>? Hosts { get; set; }
        }

        // ---------------- 会话状态更新 ----------------

        private void AppendLog(DeploySession session, string msg)
        {
            if (string.IsNullOrEmpty(msg)) return;
            var entry = new LogEntry
            {
                Time = DateTime.Now.ToString("HH:mm:ss"),
                Text = msg,
                Css = ClassifyLog(msg)
            };
            lock (session.LogLock)
            {
                session.Logs.Add(entry);
                // 限制内存中日志条数，避免无限增长（持久化时取最近 2000 条）
                if (session.Logs.Count > 2000) session.Logs.RemoveAt(0);
            }
            // 日志追加不立即持久化（太频繁），由进度更新或完成时统一持久化
            NotifyUpdate(session);
        }

        private void ApplyProgress(DeploySession session, DeployProgress p)
        {
            session.Phase = p.Phase ?? string.Empty;
            session.Percent = p.Percent;
            session.ProgressText = p.Text ?? string.Empty;
            session.ServerIndex = p.ServerIndex;
            session.ServerCount = p.ServerCount;
            if (p.IsFail)
            {
                session.Status = SessionStatus.Failed;
                session.Message = p.Text ?? string.Empty;
            }

            // 更新每台服务器的步骤进度
            lock (session.ProgressLock)
            {
                var sps = session.ServerProgresses;
                if (sps.Count > 0)
                {
                    // 共享阶段（pull/build/package）：更新所有服务器的编译/打包状态
                    if (p.Phase == "build")
                    {
                        foreach (var sp in sps)
                        {
                            sp.BuildStatus = StepStatus.InProgress;
                            sp.StatusText = "编译中";
                        }
                    }
                    else if (p.Phase == "package")
                    {
                        foreach (var sp in sps)
                        {
                            sp.BuildStatus = StepStatus.Success;
                            sp.PackageStatus = p.Percent >= 100 ? StepStatus.Success : StepStatus.InProgress;
                            sp.StatusText = p.Percent >= 100 ? "待上传" : "打包中";
                        }
                    }
                    else if (p.Phase == "pull")
                    {
                        foreach (var sp in sps)
                        {
                            sp.StatusText = "待编译";
                        }
                    }
                    else if (p.Phase == "upload" || p.Phase == "deploy")
                    {
                        // 共享阶段已完成
                        foreach (var sp in sps)
                        {
                            if (sp.BuildStatus != StepStatus.Failed) sp.BuildStatus = StepStatus.Success;
                            if (sp.PackageStatus != StepStatus.Failed) sp.PackageStatus = StepStatus.Success;
                        }
                        // 更新指定服务器的上传/部署状态
                        if (!string.IsNullOrEmpty(p.ServerHost))
                        {
                            var sp = sps.FirstOrDefault(s => s.Host == p.ServerHost);
                            if (sp != null && !sp.IsFailed)
                            {
                                if (p.Phase == "upload")
                                {
                                    sp.UploadStatus = StepStatus.InProgress;
                                    sp.UploadPercent = p.ServerUploadPercent >= 0 ? p.ServerUploadPercent : 0;
                                    sp.StatusText = string.IsNullOrEmpty(p.ServerStepText) ? $"上传中 {sp.UploadPercent}%" : p.ServerStepText;
                                }
                                else // deploy
                                {
                                    sp.UploadStatus = StepStatus.Success;
                                    sp.UploadPercent = 100;
                                    if (p.ServerStepText == "完成")
                                    {
                                        sp.DeployStatus = StepStatus.Success;
                                        sp.StatusText = "完成";
                                    }
                                    else if (p.ServerStepText?.StartsWith("失败") == true)
                                    {
                                        sp.DeployStatus = StepStatus.Failed;
                                        sp.StatusText = p.ServerStepText;
                                        sp.FailReason = p.ServerStepText;
                                    }
                                    else
                                    {
                                        sp.DeployStatus = StepStatus.InProgress;
                                        sp.StatusText = string.IsNullOrEmpty(p.ServerStepText) ? "部署中" : p.ServerStepText;
                                    }
                                }
                            }
                        }
                        // 未开始的服务器标记为"待上传"
                        foreach (var sp in sps)
                        {
                            if (sp.UploadStatus == StepStatus.Pending && sp.PackageStatus == StepStatus.Success)
                                sp.StatusText = "待上传";
                        }
                    }
                    else if (p.Phase == "fail" || p.Phase == "done")
                    {
                        // 终态：编译/打包失败时标记所有服务器
                        if (p.IsFail)
                        {
                            foreach (var sp in sps)
                            {
                                if (sp.BuildStatus == StepStatus.InProgress)
                                {
                                    sp.BuildStatus = StepStatus.Failed;
                                    sp.StatusText = "失败";
                                    sp.FailReason = p.Text ?? string.Empty;
                                }
                                else if (sp.PackageStatus == StepStatus.InProgress)
                                {
                                    sp.PackageStatus = StepStatus.Failed;
                                    sp.StatusText = "失败";
                                    sp.FailReason = p.Text ?? string.Empty;
                                }
                            }
                        }
                    }
                }
            }

            PersistSession(session);
            NotifyUpdate(session);
        }

        /// <summary>根据服务器列表初始化每台的进度跟踪对象。</summary>
        private static List<ServerProgress> BuildServerProgresses(List<Server> servers)
        {
            if (servers == null || servers.Count == 0) return new List<ServerProgress>();
            return servers.Select((s, i) => new ServerProgress
            {
                Host = s.Host ?? string.Empty,
                Display = string.IsNullOrWhiteSpace(s.NickName) ? (s.Host ?? string.Empty) : $"{s.NickName}({s.Host})",
                Index = i,
                StatusText = "待编译"
            }).ToList();
        }

        private void UpdateProfileLastDeploy(DeployProfile profile, string branch, PublishType type)
        {
            try
            {
                profile.LastPublishTime = DateTime.Now;
                profile.LastPublishBranch = branch ?? string.Empty;
                // 规范化为基础部署类型（剥离 _rollback 后缀），避免回滚把 LastDeployType 写成 iis_rollback 等 UI 无法识别的值
                profile.LastDeployType = NormalizeDeployType(type.ToString());
                _configStore.UpdateProfile(profile);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "更新项目最后发布信息失败: {ProfileId}", profile.Id);
            }
        }

        // ---------------- 持久化 ----------------

        private void PersistSession(DeploySession session)
        {
            if (!_useDb) return; // JSON 模式不持久化会话
            try
            {
                using var db = _dbFactory!.CreateDbContext();
                var entity = db.DeploySessions.FirstOrDefault(e => e.Id == session.Id);
                if (entity == null)
                {
                    entity = new DeploySessionEntity { Id = session.Id };
                    db.DeploySessions.Add(entity);
                }

                entity.ProfileId = session.ProfileId;
                entity.ProfileName = session.ProfileName;
                entity.EnvName = session.EnvName;
                entity.DeployType = session.DeployType;
                entity.Branch = session.Branch;
                entity.Remark = session.Remark;
                entity.Status = session.Status.ToString();
                entity.Phase = session.Phase;
                entity.Percent = session.Percent;
                entity.ProgressText = session.ProgressText;
                entity.ServerIndex = session.ServerIndex;
                entity.ServerCount = session.ServerCount;
                entity.SuccessCount = session.SuccessCount;
                entity.FailCount = session.FailCount;
                entity.LastError = session.LastError;
                entity.Message = session.Message;
                entity.TriggeredBy = session.TriggeredBy;
                entity.StartTime = session.StartTime;
                entity.EndTime = session.EndTime;
                entity.IsRunning = session.IsRunning;
                entity.Operation = session.Operation ?? "quick_deploy";
                entity.RollbackVersion = session.RollbackVersion ?? string.Empty;
                entity.DockerUseSsh = session.DockerUseSsh;
                entity.PullMs = session.PullMs;
                entity.BuildMs = session.BuildMs;
                entity.PackageMs = session.PackageMs;
                entity.UploadMs = session.UploadMs;
                entity.DeployMs = session.DeployMs;

                // 序列化日志与主机结果（取最近 2000 条日志）
                lock (session.LogLock)
                {
                    var logsToSave = session.Logs.Count > 2000
                        ? session.Logs.Skip(session.Logs.Count - 2000).ToList()
                        : session.Logs;
                    entity.Logs = JsonConvert.SerializeObject(logsToSave);
                }
                entity.HostResults = JsonConvert.SerializeObject(session.HostResults ?? new List<HostResult>());
                entity.SelectedFilesJson = JsonConvert.SerializeObject(session.SelectedFiles ?? new List<string>());

                db.SaveChanges();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "持久化会话失败: {SessionId}", session.Id);
            }
        }

        private DeploySession? LoadSessionFromDb(string sessionId)
        {
            if (!_useDb) return null;
            try
            {
                using var db = _dbFactory!.CreateDbContext();
                var entity = db.DeploySessions.AsNoTracking().FirstOrDefault(e => e.Id == sessionId);
                if (entity == null) return null;
                return MapToSession(entity);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "从数据库加载会话失败: {SessionId}", sessionId);
                return null;
            }
        }

        private static DeploySession MapToSession(DeploySessionEntity e)
        {
            var session = new DeploySession
            {
                Id = e.Id,
                ProfileId = e.ProfileId,
                ProfileName = e.ProfileName,
                EnvName = e.EnvName,
                DeployType = e.DeployType,
                Branch = e.Branch,
                Remark = e.Remark,
                Status = Enum.TryParse<SessionStatus>(e.Status, out var st) ? st : SessionStatus.Failed,
                Phase = e.Phase,
                Percent = e.Percent,
                ProgressText = e.ProgressText,
                ServerIndex = e.ServerIndex,
                ServerCount = e.ServerCount,
                SuccessCount = e.SuccessCount,
                FailCount = e.FailCount,
                LastError = e.LastError,
                Message = e.Message,
                TriggeredBy = e.TriggeredBy,
                StartTime = e.StartTime,
                EndTime = e.EndTime,
                IsRunning = e.IsRunning,
                Operation = e.Operation ?? "quick_deploy",
                RollbackVersion = e.RollbackVersion ?? string.Empty,
                DockerUseSsh = e.DockerUseSsh,
                PullMs = e.PullMs,
                BuildMs = e.BuildMs,
                PackageMs = e.PackageMs,
                UploadMs = e.UploadMs,
                DeployMs = e.DeployMs
            };

            try
            {
                if (!string.IsNullOrWhiteSpace(e.SelectedFilesJson))
                    session.SelectedFiles = JsonConvert.DeserializeObject<List<string>>(e.SelectedFilesJson) ?? new List<string>();
            }
            catch { session.SelectedFiles = new List<string>(); }

            try
            {
                if (!string.IsNullOrWhiteSpace(e.Logs))
                    session.Logs = JsonConvert.DeserializeObject<List<LogEntry>>(e.Logs) ?? new List<LogEntry>();
            }
            catch { session.Logs = new List<LogEntry>(); }

            try
            {
                if (!string.IsNullOrWhiteSpace(e.HostResults))
                    session.HostResults = JsonConvert.DeserializeObject<List<HostResult>>(e.HostResults) ?? new List<HostResult>();
            }
            catch { session.HostResults = new List<HostResult>(); }

            return session;
        }

        // ---------------- 历史管理 ----------------

        /// <summary>获取发布历史（按开始时间倒序，最多 limit 条）</summary>
        public List<DeploySession> GetHistory(int limit = 100, string? profileId = null)
        {
            if (!_useDb) return new List<DeploySession>();
            try
            {
                using var db = _dbFactory!.CreateDbContext();
                var query = db.DeploySessions.AsNoTracking().Where(e => !e.IsRunning);
                if (!string.IsNullOrEmpty(profileId))
                    query = query.Where(e => e.ProfileId == profileId);
                var entities = query.OrderByDescending(e => e.StartTime).Take(limit).ToList();
                return entities.Select(MapToSession).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取发布历史失败");
                return new List<DeploySession>();
            }
        }

        /// <summary>获取单条历史详情（含完整日志）</summary>
        public DeploySession? GetHistoryDetail(string sessionId)
        {
            return LoadSessionFromDb(sessionId);
        }

        /// <summary>上次发布耗时明细（毫秒）：Total=会话总时长，其余为各阶段分段耗时。</summary>
        public record LastCost(long Total, long Pull, long Build, long Package, long Upload, long Deploy);

        /// <summary>
        /// 批量获取各项目“最近一次已完成的发布类会话”（Operation ∈ quick_deploy/deploy）的耗时，
        /// 供项目列表“上次耗时”列展示。DB 不可用或无记录时返回空字典。
        /// </summary>
        public Dictionary<string, LastCost> GetLastCompletedTimings(IEnumerable<string> profileIds)
        {
            var result = new Dictionary<string, LastCost>();
            if (!_useDb || profileIds == null) return result;
            try
            {
                var ids = profileIds.Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();
                if (ids.Count == 0) return result;
                using var db = _dbFactory!.CreateDbContext();
                var rows = db.DeploySessions.AsNoTracking()
                    .Where(e => ids.Contains(e.ProfileId) && !e.IsRunning
                        && (e.Operation == "quick_deploy" || e.Operation == "deploy"))
                    .OrderByDescending(e => e.StartTime)
                    .Select(e => new { e.ProfileId, e.StartTime, e.EndTime, e.PullMs, e.BuildMs, e.PackageMs, e.UploadMs, e.DeployMs })
                    .ToList();
                foreach (var g in rows.GroupBy(r => r.ProfileId))
                {
                    var e = g.First(); // 已按 StartTime 倒序，取每个项目最近一条
                    var total = e.EndTime.HasValue ? (long)Math.Max(0, (e.EndTime.Value - e.StartTime).TotalMilliseconds) : 0;
                    result[g.Key] = new LastCost(total, e.PullMs, e.BuildMs, e.PackageMs, e.UploadMs, e.DeployMs);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取上次发布耗时失败");
            }
            return result;
        }

        /// <summary>清理超出上限的历史记录</summary>
        private void TrimHistory()
        {
            if (!_useDb) return;
            try
            {
                var max = _options.HistoryMaxRecords;
                if (max <= 0) max = 100;

                using var db = _dbFactory!.CreateDbContext();
                var count = db.DeploySessions.Count(e => !e.IsRunning);
                if (count <= max) return;

                var toDelete = db.DeploySessions
                    .Where(e => !e.IsRunning)
                    .OrderByDescending(e => e.StartTime)
                    .Skip(max)
                    .Select(e => e.Id)
                    .ToList();

                if (toDelete.Count > 0)
                {
                    db.DeploySessions.Where(e => toDelete.Contains(e.Id)).ExecuteDelete();
                    _logger.LogInformation("已清理 {Count} 条超出上限的发布历史记录", toDelete.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "清理发布历史失败");
            }
        }

        /// <summary>手动清理指定会话的历史记录</summary>
        public void DeleteHistory(string sessionId)
        {
            if (!_useDb) return;
            try
            {
                using var db = _dbFactory!.CreateDbContext();
                var entity = db.DeploySessions.FirstOrDefault(e => e.Id == sessionId);
                if (entity != null)
                {
                    db.DeploySessions.Remove(entity);
                    db.SaveChanges();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除历史记录失败: {SessionId}", sessionId);
            }
        }

        // ---------------- 通知 ----------------

        private void NotifyUpdate(DeploySession session)
        {
            try
            {
                OnSessionUpdated?.Invoke(session.Id, session);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "通知会话更新失败: {SessionId}", session.Id);
            }
        }

        private static string ClassifyLog(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return string.Empty;
            if (msg.Contains("[Error]") || msg.Contains("Fail") || msg.Contains("失败")) return "log-error";
            if (msg.Contains("[Warn]") || msg.Contains("警告")) return "log-warn";
            return string.Empty;
        }

        public void Dispose()
        {
            foreach (var session in _activeSessions.Values)
            {
                try { session.Cts?.Cancel(); session.Cts?.Dispose(); } catch { }
            }
            _activeSessions.Clear();
        }
    }

    /// <summary>会话状态枚举</summary>
    public enum SessionStatus
    {
        Running,
        Success,
        Failed,
        Cancelled,
        Partial
    }

    /// <summary>
    /// 内存中的部署会话对象（供 UI 绑定与事件通知）。
    /// </summary>
    public class DeploySession
    {
        public string Id { get; set; } = string.Empty;
        public string ProfileId { get; set; } = string.Empty;
        public string ProfileName { get; set; } = string.Empty;
        public string EnvName { get; set; } = string.Empty;
        public string DeployType { get; set; } = string.Empty;
        public string Branch { get; set; } = string.Empty;
        public string Remark { get; set; } = string.Empty;
        public SessionStatus Status { get; set; } = SessionStatus.Running;
        public string Phase { get; set; } = string.Empty;
        public int Percent { get; set; } = -1;
        public string ProgressText { get; set; } = string.Empty;
        public int ServerIndex { get; set; }
        public int ServerCount { get; set; }
        public int SuccessCount { get; set; }
        public int FailCount { get; set; }
        public string LastError { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string TriggeredBy { get; set; } = string.Empty;
        public DateTime StartTime { get; set; } = DateTime.UtcNow;
        public DateTime? EndTime { get; set; }
        public bool IsRunning { get; set; } = true;

        /// <summary>操作类型：quick_deploy / deploy / pull / build / pull_build / rollback / build_image</summary>
        public string Operation { get; set; } = "quick_deploy";
        /// <summary>回滚目标版本（重试上下文）</summary>
        public string RollbackVersion { get; set; } = string.Empty;
        /// <summary>Docker 是否走 SSH 直连通道（重试上下文）</summary>
        public bool DockerUseSsh { get; set; }
        /// <summary>选择文件部署的文件列表（重试上下文）</summary>
        public List<string> SelectedFiles { get; set; } = new();

        /// <summary>各阶段 wall-clock 耗时（毫秒，未涉及的阶段为 0）</summary>
        public long PullMs { get; set; }
        public long BuildMs { get; set; }
        public long PackageMs { get; set; }
        public long UploadMs { get; set; }
        public long DeployMs { get; set; }

        public List<LogEntry> Logs { get; set; } = new();
        public List<HostResult> HostResults { get; set; } = new();

        /// <summary>日志列表的锁（后台线程追加 vs UI 线程读取）</summary>
        [JsonIgnore]
        public object LogLock { get; } = new object();

        /// <summary>取消令牌源（仅运行中有效）</summary>
        [JsonIgnore]
        public CancellationTokenSource Cts { get; set; }

        public bool IsSuccess => Status == SessionStatus.Success;
        public bool IsFailed => Status == SessionStatus.Failed;
        public bool IsCancelled => Status == SessionStatus.Cancelled;
        public bool IsPartial => Status == SessionStatus.Partial;

        public string StatusText => Status switch
        {
            SessionStatus.Running => "进行中",
            SessionStatus.Success => "成功",
            SessionStatus.Failed => "失败",
            SessionStatus.Cancelled => "已取消",
            SessionStatus.Partial => "部分成功",
            _ => Status.ToString()
        };

        /// <summary>操作的中文名称（供历史/日志展示）。</summary>
        public string OperationText => Operation switch
        {
            "pull" => "拉取代码",
            "build" => "编译发布",
            "pull_build" => "拉取并编译",
            "deploy" => "发布部署",
            "rollback" => "版本回滚",
            "build_image" => "构建镜像",
            _ => "一键发布"
        };

        public string StatusColor => Status switch
        {
            SessionStatus.Running => "blue",
            SessionStatus.Success => "green",
            SessionStatus.Failed => "red",
            SessionStatus.Cancelled => "default",
            SessionStatus.Partial => "orange",
            _ => "default"
        };

        /// <summary>每台服务器的实时发布进度（运行中由 ApplyProgress 更新，完成后保留终态）。</summary>
        public List<ServerProgress> ServerProgresses { get; set; } = new();

        /// <summary>ServerProgresses 的锁（后台线程写 vs UI 线程读）。</summary>
        [JsonIgnore]
        public object ProgressLock { get; } = new object();

        /// <summary>整体进度百分比（0-100），综合共享阶段 + 每台服务器上传/部署进度计算。</summary>
        public int OverallPercent
        {
            get
            {
                // 共享阶段权重：pull=5%, build=15%, package=5%, 剩余 75% 按服务器均分
                var phaseWeight = Phase switch
                {
                    "pull" => 2,
                    "build" => 8,
                    "package" => 15,
                    _ => 25 // upload/deploy 阶段基础 25%
                };
                if (Phase == "done" || Phase == "fail") return 100;
                if (Phase == "pull" || Phase == "build" || Phase == "package")
                    return Percent >= 0 ? phaseWeight : phaseWeight - 2;

                // upload/deploy 阶段：25% + 75% * 各服务器平均进度
                lock (ProgressLock)
                {
                    if (ServerProgresses.Count == 0) return phaseWeight;
                    var sum = ServerProgresses.Sum(s => s.OverallPercent);
                    return phaseWeight + (int)(sum * 75.0 / ServerProgresses.Count / 100.0);
                }
            }
        }
    }

    /// <summary>单台服务器的发布步骤进度（编译/打包为共享步骤，上传/部署为逐台步骤）。</summary>
    public class ServerProgress
    {
        public string Host { get; set; } = string.Empty;
        public string Display { get; set; } = string.Empty;
        public int Index { get; set; }

        public StepStatus BuildStatus { get; set; } = StepStatus.Pending;
        public StepStatus PackageStatus { get; set; } = StepStatus.Pending;
        public StepStatus UploadStatus { get; set; } = StepStatus.Pending;
        public StepStatus DeployStatus { get; set; } = StepStatus.Pending;

        /// <summary>上传百分比 0-100（仅 UploadStatus=InProgress 时有意义）。</summary>
        public int UploadPercent { get; set; }

        /// <summary>当前状态文案（待编译/编译中/待打包/打包中/待上传/上传中 X%/待部署/部署中/延迟等待中/完成/失败）。</summary>
        public string StatusText { get; set; } = "等待中";

        /// <summary>失败原因（仅 Failed 时非空）。</summary>
        public string FailReason { get; set; } = string.Empty;

        /// <summary>该服务器的综合进度百分比（0-100），用于计算整体进度。</summary>
        public int OverallPercent
        {
            get
            {
                // 编译 20% + 打包 10% + 上传 40% + 部署 30%
                var p = 0;
                if (BuildStatus == StepStatus.Success) p += 20;
                else if (BuildStatus == StepStatus.InProgress) p += 10;
                else if (BuildStatus == StepStatus.Failed) return p;

                if (PackageStatus == StepStatus.Success) p += 10;
                else if (PackageStatus == StepStatus.InProgress) p += 5;
                else if (PackageStatus == StepStatus.Failed) return p;

                if (UploadStatus == StepStatus.Success) p += 40;
                else if (UploadStatus == StepStatus.InProgress) p += (int)(UploadPercent * 0.4);
                else if (UploadStatus == StepStatus.Failed) return p;

                if (DeployStatus == StepStatus.Success) p += 30;
                else if (DeployStatus == StepStatus.InProgress) p += 15;

                return Math.Min(100, p);
            }
        }

        public bool IsDone => DeployStatus == StepStatus.Success || DeployStatus == StepStatus.Failed
            || UploadStatus == StepStatus.Failed || BuildStatus == StepStatus.Failed || PackageStatus == StepStatus.Failed;
        public bool IsSuccess => DeployStatus == StepStatus.Success;
        public bool IsFailed => BuildStatus == StepStatus.Failed || PackageStatus == StepStatus.Failed
            || UploadStatus == StepStatus.Failed || DeployStatus == StepStatus.Failed;
    }

    /// <summary>步骤状态枚举。</summary>
    public enum StepStatus
    {
        Pending,
        InProgress,
        Success,
        Failed
    }
}
