using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace AntDeploy.Core.Util
{
    /// <summary>
    /// 通用工具：加密/哈希/机器信息。从 AntDeployWinform.Util.CodingHelper 提取并去掉 NLog 依赖。
    /// </summary>
    public static class CodingHelper
    {
        /// <summary>AES 密钥（与旧客户端保持一致以便互通；生产环境应通过配置覆盖）</summary>
        public static string AesKey { get; set; } = "56dPz3VDYwGpJYqe7dFG0g==";

        public static string MD5(string input)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(input);
                var hash = md5.ComputeHash(bytes);
                var sb = new StringBuilder();
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        public static string AESEncrypt(string plainText, string key = null)
        {
            if (string.IsNullOrEmpty(plainText)) return plainText;
            var keyBytes = Convert.FromBase64String(key ?? AesKey);
            using (var aes = Aes.Create())
            {
                aes.Key = keyBytes;
                aes.IV = keyBytes; // 与旧实现保持简单一致（非最佳实践，仅兼容）
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.PKCS7;
                using (var encryptor = aes.CreateEncryptor())
                {
                    var plainBytes = Encoding.UTF8.GetBytes(plainText);
                    var encrypted = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
                    return Convert.ToBase64String(encrypted);
                }
            }
        }

        public static string AESDecrypt(string cipherText, string key = null)
        {
            if (string.IsNullOrEmpty(cipherText)) return cipherText;
            var keyBytes = Convert.FromBase64String(key ?? AesKey);
            using (var aes = Aes.Create())
            {
                aes.Key = keyBytes;
                aes.IV = keyBytes;
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.PKCS7;
                using (var decryptor = aes.CreateDecryptor())
                {
                    var cipherBytes = Convert.FromBase64String(cipherText);
                    var decrypted = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
                    return Encoding.UTF8.GetString(decrypted);
                }
            }
        }

        public static bool IsNaturalNumber(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (var c in text) if (!char.IsDigit(c)) return false;
            return true;
        }

        public static string GetMacAddress()
        {
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    var mac = nic.GetPhysicalAddress();
                    if (mac != null && mac.GetAddressBytes().Length > 0)
                        return BitConverter.ToString(mac.GetAddressBytes()).Replace("-", "").ToUpper();
                }
            }
            catch { }
            return "000000000000";
        }

        public static string GetLocalIPAddress()
        {
            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                        return ip.ToString();
                }
            }
            catch { }
            return "127.0.0.1";
        }

        public static bool IsChineseSystem()
        {
            try { return System.Globalization.CultureInfo.CurrentCulture.Name.StartsWith("zh"); }
            catch { return true; }
        }

        /// <summary>把 "192.168.1.10:5000" 或 "http://192.168.1.10:5000" 规整成带 http:// 前缀的 base url</summary>
        public static string NormalizeAgentUrl(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return host;
            host = host.Trim();
            if (!host.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                host = "http://" + host;
            }
            return host.TrimEnd('/');
        }
    }
}
