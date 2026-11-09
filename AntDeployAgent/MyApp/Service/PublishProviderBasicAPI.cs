using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using AntDeployAgent.WebApiCore;
using AntDeployAgent.WebSocketApp;
using AntDeployAgent.Model;

namespace AntDeployAgent.MyApp.Service
{
    public abstract class PublishProviderBasicAPI : CommonProcessor, IPublishProviderAPI
    {
        private object obj = new object();
        private bool webSocketDisposed = false;

        private static readonly ConcurrentDictionary<string, ReaderWriterLockSlim> locker = new ConcurrentDictionary<string, ReaderWriterLockSlim>();
        public abstract string ProviderName { get; }
        public string LoggerKey { get; set; }
        public WebSocket WebSocket { get; set; }
        public abstract string ProjectName { get; }
        public abstract string ProjectPublishFolder { get; }
        public abstract string DeployExcutor(FormHandler.FormItem fileItem);
        public abstract string CheckData(FormHandler formHandler);
        private FormHandler _formHandler;
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
                    return ExcuteDeploy(fileItem);
                }
                finally
                {
                    ReaderWriterLockSlim.ExitWriteLock();
                }
            }
            else
            {
                return ExcuteDeploy(fileItem);
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

        private string ExcuteDeploy(FormHandler.FormItem fileItem)
        {
            var re = DeployExcutor(fileItem);
            try
            {
                if (_formHandler == null)
                {
                    return re;
                }

                if (!string.IsNullOrEmpty(re))
                {
                    return re;
                }

                if (string.IsNullOrEmpty(ProjectPublishFolder) || !Directory.Exists(ProjectPublishFolder))
                {
                    return re;
                }

                //保存参数
                var formArgs = _formHandler.FormItems.Where(r => (r.FileBody == null || r.FileBody.Length < 1) && string.IsNullOrEmpty(r.FilePath)).ToList();
                if (formArgs.Any())
                {
                    var path = Path.Combine(ProjectPublishFolder, "antdeploy_args.json");
                    var content = JsonConvert.SerializeObject(formArgs);
                    File.WriteAllText(path, content, Encoding.UTF8);
                }

                SaveCurrentVersion(ProjectPublishFolder);
            }
            catch (Exception)
            {
                //ignore
            }

            var projectRootPath = new DirectoryInfo(ProjectPublishFolder).Parent;
            if (projectRootPath == null || !projectRootPath.Exists) return re;
            //每次发布完成后清理老的发布历史记录 只清理自己项目的 
            //防止别的项目正在回滚到某个版本，你这边发现这个版本已经过时了就删除了
            Setting.ClearOldFolders(ProviderName , projectRootPath.Name, Log);
            return re;
        }

        /// <summary>
        /// 记录当前正在使用的文件记录 目的是为了让删除任务排除这个文件夹
        /// </summary>
        /// <param name="projectPublishFolder">当前发布的版本目录 或者 回滚成的版本目录</param>
        protected void SaveCurrentVersion(string projectPublishFolder)
        {
            var projectRootPath = new DirectoryInfo(projectPublishFolder).Parent;
            if (projectRootPath == null || !projectRootPath.Exists) return;
            var currentVersionName = new DirectoryInfo(projectPublishFolder).Name;
            if (string.IsNullOrEmpty(currentVersionName)) return;
            File.WriteAllText(Path.Combine(projectRootPath.FullName, "current.txt"), currentVersionName, Encoding.UTF8);
        }

        public string Check(FormHandler formHandler)
        {
            _formHandler = formHandler;
            var wsKey = formHandler.FormItems.FirstOrDefault(r => r.FieldName.Equals("wsKey"));
            if (wsKey != null && !string.IsNullOrEmpty(wsKey.TextValue))
            {
                var _wsKey = wsKey.TextValue;

                if (MyWebSocketWork.WebSockets.TryGetValue(_wsKey, out var sockert))
                {
                    WebSocket = sockert;
                    WebSocket.OnClose += sender => { webSocketDisposed = true; };
                }
            }

            var macValue = formHandler.FormItems.FirstOrDefault(r => r.FieldName.Equals("mac"));
            if (macValue != null && !string.IsNullOrEmpty(macValue.TextValue))
            {
                if (!Setting.CheckIsInWhiteMacList(macValue.TextValue))
                {
                    return $"macAddress:[{macValue.TextValue}] invaild";
                }
            }

            return CheckData(formHandler);
        }

        protected string findUploadFolder(string _projectPublishFolder, bool isPublish = false, bool docker = false)
        {

            var deployFolder = Path.Combine(_projectPublishFolder, "publish");

            if (!Directory.Exists(_projectPublishFolder)) return deployFolder;

            if (Directory.Exists(deployFolder)) return deployFolder;

            var temp = new DirectoryInfo(_projectPublishFolder);
            var tempFolderList = temp.GetDirectories();
            if (tempFolderList.Length == 1)
            {
                deployFolder = tempFolderList.First().FullName;
            }
            else if (!isPublish && tempFolderList.Length == 0)
            {
                //回滚 操作时 ：可能是被删除了 解压出来
                var zipFile = Path.Combine(_projectPublishFolder, "publish.zip");
                if (!File.Exists(zipFile)) return deployFolder;

                //解压
                ZipFile.ExtractToDirectory(zipFile, docker?Path.Combine(_projectPublishFolder,"publish"):_projectPublishFolder);
                temp = new DirectoryInfo(_projectPublishFolder);
                tempFolderList = temp.GetDirectories();
                if (tempFolderList.Length == 1)
                {
                    deployFolder = tempFolderList.First().FullName;
                }
            }

            return deployFolder;
        }

        protected void cleanRollbackTemp()
        {
            if (string.IsNullOrEmpty(ProjectPublishFolder))
            {
                return;
            }
            var temp = new DirectoryInfo(ProjectPublishFolder);
            var tempFolderList = temp.GetDirectories();
            if (tempFolderList.Length == 1)
            {
                var deployFolder = tempFolderList.First();
                if (deployFolder.Name == "increment" || deployFolder.Name == "_deploy_") return;
                
                Directory.Delete(deployFolder.FullName, true);
            }
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
            catch (Exception e)
            {
                Log("【Warn】create directory fail:" + e.Message);
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