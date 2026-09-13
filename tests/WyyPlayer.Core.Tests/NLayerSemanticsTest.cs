using System;
using NLayer;
using Xunit;
using Xunit.Abstractions;

namespace WyyPlayer.Core.Tests
{
    public class NLayerSemanticsTest
    {
        private readonly ITestOutputHelper _out;
        public NLayerSemanticsTest(ITestOutputHelper o) { _out = o; }

        [Fact]
        public void ReadSamplesInt16_返回值单位_是字节还是采样()
        {
            using var f = new MpegFile("fixtures/tone-2s-44100-stereo.mp3");
            // 注意：用 Math.Round 而非直接截断。Duration 是 double，真实总字节数
            // (帧对齐的精确整数) 落在 359423.9999… 边界上时直接截断会差 1 字节。
            long expectedBytes = (long)Math.Round(f.Duration.TotalSeconds * f.SampleRate * f.Channels * 2);
            long expectedSamples = expectedBytes / 2;

            var buf = new byte[1 << 20];   // 足够容纳整个文件
            int r = f.ReadSamplesInt16(buf, 0, buf.Length);

            _out.WriteLine($"SampleRate={f.SampleRate} Channels={f.Channels} Duration={f.Duration}");
            _out.WriteLine($"expectedBytes={expectedBytes} expectedSamples={expectedSamples} 返回值={r}");

            bool isBytes = r == expectedBytes;
            bool isSamples = r == expectedSamples;

            // 两者只可能有一个成立（字节数恒为采样数的 2 倍）
            Assert.True(isBytes || isSamples,
                $"返回值 {r} 既不等于期望字节数 {expectedBytes} 也不等于期望采样数 {expectedSamples}");
            Assert.False(isBytes && isSamples, "两个单位不可能同时成立");

            if (isBytes)
                _out.WriteLine("结论：返回值单位 = 字节。后续实现按 chunk.Count = r 处理。");
            else
                _out.WriteLine("结论：返回值单位 = 采样数(16bit)。后续实现按 chunk.Count = r * 2 处理。");
        }
    }
}