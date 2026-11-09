using System;
using System.Security.Cryptography;

namespace AntDeployWeb.Services
{
    /// <summary>
    /// PBKDF2(SHA256) 密码哈希工具。无额外依赖，恒定时间比较，避免时序侧信道。
    /// </summary>
    public static class PasswordHasher
    {
        private const int SaltSize = 16;      // 128-bit
        private const int KeySize = 32;       // 256-bit
        private const int Iterations = 100_000;

        /// <summary>生成随机盐（Base64）。</summary>
        public static string CreateSalt()
        {
            var salt = RandomNumberGenerator.GetBytes(SaltSize);
            return Convert.ToBase64String(salt);
        }

        /// <summary>用给定盐计算哈希（Base64）。</summary>
        public static string Hash(string password, string saltBase64)
        {
            var salt = Convert.FromBase64String(saltBase64);
            using var pbkdf2 = new Rfc2898DeriveBytes(password ?? string.Empty, salt, Iterations, HashAlgorithmName.SHA256);
            return Convert.ToBase64String(pbkdf2.GetBytes(KeySize));
        }

        /// <summary>校验密码是否匹配（恒定时间比较）。</summary>
        public static bool Verify(string password, string saltBase64, string hashBase64)
        {
            if (string.IsNullOrEmpty(saltBase64) || string.IsNullOrEmpty(hashBase64)) return false;
            try
            {
                var expected = Convert.FromBase64String(hashBase64);
                var actual = Convert.FromBase64String(Hash(password, saltBase64));
                return CryptographicOperations.FixedTimeEquals(expected, actual);
            }
            catch
            {
                return false;
            }
        }
    }
}
