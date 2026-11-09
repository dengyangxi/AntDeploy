using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibGit2Sharp;

namespace AntDeploy.Core.Util
{
    /// <summary>
    /// 增量部署用的本地 git 仓库操作（从 AntDeployWinform.Util.GitClient 移植）。
    /// 在发布目录 publishPath 下建立/打开一个本地 git 仓库，
    /// 通过 RetrieveStatus 得到自上次提交以来变更的文件，仅打包上传这些文件，然后提交记录本次基线。
    /// 与 Core.GitClient（远程 clone/pull）用途不同，此类专门服务于增量/选择文件部署。
    /// </summary>
    public class IncrementGit : IDisposable
    {
        private readonly ILog _logger;
        private Repository _repository;
        private readonly string _projectPath;

        public IncrementGit(string projectPath, ILog logger = null)
        {
            _logger = logger;
            _projectPath = projectPath;
            CreateGit(_projectPath);
        }

        public bool InitSuccess { get; private set; }

        /// <summary>判断指定路径是否已是合法 git 仓库</summary>
        public bool IsGitExist(string path)
        {
            try
            {
                var gitFolder = Repository.Discover(path);
                if (Repository.IsValid(gitFolder))
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger?.Error("【git】" + ex.Message);
            }
            return false;
        }

        /// <summary>切换分支</summary>
        public bool ChangeBranch(string name)
        {
            try
            {
                var branch = _repository.Branches[name];
                if (branch == null)
                {
                    _logger?.Warn($"【git】branch name:{name} not exist");
                    return false;
                }
                Branch currentBranch = Commands.Checkout(_repository, branch);
                return currentBranch != null;
            }
            catch (Exception e)
            {
                _logger?.Error($"【git】checkout branch:{name} fail:{e.Message}");
                return false;
            }
        }

        /// <summary>创建分支</summary>
        public bool CreateBranch(string name)
        {
            try
            {
                var branch = _repository.Branches[name];
                if (branch != null)
                {
                    _logger?.Info($"【git】create branch warn:{name} is already exist");
                    return true;
                }
                Branch newbranch = _repository.CreateBranch(name);
                if (newbranch != null)
                {
                    _logger?.Info($"【git】create branch success:{name}");
                    return true;
                }
                _logger?.Error($"【git】create branch:{name} fail");
                return false;
            }
            catch (Exception e)
            {
                _logger?.Error($"【git】create branch:{name} fail:{e.Message}");
                return false;
            }
        }

        /// <summary>创建（或打开）本地 git 仓库</summary>
        public bool CreateGit(string path = null, string envName = null)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) path = _projectPath;
                var path2 = Path.Combine(path, ".git");
                if (Directory.Exists(path2))
                {
                    _logger?.Info("【git】 git Repository is already created!");
                    _repository = new Repository(_projectPath);
                    InitSuccess = true;
                    return true;
                }

                string rootedPath = Repository.Init(path);
                if (!string.IsNullOrEmpty(rootedPath))
                {
                    _logger?.Info("【git】create git Repository success :" + path);
                    _repository = new Repository(_projectPath);
                    CommitChanges("first init");
                    InitSuccess = true;
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger?.Error("【git】create git repository err:" + ex.Message);
            }
            return false;
        }

        /// <summary>提交所有变动（作为本次增量部署的基线）</summary>
        public void SubmitChanges(int count)
        {
            _logger?.Info("【git】commit start");
            try
            {
                StageIgnoreFile();
                Commands.Stage(_repository, "*");
            }
            catch (Exception ex1)
            {
                _logger?.Warn("【git】stage err:" + ex1.Message);
            }
            CommitChanges(DateTime.Now.ToString("yyyyMMddHHmmss"));
            _logger?.Info($"【git】commit success,file count:{count}");
        }

        /// <summary>提交选择的文件列表（fileList 为绝对路径，dir 为其所在根目录绝对路径）</summary>
        public void SubmitSelectedChanges(List<string> fileList, string dir)
        {
            _logger?.Info("【git】commit start");
            try
            {
                foreach (var file in fileList)
                {
                    var commitFile = EntryFromPath(file, dir);
                    _repository.Index.Add(commitFile);
                    _repository.Index.Write();
                }
            }
            catch (Exception ex1)
            {
                _logger?.Warn("【git】stage err:" + ex1.Message);
            }
            CommitChanges(DateTime.Now.ToString("yyyyMMddHHmmss"));
            _logger?.Info($"【git】commit success,file count:{fileList.Count}");
        }

        /// <summary>把绝对文件路径转成相对仓库根的路径（用于 Index.Add）</summary>
        private static string EntryFromPath(string file, string dir)
        {
            var d = (dir ?? string.Empty).TrimEnd('\\', '/');
            var f = file ?? string.Empty;
            if (!string.IsNullOrEmpty(d) && f.StartsWith(d, StringComparison.OrdinalIgnoreCase))
            {
                var rel = f.Substring(d.Length);
                return rel.TrimStart('\\', '/').Replace('\\', '/');
            }
            return f.Replace('\\', '/');
        }

        /// <summary>把被忽略的文件也加入索引（保证下次能检测到它们的变化）</summary>
        private void StageIgnoreFile()
        {
            RepositoryStatus status = _repository.RetrieveStatus();
            List<string> filePathsIgnored = status.Ignored.Select(mods => mods.FilePath).ToList();
            foreach (var item in filePathsIgnored)
            {
                _repository.Index.Add(item);
                _repository.Index.Write();
            }
        }

        /// <summary>获取自上次提交以来有变动的文件相对路径列表</summary>
        public List<string> GetChanges()
        {
            var result = new List<string>();
            try
            {
                RepositoryStatus status = _repository.RetrieveStatus();
                if (!status.IsDirty)
                {
                    _logger?.Error("【git】no file changed!");
                    return result;
                }

                var modified = status.Modified.Select(m => m.FilePath).ToList();
                var untracked = status.Untracked.Select(m => m.FilePath).ToList();
                var added = status.Added.Select(m => m.FilePath).ToList();
                var ignored = status.Ignored.Select(m => m.FilePath).ToList();

                result.AddRange(modified);
                result.AddRange(untracked);
                result.AddRange(added);
                result.AddRange(ignored);
                result = result.Distinct().ToList();
            }
            catch (Exception ex)
            {
                _logger?.Error("【git】Get Changes FileList err:" + ex.Message);
            }
            return result;
        }

        /// <summary>提交</summary>
        public void CommitChanges(string commit)
        {
            try
            {
                _repository.Commit(commit,
                    new Signature("antdeploy", "antdeploy@email.com", DateTimeOffset.Now),
                    new Signature("antdeploy", "antdeploy@email.com", DateTimeOffset.Now));
            }
            catch (Exception e)
            {
                _logger?.Warn("【git】commit err:" + e.Message);
            }
        }

        public void Dispose()
        {
            _repository?.Dispose();
        }
    }
}
