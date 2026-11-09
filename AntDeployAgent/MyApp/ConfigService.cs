using AntDeployAgentWindows.Model;
using AntDeployAgentWindows.WebApiCore;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Text;

namespace AntDeployAgentWindows.MyApp
{
    /// <summary>
    /// /config 端点：在线读取(get) / 更新(set) Agent 配置（agentsettings.json），token 鉴权。
    /// <para>更新后由 <see cref="AgentConfigLoader"/> 原子写盘并热加载即时生效；
    /// 其中 Port 变更需重启 Agent 服务重新绑定 OWIN 监听，故返回 RestartNeeded=true 提示调用方。</para>
    /// <para>请求体 JSON：{ Token, Action:"get"|"set", Config:{...} }；返回：{ Success, Msg, RestartNeeded, Config }。</para>
    /// </summary>
    public class ConfigService : BaseWebApi
    {
        /// <summary>/config 请求体。</summary>
        public class ConfigRequestVm
        {
            /// <summary>鉴权 Token（须与当前生效 Token 一致）。</summary>
            public string Token { get; set; }

            /// <summary>操作：get（读取，默认）/ set（写入）。</summary>
            public string Action { get; set; }

            /// <summary>set 时的目标配置（字段由 Web 端填全）。</summary>
            public AgentConfig Config { get; set; }
        }

        protected override void ProcessRequest()
        {
            try
            {
                var body = string.Empty;
                using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
                {
                    body = reader.ReadToEnd();
                }

                ConfigRequestVm request = null;
                if (!string.IsNullOrEmpty(body))
                {
                    request = JsonConvert.DeserializeObject<ConfigRequestVm>(body);
                }

                // 鉴权：请求 Token 必须与当前生效 Token 一致（Token 为空表示未启用鉴权）
                var reqToken = request == null ? Request.Query.Get("Token") : request.Token;
                if (!string.IsNullOrEmpty(Token) && (string.IsNullOrEmpty(reqToken) || !Token.Equals(reqToken)))
                {
                    Write(false, "request Token is invaild");
                    return;
                }

                var action = (request == null || string.IsNullOrEmpty(request.Action)) ? "get" : request.Action.Trim().ToLower();

                if (action == "get")
                {
                    Write(true, "ok", false, Mask(AgentConfigLoader.Current));
                    return;
                }

                if (action == "set")
                {
                    if (request.Config == null)
                    {
                        Write(false, "config body is required for set");
                        return;
                    }

                    var old = AgentConfigLoader.Current;
                    var restartNeeded = old.Port != request.Config.Port;

                    // 以传入配置为准整体替换（Web 端已填全字段）
                    if (!AgentConfigLoader.Save(request.Config.Clone()))
                    {
                        Write(false, "save agentsettings.json failed");
                        return;
                    }

                    var msg = restartNeeded
                        ? "saved; port changed, restart the agent service to take effect"
                        : "saved";
                    Write(true, msg, restartNeeded, Mask(AgentConfigLoader.Current));
                    return;
                }

                Write(false, "unknown action: " + action);
            }
            catch (Exception ex)
            {
                Write(false, ex.Message);
            }
        }

        /// <summary>返回脱敏副本：Token 以 ****** 呈现，避免明文回传。</summary>
        private static AgentConfig Mask(AgentConfig c)
        {
            var m = c.Clone();
            m.Token = string.IsNullOrEmpty(c.Token) ? string.Empty : "******";
            return m;
        }

        private void Write(bool success, string msg, bool restartNeeded = false, AgentConfig config = null)
        {
            var obj = new
            {
                Success = success,
                Msg = msg ?? string.Empty,
                RestartNeeded = restartNeeded,
                Config = config
            };
            Response.ContentType = "application/json";
            Response.StatusCode = 200;
            Response.Write(JsonConvert.SerializeObject(obj));
        }
    }
}
