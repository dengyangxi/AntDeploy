using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Owin;

namespace AntDeployAgent.WebApiCore
{
    /// <summary>
    /// POST数据处理类
    /// </summary>
   public class FormHandler
    {

        /// <summary>
        /// 上传过程中生成的临时文件（文件型表单项），部署完成后由 PublishService 调用 CleanupTempFiles 清理
        /// </summary>
        private readonly List<string> _tempFiles = new List<string>();

        /// <summary>
        /// 表单元素对象
        /// </summary>
        public class FormItem
        {
            /// <summary>
            /// 字段名(表单域名称)
            /// </summary>
            public string FieldName;

            /// <summary>
            /// 文件名
            /// </summary>
            public string FileName;

            /// <summary>
            /// 文件数据实体（仅历史兼容：当以小内存方式解析且保留在内存时使用；流式解析时为 null）
            /// </summary>
            public byte[] FileBody;

            /// <summary>
            /// 文件落盘的临时文件路径（方案D：流式写入临时文件，避免整包进内存）。
            /// 当 FileBody 为 null 时，文件内容以该路径提供。
            /// </summary>
            public string FilePath;

            /// <summary>
            /// 文本内容
            /// </summary>
            public string TextValue;
        }


        /// <summary>
        /// 保存表单域的字典
        /// </summary>
        private IDictionary<string, FormItem> _formDict = new Dictionary<string, FormItem>();


        /// <summary>
        /// 表单字段名列表
        /// </summary>
        public List<string> FormNames
        {
            get { return _formDict.Keys.ToList(); }
        }

        /// <summary>
        /// 获得的表单对象列表
        /// </summary>
        public IList<FormItem> FormItems
        {
            get
            {
                if (_formDict.Count < 1) return new List<FormItem>();
                return _formDict.Values.ToList();
            }
        }

        /// <summary>
        /// 表单字段数量
        /// </summary>
        public int FieldCount { get { return _formDict.Count; } }


        /// <summary>
        /// 根据字段名获取表单域内容
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public FormItem this[string name]
        {
            get
            {
                if (!_formDict.Keys.Contains(name)) return null;
                return _formDict[name];
            }
        }


        /// <summary>
        /// 实例化一个表单处理对象
        /// </summary>
        /// <param name="context"></param>
        public FormHandler(IOwinContext context)
        {
            if (context.Request.Method.ToUpper() != "POST") throw new Exception();
            var strLen = context.Request.Headers["Content-Length"];
            if (string.IsNullOrEmpty(strLen)) throw new Exception("Not 'Content-Length'");
            if (!uint.TryParse(strLen, out uint sumsize)) throw new Exception("Content-Length Error");
            if (string.IsNullOrEmpty(context.Request.ContentType)) throw new Exception("Error:ContentType is NULL.");

            // 方案D：把请求体“流式”写入临时文件，避免把整个上传包缓冲到内存（降低 LOH 压力与 OOM 风险）
            var bodyTempFile = Path.GetTempFileName();
            try
            {
                var tmpbuffer = new byte[1024 * 64];
                uint nextsize = sumsize;

                // 关键修复（A）：读取加超时，避免客户端网络抖动导致连接假死时 Read 永久阻塞线程池线程
                const int readTimeoutMilliseconds = 5 * 60 * 1000; // 5 分钟
                using (var bodyFs = new FileStream(bodyTempFile, FileMode.Create, FileAccess.Write, FileShare.None, tmpbuffer.Length))
                {
                    while (nextsize > 0)
                    {
                        var toRead = (int)Math.Min(tmpbuffer.Length, nextsize);
                        var readTask = context.Request.Body.ReadAsync(tmpbuffer, 0, toRead);
                        if (!readTask.Wait(readTimeoutMilliseconds))
                        {
                            throw new Exception("Upload Read Timeout(>5min),connection maybe broken");
                        }

                        int len;
                        try
                        {
                            len = readTask.Result;
                        }
                        catch (AggregateException ae)
                        {
                            throw new Exception("Upload Read Error:" + (ae.InnerException?.Message ?? ae.Message));
                        }

                        if (len < 1) throw new Exception("Network Is Disconnect?");
                        nextsize -= (uint)len;

                        bodyFs.Write(tmpbuffer, 0, len);
                    }
                }

                // 正文类型
                var contentTypes = context.Request.ContentType.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

                var isMultipart = false;
                var boundar = "";
                foreach (var s in contentTypes)
                {
                    var ss = s.Trim().ToLower();
                    if (ss == "multipart/form-data") isMultipart = true;
                    if (ss.StartsWith("boundary=")) boundar = "--" + s.Trim().Substring(9);
                }

                if (isMultipart && boundar != "")
                {
                    ParseMultipartFromFile(bodyTempFile, boundar);
                }
                else
                {
                    // 其它格式（x-www-form-urlencoded 等，体积很小）：直接从临时文件读全量解析
                    ParseForm(File.ReadAllBytes(bodyTempFile), null);
                }
            }
            catch
            {
                // 解析阶段异常：清理已生成的文件型临时文件，避免磁盘泄漏
                foreach (var t in _tempFiles)
                {
                    try { File.Delete(t); } catch { /* ignore */ }
                }
                _tempFiles.Clear();
                throw;
            }
            finally
            {
                // 请求体临时文件解析完毕后不再需要
                try { File.Delete(bodyTempFile); } catch { /* ignore */ }
            }
        }


        /// <summary>
        /// 部署完成后清理“文件型表单项”的临时文件（由 PublishService 调用）
        /// </summary>
        public void CleanupTempFiles()
        {
            foreach (var t in _tempFiles)
            {
                try { File.Delete(t); } catch { /* ignore */ }
            }
            _tempFiles.Clear();
        }


        /// <summary>
        /// 处理多实体表单（流式：从临时文件按偏移截取每个 part，文件型 part 直接落盘到临时文件，不再整包进内存）
        /// </summary>
        /// <param name="bodyFile">请求体落盘的临时文件</param>
        /// <param name="boundar">分隔线（已带前缀 "--"）</param>
        private void ParseMultipartFromFile(string bodyFile, string boundar)
        {
            var delimiter = Encoding.ASCII.GetBytes(boundar);
            var boundaryOffsets = FindBoundaryOffsets(bodyFile, delimiter);
            if (boundaryOffsets.Count < 2) return;

            using (var fs = new FileStream(bodyFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            {
                for (int i = 0; i < boundaryOffsets.Count - 1; i++)
                {
                    long partStart = boundaryOffsets[i];
                    long partEnd = boundaryOffsets[i + 1];

                    long headerStart = partStart + delimiter.Length + 2; // 跳过 "--boundary" 及其后的 \r\n
                    if (headerStart >= partEnd - 2) continue;

                    long contentStart = FindDoubleCrlf(fs, headerStart, partEnd - 2);
                    if (contentStart < 0) continue;
                    contentStart += 4; // 跳过双回车

                    long contentEnd = partEnd - 2; // 排除下一个 boundary 之前的 \r\n
                    if (contentEnd <= contentStart) continue;

                    int headerLen = (int)(contentStart - 4 - headerStart);
                    if (headerLen < 0) continue;
                    var headerBytes = ReadRange(fs, headerStart, headerLen);
                    var headerText = Encoding.ASCII.GetString(headerBytes);

                    string name = "", fileName = "";
                    foreach (var s in headerText.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var ss = s.Trim();
                        if (ss.StartsWith("Content-Disposition:"))
                        {
                            var disp = ss.Substring(20).Trim();
                            foreach (var item in disp.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                            {
                                var itemx = item.Trim();
                                if (itemx.StartsWith("name=")) name = itemx.Substring(5).Trim(new[] { '\x20', '\"' });
                                if (itemx.StartsWith("filename=")) fileName = itemx.Substring(9).Trim(new[] { '\x20', '\"' });
                            }
                        }
                    }

                    if (name == "") continue;

                    if (!string.IsNullOrEmpty(fileName))
                    {
                        // 文件型：把内容区流式拷贝到临时文件，仅保留路径，避免整包进内存
                        string partTemp = Path.GetTempFileName();
                        _tempFiles.Add(partTemp);
                        using (var outFs = new FileStream(partTemp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                        {
                            CopyRange(fs, contentStart, contentEnd - contentStart, outFs);
                        }

                        _formDict[name] = new FormItem { FieldName = name, FileName = fileName, FilePath = partTemp };
                    }
                    else
                    {
                        // 普通文本表单项（体积小）
                        long textLen = contentEnd - contentStart;
                        var textBytes = ReadRange(fs, contentStart, (int)Math.Min(textLen, int.MaxValue));
                        var txt = Encoding.UTF8.GetString(textBytes);
                        _formDict[name] = new FormItem { FieldName = name, TextValue = txt };
                    }
                }
            }
        }


        /// <summary>
        /// 处理普通表单（键值对）
        /// </summary>
        /// <param name="body">正文数据</param>
        /// <param name="charset">目标数据字符集</param>
        void ParseForm(byte[] body, Encoding charset)
        {
            if (charset == null) charset = Encoding.UTF8;

            //ContentType = "application/x-www-form-urlencoded;charset=utf8";
            // name=value&name1=value1

            var src_string = Encoding.ASCII.GetString(body);
            var kvs = src_string.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var kv in kvs)
            {

                var pos = kv.IndexOf('=');
                if (pos < 1) continue;

                var key = kv.Substring(pos).Trim();
                if (string.IsNullOrEmpty(key)) continue;

                var val = kv.Substring(pos + 1).Trim();
                if (string.IsNullOrEmpty(val) == false && val.IndexOf("%") != -1) val = UrlHelper.UrlDecode(val, charset);

                var content = new FormItem { FieldName = key, TextValue = val };
                _formDict.Add(key, content);
            }
        }




        #region 流式解析辅助方法

        /// <summary>
        /// KMP：计算模式串的最长公共前后缀
        /// </summary>
        private static int[] ComputeLps(byte[] pattern)
        {
            int m = pattern.Length;
            var lps = new int[m];
            int len = 0;
            int i = 1;
            while (i < m)
            {
                if (pattern[i] == pattern[len])
                {
                    lps[i++] = ++len;
                }
                else if (len != 0)
                {
                    len = lps[len - 1];
                }
                else
                {
                    lps[i++] = 0;
                }
            }
            return lps;
        }

        /// <summary>
        /// 在文件中查找所有 boundary 出现的位置（流式扫描，内存占用恒定）
        /// </summary>
        private static List<long> FindBoundaryOffsets(string filePath, byte[] delimiter)
        {
            var offsets = new List<long>();
            int dlen = delimiter.Length;
            if (dlen == 0) return offsets;
            var lps = ComputeLps(delimiter);
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            {
                var buf = new byte[1 << 16];
                long absPos = 0;
                int matched = 0;
                int read;
                while ((read = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    for (int j = 0; j < read; j++)
                    {
                        while (matched > 0 && buf[j] != delimiter[matched])
                            matched = lps[matched - 1];
                        if (buf[j] == delimiter[matched]) matched++;
                        if (matched == dlen)
                        {
                            offsets.Add(absPos + j - dlen + 1);
                            matched = 0;
                        }
                    }
                    absPos += read;
                }
            }
            return offsets;
        }

        /// <summary>
        /// 在 [start, endExclusive) 区间查找第一个 \r\n\r\n 的位置
        /// </summary>
        private static long FindDoubleCrlf(FileStream fs, long start, long endExclusive)
        {
            byte[] target = { 0x0D, 0x0A, 0x0D, 0x0A };
            int state = 0;
            var buf = new byte[1 << 12];
            fs.Seek(start, SeekOrigin.Begin);
            long pos = start;
            while (pos < endExclusive)
            {
                int toRead = (int)Math.Min(buf.Length, endExclusive - pos);
                int read = fs.Read(buf, 0, toRead);
                if (read == 0) break;
                for (int j = 0; j < read; j++)
                {
                    if (buf[j] == target[state])
                    {
                        state++;
                        if (state == 4) return pos + j - 3;
                    }
                    else
                    {
                        state = buf[j] == target[0] ? 1 : 0;
                    }
                }
                pos += read;
            }
            return -1;
        }

        /// <summary>
        /// 从文件指定偏移读取指定长度的内容（用于小体积的 header / 文本表单项）
        /// </summary>
        private static byte[] ReadRange(FileStream fs, long offset, int length)
        {
            if (length <= 0) return new byte[0];
            var bytes = new byte[length];
            fs.Seek(offset, SeekOrigin.Begin);
            int total = 0;
            while (total < length)
            {
                int r = fs.Read(bytes, total, length - total);
                if (r == 0) break;
                total += r;
            }
            if (total < length) Array.Resize(ref bytes, total);
            return bytes;
        }

        /// <summary>
        /// 将文件指定区间的内容流式拷贝到目标流（用于大体积文件型表单项，内存占用恒定）
        /// </summary>
        private static void CopyRange(FileStream srcFs, long offset, long length, Stream outFs)
        {
            if (length <= 0) return;
            srcFs.Seek(offset, SeekOrigin.Begin);
            var buf = new byte[1 << 16];
            long remaining = length;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(buf.Length, remaining);
                int r = srcFs.Read(buf, 0, toRead);
                if (r == 0) break;
                outFs.Write(buf, 0, r);
                remaining -= r;
            }
        }

        #endregion
    }
}
