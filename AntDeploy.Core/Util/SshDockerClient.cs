using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using AntDeploy.Core.Models;
using Newtonsoft.Json;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace AntDeploy.Core.Util
{
    /// <summary>
    /// Docker-over-SSH 部署客户端（从 AntDeployWinform.Util.SSHClient 移植）。
    /// 负责：SFTP 上传发布包 → 远程解压 → 生成/复用 Dockerfile → docker build/run →
    /// 清理旧镜像与旧版本 → 可选推送镜像仓库；并支持增量、选择文件、回滚、历史查询。
    /// 相比 WinForm 版：NLog 改为 ILog，移除 System.Reactive 节流（进度按整数百分比自然节流），
    /// 取消检测由外部 Func&lt;bool&gt; checkCancel 提供。
    /// </summary>
    public class SshDockerClient : IDisposable
    {
        private readonly string volumeProfix = "# volume@";
        private readonly string otherProfix = "# other@";
        private readonly string serverPortProfix = "# server_port@";

        public string Host { get; set; }
        public string UserName { get; set; }
        public string Pwd { get; set; }
        public string NetCoreVersion { get; set; }

        public string PorjectName
        {
            get
            {
                if (!string.IsNullOrEmpty(NetCoreENTRYPOINT))
                {
                    return GetSafeProjectName(NetCoreENTRYPOINT.Replace(".dll", "")).ToLower();
                }
                return string.Empty;
            }
        }

        public string NetCorePort { get; set; }
        public string Sudo { get; set; }

        /// <summary>宿主机开启的端口</summary>
        public string ServerPort
        {
            get
            {
                if (string.IsNullOrEmpty(this.NetCorePort)) return "5000";
                if (this.NetCorePort.Contains(":"))
                {
                    return this.NetCorePort.Split(':')[0];
                }
                return NetCorePort;
            }
        }

        /// <summary>容器暴露的端口</summary>
        public string ContainerPort
        {
            get
            {
                if (string.IsNullOrEmpty(this.NetCorePort)) return "5000";
                if (this.NetCorePort.Contains(":"))
                {
                    return this.NetCorePort.Split(':')[1];
                }
                return NetCorePort;
            }
        }

        public string WorkSpace
        {
            get
            {
                if (string.IsNullOrEmpty(this.WorkDir)) return "antdeploy";
                return WorkDir;
            }
        }

        #region 镜像上传
        public bool DockerServiceEnableUpload { get; set; }
        public bool DockerServiceBuildImageOnly { get; set; }
        public string RepositoryUrl { get; set; }
        public string RepositoryUserName { get; set; }
        public string RepositoryUserPwd { get; set; }
        public string RepositoryNameSpace { get; set; }
        public string RepositoryImageName { get; set; }
        #endregion

        public string NetCoreEnvironment { get; set; }
        public string NetCoreENTRYPOINT { get; set; }
        public bool Increment { get; set; }
        public bool UseAsiaShanghai { get; set; }
        public bool IsSelect { get; set; }
        public string ClientDateTimeFolderName { get; set; }
        public string ProjectDeployRoot { get; set; }
        public string RemoveDaysFromPublished { get; set; }

        public string WorkDir { get; set; }

        public string Remark { get; set; }
        public string Volume { get; set; }
        public string Other { get; set; }
        public string RootFolder { get; set; }

        private readonly ILog _log;
        private readonly Func<bool> _checkCancel;
        private readonly Action<int> _uploadLogger;

        private readonly SftpClient _sftpClient;
        private readonly SshClient _sshClient;
        private long _lastProgressNumber;

        public SshDockerClient(string host, string userName, string pwd, string proxy, ILog logger, Action<int> uploadLogger = null, Func<bool> checkCancel = null)
        {
            this.UserName = userName;
            this.Pwd = pwd;
            _log = logger ?? new NullLog();
            _uploadLogger = uploadLogger;
            _checkCancel = checkCancel;

            var harr = host.Split(':');
            this.Host = harr[0];
            var hPort = 22;
            if (harr.Length == 2)
            {
                hPort = int.Parse(harr[1]);
            }

            var useProxy = false;
            if (!string.IsNullOrEmpty(proxy))
            {
                var arr = proxy.Split(':');
                if (arr.Length == 2)
                {
                    ConnectionInfo infoConnection = new ConnectionInfo(this.Host, hPort, userName, ProxyTypes.Http, arr[0], int.Parse(arr[1]), null, null, new PasswordAuthenticationMethod(userName, pwd));
                    _sftpClient = new SftpClient(infoConnection);
                    _sshClient = new SshClient(infoConnection);
                    useProxy = true;
                }
            }

            if (!useProxy)
            {
                _sftpClient = new SftpClient(this.Host, hPort, userName, pwd);
                _sshClient = new SshClient(this.Host, hPort, userName, pwd);
            }

            _sftpClient.BufferSize = 6 * 1024; // bypass Payload error large files
        }

        /// <summary>统一日志入口，返回是否已被取消（供上传进度处使用）</summary>
        private bool Log(string msg, LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Error: _log.Error(msg); break;
                case LogLevel.Warn: _log.Warn(msg); break;
                case LogLevel.Debug:
                case LogLevel.Trace: _log.Debug(msg); break;
                default: _log.Info(msg); break;
            }
            return _checkCancel != null && _checkCancel();
        }

        public bool Connect(bool ignoreLog = false)
        {
            try
            {
                if (!ignoreLog) Log("ssh connecting " + Host + "... ", LogLevel.Info);
                _sshClient.Connect();
                _sftpClient.Connect();
                if (_sshClient.IsConnected && _sftpClient.IsConnected)
                {
                    if (!ignoreLog)
                    {
                        RootFolder = _sftpClient.WorkingDirectory;
                        var connectionInfo = _sshClient.ConnectionInfo;
                        if (connectionInfo != null && !string.IsNullOrEmpty(connectionInfo.Host))
                        {
                            Log($"Connected to {connectionInfo.Username}@{connectionInfo.Host}:{connectionInfo.Port} via SSH", LogLevel.Info);
                        }
                        else
                        {
                            Log($"ssh connect success:{Host}", LogLevel.Info);
                        }
                    }
                    return true;
                }

                if (!ignoreLog) Log($"ssh connect fail", LogLevel.Error);
                return false;
            }
            catch (Exception ex)
            {
                if (!ignoreLog) Log($"ssh connect fail:{ex.Message}", LogLevel.Error);
                return false;
            }
        }

        /// <summary>测试连接（用于 UI 的“测试服务器”）</summary>
        public bool TestConnection(out string message)
        {
            try
            {
                _sshClient.Connect();
                _sshClient.Disconnect();
                message = $"ssh connect success:{Host}";
                return true;
            }
            catch (Exception ex)
            {
                message = $"ssh connect fail:{ex.Message}";
                return false;
            }
        }

        /// <summary>上传文件流到服务器目录</summary>
        public void Upload(Stream stream, string destinationFolder, string fileName)
        {
            if (!destinationFolder.EndsWith("/")) destinationFolder = destinationFolder + "/";

            CreateServerDirectoryIfItDoesntExist(destinationFolder);

            if (_sftpClient.Exists(destinationFolder + fileName))
            {
                _sftpClient.Delete(destinationFolder + fileName);
            }

            var publishUnzipFolder = destinationFolder + "publish";
            if (_sftpClient.Exists(publishUnzipFolder))
            {
                DeleteDirectory(publishUnzipFolder);
            }

            CreateServerDirectoryIfItDoesntExist(publishUnzipFolder + "/");

            ChangeToFolder(destinationFolder);

            var fileSize = stream.Length;
            _lastProgressNumber = 0;

            _sftpClient.UploadFile(stream, fileName, (uploaded) =>
            {
                uploadProgress(fileSize, uploaded);
            });

            if (_lastProgressNumber != 100)
            {
                _uploadLogger?.Invoke(100);
            }
        }

        public void ChangeToFolder(string changeTo)
        {
            _sftpClient.ChangeDirectory(changeTo);
            Log($"Changed directory to {changeTo}", LogLevel.Info);
        }

        private void uploadProgress(long size, ulong uploadedSize)
        {
            if (size <= 0) return;
            var lastProgressNumber = (((long)uploadedSize * 100 / size));

            if (lastProgressNumber < 1)
            {
                return;
            }

            if (lastProgressNumber <= _lastProgressNumber)
            {
                return;
            }

            _lastProgressNumber = lastProgressNumber;
            var isCanceled = Log($"uploaded {lastProgressNumber} %", LogLevel.Info);
            if (isCanceled)
            {
                this.Dispose();
            }
            _uploadLogger?.Invoke((int)_lastProgressNumber);
        }

        /// <summary>获取发布的历史（不带备注），用于清理过期版本</summary>
        public List<string> GetDeployHistoryWithOutRemark(string destinationFolder, int pageNumber = 0)
        {
            var result = new List<Tuple<string, DateTime>>();
            try
            {
                if (!destinationFolder.EndsWith("/")) destinationFolder = destinationFolder + "/";
                destinationFolder = destinationFolder + PorjectName + "/";

                if (!_sftpClient.Exists(destinationFolder))
                {
                    return new List<string>();
                }

                List<ISftpFile> folderList;
                if (pageNumber < 1)
                {
                    folderList = _sftpClient.ListDirectory(destinationFolder).Where(r => r.IsDirectory).
                        OrderByDescending(r => r.LastWriteTime).ToList();
                }
                else
                {
                    folderList = _sftpClient.ListDirectory(destinationFolder).Where(r => r.IsDirectory).
                        OrderByDescending(r => r.LastWriteTime).Take(pageNumber).ToList();
                }

                foreach (var folder in folderList)
                {
                    if ((folder.Name == ".") || (folder.Name == "..")) continue;
                    var temp = folder.Name.Replace("_", "");
                    if (DateTime.TryParseExact(temp, "yyyyMMddHHmmss", null, DateTimeStyles.None, out DateTime d))
                    {
                        result.Add(new Tuple<string, DateTime>(folder.Name, d));
                    }
                }
            }
            catch (Exception ex)
            {
                Log(ex.Message, LogLevel.Error);
            }

            return result.OrderByDescending(r => r.Item2).Select(r => r.Item1).ToList();
        }

        /// <summary>获取发布历史（带当前版本与备注），用于展示给用户</summary>
        public Tuple<string, List<Tuple<string, string>>> GetDeployHistory(int pageNumber = 0)
        {
            var currentVersion = string.Empty;
            var dic = new Dictionary<string, Tuple<string, string, DateTime>>();
            try
            {
                string destinationFolder = WorkSpace;
                if (!destinationFolder.EndsWith("/")) destinationFolder = destinationFolder + "/";
                destinationFolder = destinationFolder + PorjectName + "/";

                if (!_sftpClient.Exists(destinationFolder))
                {
                    return new Tuple<string, List<Tuple<string, string>>>(currentVersion, new List<Tuple<string, string>>());
                }

                var current = destinationFolder + "deploy/current";
                if (_sftpClient.Exists(current))
                {
                    currentVersion = _sftpClient.ReadAllText(current);
                }

                List<ISftpFile> folderList;
                if (pageNumber < 1)
                {
                    folderList = _sftpClient.ListDirectory(destinationFolder).Where(r => r.IsDirectory).
                        OrderByDescending(r => r.LastWriteTime).ToList();
                }
                else
                {
                    folderList = _sftpClient.ListDirectory(destinationFolder).Where(r => r.IsDirectory).
                        OrderByDescending(r => r.LastWriteTime).Take(pageNumber).ToList();
                }

                foreach (var folder in folderList)
                {
                    if ((folder.Name == ".") || (folder.Name == "..")) continue;
                    var temp = folder.Name.Replace("_", "");
                    if (DateTime.TryParseExact(temp, "yyyyMMddHHmmss", null, DateTimeStyles.None, out DateTime d))
                    {
                        string remark = string.Empty;
                        try
                        {
                            var path = folder.FullName + (folder.FullName.EndsWith("/") ? "antdeploy_args" : "/antdeploy_args");
                            remark = _sftpClient.ReadAllText(path);
                        }
                        catch (Exception)
                        {
                            //ignore
                        }

                        if (dic.ContainsKey(temp))
                        {
                            var value = dic[temp];
                            if (value.Item1.Length < folder.Name.Length)
                            {
                                dic[temp] = new Tuple<string, string, DateTime>(folder.Name, remark, d);
                            }
                        }
                        else
                        {
                            dic.Add(temp, new Tuple<string, string, DateTime>(folder.Name, remark, d));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log(ex.Message, LogLevel.Error);
            }

            return new Tuple<string, List<Tuple<string, string>>>(currentVersion, dic.Values.ToList().OrderByDescending(r => r.Item3).Select(r => new Tuple<string, string>(r.Item1, r.Item2)).ToList());
        }

        public void PublishZip(Stream stream, string destinationfileName, Func<bool> continuetask = null, Dictionary<string, Tuple<string, bool>> chineseMapper = null)
        {
            bool CheckCancel()
            {
                if (continuetask != null)
                {
                    var canContinue = continuetask();
                    if (!canContinue)
                    {
                        return true;
                    }
                }
                return false;
            }
            string destinationFolder = WorkSpace;

            if (!destinationFolder.EndsWith("/")) destinationFolder = destinationFolder + "/";
            var projectPath = destinationFolder + PorjectName + "/";
            destinationFolder = projectPath + ClientDateTimeFolderName + "/";

            var deploySaveFolder = projectPath + "deploy/";
            ProjectDeployRoot = deploySaveFolder;
            try
            {
                if (IsSelect)
                {
                    if (!_sftpClient.Exists(deploySaveFolder))
                    {
                        Log($"The first time Can not use select file deploy", LogLevel.Error);
                        return;
                    }
                }

                Upload(stream, destinationFolder, destinationfileName);
            }
            catch (Exception e)
            {
                if (CheckCancel()) return;
                Log(e.ToString(), LogLevel.Error);
                throw;
            }

            if (!_sftpClient.Exists(destinationfileName))
            {
                Log($"upload fail, {destinationfileName} not exist!", LogLevel.Error);
                return;
            }

            if (CheckCancel()) return;
            var argsFilePath = destinationFolder + "antdeploy_args";
            CreateArgsFile(argsFilePath);

            Log($"unzip -o -q {destinationFolder + destinationfileName} -d publish/", LogLevel.Info);
            var unzipresult = _sshClient.RunCommand($"cd {destinationFolder} && unzip -o -q {destinationfileName} -d publish/");
            if (unzipresult.ExitStatus != 0)
            {
                Log($"excute zip command error,return status is not 0", LogLevel.Error);
                Log($"please check 【zip】 is installed in your server!", LogLevel.Error);
                return;
            }

            if (CheckCancel()) return;

            var publishFolder = $"{destinationFolder}publish/";

            if (!_sftpClient.Exists("publish"))
            {
                Log($"unzip fail: {publishFolder}", LogLevel.Error);
                return;
            }
            Log($"unzip success: {publishFolder}", LogLevel.Info);

            if (chineseMapper != null && chineseMapper.Any())
            {
                ChineseMapping(publishFolder, chineseMapper);
            }

            var isExistDockFile = true;
            if (!Increment)
            {
                var dockFilePath = publishFolder + "Dockerfile";
                var dockFilePath2 = "publish/Dockerfile";
                isExistDockFile = _sftpClient.Exists(dockFilePath2);
                if (!isExistDockFile)
                {
                    var createDockerFileResult = CreateDockerFile(dockFilePath);
                    if (!createDockerFileResult) return;
                }

                if (CheckCancel()) return;
            }

            CopyCpFolder(publishFolder, deploySaveFolder);
            if (CheckCancel()) return;
            var temp = publishFolder;
            if (Increment)
            {
                var incrementFoler = $"{destinationFolder}increment/";
                Log($"Increment deploy start backup to [{incrementFoler}]", LogLevel.Info);
                CopyCpFolder(deploySaveFolder, incrementFoler);
                Log($"Increment deploy success backup to [{incrementFoler}]", LogLevel.Info);
                if (CheckCancel()) return;
                temp = incrementFoler;
            }

            _sftpClient.ChangeDirectory(RootFolder);
            ChangeToFolder(deploySaveFolder);
            DoDockerCommand(deploySaveFolder, false, !isExistDockFile, publishName: "", fromFolder: temp);
        }

        /// <summary>回退到指定日期文件夹版本</summary>
        public void RollBack(string version)
        {
            string destinationFolder = WorkSpace;

            if (!destinationFolder.EndsWith("/")) destinationFolder = destinationFolder + "/";

            var path = destinationFolder + PorjectName + "/" + version + "/";

            ProjectDeployRoot = destinationFolder + PorjectName + "/" + "deploy/";

            ChangeToFolder(path);

            var publishFolder = $"{path}publish/";
            if (!_sftpClient.Exists("publish"))
            {
                Log($"rollback fail: {publishFolder} not exist", LogLevel.Error);
                return;
            }

            ClientDateTimeFolderName = version;

            var isIncrement = false;
            var increment = $"{path}increment/";
            try
            {
                if (_sftpClient.Exists("increment"))
                {
                    isIncrement = true;
                }
            }
            catch (Exception)
            {
                //ignore
            }

            if (isIncrement)
            {
                DoDockerCommand(increment, true, publishName: "increment");
                return;
            }

            DoDockerCommand(publishFolder, true);
        }

        /// <summary>
        /// 执行 docker build/run 主流程。
        /// publishFolder：deploy 文件目录；isrollBack：是否回滚；
        /// isDefaultDockfile：上传时没有 Dockerfile 需创建；publishName：远程解压子目录名；fromFolder：增量备份目录。
        /// </summary>
        public void DoDockerCommand(string publishFolder, bool isrollBack = false, bool isDefaultDockfile = false, string publishName = "publish", string fromFolder = null)
        {
            string port = string.Empty;
            string server_port = string.Empty;
            if (!publishFolder.EndsWith("/")) publishFolder = publishFolder + "/";
            var dockFilePath = publishFolder + "Dockerfile";
            var dockFilePath2 = $"{(string.IsNullOrEmpty(publishName) ? "" : publishName + "/")}Dockerfile";
            var isExistDockFile = isDefaultDockfile ? false : _sftpClient.Exists(dockFilePath2);

            if (!isExistDockFile)
            {
                if (isrollBack)
                {
                    Log($"dockerFile is not exist: {dockFilePath}", LogLevel.Error);
                    return;
                }

                if (!isDefaultDockfile)
                {
                    var createDockerFileResult = CreateDockerFile(dockFilePath);
                    if (!createDockerFileResult) return;
                }
            }
            else
            {
                try
                {
                    if (!string.IsNullOrEmpty(Sudo))
                    {
                        _sshClient.RunCommand($"{Sudo} chmod 777 \"{dockFilePath}\";");
                    }
                    var dockerFileText = _sftpClient.ReadAllText(dockFilePath);
                    if (string.IsNullOrEmpty(dockerFileText))
                    {
                        Log($"dockerFile is empty: {dockFilePath}", LogLevel.Error);
                        return;
                    }
                    var needAddPort = false;
                    var newPort = string.Empty;
                    var newPortA = dockerFileText.Split(new string[] { "EXPOSE " }, StringSplitOptions.None);
                    if (newPortA.Length != 2)
                    {
                        needAddPort = true;
                    }
                    else
                    {
                        if (!newPortA[0].EndsWith("#"))
                        {
                            foreach (var item in newPortA[1].Trim())
                            {
                                if (char.IsDigit(item))
                                {
                                    newPort += item;
                                }
                                else
                                {
                                    break;
                                }
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(newPort))
                    {
                        needAddPort = true;
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(NetCorePort) && !this.ContainerPort.Equals(newPort))
                        {
                            Log($"EXPOSE in dockerFile is defined,will use【{newPort}】replace【{ContainerPort}】", LogLevel.Warn);
                        }
                        else
                        {
                            Log($"EXPOSE in dockerFile is : 【{newPort}】", LogLevel.Info);
                        }
                    }

                    port = needAddPort ? ContainerPort : newPort;

                    var volumeInDockerFile = string.Empty;
                    var volumeExist = dockerFileText.Split(new string[] { volumeProfix }, StringSplitOptions.None);
                    if (volumeExist.Length == 2)
                    {
                        var temp2 = volumeExist[1].Split('@');
                        if (temp2.Length > 0)
                        {
                            volumeInDockerFile = temp2[0];
                        }
                    }

                    if (!string.IsNullOrEmpty(volumeInDockerFile))
                    {
                        if (!string.IsNullOrEmpty(Volume) && !Volume.Equals(volumeInDockerFile))
                        {
                            Log($"Volume in dockerFile is defined,will use【{volumeInDockerFile}】replace【{Volume}】", LogLevel.Warn);
                        }
                        else
                        {
                            Log($"Volume in dockerFile is : 【{volumeInDockerFile}】", LogLevel.Info);
                        }
                        Volume = volumeInDockerFile;
                    }
                    else
                    {
                        Log($"Volume in dockerFile is not defined", LogLevel.Warn);
                    }

                    var otherInDockerFile = string.Empty;
                    var otherExist = dockerFileText.Split(new string[] { otherProfix }, StringSplitOptions.None);
                    if (otherExist.Length == 2)
                    {
                        var temp2 = otherExist[1].Split('@');
                        if (temp2.Length > 0)
                        {
                            otherInDockerFile = temp2[0];
                        }
                    }

                    if (!string.IsNullOrEmpty(otherInDockerFile))
                    {
                        if (!string.IsNullOrEmpty(Other) && !Other.Equals(otherInDockerFile))
                        {
                            Log($"Docker Run Other Args in dockerFile is defined,will use【{otherInDockerFile}】replace【{Other}】", LogLevel.Warn);
                        }
                        else
                        {
                            Log($"Docker Run Other Args in dockerFile is : 【{otherInDockerFile}】", LogLevel.Info);
                        }
                        Other = otherInDockerFile;
                    }
                    else
                    {
                        Log($"Docker Run Other Args in dockerFile is not defined", LogLevel.Warn);
                    }

                    var serverPostDockerFile = string.Empty;
                    var serverPostDockerFileExist = dockerFileText.Split(new string[] { serverPortProfix }, StringSplitOptions.None);
                    if (serverPostDockerFileExist.Length == 2)
                    {
                        var temp2 = serverPostDockerFileExist[1].Split('@');
                        if (temp2.Length > 0)
                        {
                            serverPostDockerFile = temp2[0];
                        }
                    }

                    if (!string.IsNullOrEmpty(serverPostDockerFile))
                    {
                        if (!string.IsNullOrEmpty(NetCorePort) && !ServerPort.Equals(serverPostDockerFile))
                        {
                            Log($"ServerPort in dockerFile is defined,will use【{serverPostDockerFile}】replace【{ServerPort}】", LogLevel.Warn);
                        }
                        else
                        {
                            Log($"ServerPort in dockerFile is : 【{serverPostDockerFile}】", LogLevel.Info);
                        }
                        server_port = serverPostDockerFile;
                    }
                    else
                    {
                        server_port = needAddPort ? ServerPort : port;
                    }

                    var addV = false;
                    if (!string.IsNullOrEmpty(NetCoreEnvironment) || needAddPort)
                    {
                        var allLines = _sftpClient.ReadAllLines(dockFilePath).ToList();
                        var entryPointIndex = 0;
                        var haveEnv = false;
                        for (int i = 0; i < allLines.Count; i++)
                        {
                            var line = allLines[i];
                            if (line.Trim().StartsWith("ENTRYPOINT"))
                            {
                                entryPointIndex = i;
                            }
                            if (line.Trim().StartsWith("ENV ASPNETCORE_ENVIRONMENT"))
                            {
                                haveEnv = true;
                            }
                        }

                        if (entryPointIndex > 0)
                        {
                            var add = false;
                            if (needAddPort && !port.Equals("0"))
                            {
                                add = true;
                                allLines.Insert(entryPointIndex, "EXPOSE " + port);
                                Log($"Add EXPOSE " + port + $" to dockerFile  : 【{dockFilePath}】", LogLevel.Info);

                                if (!dockerFileText.Contains("ENV ASPNETCORE_URLS=") && dockerFileText.Contains("dotnet"))
                                {
                                    allLines.Insert(entryPointIndex, "ENV ASPNETCORE_URLS=http://*:" + port);
                                }
                            }

                            if (!haveEnv && !string.IsNullOrEmpty(NetCoreEnvironment))
                            {
                                add = true;
                                allLines.Insert(entryPointIndex, "ENV ASPNETCORE_ENVIRONMENT " + NetCoreEnvironment);
                                Log($"Add ENV ASPNETCORE_ENVIRONMENT " + NetCoreEnvironment + $" to dockerFile  : 【{dockFilePath}】", LogLevel.Info);
                            }

                            if (add)
                            {
                                _sshClient.RunCommand($"set -e;{Sudo} cd ~;{Sudo} \\rm -rf \"{dockFilePath}\";");
                                using (var writer = _sftpClient.CreateText(dockFilePath))
                                {
                                    foreach (var line in allLines)
                                    {
                                        writer.WriteLine(line);
                                    }
                                    if (string.IsNullOrEmpty(volumeInDockerFile) && !string.IsNullOrEmpty(this.Volume))
                                    {
                                        addV = true;
                                        writer.WriteLine(volumeProfix + this.Volume + "@");
                                        Log(volumeProfix + this.Volume + "@", LogLevel.Info);
                                    }
                                    if (string.IsNullOrEmpty(otherInDockerFile) && !string.IsNullOrEmpty(this.Other))
                                    {
                                        addV = true;
                                        writer.WriteLine(otherProfix + this.Other + "@");
                                        Log(otherProfix + this.Other + "@", LogLevel.Info);
                                    }
                                    writer.Flush();
                                }
                                Log($"Update dockFile step1 【{dockFilePath}】", LogLevel.Info);
                            }
                        }
                    }

                    if (!addV)
                    {
                        if ((string.IsNullOrEmpty(volumeInDockerFile) && !string.IsNullOrEmpty(this.Volume)) ||
                            (string.IsNullOrEmpty(otherInDockerFile) && !string.IsNullOrEmpty(this.Other)))
                        {
                            var allLines = _sftpClient.ReadAllLines(dockFilePath).ToList();
                            _sshClient.RunCommand($"set -e;{Sudo} cd ~;{Sudo} \\rm -rf \"{dockFilePath}\";");
                            using (var writer = _sftpClient.CreateText(dockFilePath))
                            {
                                foreach (var line in allLines)
                                {
                                    writer.WriteLine(line);
                                }
                                if (string.IsNullOrEmpty(volumeInDockerFile) && !string.IsNullOrEmpty(this.Volume))
                                {
                                    writer.WriteLine(volumeProfix + this.Volume + "@");
                                    Log(volumeProfix + this.Volume + "@", LogLevel.Info);
                                }
                                if (string.IsNullOrEmpty(otherInDockerFile) && !string.IsNullOrEmpty(this.Other))
                                {
                                    writer.WriteLine(otherProfix + this.Other + "@");
                                    Log(otherInDockerFile + this.Other + "@", LogLevel.Info);
                                }
                                writer.Flush();
                            }
                            Log($"Update dockFile step2【{dockFilePath}】", LogLevel.Info);
                        }
                    }

                    if (!string.IsNullOrEmpty(fromFolder))
                    {
                        var command = $"{Sudo} \\cp -rf {dockFilePath} {fromFolder}";
                        Log($"Update DockerFile 【{command}】", LogLevel.Info);
                        _sshClient.RunCommand(command);
                    }
                }
                catch (Exception ex)
                {
                    Log($"parse param in dockerFile fail: {dockFilePath},err:{ex.Message}", LogLevel.Error);
                    return;
                }
            }

            var specialName = PorjectName;
            var continarName = "d_" + PorjectName;
            var runContainerName = $"--name {continarName}";

            if (!string.IsNullOrEmpty(this.Other) && this.Other.Contains("--name "))
            {
                var arrOther = this.Other.Split(new string[] { "--name " }, StringSplitOptions.None);
                if (arrOther.Length == 2 && !string.IsNullOrEmpty(arrOther[1]))
                {
                    var specialName1 = arrOther[1].Split(' ').FirstOrDefault();
                    if (!string.IsNullOrEmpty(specialName1))
                    {
                        Log($"--name in Other Args is : {specialName1}", LogLevel.Warn);
                        specialName = specialName1;
                        continarName = specialName;
                        runContainerName = $"--name {continarName}";
                    }
                }
                else
                {
                    Log($"--name in Other Args is invaild", LogLevel.Warn);
                }
            }
            else
            {
                Log($"--name in Other Args is not defined", LogLevel.Warn);
            }

            var dockerBuildResult = RunSheell($"{Sudo} docker build --no-cache --rm -t {specialName}:{ClientDateTimeFolderName} -f {dockFilePath} {publishFolder} ");
            if (!dockerBuildResult)
            {
                Log($"build image fail", LogLevel.Error);
                return;
            }

            if (this.DockerServiceBuildImageOnly)
            {
                goto DockerServiceBuildImageOnlyLEVEL;
            }

            SshCommand r1 = null;
            try
            {
                r1 = _sshClient.RunCommand($"{Sudo} docker stop -t 10 {continarName}");
                if (r1.ExitStatus == 0)
                {
                    Log($"{Sudo} docker stop -t 10 {continarName}", LogLevel.Info);
                }
                Thread.Sleep(5000);
            }
            catch (Exception)
            {
                //ignore
            }

            try
            {
                r1 = _sshClient.RunCommand($"{Sudo} docker rm -f {continarName}");
                if (r1.ExitStatus == 0)
                {
                    Log($"{Sudo} docker rm -f {continarName}", LogLevel.Info);
                }
            }
            catch (Exception)
            {
                //ignore
            }

            if (string.IsNullOrEmpty(port))
            {
                port = ContainerPort;
            }

            if (string.IsNullOrEmpty(server_port))
            {
                server_port = ServerPort;
            }

            string volume = GetVolume();

            var dockerRunRt = RunSheell($"{Sudo} docker run -d {runContainerName}{volume}{(string.IsNullOrEmpty(this.Other) ? "" : $" {this.Other}")} --restart=always {(!server_port.Equals("0") && !port.Equals("0") ? $"-p {server_port}:{port}" : "")} {specialName}:{ClientDateTimeFolderName}");

            if (!dockerRunRt)
            {
                Log($"docker run fail", LogLevel.Error);
                return;
            }

            DockerServiceBuildImageOnlyLEVEL:
            if (DockerServiceBuildImageOnly)
            {
                Log($"ignore docker run", LogLevel.Warn);
            }

            r1 = _sshClient.RunCommand(Sudo + " docker images --format '{{.Repository}}:{{.Tag}}:{{.ID}}' | grep '^" + specialName + ":'");
            Tuple<string, string, string> currentImageInfo = null;
            if (r1.ExitStatus == 0 && !string.IsNullOrEmpty(r1.Result))
            {
                var deleteImageArr = r1.Result.Split('\n');
                var clearOldImages = false;
                foreach (var imageName in deleteImageArr)
                {
                    if (imageName.StartsWith($"{specialName}:{ClientDateTimeFolderName}:"))
                    {
                        var imageArr2 = imageName.Split(':');
                        if (imageArr2.Length == 3)
                        {
                            currentImageInfo = new Tuple<string, string, string>(imageArr2[0], imageArr2[1], imageArr2[2]);
                        }
                        continue;
                    }

                    var imageArr = imageName.Split(':');
                    if (imageArr.Length == 3)
                    {
                        var r2 = _sshClient.RunCommand($"{Sudo} docker rmi {imageArr[2]}");
                        if (r2.ExitStatus == 0)
                        {
                            if (!clearOldImages)
                            {
                                Log($"start to clear old images of name:{specialName}", LogLevel.Info);
                                clearOldImages = true;
                            }
                            Log($"{Sudo} docker rmi {imageArr[2]} [{imageName}]", LogLevel.Info);
                        }
                    }
                }
            }

            try
            {
                _sshClient.RunCommand($"if {Sudo} docker images -f \"dangling=true\" | grep ago --quiet; then {Sudo} docker rmi -f $({Sudo} docker images -f \"dangling=true\" -q); fi");
            }
            catch (Exception)
            {
                //ignore
            }

            if (!isrollBack && currentImageInfo != null && this.DockerServiceEnableUpload)
            {
                var uploadTag = currentImageInfo.Item2;
                var uploadImage = this.RepositoryImageName;
                if (uploadImage.Contains(":"))
                {
                    var arr = uploadImage.Split(new string[] { ":" }, StringSplitOptions.RemoveEmptyEntries);
                    if (arr.Length == 2)
                    {
                        uploadImage = arr[0];
                        uploadTag = arr[1];
                    }
                    else
                    {
                        Log($"[upload image] - image name invaild", LogLevel.Error);
                    }
                }

                uploadImage = uploadImage.Replace("：", ":");
                if (Regex.IsMatch(uploadImage, @"[\u4e00-\u9fa5]"))
                {
                    Log($"[upload image] - image name invaild", LogLevel.Error);
                }
                if (Regex.IsMatch(uploadTag, @"[\u4e00-\u9fa5]"))
                {
                    Log($"[upload image] - image tab name invaild", LogLevel.Error);
                }

                var uploadImageName = $"{(string.IsNullOrEmpty(this.RepositoryUrl) ? "" : this.RepositoryUrl + "/")}{this.RepositoryNameSpace}/{uploadImage.ToLower()}:{uploadTag}";
                _sshClient.RunCommand($"{Sudo} docker rmi {uploadImageName}");
                string uploadCommand;
                string uploadCommandLog;
                if (string.IsNullOrEmpty(this.RepositoryUrl))
                {
                    uploadCommandLog =
                        $"set -e;{Sudo} docker login -u {this.RepositoryUserName} -p {{PWD}}; {Sudo} docker tag {currentImageInfo.Item3} {uploadImageName};{Sudo} docker push {uploadImageName}";
                    uploadCommand =
                        $"set -e;{Sudo} docker login -u {this.RepositoryUserName} -p {this.RepositoryUserPwd}; {Sudo} docker tag {currentImageInfo.Item3} {uploadImageName};{Sudo} docker push {uploadImageName}";
                }
                else
                {
                    uploadCommandLog =
                        $"set -e;{Sudo} docker login -u {this.RepositoryUserName} -p {{PWD}} {this.RepositoryUrl}; {Sudo} docker tag {currentImageInfo.Item3} {uploadImageName};{Sudo} docker push {uploadImageName}";
                    uploadCommand =
                        $"set -e;{Sudo} docker login -u {this.RepositoryUserName} -p {this.RepositoryUserPwd} {this.RepositoryUrl}; {Sudo} docker tag {currentImageInfo.Item3} {uploadImageName};{Sudo} docker push {uploadImageName}";
                }

                Log($"[upload image] - " + uploadCommandLog, LogLevel.Warn);
                var rr11 = _sshClient.CreateCommand(uploadCommand);
                var result = rr11.BeginExecute();
                using (var reader = new StreamReader(rr11.OutputStream, Encoding.UTF8, true, 1024, true))
                {
                    while (!result.IsCompleted || !reader.EndOfStream)
                    {
                        string line = reader.ReadLine();
                        if (line != null)
                        {
                            Log($"[upload image] - {line}", LogLevel.Warn);
                        }
                    }
                }
                rr11.EndExecute(result);
                if (rr11.ExitStatus != 0)
                {
                    Log($"[upload image] - Fail", LogLevel.Error);
                    if (!string.IsNullOrEmpty(rr11.Error))
                    {
                        Log(rr11.Error, LogLevel.Error);
                    }
                }
                else
                {
                    Log($"[upload image] - Success", LogLevel.Info);
                }
                _sshClient.RunCommand($"{Sudo} docker rmi {uploadImageName}");
                _sshClient.RunCommand($"{Sudo} docker rmi {currentImageInfo.Item3}");
            }

            try
            {
                if (!string.IsNullOrEmpty(ProjectDeployRoot))
                {
                    _sftpClient.WriteAllText(ProjectDeployRoot + "/current", ClientDateTimeFolderName);
                }
            }
            catch (Exception)
            {
                //ignore
            }

            ClearOldHistroy();
        }

        /// <summary>删除超过 RemoveDaysFromPublished 天的旧版本目录（至少保留 10 个）</summary>
        public void ClearOldHistroy()
        {
            if (string.IsNullOrEmpty(RemoveDaysFromPublished)) return;

            if (!int.TryParse(RemoveDaysFromPublished, out var removeDays))
            {
                return;
            }
            if (string.IsNullOrEmpty(RootFolder))
            {
                return;
            }
            var destinationFolder = WorkSpace;
            if (!destinationFolder.EndsWith("/")) destinationFolder = destinationFolder + "/";
            _sftpClient.ChangeDirectory(RootFolder);
            var now = DateTime.Now.Date;
            var histroryList = GetDeployHistoryWithOutRemark(destinationFolder);
            if (histroryList.Count <= 10) return;
            var oldFolderList = new List<OldFolder>();
            foreach (var histroy in histroryList)
            {
                var temp = histroy.Replace("_", "");
                if (!DateTime.TryParseExact(temp, "yyyyMMddHHmmss", null, DateTimeStyles.None, out DateTime createDate))
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(ClientDateTimeFolderName) && temp == ClientDateTimeFolderName)
                {
                    continue;
                }
                oldFolderList.Add(new OldFolder
                {
                    DateTime = createDate,
                    Name = histroy,
                    DiffDays = (now - createDate.Date).TotalDays
                });
            }

            var targetList = oldFolderList.OrderByDescending(r => r.DateTime)
                .Where(r => r.DiffDays >= removeDays)
                .ToList();

            var diff = histroryList.Count - targetList.Count;

            if (diff >= 0 && diff < 10)
            {
                targetList = targetList.Skip(10 - diff).ToList();
            }

            if (targetList.Any())
            {
                Log($"Remove backup version that have been published for more than:{removeDays} days", LogLevel.Info);
            }
            foreach (var target in targetList)
            {
                try
                {
                    var toDelete = $"{destinationFolder}{PorjectName}/{target.Name}/";
                    this.DeleteDirectory(toDelete);
                    Log($"Remove backup version success: {toDelete}", LogLevel.Info);
                }
                catch
                {
                    //ignore
                }
            }
        }

        private bool CreateArgsFile(string path)
        {
            try
            {
                using (var writer = _sftpClient.CreateText(path))
                {
                    var obj = new LinuxArgModel
                    {
                        Remark = Remark ?? string.Empty,
                        Mac = CodingHelper.GetMacAddress(),
                        Ip = CodingHelper.GetLocalIPAddress(),
                        Pc = Environment.MachineName
                    };
                    writer.WriteLine(JsonConvert.SerializeObject(obj));
                    writer.Flush();
                }
                return true;
            }
            catch (Exception ex)
            {
                Log($"create deploy remark file fail: {path},err:{ex.Message}", LogLevel.Error);
                return false;
            }
        }

        private bool CreateDockerFile(string path)
        {
            try
            {
                Log($"dockerFile not found in: [{path}],will create default Dockerfile!", LogLevel.Info);
                string dllName = NetCoreENTRYPOINT;

                string sdkVersion = NetCoreVersion;
                if (string.IsNullOrEmpty(sdkVersion))
                {
                    sdkVersion = "3.1";
                }

                string environment = NetCoreEnvironment;

                Log($"create docker file: {path}", LogLevel.Info);
                using (var writer = _sftpClient.CreateText(path))
                {
                    var versionNumber = sdkVersion.Replace(".", "");
                    int number = 0;
                    int.TryParse(versionNumber, out number);
                    string baseDockerImage = number > 31
                        ? $"FROM mcr.microsoft.com/dotnet/aspnet:{sdkVersion}"
                        : $"FROM mcr.microsoft.com/dotnet/core/aspnet:{sdkVersion}";
                    writer.WriteLine(baseDockerImage);
                    Log(baseDockerImage, LogLevel.Info);

                    writer.WriteLine($"COPY . /publish");
                    Log($"COPY . /publish", LogLevel.Info);

                    writer.WriteLine($"WORKDIR /publish");
                    Log($"WORKDIR /publish", LogLevel.Info);

                    writer.WriteLine($"ENV ASPNETCORE_URLS=http://*:{this.ContainerPort}");
                    Log($"ENV ASPNETCORE_URLS=http://*:{this.ContainerPort}", LogLevel.Info);

                    if (UseAsiaShanghai)
                    {
                        writer.WriteLine("RUN cp /usr/share/zoneinfo/Asia/Shanghai /etc/localtime");
                        Log("RUN cp /usr/share/zoneinfo/Asia/Shanghai /etc/localtime", LogLevel.Info);
                    }

                    if (!string.IsNullOrEmpty(environment))
                    {
                        writer.WriteLine($"ENV ASPNETCORE_ENVIRONMENT={environment}");
                        Log($"ENV ASPNETCORE_ENVIRONMENT={environment}", LogLevel.Info);
                    }

                    if (!this.ContainerPort.Equals("0"))
                    {
                        writer.WriteLine($"EXPOSE {this.ContainerPort}");
                        Log($"EXPOSE {this.ContainerPort}", LogLevel.Info);
                    }
                    else
                    {
                        Log($"Ignore EXPOSE", LogLevel.Info);
                    }

                    var excuteLine = $"ENTRYPOINT [\"dotnet\", \"{dllName}\"]";
                    writer.WriteLine(excuteLine);
                    Log(excuteLine, LogLevel.Info);

                    if (!string.IsNullOrEmpty(this.NetCorePort) && this.NetCorePort.Contains(":"))
                    {
                        writer.WriteLine(serverPortProfix + this.ServerPort + "@");
                        Log(serverPortProfix + this.ServerPort + "@", LogLevel.Info);
                    }

                    if (!string.IsNullOrEmpty(this.Volume))
                    {
                        writer.WriteLine(volumeProfix + this.Volume + "@");
                        Log(volumeProfix + this.Volume + "@", LogLevel.Info);
                    }

                    if (!string.IsNullOrEmpty(this.Other))
                    {
                        writer.WriteLine(otherProfix + this.Other + "@");
                        Log(otherProfix + this.Other + "@", LogLevel.Info);
                    }

                    writer.Flush();
                }
                Log($"create docker file success: {path}", LogLevel.Info);
                return true;
            }
            catch (Exception ex)
            {
                Log($"create docker file fail: {path},err:{ex.Message}", LogLevel.Error);
                return false;
            }
        }

        public bool RunSheell(string command)
        {
            SshCommand cmd = _sshClient.CreateCommand(command);
            var result = cmd.BeginExecute();
            Log(command, LogLevel.Info);
            using (var reader = new StreamReader(cmd.OutputStream, Encoding.UTF8, true, 1024, true))
            {
                while (!result.IsCompleted || !reader.EndOfStream)
                {
                    string line = reader.ReadLine();
                    if (!string.IsNullOrEmpty(line))
                    {
                        Log(line, LogLevel.Info);
                    }
                }
            }

            cmd.EndExecute(result);

            if (!string.IsNullOrEmpty(cmd.Error))
            {
                if (cmd.Error.Contains("unable to resolve host"))
                {
                    Log(cmd.Error, LogLevel.Warn);
                    return true;
                }
                Log(cmd.Error, LogLevel.Error);
                return false;
            }

            return true;
        }

        public void Dispose()
        {
            try
            {
                _sftpClient.Disconnect();
                _sftpClient.Dispose();
            }
            catch (Exception)
            {
                //ignore
            }

            try
            {
                _sshClient.Disconnect();
                _sshClient.Dispose();
            }
            catch (Exception)
            {
                //ignore
            }
        }

        private void ChineseMapping(string from, Dictionary<string, Tuple<string, bool>> chineseMapper)
        {
            if (!from.EndsWith("/")) from = from + "/";
            foreach (var mapper in chineseMapper.Where(r => r.Value.Item2))
            {
                var frompath = from + (mapper.Value.Item1).Replace(" ", "\\ ");
                var topath = from + mapper.Key.Replace(" ", "\\ ");
                var command = $"{Sudo} \\mv {frompath} {topath}";
                var cmd = _sshClient.RunCommand(command);
                if (cmd.ExitStatus != 0)
                {
                    Log($"change name From [{frompath}] To [{topath}] fail", LogLevel.Error);
                }
            }
            foreach (var mapper in chineseMapper.Where(r => !r.Value.Item2))
            {
                var frompath = from + (mapper.Value.Item1).Replace(" ", "\\ ");
                var topath = from + mapper.Key.Replace(" ", "\\ ");
                var command = $"{Sudo} \\mv {frompath} {topath}";
                var cmd = _sshClient.RunCommand(command);
                if (cmd.ExitStatus != 0)
                {
                    Log($"change name From [{frompath}] To [{topath}] fail", LogLevel.Error);
                }
            }
        }

        private void CopyCpFolder(string from, string to)
        {
            Log($"Start Copy Files From [{from}] To [{to}]", LogLevel.Info);
            if (!from.EndsWith("/")) from = from + "/";
            if (!to.EndsWith("/")) from = to + "/";
            var command = $"{Sudo} \\cp -rf {from}. {to}";
            SshCommand cmd = _sshClient.RunCommand(command);
            if (cmd.ExitStatus != 0)
            {
                Log($"Copy Files From [{from}] To [{to}] fail", LogLevel.Error);
            }
            else
            {
                Log($"Success Copy Files From [{from}] To [{to}]", LogLevel.Info);
            }
        }

        private void DeleteDirectory(string path, bool useCommand = true)
        {
            if (useCommand)
            {
                SshCommand cmd = _sshClient.RunCommand($"set -e;{Sudo} cd ~;{Sudo} \\rm -rf \"{path}\";");
                if (cmd.ExitStatus != 0)
                {
                    Log($"Delete directory:{path} fail", LogLevel.Warn);
                }
                return;
            }

            foreach (SftpFile file in _sftpClient.ListDirectory(path))
            {
                if ((file.Name != ".") && (file.Name != ".."))
                {
                    if (file.IsDirectory)
                    {
                        DeleteDirectory(file.FullName);
                    }
                    else
                    {
                        _sftpClient.DeleteFile(file.FullName);
                    }
                }
            }

            _sftpClient.DeleteDirectory(path);
        }

        private void CreateServerDirectoryIfItDoesntExist(string serverDestinationPath)
        {
            try
            {
                string[] directories = serverDestinationPath.Split('/');
                for (int i = 0; i < directories.Length; i++)
                {
                    string dirName = string.Join("/", directories, 0, i + 1);
                    if (string.IsNullOrEmpty(dirName)) continue;
                    if (!_sftpClient.Exists(dirName))
                        _sftpClient.CreateDirectory(dirName);
                }
            }
            catch (Exception)
            {
                Log("create folder fail:" + serverDestinationPath, LogLevel.Warn);
                throw;
            }
        }

        private string GetSafeProjectName(string projectName)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                projectName = projectName.Replace(char.ToString(c), "");
            }
            var aa = Regex.Replace(projectName, "[ \\[ \\] \\^ \\-*×――(^)（^）$%~!@#$…&%￥—+=<>《》!！??？:：•`·、。，；,.;\"‘’“”-]", "");
            aa = aa.Replace(" ", "").Replace("　", "");
            aa = Regex.Replace(aa, @"[~!@#\$%\^&\*\(\)\+=\|\\\}\]\{\[:;<,>\?\/""]+", "");
            return aa;
        }

        private string GetVolume(bool ignoreFirstSpace = false)
        {
            if (string.IsNullOrEmpty(this.Volume)) return string.Empty;
            var volume = Volume.Replace('；', ';');
            var arr = volume.Split(';');
            var result = new List<string>();
            foreach (var item in arr)
            {
                if (!item.Contains(':'))
                {
                    Log("ignore volume set,because is not correct!", LogLevel.Error);
                    return string.Empty;
                }
                result.Add($"-v {item}");
            }
            if (!result.Any()) return string.Empty;
            return (!ignoreFirstSpace ? " " : "") + string.Join(" ", result);
        }
    }

    internal class OldFolder
    {
        public string Name { get; set; }
        public DateTime DateTime { get; set; }
        public double DiffDays { get; set; }
    }
}
