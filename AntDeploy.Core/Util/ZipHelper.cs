using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace AntDeploy.Core.Util
{
    /// <summary>
    /// 打包辅助：把目录或指定文件列表压缩成 zip 流。
    /// 从 AntDeployWinform.Util.ZipHelper 提取核心能力，去掉中文路径特殊映射等无关依赖。
    /// </summary>
    public static class ZipHelper
    {
        /// <summary>
        /// 把整个目录压缩到输出流。
        /// compressionLevel 默认 <see cref="CompressionLevel.Fastest"/>：发布目录多为已压缩的 DLL/资源，
        /// Optimal 压缩比几乎无收益却极耗 CPU；上传通常在快速内网，取 Fastest 显著缩短打包耗时。
        /// </summary>
        public static void CreateFromDirectory(string sourceDir, Stream output, List<string> ignoreList = null, bool includeBaseDirectory = false, CompressionLevel compressionLevel = CompressionLevel.Fastest)
        {
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                var baseName = includeBaseDirectory ? new DirectoryInfo(sourceDir).Name : string.Empty;
                var files = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    var rel = GetRelativePath(sourceDir, file).Replace('\\', '/');
                    if (IsGitMeta(rel) || IsIgnore(rel, ignoreList)) continue;
                    var entryName = string.IsNullOrEmpty(baseName) ? rel : baseName + "/" + rel;
                    AddFile(archive, file, entryName, compressionLevel);
                }
            }
        }

        /// <summary>仅压缩给定文件列表（fileList 为相对 baseDir 的相对路径或绝对路径）。</summary>
        public static void CreateFromFiles(List<string> fileList, string baseDir, Stream output, List<string> ignoreList = null, bool includeBaseDirectory = false, CompressionLevel compressionLevel = CompressionLevel.Fastest)
        {
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                var baseName = includeBaseDirectory ? new DirectoryInfo(baseDir).Name : string.Empty;
                foreach (var file in fileList)
                {
                    var full = Path.IsPathRooted(file) ? file : Path.Combine(baseDir, file);
                    if (!File.Exists(full)) continue;
                    var rel = GetRelativePath(baseDir, full).Replace('\\', '/');
                    if (IsGitMeta(rel) || IsIgnore(rel, ignoreList)) continue;
                    var entryName = string.IsNullOrEmpty(baseName) ? rel : baseName + "/" + rel;
                    AddFile(archive, full, entryName, compressionLevel);
                }
            }
        }

        private static void AddFile(ZipArchive archive, string fullPath, string entryName, CompressionLevel compressionLevel)
        {
            var entry = archive.CreateEntry(entryName, compressionLevel);
            using (var entryStream = entry.Open())
            using (var fs = File.OpenRead(fullPath))
            {
                fs.CopyTo(entryStream);
            }
        }

        /// <summary>
        /// 是否为增量基线仓库的 .git 元数据（顶层 .git 目录内）。该仓库仅用于本地增量 diff，
        /// 绝不能打进发布包（会撑大包并把 git 对象污染到目标服务器）；rel 已统一为 '/' 分隔。
        /// </summary>
        public static bool IsGitMeta(string rel)
        {
            if (string.IsNullOrEmpty(rel)) return false;
            return string.Equals(rel, ".git", StringComparison.OrdinalIgnoreCase)
                   || rel.StartsWith(".git/", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsIgnore(string matchEntryName, List<string> ignoreList)
        {
            if (ignoreList == null || ignoreList.Count == 0) return false;
            foreach (var ig in ignoreList)
            {
                if (string.IsNullOrWhiteSpace(ig)) continue;
                if (matchEntryName.IndexOf(ig.Trim(), StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        /// <summary>
        /// 计算 fullPath 相对 basePath 的相对路径。
        /// 用字符串前缀截断替代 Uri.MakeRelativeUri：后者每文件分配多个 Uri 对象，
        /// 数千文件时是打包热点；此处 basePath 恒为 Directory.GetFiles 的枚举根，前缀关系成立。
        /// </summary>
        private static string GetRelativePath(string basePath, string fullPath)
        {
            var prefix = basePath.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            if (fullPath.Length > prefix.Length &&
                string.Compare(fullPath, 0, prefix, 0, prefix.Length, StringComparison.OrdinalIgnoreCase) == 0)
            {
                return fullPath.Substring(prefix.Length);
            }
            // 兜底（分隔符风格不一致等罕见情况）：退回原 Uri 计算，保证正确性优先。
            var baseUri = new Uri(prefix);
            var fullUri = new Uri(fullPath);
            return Uri.UnescapeDataString(baseUri.MakeRelativeUri(fullUri).ToString()).Replace('/', Path.DirectorySeparatorChar);
        }
    }
}
