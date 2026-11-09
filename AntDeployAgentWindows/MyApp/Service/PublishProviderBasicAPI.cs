using AntDeployAgentWindows.WebApiCore;
using AntDeployAgentWindows.WebSocketApp;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using AntDeployAgentWindows.Model;

namespace AntDeployAgentWindows.MyApp.Service
{
    public abstract class PublishProviderBasicAPI : CommonProcessor, IPublishProviderAPI
    {
        private object obj = new object();
        private bool webSocketDisposed = false;
       
        private static readonly ConcurrentDictionary<string, ReaderWriterLockSlim> locker = new ConcurrentDictionary<string, ReaderWriterLockSlim>();
        public abstract string ProviderName { get; }
        public string LoggerKey { get; set; }
        public WebSocketApp.WebSocket WebSocket { get; set; }
        public abstract string ProjectName { get; }
        public abstract string DeployExcutor(FormHandler.FormItem fileItem);
        public abstract string CheckData(FormHandler formHandler);
        public string Deploy(FormHandler.FormItem fileItem)
        {
            //按照项目名称 不能并发发布
            if (!string.IsNullOrEmpty(ProjectName))
            {
                var key = (ProviderName ?? string.Empty) + ProjectName;
                if (!locker.TryGetValue(key, out var ReaderWriterLockSlim))
                {
                    ReaderWriterLockSlim = new ReaderWriterLockSlim();
                    locker.TryAdd(key, ReaderWriterLockSlim);
                }

                // 关键修复：IsWriteLockHeld 是“线程级”判断，而每个请求运行在不同线程池线程上，
                // 导致该判断永远为 false、同项目并发部署并未真正串行。改用 TryEnterWriteLock(0)
                // 真正检测是否有其它线程正在部署，获取不到锁则直接返回“正在部署”，避免竞态
                if (!ReaderWriterLockSlim.TryEnterWriteLock(0))
                {
                    return $"{ProjectName} is deploying!,please wait for seconds!";
                }

                try
                {
                    return DeployExcutor(fileItem);
                }
                finally
                {
                    ReaderWriterLockSlim.ExitWriteLock();
                }

            }
            else
            {
                return DeployExcutor(fileItem);
            }
        }

        public virtual string RollBack()
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// 将上传文件写入目标路径。兼容两种来源：
        /// 1) 内存模式（fileItem.FileBody 非空）——历史兼容路径；
        /// 2) 流式模式（fileItem.FilePath 指向临时文件）——方案D，避免整包进内存。
        /// 返回空字符串表示成功，否则返回错误信息。
        /// </summary>
        protected string SaveUploadFile(FormHandler.FormItem fileItem, string targetPath)
        {
            try
            {
                if (fileItem.FileBody != null && fileItem.FileBody.Length > 0)
                {
                    using (var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write))
                    {
                        fs.Write(fileItem.FileBody, 0, fileItem.FileBody.Length);
                    }
                    return string.Empty;
                }

                if (!string.IsNullOrEmpty(fileItem.FilePath) && File.Exists(fileItem.FilePath))
                {
                    File.Copy(fileItem.FilePath, targetPath, true);
                    return string.Empty;
                }

                return "publish file is empty";
            }
            catch (Exception ex)
            {
                return "save publish file fail:" + ex.Message;
            }
        }


        public string Check(FormHandler formHandler)
        {
            var wsKey = formHandler.FormItems.FirstOrDefault(r => r.FieldName.Equals("wsKey"));
            if (wsKey != null  && !string.IsNullOrEmpty(wsKey.TextValue))
            {
                var _wsKey = wsKey.TextValue;

                if (MyWebSocketWork.WebSockets.TryGetValue(_wsKey, out var sockert))
                {
                    WebSocket = sockert;
                    WebSocket.OnClose += sender => { webSocketDisposed = true; };
                }
            }

            return CheckData(formHandler);
        }

        protected void EnsureProjectFolder(string path)
        {
            try
            {
                lock (obj)
                {
                    if (!Directory.Exists(path))
                    {
                        Directory.CreateDirectory(path);
                    }
                }
            }
            catch (Exception)
            {
                //ignore
            }
        }

        protected void Log(string str)
        {
            try
            {
                if (WebSocket != null && !webSocketDisposed)
                {
                    try
                    {
                        WebSocket.Send(str + "@_@" + str.Length);
                    }
                    catch (Exception ex)
                    {
                        //WebSocket发送失败
                        if (!string.IsNullOrEmpty(LoggerKey))
                        {
                            if (LoggerService.loggerCollection.TryGetValue(LoggerKey, out var list))
                            {
                                list.Add(new LoggerModel
                                {
                                    Date = DateTime.Now,
                                    IsActive = false,
                                    Msg = str
                                });
                            }
                        }
                    }
                }
                else
                {
                    //WebSocket发送失败
                    if (!string.IsNullOrEmpty(LoggerKey))
                    {
                        if (LoggerService.loggerCollection.TryGetValue(LoggerKey, out var list))
                        {
                            list.Add(new LoggerModel
                            {
                                Date = DateTime.Now,
                                IsActive = false,
                                Msg = str
                            });
                        }
                    }
                }
            }
            catch (Exception)
            {
                //ignore

            }
        }

    }


}
