using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using WyyPlayer.Core.Net;
using Xunit;

namespace WyyPlayer.Core.Tests
{
    /// <summary>
    /// Task 4 传输层测试。不触碰网络：
    /// - cookie 维护走公开 API（SetCookie/GetCookie/CookieHeader/ImportCookieHeader）；
    /// - csrf/header 注入是纯字符串逻辑，经反射直击私有方法，并校验输出是可解析的 JSON；
    /// - EAPI header 构造（pc 设备默认、requestId 形态、Cookie 头生成）为内部成员直击调用。
    /// URL 重写与真实请求由 Task 5-7 端到端覆盖，此处不伪造网络测试。
    /// </summary>
    public class NeteaseHttpTests
    {
        // ---------- cookie 层（公开 API，无网络） ----------

        [Fact]
        public void SetCookie_后_CookieHeader_为_name_value_分号拼接()
        {
            using var http = new NeteaseHttp();
            http.SetCookie("a", "1");
            http.SetCookie("b", "2");

            var header = http.CookieHeader;
            var parts = header.Split("; ").OrderBy(x => x).ToArray();
            Assert.Equal(new[] { "a=1", "b=2" }, parts);
        }

        [Fact]
        public void GetCookie_大小写不敏感_未命中返回null()
        {
            using var http = new NeteaseHttp();
            http.SetCookie("MUSIC_U", "abc123");

            Assert.Equal("abc123", http.GetCookie("music_u"));
            Assert.Equal("abc123", http.GetCookie("MUSIC_U"));
            Assert.Null(http.GetCookie("NOPE"));
        }

        [Fact]
        public void 构造传入原始cookie头_可解析出行内cookie()
        {
            using var http = new NeteaseHttp("MUSIC_U=xyz; __csrf=789");

            Assert.Equal("xyz", http.GetCookie("MUSIC_U"));
            Assert.Equal("789", http.GetCookie("__csrf"));

            var header = http.CookieHeader;
            Assert.Contains("MUSIC_U=xyz", header);
            Assert.Contains("__csrf=789", header);
        }

        [Fact]
        public void ImportCookieHeader_可增量导入()
        {
            using var http = new NeteaseHttp();
            http.ImportCookieHeader("  __csrf = 111 ; k=v  ");

            Assert.Equal("111", http.GetCookie("__csrf"));
            Assert.Equal("v", http.GetCookie("k"));
        }

        // ---------- InjectCsrf 字符串手术（私有静态，反射直击） ----------

        [Theory]
        [InlineData("{}", "{\"csrf_token\":\"abc\"}")]
        [InlineData("", "{\"csrf_token\":\"abc\"}")]
        [InlineData("   ", "{\"csrf_token\":\"abc\"}")]
        [InlineData("{\"type\":3}", "{\"type\":3,\"csrf_token\":\"abc\"}")]
        [InlineData("{\"type\":3 }", "{\"type\":3,\"csrf_token\":\"abc\"}")]
        [InlineData("{ }", "{\"csrf_token\":\"abc\"}")]
        [InlineData("{\"a\":1, \"b\":2}", "{\"a\":1, \"b\":2,\"csrf_token\":\"abc\"}")]
        public void InjectCsrf_输出与手推一致(string input, string expected)
        {
            Assert.Equal(expected, InvokeInjectCsrf(input, "abc"));
        }

        [Fact]
        public void InjectCsrf_非对象输入_原样返回()
        {
            // 不以 } 结尾的输入按约定原样返回，不注入
            Assert.Equal("[1,2]", InvokeInjectCsrf("[1,2]", "abc"));
            Assert.Equal("\"hello\"", InvokeInjectCsrf("\"hello\"", "abc"));
        }

        [Theory]
        [InlineData("{}")]
        [InlineData("{\"type\":3}")]
        [InlineData("{\"s\":\"看看中文歌词\",\"t\":1}")]
        public void InjectCsrf_输出始终是可解析的JSON(string input)
        {
            var result = InvokeInjectCsrf(input, "tok");
            using var doc = JsonDocument.Parse(result);
            Assert.Equal("tok", doc.RootElement.GetProperty("csrf_token").GetString());
        }

        // ---------- InjectHeader 字符串手术（私有静态，反射直击） ----------

        [Theory]
        [InlineData("{}", "{\"os\":\"android\"}", "{\"header\":{\"os\":\"android\"}}")]
        [InlineData("{\"id\":123}", "{\"os\":\"android\"}", "{\"id\":123,\"header\":{\"os\":\"android\"}}")]
        public void InjectHeader_输出与手推一致(string input, string header, string expected)
        {
            Assert.Equal(expected, InvokeInjectHeader(input, header));
        }

        [Fact]
        public void InjectHeader_输出始终是可解析的JSON()
        {
            var result = InvokeInjectHeader("{\"id\":123}", BuildFakeHeader());
            using var doc = JsonDocument.Parse(result);
            Assert.Equal("android", doc.RootElement.GetProperty("header").GetProperty("os").GetString());
        }

        // ---------- EAPI header 构造（内部成员直击，无网络） ----------

        [Fact]
        public void BuildEapiHeaderFields_缺省设备字段取pc默认_并注入账号cookie()
        {
            using var http = new NeteaseHttp("MUSIC_U=U_123; channel=y; __csrf=abc");

            var fields = http.BuildEapiHeaderFields();
            var cookie = NeteaseHttp.CreateHeaderCookie(fields);

            // pc 设备默认：无对应 cookie 时 os/appver/osver/channel 取 osMap['pc']
            Assert.Equal("pc", Field(fields, "os"));
            Assert.Equal("3.1.17.204416", Field(fields, "appver"));
            Assert.Equal("Microsoft-Windows-10-Professional-build-19045-64bit", Field(fields, "osver"));
            Assert.Equal("y", Field(fields, "channel"));               // cookie 覆盖默认 netease
            Assert.Equal("140", Field(fields, "versioncode"));
            Assert.Equal("", Field(fields, "mobilename"));
            Assert.Equal("1920x1080", Field(fields, "resolution"));
            Assert.Equal("abc", Field(fields, "__csrf"));
            Assert.Equal("U_123", Field(fields, "MUSIC_U"));

            // deviceId 无 cookie → null（JS undefined）：不进 JSON body，但按 JS 语义进 Cookie 头为字面 undefined
            Assert.Null(Field(fields, "deviceId"));
            Assert.Contains("deviceId=undefined", cookie);
            Assert.DoesNotContain("MUSIC_A", cookie, StringComparison.Ordinal);

            Assert.Matches(@"^\d{13}_\d{4}$", Field(fields, "requestId"));
            Assert.Matches(@"^\d{10}$", Field(fields, "buildver"));
        }

        [Fact]
        public void HeaderToJson_丢弃null字段_保留空串字段()
        {
            var fields = new[]
            {
                new KeyValuePair<string, string?>("os", "pc"),
                new KeyValuePair<string, string?>("deviceId", null),
                new KeyValuePair<string, string?>("__csrf", ""),
            };

            var json = NeteaseHttp.HeaderToJson(fields);
            using var doc = JsonDocument.Parse(json);

            Assert.Equal("pc", doc.RootElement.GetProperty("os").GetString());
            Assert.Equal("", doc.RootElement.GetProperty("__csrf").GetString());
            Assert.False(doc.RootElement.TryGetProperty("deviceId", out _),
                "null（JS undefined）字段不应出现在加密 body 的 header 中");
        }

        [Fact]
        public void CreateHeaderCookie_按header字段_encodeURIComponent_分号连接()
        {
            var fields = new[]
            {
                new KeyValuePair<string, string?>("os", "pc"),
                new KeyValuePair<string, string?>("osver", "Microsoft-Windows-10-Professional-build-19045-64bit"),
                new KeyValuePair<string, string?>("deviceId", null),
                new KeyValuePair<string, string?>("mobilename", ""),
                new KeyValuePair<string, string?>("__csrf", "abc"),
                new KeyValuePair<string, string?>("requestId", "1700000000000_0123"),
            };

            Assert.Equal(
                "os=pc; osver=Microsoft-Windows-10-Professional-build-19045-64bit; deviceId=undefined; " +
                "mobilename=; __csrf=abc; requestId=1700000000000_0123",
                NeteaseHttp.CreateHeaderCookie(fields));
        }

        [Fact]
        public void GenerateRequestId_形态为毫秒时间戳_下划线_四位随机()
        {
            var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

            Assert.Matches(@"^1767225600000_\d{4}$", NeteaseHttp.GenerateRequestId(now));
        }

        [Theory]
        [InlineData("SM-A(1)", "SM-A(1)")]            // JS encodeURIComponent 不转义括号/叹号/星号等
        [InlineData("!*'()", "!*'()")]
        [InlineData("a b", "a%20b")]
        [InlineData("a+b&c=d", "a%2Bb%26c%3Dd")]
        [InlineData("中文", "%E4%B8%AD%E6%96%87")]
        [InlineData("~-._", "~-._")]
        public void EncodeUriComponent_与JS语义一致(string input, string expected)
        {
            Assert.Equal(expected, NeteaseHttp.EncodeUriComponent(input));
        }

        private static string? Field(IEnumerable<KeyValuePair<string, string?>> fields, string key)
        {
            foreach (var kv in fields)
                if (kv.Key == key) return kv.Value;
            return null;
        }

        private static string BuildFakeHeader() => "{\"os\":\"android\",\"appver\":\"8.10.90\"}";

        private static string InvokeInjectCsrf(string json, string csrf) =>
            InvokeStatic("InjectCsrf", json, csrf);

        private static string InvokeInjectHeader(string json, string header) =>
            InvokeStatic("InjectHeader", json, header);

        private static string InvokeStatic(string name, params object[] args)
        {
            var m = typeof(NeteaseHttp).GetMethod(name,
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(m);
            return (string)m.Invoke(null, args)!;
        }
    }
}