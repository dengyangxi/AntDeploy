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
        /// <summary>把整个目录压缩到输出流</summary>
        public static void CreateFromDirectory(string sourceDir, Stream output, List<string> ignoreList = null, bool includeBaseDirectory = false)
        {
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                var baseName = includeBaseDirectory ? new DirectoryInfo(sourceDir).Name : string.Empty;
                var files = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    var rel = GetRelativePath(sourceDir, file).Replace('\\', '/');
                    if (IsIgnore(rel, ignoreList)) continue;
                    var entryName = string.IsNullOrEmpty(baseName) ? rel : baseName + "/" + rel;
                    AddFile(archive, file, entryName);
                }
            }
        }

        /// <summary>仅压缩给定文件列表（fileList 为相对 baseDir 的相对路径或绝对路径）</summary>
        public static void CreateFromFiles(List<string> fileList, string baseDir, Stream output, List<string> ignoreList = null, bool includeBaseDirectory = false)
        {
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                var baseName = includeBaseDirectory ? new DirectoryInfo(baseDir).Name : string.Empty;
                foreach (var file in fileList)
                {
                    var full = Path.IsPathRooted(file) ? file : Path.Combine(baseDir, file);
                    if (!File.Exists(full)) continue;
                    var rel = GetRelativePath(baseDir, full).Replace('\\', '/');
                    if (IsIgnore(rel, ignoreList)) continue;
                    var entryName = string.IsNullOrEmpty(baseName) ? rel : baseName + "/" + rel;
                    AddFile(archive, full, entryName);
                }
            }
        }

        private static void AddFile(ZipArchive archive, string fullPath, string entryName)
        {
            var entry = archive.CreateEntry(entryName);
            using (var entryStream = entry.Open())
            using (var fs = File.OpenRead(fullPath))
            {
                fs.CopyTo(entryStream);
            }
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

        private static string GetRelativePath(string basePath, string fullPath)
        {
            basePath = basePath.TrimEnd('\\', '/');
            if (!basePath.EndsWith(Path.DirectorySeparatorChar.ToString()) && !basePath.EndsWith("/"))
                basePath += Path.DirectorySeparatorChar;
            var baseUri = new Uri(basePath);
            var fullUri = new Uri(fullPath);
            var rel = Uri.UnescapeDataString(baseUri.MakeRelativeUri(fullUri).ToString());
            return rel.Replace('/', Path.DirectorySeparatorChar);
        }
    }
}
