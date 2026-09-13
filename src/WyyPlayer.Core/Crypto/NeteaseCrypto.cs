using System;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace WyyPlayer.Core.Crypto
{
    /// <summary>
    /// 网易云 WEAPI / EAPI 加密原语。纯函数，无状态，可脱离游戏与网络测试。
    /// 不使用任何第三方库 —— AES/MD5 来自 BCL，教科书 RSA 用 BigInteger 实现。
    /// </summary>
    public static class NeteaseCrypto
    {
        private const string Base62 =
            "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

        private static readonly BigInteger RsaExponent;
        private static readonly BigInteger RsaModulus;

        static NeteaseCrypto()
        {
            var b64 = NeteaseRsaKey.PublicKeyPem
                .Replace("-----BEGIN PUBLIC KEY-----", "")
                .Replace("-----END PUBLIC KEY-----", "")
                .Replace("\n", "")
                .Replace("\r", "")
                .Trim();

            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(b64), out _);
            var p = rsa.ExportParameters(false);

            RsaModulus = new BigInteger(p.Modulus, isUnsigned: true, isBigEndian: true);
            RsaExponent = new BigInteger(p.Exponent, isUnsigned: true, isBigEndian: true);
        }

        /// <summary>生成 16 位 base62 随机密钥。</summary>
        public static string CreateSecretKey(int size = 16)
        {
            var sb = new StringBuilder(size);
            for (int i = 0; i < size; i++)
                sb.Append(Base62[RandomNumberGenerator.GetInt32(Base62.Length)]);
            return sb.ToString();
        }

        private const string PresetKey = "0CoJUm6Qyw8W8jud";
        private const string CbcIv = "0102030405060708";
        private const string EapiKey = "e82ckenh8dichen8";

        public readonly record struct WeapiPayload(string Params, string EncSecKey);

        /// <summary>WEAPI 双层加密。每次调用使用新的随机密钥。</summary>
        public static WeapiPayload Weapi(string jsonBody) => Weapi(jsonBody, CreateSecretKey(16));

        /// <summary>
        /// WEAPI 双层加密，使用指定的 secretKey。
        /// 仅供测试与重现使用 —— 生产路径请用随机密钥重载。
        /// </summary>
        public static WeapiPayload Weapi(string jsonBody, string secretKey)
        {
            var first = AesCbcBase64(jsonBody, PresetKey, CbcIv);
            var second = AesCbcBase64(first, secretKey, CbcIv);
            var encSecKey = RsaNoPaddingHexLower(secretKey);
            return new WeapiPayload(second, encSecKey);
        }

        /// <summary>EAPI 加密。uri 必须是形如 /api/song/lyric/v1 的路径。</summary>
        public static string Eapi(string uri, string jsonBody)
        {
            var digest = Md5HexLower($"nobody{uri}use{jsonBody}md5forencrypt");
            var data = $"{uri}-36cd479b6b5-{jsonBody}-36cd479b6b5-{digest}";
            return AesEcbHexUpper(data, EapiKey);
        }

        public static string AesCbcBase64(string plaintext, string key, string iv)
        {
            using var aes = Aes.Create();
            aes.Key = Encoding.UTF8.GetBytes(key);
            aes.IV = Encoding.UTF8.GetBytes(iv);
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var enc = aes.CreateEncryptor();
            var bytes = Encoding.UTF8.GetBytes(plaintext);
            return Convert.ToBase64String(enc.TransformFinalBlock(bytes, 0, bytes.Length));
        }

        public static string AesEcbHexUpper(string plaintext, string key)
        {
            using var aes = Aes.Create();
            aes.Key = Encoding.UTF8.GetBytes(key);
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.PKCS7;

            using var enc = aes.CreateEncryptor();
            var bytes = Encoding.UTF8.GetBytes(plaintext);
            var cipher = enc.TransformFinalBlock(bytes, 0, bytes.Length);
            return Convert.ToHexString(cipher);   // 大写
        }

        public static string AesCbcDecryptBase64(string base64, string key, string iv)
        {
            using var aes = Aes.Create();
            aes.Key = Encoding.UTF8.GetBytes(key);
            aes.IV = Encoding.UTF8.GetBytes(iv);
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var dec = aes.CreateDecryptor();
            var cipher = Convert.FromBase64String(base64);
            return Encoding.UTF8.GetString(dec.TransformFinalBlock(cipher, 0, cipher.Length));
        }

        public static byte[] AesEcbDecryptHex(string hexUpper, string key)
        {
            using var aes = Aes.Create();
            aes.Key = Encoding.UTF8.GetBytes(key);
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.PKCS7;

            using var dec = aes.CreateDecryptor();
            var cipher = Convert.FromHexString(hexUpper);
            return dec.TransformFinalBlock(cipher, 0, cipher.Length);
        }

        public static string Md5HexLower(string plaintext)
        {
            var hash = MD5.HashData(Encoding.UTF8.GetBytes(plaintext));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        /// <summary>
        /// 教科书 RSA（无填充）。先把输入字符反转，再按 UTF-8 字节解释为大整数，
        /// 结果转小写十六进制并左补零至 256 字符。
        /// </summary>
        public static string RsaNoPaddingHexLower(string text)
        {
            var chars = text.ToCharArray();
            Array.Reverse(chars);
            var bytes = Encoding.UTF8.GetBytes(new string(chars));

            var m = new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
            var c = BigInteger.ModPow(m, RsaExponent, RsaModulus);

            return c.ToString("x").PadLeft(256, '0');
        }
    }
}