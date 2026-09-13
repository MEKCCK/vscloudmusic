using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using WyyPlayer.Core.Net;
using Xunit;

namespace WyyPlayer.Core.Tests
{
    /// <summary>
    /// Fix Round 3：QR 轮询的 Set-Cookie 会跨 path/domain 变体累积同名条目，
    /// CookieContainer（按 domain+path+name 索引）会把它们全部吐给 GetAllCookies，
    /// CookieHeader 因此导出重复的 MUSIC_A_T/MUSIC_R_T。本测试锁定三件事：
    /// 同名仅导出一条（后写 / 最具体 path 胜出）、GetCookie 与之决定性一致、
    /// 导出⟶导入⟶导出逐字节幂等（被违反的属性）。
    /// </summary>
    public class NeteaseHttpCookieTests
    {
        private static readonly Uri Music163 = new("https://music.163.com");

        [Fact]
        public void CookieHeader_同名多路径条目_每名恰好一条_取后写值()
        {
            using var http = new NeteaseHttp();
            http.SetCookie("__csrf", "cs1");
            http.SetCookie("MUSIC_U", "U_1");
            http.SetCookie("MUSIC_A_T", "a_old");                              // path /
            AddWithPath(http, "MUSIC_A_T", "a_new", "/api/login/qrcode");      // 轮换路径
            http.SetCookie("MUSIC_R_T", "r_old");
            AddWithPath(http, "MUSIC_R_T", "r_new", "/api/login/qrcode");

            var byName = ParseHeader(http.CookieHeader);

            Assert.Equal(4, byName.Count);                       // __csrf/MUSIC_U/MUSIC_A_T/MUSIC_R_T
            foreach (var pair in byName)
                Assert.Single(pair.Value);                     // 同名只导出一次
            Assert.Equal("a_new", Assert.Single(byName["MUSIC_A_T"]));  // 后写（且更具体）胜出
            Assert.Equal("r_new", Assert.Single(byName["MUSIC_R_T"]));
        }

        [Fact]
        public void GetCookie_同名多条目_返回与CookieHeader一致的后写值()
        {
            using var http = new NeteaseHttp();
            // 先写下的长 path 条目（旧但更具体）；再写短 path 条目（新）。
            // 当前实现按 GetAllCookies 顺序首匹配 → 会错取旧值，必须按后写规则取新值。
            AddWithPath(http, "__csrf", "cs_old", "/api/login");
            http.SetCookie("__csrf", "cs_new");

            Assert.Equal("cs_new", http.GetCookie("__csrf"));
            // 与导出串同源（WEAPI 注入 csrf、EAPI header 的 __csrf 都走 GetCookie）
            Assert.Equal(Assert.Single(ParseHeader(http.CookieHeader)["__csrf"]),
                http.GetCookie("__csrf"));
        }

        [Fact]
        public void 导出_回灌_再导出_逐字节幂等()
        {
            using var http = new NeteaseHttp();
            http.SetCookie("__csrf", "cs1");
            http.SetCookie("MUSIC_U", "U_1");
            http.SetCookie("MUSIC_A_T", "a_old");
            AddWithPath(http, "MUSIC_A_T", "a_new", "/api/login/qrcode");
            http.SetCookie("MUSIC_R_T", "r_old");
            AddWithPath(http, "MUSIC_R_T", "r_new", "/api/login/qrcode");

            var first = http.CookieHeader;                    // 修复前：同名条目重复出现
            using var http2 = new NeteaseHttp(first);         // 模拟持久化后的回灌
            var second = http2.CookieHeader;

            Assert.Equal(first, second);                      // 修复前此断言必然失败
            var byName = ParseHeader(second);
            Assert.Equal(4, byName.Count);
            Assert.Equal("a_new", Assert.Single(byName["MUSIC_A_T"]));
        }

        [Fact]
        public void 偏好评判_后写优先于路径更具体()
        {
            var earlierMoreSpecific = WithTimeStamp(new Cookie("K", "earlier") { Path = "/api/login" }, T(1));
            var laterGeneric = WithTimeStamp(new Cookie("K", "later") { Path = "/" }, T(2));

            Assert.True(NeteaseHttp.IsPreferred(laterGeneric, earlierMoreSpecific),
                "后写（更新 TimeStamp）应优先于更具体 path 的旧条目");
            Assert.False(NeteaseHttp.IsPreferred(earlierMoreSpecific, laterGeneric));
        }

        [Fact]
        public void 偏好评判_时间戳并列_更具体path胜出()
        {
            var generic = WithTimeStamp(new Cookie("K", "generic") { Path = "/" }, T(5));
            var specific = WithTimeStamp(new Cookie("K", "specific") { Path = "/api/login/qrcode" }, T(5));

            Assert.True(NeteaseHttp.IsPreferred(specific, generic));
            Assert.False(NeteaseHttp.IsPreferred(generic, specific));
        }

        [Fact]
        public void 偏好评判_完全并列_不更换_保持确定性()
        {
            var a = WithTimeStamp(new Cookie("K", "same") { Path = "/" }, T(7));
            var b = WithTimeStamp(new Cookie("K", "same") { Path = "/" }, T(7));

            Assert.False(NeteaseHttp.IsPreferred(b, a));   // 完全并列：保留先到者，结果与枚举顺序解耦
        }

        private static DateTime T(int day) => new(2026, 1, day, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>Cookie.TimeStamp 在 net10.0 为只读，经私有字段 m_timeStamp 播种。</summary>
        private static Cookie WithTimeStamp(Cookie c, DateTime ts)
        {
            typeof(Cookie).GetField("m_timeStamp", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(c, ts);
            return c;
        }

        /// <summary>直接向容器写入不同 path 的同名条目（模拟 Set-Cookie 的路径轮换）。</summary>
        private static void AddWithPath(NeteaseHttp http, string name, string value, string path)
        {
            var container = (CookieContainer)typeof(NeteaseHttp)
                .GetField("_cookies", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(http)!;
            container.Add(Music163, new Cookie(name, value) { Domain = ".music.163.com", Path = path });
        }

        private static Dictionary<string, List<string>> ParseHeader(string header)
        {
            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var seg in header.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = seg.Split('=', 2);
                if (kv.Length != 2) continue;
                var name = kv[0].Trim();
                if (!map.TryGetValue(name, out var list)) map[name] = list = new List<string>();
                list.Add(kv[1].Trim());
            }
            return map;
        }
    }
}