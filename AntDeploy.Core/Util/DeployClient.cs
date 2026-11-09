using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AntDeploy.Core.Models;
using Newtonsoft.Json;

namespace AntDeploy.Core.Util
{
    /// <summary>
    /// 与 AntDeployAgent 通信的协议客户端：
    /// - /publish  multipart 上传发布包
    /// - /rollback multipart 上传回滚请求
    /// - /version  预检
    /// - /logger   轮询拉取实时日志（List&lt;LoggerModel&gt;，已读标记 IsActive）
    /// 日志通道采用 HTTP 轮询（与 Agent 的 loggerCollection[id] 一致），无需 WebSocket。
    /// </summary>
    public class DeployClient
    {
        private readonly HttpClient _http;
        private readonly ILog _log;

        public DeployClient(ILog log, HttpMessageHandler handler = null)
        {
            _log = log;
            _http = new HttpClient(handler ?? new HttpClientHandler { UseProxy = false, Proxy = null })
            {
                Timeout = TimeSpan.FromMinutes(30)
            };
        }

        /// <summary>
        /// 发布一个 zip 包到 Agent。
        /// </summary>
        /// <param name="agentUrl">Agent 地址（自动补全 http://）</param>
        /// <param name="token">Agent 令牌</param>
        /// <param name="publishType">发布类型</param>
        /// <param name="extraFields">业务字段（webSiteName / port / poolName ...）</param>
        /// <param name="zipStream">已打包的发布包流（调用方负责释放）</param>
        /// <param name="loggerId">日志 key（GUID），与 /logger 轮询对应</param>
        /// <param name="onLog">每条日志回调</param>
        public async Task<DeployResult> PublishAsync(string agentUrl, string token, PublishType publishType,
            Dictionary<string, string> extraFields, Stream zipStream, string loggerId, Action<string> onLog,
            CancellationToken cancellationToken = default)
        {
            agentUrl = CodingHelper.NormalizeAgentUrl(agentUrl);
            var content = CreateMultipart();

            foreach (var kv in extraFields ?? new Dictionary<string, string>())
                content.Add(new StringContent(kv.Value ?? string.Empty), kv.Key);

            content.Add(new StringContent(token ?? string.Empty), "Token");
            content.Add(new StringContent(loggerId), "id");
            content.Add(new StringContent(publishType.ToString()), "publishType");

            // 用进度流包裹发布包：上传大文件（几十~上百 MB）时按节流回调进度，
            // 避免界面长时间只显示“uploading”而被误认为“无响应”。
            // 关键：ProgressStream 必须透传 CanSeek/Length，MultipartFormDataContent 依赖它计算 Content-Length，
            // 否则会退化为 chunked 传输，Agent 端 FormHandler 因缺少 Content-Length 直接抛错。
            long total = zipStream.CanSeek ? zipStream.Length : -1;
            Stream uploadStream = total > 0
                ? new ProgressStream(zipStream, total, (sent, tot) =>
                    onLog?.Invoke($"【deploy】上传进度 {sent * 100 / tot}%  ({sent / 1048576.0:F1} MB / {tot / 1048576.0:F1} MB)"))
                : zipStream;

            var fileContent = new StreamContent(uploadStream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(fileContent, "publish", "publish.zip");

            using (content)
            {
                // 与外部取消令牌联动：点击“停止”可中断正在进行的上传（PostAsync 传入 cancellationToken）。
                var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var pollTask = PollLogsAsync(agentUrl, loggerId, onLog, cts.Token);
                DeployResult result;
                try
                {
                    var sizeText = total > 0 ? (total / 1048576.0).ToString("F1") + " MB" : "unknown";
                    _log.Info($"【deploy】uploading package to {agentUrl}/publish (type:{publishType}, size:{sizeText})");
                    var resp = await _http.PostAsync(agentUrl + "/publish", content, cancellationToken);
                    var body = await resp.Content.ReadAsStringAsync();
                    result = ParseResult(body, resp.IsSuccessStatusCode);
                }
                catch (OperationCanceledException)
                {
                    result = new DeployResult { Success = false, Msg = "上传已取消" };
                }
                catch (Exception ex)
                {
                    result = new DeployResult { Success = false, Msg = "upload fail: " + ex.Message };
                }
                finally
                {
                    cts.Cancel();
                }

                // 收尾轮询，确保尾部日志被读取
                try { await FinalDrainAsync(agentUrl, loggerId, onLog); } catch { }
                try { await pollTask; } catch { }
                cts.Dispose();

                if (result.Success) _log.Info("【deploy】Deploy Success");
                else _log.Error("【deploy】Deploy Fail: " + result.Msg);
                return result;
            }
        }

        /// <summary>
        /// 两阶段发布的第二阶段：仅触发部署，不上传 zip。
        /// 前提是第一阶段（PublishAsync，extraFields 内含 deployPhase=upload）已把包上传并解压到同一 deployFolderName 版本目录。
        /// extraFields 内应包含 deployPhase=deploy。Agent 端据此定位已解压目录并执行站点操作。
        /// 与 PublishAsync 一致：复用日志轮询、取消联动、boundary 不带引号的 multipart。
        /// </summary>
        public async Task<DeployResult> DeployCommitAsync(string agentUrl, string token, PublishType publishType,
            Dictionary<string, string> extraFields, string loggerId, Action<string> onLog,
            CancellationToken cancellationToken = default)
        {
            agentUrl = CodingHelper.NormalizeAgentUrl(agentUrl);
            var content = CreateMultipart();

            foreach (var kv in extraFields ?? new Dictionary<string, string>())
                content.Add(new StringContent(kv.Value ?? string.Empty), kv.Key);

            content.Add(new StringContent(token ?? string.Empty), "Token");
            content.Add(new StringContent(loggerId), "id");
            content.Add(new StringContent(publishType.ToString()), "publishType");
            // 关键：不添加 "publish" 文件分部，Agent 端在 deployPhase=deploy 时放开 file required 校验。

            using (content)
            {
                var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var pollTask = PollLogsAsync(agentUrl, loggerId, onLog, cts.Token);
                DeployResult result;
                try
                {
                    _log.Info($"【deploy】commit deploy to {agentUrl}/publish (type:{publishType}, phase:deploy)");
                    var resp = await _http.PostAsync(agentUrl + "/publish", content, cancellationToken);
                    var body = await resp.Content.ReadAsStringAsync();
                    result = ParseResult(body, resp.IsSuccessStatusCode);
                }
                catch (OperationCanceledException)
                {
                    result = new DeployResult { Success = false, Msg = "部署已取消" };
                }
                catch (Exception ex)
                {
                    result = new DeployResult { Success = false, Msg = "commit deploy fail: " + ex.Message };
                }
                finally
                {
                    cts.Cancel();
                }

                try { await FinalDrainAsync(agentUrl, loggerId, onLog); } catch { }
                try { await pollTask; } catch { }
                cts.Dispose();

                if (result.Success) _log.Info("【deploy】Commit Deploy Success");
                else _log.Error("【deploy】Commit Deploy Fail: " + result.Msg);
                return result;
            }
        }

        /// <summary>回滚到指定版本</summary>
        public async Task<DeployResult> RollbackAsync(string agentUrl, string token, PublishType rollbackType,
            string version, Dictionary<string, string> extraFields, string loggerId, Action<string> onLog)
        {
            agentUrl = CodingHelper.NormalizeAgentUrl(agentUrl);
            var content = CreateMultipart();
            content.Add(new StringContent(token ?? string.Empty), "Token");
            content.Add(new StringContent(loggerId), "id");
            content.Add(new StringContent(rollbackType.ToString()), "publishType");
            content.Add(new StringContent(version ?? string.Empty), "version");
            foreach (var kv in extraFields ?? new Dictionary<string, string>())
                content.Add(new StringContent(kv.Value ?? string.Empty), kv.Key);

            using (content)
            {
                var cts = new CancellationTokenSource();
                var pollTask = PollLogsAsync(agentUrl, loggerId, onLog, cts.Token);
                DeployResult result;
                try
                {
                    var resp = await _http.PostAsync(agentUrl + "/rollback", content);
                    var body = await resp.Content.ReadAsStringAsync();
                    result = ParseResult(body, resp.IsSuccessStatusCode);
                }
                catch (Exception ex)
                {
                    result = new DeployResult { Success = false, Msg = "rollback fail: " + ex.Message };
                }
                finally { cts.Cancel(); }
                try { await FinalDrainAsync(agentUrl, loggerId, onLog); } catch { }
                try { await pollTask; } catch { }
                return result;
            }
        }

        /// <summary>预检：/version。Type 如 checkiis / checkwindowservice / checklinux / checkdocker</summary>
        public async Task<T> CheckVersionAsync<T>(string agentUrl, string token, string type, string name, string mac = null, string pc = null) where T : class
        {
            agentUrl = CodingHelper.NormalizeAgentUrl(agentUrl);
            var fields = new Dictionary<string, string>
            {
                ["Token"] = token ?? string.Empty,
                ["Type"] = type,
                ["Name"] = name ?? string.Empty,
                ["Mac"] = mac ?? CodingHelper.GetMacAddress(),
                ["Pc"] = pc ?? Environment.MachineName
            };
            var content = new FormUrlEncodedContent(fields);
            try
            {
                var resp = await _http.PostAsync(agentUrl + "/version", content);
                var body = await resp.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(body)) return null;
                return JsonConvert.DeserializeObject<T>(body);
            }
            catch (Exception ex)
            {
                _log.Error("【version】check fail: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 获取 Agent 版本号（/version 空 body 返回纯文本版本）。用于能力探测：
        /// 版本 &gt;= <see cref="AgentCapability.MinTwoPhaseVersion"/> 才支持两阶段发布与回调。
        /// 失败返回 null（调用方按“不支持”保守降级）。
        /// </summary>
        public async Task<string> GetAgentVersionAsync(string agentUrl, CancellationToken cancellationToken = default)
        {
            agentUrl = CodingHelper.NormalizeAgentUrl(agentUrl);
            try
            {
                var resp = await _http.PostAsync(agentUrl + "/version", new StringContent(string.Empty), cancellationToken);
                var body = await resp.Content.ReadAsStringAsync();
                return body?.Trim();
            }
            catch (Exception ex)
            {
                _log.Error("【version】get agent version fail: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 读取 Agent 当前配置（/config，Action=get）。返回脱敏后的配置；失败返回 Success=false。
        /// 需 Agent 版本 &gt;= <see cref="AgentCapability.MinConfigSyncVersion"/>（调用方应先探测）。
        /// </summary>
        public async Task<AgentConfigResult> GetAgentConfigAsync(string agentUrl, string token, CancellationToken cancellationToken = default)
        {
            agentUrl = CodingHelper.NormalizeAgentUrl(agentUrl);
            try
            {
                var payload = JsonConvert.SerializeObject(new { Token = token ?? string.Empty, Action = "get" });
                var resp = await _http.PostAsync(agentUrl + "/config", new StringContent(payload, Encoding.UTF8, "application/json"), cancellationToken);
                var body = await resp.Content.ReadAsStringAsync();
                return ParseConfigResult(body, resp.IsSuccessStatusCode);
            }
            catch (Exception ex)
            {
                _log.Error("【config】get agent config fail: " + ex.Message);
                return new AgentConfigResult { Success = false, Msg = ex.Message };
            }
        }

        /// <summary>
        /// 推送配置到 Agent（/config，Action=set）：Agent 端原子写 agentsettings.json 并热加载。
        /// RestartNeeded=true 表示 Port 变更需重启服务生效。失败返回 Success=false。
        /// </summary>
        public async Task<AgentConfigResult> PushAgentConfigAsync(string agentUrl, string token, AgentConfigDto config, CancellationToken cancellationToken = default)
        {
            agentUrl = CodingHelper.NormalizeAgentUrl(agentUrl);
            if (config == null) return new AgentConfigResult { Success = false, Msg = "config is null" };
            try
            {
                var payload = JsonConvert.SerializeObject(new { Token = token ?? string.Empty, Action = "set", Config = config });
                var resp = await _http.PostAsync(agentUrl + "/config", new StringContent(payload, Encoding.UTF8, "application/json"), cancellationToken);
                var body = await resp.Content.ReadAsStringAsync();
                var r = ParseConfigResult(body, resp.IsSuccessStatusCode);
                if (r.Success) _log.Info("【config】push agent config success" + (r.RestartNeeded ? " (port changed, restart needed)" : string.Empty));
                else _log.Error("【config】push agent config fail: " + r.Msg);
                return r;
            }
            catch (Exception ex)
            {
                _log.Error("【config】push agent config fail: " + ex.Message);
                return new AgentConfigResult { Success = false, Msg = ex.Message };
            }
        }

        private AgentConfigResult ParseConfigResult(string body, bool httpOk)
        {
            if (string.IsNullOrWhiteSpace(body))
                return new AgentConfigResult { Success = false, Msg = httpOk ? "empty response" : "http error" };
            try
            {
                var r = JsonConvert.DeserializeObject<AgentConfigResult>(body);
                return r ?? new AgentConfigResult { Success = false, Msg = body };
            }
            catch
            {
                return new AgentConfigResult { Success = false, Msg = body };
            }
        }

        private async Task PollLogsAsync(string agentUrl, string loggerId, Action<string> onLog, CancellationToken token)
        {
            var url = agentUrl + "/logger?key=" + Uri.EscapeDataString(loggerId);
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using (var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token))
                    {
                        var body = await resp.Content.ReadAsStringAsync();
                        AppendLogs(body, onLog);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { }
                try { await Task.Delay(700, token); } catch { break; }
            }
        }

        private async Task FinalDrainAsync(string agentUrl, string loggerId, Action<string> onLog)
        {
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    var resp = await _http.GetAsync(agentUrl + "/logger?key=" + Uri.EscapeDataString(loggerId));
                    var body = await resp.Content.ReadAsStringAsync();
                    AppendLogs(body, onLog);
                }
                catch { }
                await Task.Delay(300);
            }
        }

        private void AppendLogs(string body, Action<string> onLog)
        {
            if (string.IsNullOrWhiteSpace(body)) return;
            try
            {
                var logs = JsonConvert.DeserializeObject<List<LoggerModel>>(body);
                if (logs == null) return;
                foreach (var l in logs)
                {
                    if (!string.IsNullOrEmpty(l.Msg)) onLog?.Invoke(l.Msg);
                }
            }
            catch { }
        }

        /// <summary>
        /// 创建 multipart 请求体，并把 Content-Type 的 boundary 重写为“不带引号”的形式。
        /// 原因：.NET 的 MultipartFormDataContent 会把 boundary 用双引号包裹写入 Content-Type
        /// （boundary="----Xxx"），而 Agent 端 FormHandler 解析时用 "--" + Substring(9) 直接截取、
        /// 不会去掉引号，导致算出的分隔符带引号而与 body 中真实分隔符不匹配 → 整个 multipart 解析为空 →
        /// 读不到 Token 等字段 → Agent 返回 "token required"。body 分隔符由内部 boundary 决定、
        /// 不受此 header 影响，因此重写 header 为不带引号后即可与 Agent 对齐。
        /// </summary>
        private static MultipartFormDataContent CreateMultipart()
        {
            var boundary = "----AntDeployBoundary" + Guid.NewGuid().ToString("N");
            var content = new MultipartFormDataContent(boundary);
            content.Headers.ContentType = null;
            content.Headers.TryAddWithoutValidation("Content-Type", "multipart/form-data; boundary=" + boundary);
            return content;
        }

        private DeployResult ParseResult(string body, bool httpOk)
        {
            if (string.IsNullOrWhiteSpace(body))
                return new DeployResult { Success = false, Msg = httpOk ? "empty response" : "http error" };
            try
            {
                var r = JsonConvert.DeserializeObject<DeployResult>(body);
                return r ?? new DeployResult { Success = false, Msg = body };
            }
            catch
            {
                return new DeployResult { Success = false, Msg = body };
            }
        }

        /// <summary>
        /// 只读进度流：包裹上传流，按“时间(≥3s)或百分比(≥10%)”节流回调已发送字节数。
        /// 透传 CanSeek/Length/Position，保证 MultipartFormDataContent 能算出 Content-Length。
        /// </summary>
        private sealed class ProgressStream : Stream
        {
            private readonly Stream _inner;
            private readonly long _total;
            private readonly Action<long, long> _onProgress;
            private long _sent;
            private DateTime _last = DateTime.MinValue;
            private int _lastPct = -1;

            public ProgressStream(Stream inner, long total, Action<long, long> onProgress)
            {
                _inner = inner;
                _total = total;
                _onProgress = onProgress;
            }

            public override bool CanRead => _inner.CanRead;
            public override bool CanSeek => _inner.CanSeek;
            public override bool CanWrite => false;
            public override long Length => _inner.Length;
            public override long Position { get => _inner.Position; set => _inner.Position = value; }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override int Read(byte[] buffer, int offset, int count)
            {
                int n = _inner.Read(buffer, offset, count);
                Advance(n);
                return n;
            }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            {
                int n = await _inner.ReadAsync(buffer, offset, count, ct).ConfigureAwait(false);
                Advance(n);
                return n;
            }

            private void Advance(int n)
            {
                if (n <= 0) return;
                _sent += n;
                if (_total <= 0) return;
                var now = DateTime.Now;
                int pct = (int)(_sent * 100 / _total);
                if ((now - _last).TotalSeconds >= 3 || pct >= _lastPct + 10 || _sent >= _total)
                {
                    _last = now;
                    _lastPct = pct;
                    _onProgress?.Invoke(_sent, _total);
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) _inner.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
