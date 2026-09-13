using System;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using WyyPlayer.Core.Net;
using WyyPlayer.Core.Net.Models;
using Xunit;
using Xunit.Abstractions;

namespace WyyPlayer.Core.Tests
{
    public class NeteaseClientTests
    {
        private readonly ITestOutputHelper _out;
        public NeteaseClientTests(ITestOutputHelper o) { _out = o; }

        [Fact]
        public async Task 匿名搜索_能返回结果()
        {
            using var http = new NeteaseHttp();
            var client = new NeteaseClient(http);

            // 实测服务端对匿名请求偶发风控：返回空 body（导致 Deserialize 抛异常）或
            // 无 result 的 JSON（total=0）。两者都是瞬时环境抖动，不是本客户端缺陷。
            // 有界重试仅容忍抖动，断言强度不变 —— 若加密/HTTP 层有真实缺陷，重试会全部失败。
            SearchResult? result = null;
            Exception? lastError = null;
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                if (attempt > 1) await Task.Delay(attempt * 1000);
                try
                {
                    result = await client.SearchAsync("周杰伦", 5);
                    _out.WriteLine($"attempt={attempt} total={result.TotalCount} 返回={result.Songs.Count}");
                    if (result.TotalCount > 0) break;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    _out.WriteLine($"attempt={attempt} 异常: {ex.GetType().Name}: {ex.Message}");
                }
            }

            Assert.NotNull(result);
            Assert.True(result!.TotalCount > 0,
                lastError == null ? "搜索应有命中（服务端风控空响应）"
                    : $"搜索应有命中（末次尝试异常 {lastError.GetType().Name}: {lastError.Message}）");
            Assert.NotEmpty(result.Songs);
            Assert.All(result.Songs, s => Assert.True(s.Id > 0, $"歌曲 Id 应 > 0，实际 {s.Id}"));
        }

        [Fact]
        public async Task 二维码key_能申请到()
        {
            using var http = new NeteaseHttp();
            var client = new NeteaseClient(http);

            // 二维码 key 与轮询走 EAPI（参考实现 login_qr_key/login_qr_check 均为 eapi）。
            // 服务端对匿名请求偶发风控（HTTP 200 空 body），与 search 测试同症状。
            // 实测同一字节级请求几秒内交替成功/空体 —— 故障是时间性的（网关限流），非实现缺陷。
            // 有界重试仅容忍环境抖动，断言强度不变：若加密/HTTP 层有真实缺陷，每次重试都失败，测试照常红。
            string? unikey = null;
            Exception? lastError = null;
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                if (attempt > 1) await Task.Delay(attempt * 1000);
                try
                {
                    unikey = await client.CreateQrKeyAsync();
                    _out.WriteLine($"attempt={attempt} unikey={unikey}");
                    if (!string.IsNullOrWhiteSpace(unikey)) break;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    _out.WriteLine($"attempt={attempt} 异常: {ex.GetType().Name}: {ex.Message}");
                }
            }

            Assert.False(string.IsNullOrWhiteSpace(unikey),
                lastError == null ? "应能申请到二维码 key（服务端风控空响应）"
                    : $"应能申请到二维码 key（末次尝试异常 {lastError.GetType().Name}: {lastError.Message}）");
            Assert.Contains("codekey=", NeteaseClient.BuildQrContent(unikey!));
            _out.WriteLine("unikey=" + unikey);
        }

        [Fact]
        public void ToBitrate_level映射()
        {
            Assert.Equal(128000, NeteaseClient.ToBitrate("standard"));
            Assert.Equal(192000, NeteaseClient.ToBitrate("higher"));
            Assert.Equal(320000, NeteaseClient.ToBitrate("exhigh"));
            Assert.Equal(999000, NeteaseClient.ToBitrate("lossless"));
            // 未知 level 兜底 exhigh 码率
            Assert.Equal(320000, NeteaseClient.ToBitrate(""));
            Assert.Equal(320000, NeteaseClient.ToBitrate("hires"));
        }

        // ---- 兜底端点响应解析（ParseFallbackUrl，纯函数，离线）----

        [Fact]
        public void 兜底解析_数组形态无地址_返回null()
        {
            // 数据形态数组 + url:null —— VIP/下架/免费号无权限的合法结果。
            // 修复前：数组形态解析成功、首元素 url 为空 → 落到单对象解析，
            // Json.NET 对「JSON 数组 → RawSongUrlItem」抛 JsonSerializationException，方法异常而非 null。
            Assert.Null(NeteaseClient.ParseFallbackUrl(
                """{"data":[{"id":5257138,"url":null,"br":0}],"code":200}"""));
        }

        [Fact]
        public void 兜底解析_空数组_返回null()
        {
            // 另一个数组形态“无地址”结果；修复前同样在单对象解析处抛异常。
            Assert.Null(NeteaseClient.ParseFallbackUrl("""{"data":[],"code":200}"""));
        }

        [Fact]
        public void 兜底解析_对象形态无地址_返回null()
        {
            // 公式实测形态：download/url/v1 匿名返回 data 为对象且 url:null（内层 code:-105 需 VIP）。
            Assert.Null(NeteaseClient.ParseFallbackUrl(
                """{"data":{"id":5257138,"url":null,"br":0,"code":-105},"code":200}"""));
        }

        [Fact]
        public void 兜底解析_无data键_返回null()
        {
            // 纯码响应（如 code:-105）无 data 键 —— 视为取不到地址。
            Assert.Null(NeteaseClient.ParseFallbackUrl("""{"code":-105}"""));
        }

        [Fact]
        public void 兜底解析_数组形态有地址_返回URL()
        {
            Assert.Equal("http://m.example.com/a.mp3", NeteaseClient.ParseFallbackUrl(
                """{"data":[{"id":1,"url":"http://m.example.com/a.mp3"}],"code":200}"""));
        }

        [Fact]
        public void 兜底解析_对象形态有地址_返回URL()
        {
            Assert.Equal("http://m.example.com/b.mp3", NeteaseClient.ParseFallbackUrl(
                """{"data":{"id":1,"url":"http://m.example.com/b.mp3"},"code":200}"""));
        }

        [Fact]
        public void 兜底解析_畸形body_抛JsonException而非返回null()
        {
            // 畸形 body 必须仍以异常暴露，不能被吞成 null —— 那是诊断盲区。
            // 抛出的具体类型可能是 JsonException 的派生类（JsonReaderException/JsonSerializationException），
            // 语义断言是「以 JsonException 家族异常暴露」而非「返回 null」。
            Assert.ThrowsAny<JsonException>(() => NeteaseClient.ParseFallbackUrl("<html>gateway error</html>"));
            Assert.ThrowsAny<JsonException>(() => NeteaseClient.ParseFallbackUrl(""));
            // data 既非对象也非数组（字符串 / 数组元素非对象）同样属畸形。
            Assert.ThrowsAny<JsonException>(() => NeteaseClient.ParseFallbackUrl(
                """{"data":"oops","code":200}"""));
            Assert.ThrowsAny<JsonException>(() => NeteaseClient.ParseFallbackUrl(
                """{"data":[123],"code":200}"""));
        }

        // ---- 登录状态响应解析（ParseAccountStatus，纯函数，离线）----

        [Fact]
        public void 登录状态_完整会话_映射账号信息()
        {
            // profile 与 account 齐全的典型已登录响应；UserId 取 account.id，昵称与 VIP 取各自字段
            // （这些字段在响应顶层，不在 data 包装内）。
            var info = NeteaseClient.ParseAccountStatus(
                """{"code":200,"account":{"id":1001,"vipType":11},"profile":{"userId":1001,"nickname":"测试号","vipType":11}}""");

            Assert.NotNull(info);
            Assert.Equal(1001, info!.UserId);
            Assert.Equal("测试号", info.Nickname);
            Assert.True(info.VipType);
        }

        [Fact]
        public void 登录状态_仅profile_取profile字段且VIP按vipType判定()
        {
            // 服务端偶发缺 account（字段缺失，不是 null）；UserId 回退 profile.userId，
            // vipType 为 0 → 非 VIP。
            var info = NeteaseClient.ParseAccountStatus(
                """{"code":200,"profile":{"userId":2002,"nickname":"仅资料号","vipType":0}}""");

            Assert.NotNull(info);
            Assert.Equal(2002, info!.UserId);
            Assert.Equal("仅资料号", info.Nickname);
            Assert.False(info.VipType);
        }

        [Fact]
        public void 登录状态_仅account_昵称为空且VIP取account()
        {
            // 同理只有 account 时：昵称无来源留空，VIP 取 account.vipType。
            var info = NeteaseClient.ParseAccountStatus(
                """{"code":200,"account":{"id":3003,"vipType":11}}""");

            Assert.NotNull(info);
            Assert.Equal(3003, info!.UserId);
            Assert.Equal("", info.Nickname);
            Assert.True(info.VipType);
        }

        [Fact]
        public void 登录状态_code200但双null_返回null()
        {
            // 未登录的常见形态之一：code 200 但 account/profile 均为 null。
            Assert.Null(NeteaseClient.ParseAccountStatus(
                """{"code":200,"account":null,"profile":null}"""));
        }

        [Fact]
        public void 登录状态_code301_返回null()
        {
            // 未登录的常见形态之二：纯码 301（重定向），无任何字段。
            Assert.Null(NeteaseClient.ParseAccountStatus("""{"code":301}"""));
        }

        [Fact]
        public void 登录状态_非200码但带字段_返回null()
        {
            // 防御性契约：非 200 code 即使 profile/account 字段齐全，也按未登录处理 ——
            // 无效会话不得被报告为已登录；宁可在灰色地带误报未登录，不可误报已登录。
            Assert.Null(NeteaseClient.ParseAccountStatus(
                """{"code":301,"account":{"id":1,"vipType":0},"profile":{"userId":1,"nickname":"x","vipType":0}}"""));
        }

        [Theory]
        [InlineData("")]
        [InlineData("not json")]
        public void 登录状态_不可解析body_返回null而非抛异常(string raw)
        {
            // 网关限流空响应 / 错误页：按「未登录」处理，不抛异常（与 ParseQrPoll 对空体的契约一致）
            // —— 探针 status 命令需要干净的 null 结果而非崩溃。
            Assert.Null(NeteaseClient.ParseAccountStatus(raw));
        }

        [Fact]
        public async Task 取歌词_能返回LRC()
        {
            using var http = new NeteaseHttp();
            var client = new NeteaseClient(http);

            // 服务端对匿名请求偶发风控（HTTP 200 空 body / 空结果），与 search/QR 测试同症状；
            // 有界重试仅容忍环境抖动，断言强度不变 —— 请求字节级一致，真实缺陷让每次重试都失败。
            // 实测 /api/song/lyric/v1 的 lrc.lyric 以若干 JSON 词曲信息行开头（{"t":0,"c":[...]}），
            // 随后才是真正的 LRC 行（[mm:ss.xx]歌词）—— 断言必须落在真实时间标签上，不能只查 "["。
            long songId = 0;
            string? lrc = null;
            Exception? lastError = null;
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                if (attempt > 1) await Task.Delay(attempt * 1000);
                try
                {
                    var search = await client.SearchAsync("周杰伦", 5);
                    Assert.NotEmpty(search.Songs);
                    foreach (var s in search.Songs.Take(3))
                    {
                        songId = s.Id;
                        lrc = await client.GetLyricAsync(songId);
                        _out.WriteLine($"attempt={attempt} songId={songId} 歌词前80字: " +
                            (lrc is null ? "(null)" : lrc.Substring(0, Math.Min(80, lrc.Length))));
                        if (LrcHasTimeTags(lrc)) break;
                    }
                    if (LrcHasTimeTags(lrc)) break;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    _out.WriteLine($"attempt={attempt} 异常: {ex.GetType().Name}: {ex.Message}");
                }
            }

            Assert.NotNull(lrc);
            Assert.Matches(@"\[\d{1,2}:\d{2}[\.:]\d{1,3}\]", lrc!);   // LRC 时间标签
            _out.WriteLine($"歌词长度: {lrc!.Length}");
        }

        /// <summary>LRC 时间标签行形如 [mm:ss.xx] 歌词（区别于词曲信息 JSON 行）。</summary>
        private static bool LrcHasTimeTags(string? lrc)
        {
            if (string.IsNullOrWhiteSpace(lrc)) return false;
            return System.Text.RegularExpressions.Regex.IsMatch(lrc, @"\[\d{1,2}:\d{2}[\.:]\d{1,3}\]");
        }
    }
}