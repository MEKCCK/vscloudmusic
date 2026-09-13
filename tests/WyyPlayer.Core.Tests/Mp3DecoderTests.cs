using System;
using System.IO;
using WyyPlayer.Core.Audio;
using Xunit;

namespace WyyPlayer.Core.Tests
{
    public class Mp3DecoderTests
    {
        private const string Fixture = "fixtures/tone-2s-44100-stereo.mp3";

        [Fact]
        public void 读取格式信息正确()
        {
            using var stream = File.OpenRead(Fixture);
            using var decoder = new Mp3Decoder(stream);

            Assert.Equal(44100, decoder.SampleRate);
            Assert.Equal(2, decoder.Channels);
            Assert.InRange(decoder.Duration.TotalSeconds, 1.9, 2.2);
        }

        [Fact]
        public void 累计解码字节数与时长一致()
        {
            using var stream = File.OpenRead(Fixture);
            using var decoder = new Mp3Decoder(stream);

            var buffer = new byte[8192];
            long totalBytes = 0;
            while (decoder.TryReadChunk(buffer, out var chunk))
            {
                Assert.True(chunk.Count > 0);
                Assert.Equal(44100, chunk.SampleRate);
                Assert.Equal(2, chunk.Channels);
                totalBytes += chunk.Count;
            }

            long expected = (long)(decoder.Duration.TotalSeconds * 44100 * 2 * 2);
            // MP3 帧按 1152 采样对齐，允许 1% 误差
            Assert.InRange(totalBytes, (long)(expected * 0.99), (long)(expected * 1.01));
        }

        [Fact]
        public void 空输入会抛异常而非默默返回空数据()
        {
            using var stream = new MemoryStream(Array.Empty<byte>());
            var buffer = new byte[1024];

            // 空流没有合法 MP3 头，NLayer 在构造或首次读取时必然报错。
            // 这个测试钉死的是「不得静默地返回」—— 出错比静默给错数据安全。
            Assert.ThrowsAny<Exception>(() =>
            {
                using var decoder = new Mp3Decoder(stream);
                decoder.TryReadChunk(buffer, out _);
            });
        }
    }
}