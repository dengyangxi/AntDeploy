using System;
using System.Net;

namespace AntDeployAgentWindows.Util
{
    /// <summary>
    /// 发布回调工具：更新前(下负载)/更新成功后(上负载) 调用 HTTP 接口。
    /// 选填、best-effort：任何异常只记日志，不阻断发布流程。
    /// 支持两种地址：
    ///  - 完整 http(s):// 地址：直接 GET 调用（通常指向负载均衡的摘除/挂回接口）；
    ///  - 以 "/" 开头的相对路径：由 Agent 组装 http://127.0.0.1:{port}{path} 调用本机站点
    ///    （port 由发布提供方解析，如 IIS 站点绑定端口）。
    /// 采用 HttpWebRequest 而非 HttpClient，兼容 net472 与 netstandard2.0 双目标且无需额外程序集引用。
    /// </summary>
    public static class CallbackHelper
    {
        /// <summary>
        /// 发起一次回调。urlOrNull 为空表示未配置（选填），直接返回。
        /// </summary>
        /// <param name="urlOrNull">完整 http(s) 地址，或以 / 开头的相对路径</param>
        /// <param name="portForRelative">相对路径回调时用于组装本机地址的站点端口，可空</param>
        /// <param name="timeoutSeconds">超时秒数，&lt;=0 时取默认 10s</param>
        /// <param name="log">日志回调（Agent 的 Log）</param>
        public static void Invoke(string urlOrNull, string portForRelative, int timeoutSeconds, Action<string> log)
        {
            if (string.IsNullOrWhiteSpace(urlOrNull)) return;
            var url = urlOrNull.Trim();
            try
            {
                string target;
                if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    target = url;
                }
                else if (url.StartsWith("/"))
                {
                    if (string.IsNullOrWhiteSpace(portForRelative))
                    {
                        log?.Invoke($"【callback】相对路径回调 [{url}] 需要站点端口，但未解析到端口，已跳过");
                        return;
                    }
                    target = $"http://127.0.0.1:{portForRelative.Trim()}{url}";
                }
                else
                {
                    log?.Invoke($"【callback】回调地址 [{url}] 既不是完整 http(s):// 也不以 / 开头，已跳过");
                    return;
                }

                if (timeoutSeconds <= 0) timeoutSeconds = 10;
                log?.Invoke($"【callback】调用 {target} (超时 {timeoutSeconds}s)");

                var req = (HttpWebRequest)WebRequest.Create(target);
                req.Method = "GET";
                req.Timeout = timeoutSeconds * 1000;
                req.ReadWriteTimeout = timeoutSeconds * 1000;
                req.KeepAlive = false;
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    log?.Invoke($"【callback】{target} 返回 {(int)resp.StatusCode}");
                }
            }
            catch (WebException wex)
            {
                // 非 2xx 也尽量读出状态码供诊断；best-effort，不阻断发布
                var errResp = wex.Response as HttpWebResponse;
                if (errResp != null)
                {
                    log?.Invoke($"【callback】调用返回非成功状态 {(int)errResp.StatusCode}（已忽略，不阻断发布）：{wex.Message}");
                }
                else
                {
                    log?.Invoke($"【callback】调用失败（已忽略，不阻断发布）：{wex.Message}");
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"【callback】调用异常（已忽略，不阻断发布）：{ex.Message}");
            }
        }
    }
}
