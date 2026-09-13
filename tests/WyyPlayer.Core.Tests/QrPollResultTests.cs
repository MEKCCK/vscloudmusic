using WyyPlayer.Core.Net;
using WyyPlayer.Core.Net.Models;
using Xunit;

namespace WyyPlayer.Core.Tests
{
    /// <summary>
    /// 验证 PollQrAsync 的响应解析/映射契约：
    /// 解析失败或空 body → Unknown + RawCode=0；已建模码 → 对应 QrState + RawCode=code；
    /// 未建模码（如实测的 8821）→ Unknown + RawCode=code（非零），Message 保留服务端原话。
    /// </summary>
    public class QrPollResultTests
    {
        [Theory]
        [InlineData(800, QrState.Expired)]
        [InlineData(801, QrState.Waiting)]
        [InlineData(802, QrState.Scanned)]
        [InlineData(803, QrState.Confirmed)]
        public void 已建模码_映射到对应状态并保留原始码(int code, QrState expected)
        {
            var r = NeteaseClient.ParseQrPoll(
                $"{{\"code\":{code},\"message\":\"m{code}\",\"nickname\":\"nick\"}}");

            Assert.Equal(expected, r.State);
            Assert.Equal(code, r.RawCode);
            Assert.Equal($"m{code}", r.Message);
            Assert.Equal("nick", r.Nickname);
        }

        [Fact]
        public void 未建模码_归为Unknown但保留原始码与消息()
        {
            var r = NeteaseClient.ParseQrPoll(
                "{\"code\":8821,\"message\":\"该二维码已被验证\",\"nickname\":\"\"}");

            Assert.Equal(QrState.Unknown, r.State);
            Assert.Equal(8821, r.RawCode);
            Assert.Equal("该二维码已被验证", r.Message);
        }

        [Theory]
        [InlineData("")]
        [InlineData("not json")]
        public void 解析失败_归为Unknown且原始码为零(string raw)
        {
            var r = NeteaseClient.ParseQrPoll(raw);

            Assert.Equal(QrState.Unknown, r.State);
            Assert.Equal(0, r.RawCode);
            Assert.Equal("", r.Message);
        }

        [Fact]
        public void 零码响应_归为Unknown且原始码为零()
        {
            // 契约：Unknown && RawCode==0 视为「无法解析/空 body」；
            // 服务端伪造的 code=0 或 {} 也按此处理，与真实未建模码（非零）区分。
            var r = NeteaseClient.ParseQrPoll("{\"code\":0,\"nickname\":\"\"}");

            Assert.Equal(QrState.Unknown, r.State);
            Assert.Equal(0, r.RawCode);
        }
    }
}