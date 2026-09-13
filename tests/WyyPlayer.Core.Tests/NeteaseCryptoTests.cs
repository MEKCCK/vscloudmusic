using System.Text;
using WyyPlayer.Core.Crypto;
using Xunit;

namespace WyyPlayer.Core.Tests
{
    public class NeteaseCryptoTests
    {
        private const string PresetKey = "0CoJUm6Qyw8W8jud";
        private const string Iv = "0102030405060708";
        private const string EapiKey = "e82ckenh8dichen8";

        [Fact]
        public void AES_CBC_单层_向量匹配()
        {
            var actual = NeteaseCrypto.AesCbcBase64("{\"a\":1}", PresetKey, Iv);
            Assert.Equal("U7nqija8yGUt9t6SPGXNjQ==", actual);
        }

        [Fact]
        public void AES_CBC_双层_向量匹配()
        {
            var once = NeteaseCrypto.AesCbcBase64("{\"a\":1}", PresetKey, Iv);
            var twice = NeteaseCrypto.AesCbcBase64(once, "ABCDEFGHIJKLMNOP", Iv);
            Assert.Equal("wCWrbf/YYR8IAh95T/DZcn9SZ1h0hDD2szuMkooQwq4=", twice);
        }

        [Fact]
        public void MD5_小写向量匹配()
        {
            var msg = "nobody/api/song/lyric/v1use{\"id\":123}md5forencrypt";
            Assert.Equal("3b3e7370a87bac5ad17eb5e768fc8a4a", NeteaseCrypto.Md5HexLower(msg));
        }

        [Fact]
        public void 教科书RSA_向量匹配()
        {
            // 输入会被反转，再解释为 UTF-8 字节的大整数
            var actual = NeteaseCrypto.RsaNoPaddingHexLower("ABCDEFGHIJKLMNOP");

            const string Expected =
                "45a6dd522c7172459e1b4e203e01e846983fab6ef9974c18aedcd06f01dcad661db9daafe903fa12cead653fc4ed21a5a0d1ecc5b16bc7b4f1c3552d0bfffca5a1f0984b191132e98be8479a49f1b4e7ef0a5e3cb83613483c280d6e5373b79b31a91331df8dd064c5083900f471d7d11ed2d33fd5fcf7f8d64d5c37c1e2f249";

            Assert.Equal(256, actual.Length);
            Assert.Equal(Expected, actual);
        }

        [Fact]
        public void AES_ECB_可回环解密()
        {
            var original = "/api/song/lyric/v1-36cd479b6b5-{\"id\":123}-36cd479b6b5-abc";
            var enc = NeteaseCrypto.AesEcbHexUpper(original, EapiKey);
            var dec = Encoding.UTF8.GetString(NeteaseCrypto.AesEcbDecryptHex(enc, EapiKey));
            Assert.Equal(original, dec);
        }

        [Fact]
        public void Eapi_向量匹配()
        {
            var actual = NeteaseCrypto.Eapi("/api/song/lyric/v1", "{\"id\":123}");

            const string Expected =
                "04AE33D34A93FE3EC22DA8FA305D290AB337D0FE5F36D211DE0D338CC6AA89D0" +
                "D49D8C5E8D4B64EFB325F3B7A3EDDD9A1D62D0B8E1CFC551A6A06B0649872AA" +
                "3A612B2E13F6699C67A81B248DE9793B24934FC8F4D93886D0EF9FA7F6741C635";

            Assert.Equal(Expected, actual);
        }

        [Fact]
        public void Weapi_指定密钥时_逐字符匹配参考向量()
        {
            // 参考向量由 node-forge + crypto-js 实测产出
            var payload = NeteaseCrypto.Weapi("{\"a\":1}", "ABCDEFGHIJKLMNOP");

            Assert.Equal("wCWrbf/YYR8IAh95T/DZcn9SZ1h0hDD2szuMkooQwq4=", payload.Params);

            const string ExpectedEncSecKey =
                "45a6dd522c7172459e1b4e203e01e846983fab6ef9974c18aedcd06f01dcad661db9daafe903fa12cead653fc4ed21a5a0d1ecc5b16bc7b4f1c3552d0bfffca5a1f0984b191132e98be8479a49f1b4e7ef0a5e3cb83613483c280d6e5373b79b31a91331df8dd064c5083900f471d7d11ed2d33fd5fcf7f8d64d5c37c1e2f249";

            Assert.Equal(256, payload.EncSecKey.Length);
            Assert.Equal(ExpectedEncSecKey, payload.EncSecKey);
        }

        [Fact]
        public void Weapi_可回环还原出原文()
        {
            var json = "{\"s\":\"测试\",\"type\":1}";
            const string Key = "ABCDEFGHIJKLMNOP";

            // 必须走指定密钥的重载 —— 随机密钥版本无法在测试里解密外层
            var payload = NeteaseCrypto.Weapi(json, Key);

            // params = AES-CBC( AES-CBC(json, presetKey), secretKey )，故先解外层再解内层
            var inner = NeteaseCrypto.AesCbcDecryptBase64(payload.Params, Key, "0102030405060708");
            var plain = NeteaseCrypto.AesCbcDecryptBase64(inner, "0CoJUm6Qyw8W8jud", "0102030405060708");

            Assert.Equal(json, plain);

            // 随机密钥版本仍应可用且每次不同
            Assert.NotEqual(NeteaseCrypto.Weapi(json).Params, NeteaseCrypto.Weapi(json).Params);
        }
    }
}