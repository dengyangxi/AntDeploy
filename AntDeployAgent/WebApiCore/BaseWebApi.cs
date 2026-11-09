using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using AntDeployAgentWindows.Model;
using Microsoft.Owin;
using Newtonsoft.Json;

namespace AntDeployAgentWindows.WebApiCore
{
    /// <summary>
    /// Fast WebApi基类
    /// </summary>
    public abstract class BaseWebApi
    {

        /// <summary>授权 Token（热加载：每次读取 <see cref="AgentConfigLoader.Current"/>，改配置无需重启）。</summary>
        protected static string Token => AgentConfigLoader.Current.Token;

        #region <子类可见的保护对象>

        /// <summary>
        /// HTML 请求（输入）对象
        /// </summary>
        protected IOwinRequest Request;

        /// <summary>
        /// HTML 应答（输出）对象
        /// </summary>
        protected IOwinResponse Response;

        /// <summary>
        /// OWIN上下文对象
        /// </summary>
        protected IOwinContext Context;



        #endregion

        public bool CheckToken()
        {
            if (Request.Method.ToUpper() != "POST")
            {
                Response.ContentType = "text/plain";

                if (!string.IsNullOrEmpty(Token))
                {
                    //验证Token
                    var token = Request.Query.Get("Token");
                    if (string.IsNullOrEmpty(token))
                    {
                        Response.Write("token required");
                        return false;
                    }

                    if (!Token.Equals(token))
                    {
                        token = WebUtility.UrlDecode(token);
                        if (!Token.Equals(token))
                        {
                            Response.Write("token invaid");
                            return false;
                        }
                    }

                }


                Response.Write("success");
                return false;
            }

            return true;
        }

        /// <summary>
        /// 处理请求
        /// </summary>
        /// <param name="context"></param>
        /// <returns></returns>
        public Task ProcessRequest(IOwinContext context)
        {

            Context = context;
            Response = context.Response;
            Request = context.Request;
            // 关键修复：长耗时的部署同步操作放在专用线程(LongRunning)执行，
            // 避免占用线程池线程导致线程池耗尽，从而让 Agent 仍能接收新的上传请求
            return Task.Factory.StartNew(() =>
            {
                try
                {
                    ProcessRequest();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                    DeployResult obj = new DeployResult {Success = false, Msg = ex.Message};
                    Response.ContentType = "application/json";
                    Response.Write(JsonConvert.SerializeObject(obj));
                }
            }, TaskCreationOptions.LongRunning);
        }

        /// <summary>
        /// 具体处理请求
        /// </summary>
        protected abstract void ProcessRequest();

    }
}
