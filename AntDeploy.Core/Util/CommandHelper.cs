using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using AntDeploy.Core.Models;
using Newtonsoft.Json;

namespace AntDeploy.Core.Util
{
    /// <summary>
    /// 本地构建辅助：msbuild / dotnet publish / 执行 shell 命令。
    /// 从 AntDeployWinform.Util.CommandHelper 提取，去掉 NLog 与 System.Reactive 依赖，改用 ILog。
    /// </summary>
    public static class CommandHelper
    {
        public static string MsBuildPath { get; set; } = string.Empty;

        /// <summary>执行 msbuild 编译（net framework 项目或需要 msbuild 的场景）</summary>
        public static bool RunMsbuild(string projectPath, string publishPath, ILog logger, bool isWeb = false, Func<bool> checkCancel = null)
        {
            if (string.IsNullOrEmpty(MsBuildPath) || !File.Exists(MsBuildPath))
            {
                MsBuildPath = GetMsBuildPath();
            }
            if (string.IsNullOrEmpty(MsBuildPath) || !File.Exists(MsBuildPath))
            {
                logger.Error("未找到 msbuild.exe，请在设置中配置 VS 安装路径下的 MSBuild.exe。");
                return false;
            }

            var path2 = publishPath.TrimEnd('\\');
            ClearPublishFolder(path2);

            // framework/packages.config 项目用 MSBuild 编译不会自动还原 NuGet 包，编译前先显式还原，
            // 否则会遇到 NETSDK1004 / “找不到包” 等缺包错误。
            RestorePackages(projectPath, logger, checkCancel);

            string buildArg = "\"" + projectPath + "\"";
            if (isWeb)
            {
                // 必须先 /t:Build 再 /t:WebPublish：WebPublish 目标本身不编译项目，
                // 旧写法用 /t:ResolveReferences /t:WebPublish 会跳过 CoreCompile，
                // 导致 bin\{Assembly}.dll 从未产出，打包复制到 PackageTmp 时因源文件缺失报“句柄无效”(ERROR_INVALID_HANDLE)。
                buildArg += " /t:Build /t:WebPublish /verbosity:minimal /p:BuildingProject=true /p:Configuration=Release /p:Platform=AnyCPU /p:WebPublishMethod=FileSystem /p:PublishProvider=FileSystem /p:DeleteExistingFiles=True /p:publishUrl=\"" + path2 + "\" /nr:false";
            }
            else
            {
                buildArg += " /t:Rebuild /v:m /p:Configuration=Release;OutDir=\"" + path2 + "\"";
            }

            logger.Info($"current project Path:{projectPath}");
            return RunExternalExe(Path.GetDirectoryName(projectPath) ?? Path.GetPathRoot(projectPath), MsBuildPath, buildArg, logger, checkCancel);
        }

        /// <summary>
        /// 编译前还原 NuGet 包。framework/packages.config 项目用 MSBuild 编译时不会自动还原，
        /// 需显式执行 msbuild -t:restore -p:RestorePackagesConfig=true（该参数从 MSBuild 16.5 起支持，
        /// 同时兼容 PackageReference 与 packages.config 两种模式，无需额外的 nuget.exe）。
        /// 优先还原解决方案(.sln)以覆盖多项目依赖；找不到 sln 时还原项目文件本身。
        /// 还原失败仅告警并继续编译，以便暴露更具体的缺包错误。
        /// </summary>
        private static bool RestorePackages(string projectPath, ILog logger, Func<bool> checkCancel)
        {
            if (string.IsNullOrEmpty(MsBuildPath) || !File.Exists(MsBuildPath))
            {
                logger.Warn("【restore】未找到 msbuild.exe，跳过 NuGet 还原");
                return false;
            }
            var target = FindSolution(projectPath) ?? projectPath;
            var workDir = Path.GetDirectoryName(target) ?? Path.GetPathRoot(target);
            var arg = "\"" + target + "\" -t:restore -p:RestorePackagesConfig=true -verbosity:minimal";
            logger.Info($"【restore】开始还原 NuGet 包：{target}");
            var ok = RunExternalExe(workDir, MsBuildPath, arg, logger, checkCancel);
            if (ok) logger.Info("【restore】NuGet 还原完成");
            else logger.Warn("【restore】NuGet 还原返回非零，仍尝试继续编译（若包已存在可能不影响）");
            return ok;
        }

        /// <summary>从项目文件逐级向上查找解决方案(.sln)；到达仓库根(.git 所在目录)仍未找到即停止，避免越界到上层目录。</summary>
        private static string FindSolution(string projectPath)
        {
            try
            {
                var dir = new DirectoryInfo(Path.GetDirectoryName(projectPath) ?? projectPath);
                while (dir != null)
                {
                    var slns = dir.GetFiles("*.sln");
                    if (slns.Length > 0) return slns[0].FullName;
                    if (Directory.Exists(Path.Combine(dir.FullName, ".git"))) break;
                    dir = dir.Parent;
                }
            }
            catch { }
            return null;
        }

        /// <summary>执行 dotnet publish（.NET Core / .NET 5+ 项目）</summary>
        public static bool RunDotnetExe(string projectPath, string publishPath, string extraArgs, ILog logger, Func<bool> checkCancel = null)
        {
            if (!string.IsNullOrEmpty(publishPath))
            {
                var path2 = publishPath.TrimEnd('\\');
                ClearPublishFolder(path2);
                extraArgs = (extraArgs ?? string.Empty) + " -o \"" + publishPath + "\"";
            }
            logger.Info($"current project Path:{projectPath}");
            return RunExternalExe(Path.GetDirectoryName(projectPath) ?? Path.GetPathRoot(projectPath), "dotnet", $"publish \"{projectPath}\" {extraArgs}", logger, checkCancel);
        }

        /// <summary>执行任意命令行</summary>
        public static bool RunCommand(string commandToRun, ILog logger, Func<bool> checkCancel = null, string workingDirectory = null)
        {
            if (string.IsNullOrEmpty(workingDirectory)) workingDirectory = Directory.GetDirectoryRoot(Directory.GetCurrentDirectory());
            return RunExternalExe(workingDirectory, "cmd", "/c " + commandToRun, logger, checkCancel, useCmd: true);
        }

        /// <summary>
        /// 调用 jib.exe 构建 Docker 镜像（从 AntDeployWinform.Util.CommandHelper.RunJibExe 移植）。
        /// rootPath 为 jib.exe 所在目录；publishPath 为镜像层内容目录。
        /// TargetImage 非 http:// 开头且无法解析为镜像引用时打本地 tar 包，否则推送远程仓库。
        /// </summary>
        public static bool RunJibExe(string publishPath, string rootPath, DockerImageConfig config2, ILog logger, Func<bool> checkCancel = null)
        {
            var config = DockerImageConfigGo.get(config2);
            var jibExe = Path.Combine(rootPath, "jib.exe");
            if (!File.Exists(jibExe))
            {
                logger.Error($"can not found :{jibExe}");
                return false;
            }

            config.ImageLayersFolder = publishPath;
            var dockerImageCache = Path.Combine(new DirectoryInfo(publishPath).Parent.FullName, "dockerImage_cache");
            config.ApplicationLayersCacheDirectory = dockerImageCache;

            var path = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string projectPramPath = string.Empty;
            string tarFolder = string.Empty;
            try
            {
                var bash = string.Empty;
                if (!config.TargetImage.StartsWith("http://") && ImageReference.Parse(config.TargetImage) == null)
                {
                    var output = config.TargetImage;
                    config.TargetImage = Path.GetFileNameWithoutExtension(config.TargetImage);
                    tarFolder = Path.GetDirectoryName(output);
                    var projectPram = JsonConvert.SerializeObject(config);
                    var md5 = CodingHelper.MD5(projectPram);
                    projectPramPath = Path.Combine(path, md5 + "_param.json");
                    File.WriteAllText(projectPramPath, projectPram, Encoding.UTF8);
                    bash = $"tar --outputfile=\"{output}\" --configfile=\"{projectPramPath}\"";
                }
                else
                {
                    var projectPram = JsonConvert.SerializeObject(config);
                    var md5 = CodingHelper.MD5(projectPram);
                    projectPramPath = Path.Combine(path, md5 + "_param.json");
                    File.WriteAllText(projectPramPath, projectPram, Encoding.UTF8);
                    bash = $"push --configfile=\"{projectPramPath}\"";
                }

                return RunExternalExe(rootPath, "jib.exe", bash, logger, checkCancel);
            }
            finally
            {
                logger.Info("global pull image cache folder  ==>  %LOCALAPPDATA%/fibdotnet");
                logger.Info($"local build image cache folder  ==>  {dockerImageCache}");
                if (!string.IsNullOrEmpty(tarFolder))
                {
                    logger.Info($"local build image tar folder  ==>  {tarFolder}");
                }
                try { File.Delete(projectPramPath); } catch { }
            }
        }

        private static bool RunExternalExe(string workingDirectory, string fileName, string arguments, ILog logger, Func<bool> checkCancel, bool useCmd = false)
        {
            Process process = null;
            try
            {
                logger.Info($"> {fileName} {arguments}");
                process = new Process
                {
                    StartInfo =
                    {
                        WorkingDirectory = workingDirectory,
                        FileName = useCmd ? "cmd" : fileName,
                        Arguments = useCmd ? arguments : arguments,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        UseShellExecute = false,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true
                    }
                };
                if (useCmd) process.StartInfo.Arguments = "/c " + arguments;

                process.OutputDataReceived += (s, e) =>
                {
                    if (checkCancel != null && checkCancel())
                    {
                        TryKill(process);
                        return;
                    }
                    if (!string.IsNullOrWhiteSpace(e.Data)) logger.Info(e.Data);
                };
                process.ErrorDataReceived += (s, e) =>
                {
                    if (checkCancel != null && checkCancel())
                    {
                        TryKill(process);
                        return;
                    }
                    if (!string.IsNullOrWhiteSpace(e.Data)) logger.Error(e.Data);
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();

                try { process.Kill(); } catch { }
                return process.ExitCode == 0;
            }
            catch (Exception ex)
            {
                if (checkCancel != null && checkCancel())
                {
                    logger.Error("deploy task was canceled!");
                    return false;
                }
                logger.Error(ex.Message);
                return false;
            }
            finally
            {
                TryKill(process);
                try { process?.Dispose(); } catch { }
            }
        }

        private static void TryKill(Process process)
        {
            try { process?.Kill(); } catch { }
        }

        private static void ClearPublishFolder(string srcDir)
        {
            try
            {
                var dir = new DirectoryInfo(srcDir);
                if (!dir.Exists) return;
                foreach (var file in dir.GetFiles()) { try { file.Delete(); } catch { } }
                foreach (var folder in dir.GetDirectories())
                {
                    if (folder.Name.EndsWith(".git")) continue;
                    try { folder.Delete(true); } catch { }
                }
            }
            catch { }
        }

        /// <summary>通过 vswhere 探测 MSBuild.exe 路径；找不到则返回已配置的 MsBuildPath</summary>
        public static string GetMsBuildPath()
        {
            if (!string.IsNullOrEmpty(MsBuildPath) && File.Exists(MsBuildPath)) return MsBuildPath;
            try
            {
                var vswhere = @"C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe";
                if (File.Exists(vswhere))
                {
                    var psi = new ProcessStartInfo(vswhere, "-latest -products * -requires Microsoft.Component.MSBuild -property installationPath")
                    {
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (var p = Process.Start(psi))
                    {
                        var outp = p.StandardOutput.ReadToEnd().Trim();
                        p.WaitForExit();
                        if (!string.IsNullOrEmpty(outp))
                        {
                            var candidate = Path.Combine(outp, "MSBuild", "Current", "Bin", "MSBuild.exe");
                            if (File.Exists(candidate)) return candidate;
                        }
                    }
                }
            }
            catch { }
            return MsBuildPath;
        }
    }
}
