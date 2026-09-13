using System;
using System.IO;
using NLayer;

namespace WyyPlayer.Core.Audio
{
    /// <summary>
    /// 把 MP3 流解码为 16-bit PCM 块。不依赖任何游戏程序集，可脱离游戏测试。
    /// 注意：非线程安全。
    /// </summary>
    public sealed class Mp3Decoder : IDisposable
    {
        private readonly MpegFile _file;
        private bool _disposed;

        public Mp3Decoder(Stream stream)
        {
            _file = new MpegFile(stream);
        }

        public int SampleRate => _file.SampleRate;
        public int Channels => _file.Channels;
        public TimeSpan Duration => _file.Duration;

        /// <summary>
        /// 读取下一块 PCM。返回 false 表示流已结束。
        /// 返回的 PcmChunk.Data 是内部缓冲的复用，调用方须在下一次调用前消费完毕。
        /// </summary>
        public bool TryReadChunk(byte[] buffer, out PcmChunk chunk)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Mp3Decoder));

            // count 必须不超过 buffer.Length，否则 NLayer 抛 ArgumentOutOfRangeException
            int read = _file.ReadSamplesInt16(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                chunk = null!;
                return false;
            }

            // read 的单位由 NLayerSemanticsTest 钉死。
            // 若该测试结论为「字节」→ 直接用；若为「采样数」→ 改为 read * 2
            int byteCount = read;
            if (byteCount > buffer.Length) byteCount = buffer.Length;

            chunk = new PcmChunk(buffer, byteCount, SampleRate, Channels);
            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _file.Dispose();
        }
    }
}