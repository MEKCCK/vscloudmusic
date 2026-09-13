namespace WyyPlayer.Core.Audio
{
    /// <summary>一块 16-bit 有符号小端 PCM 数据及其格式。</summary>
    public sealed class PcmChunk
    {
        /// <summary>底层缓冲。有效数据为 [0, Count)。</summary>
        public byte[] Data { get; }

        /// <summary>有效字节数。</summary>
        public int Count { get; }

        public int SampleRate { get; }
        public int Channels { get; }

        public PcmChunk(byte[] data, int count, int sampleRate, int channels)
        {
            Data = data;
            Count = count;
            SampleRate = sampleRate;
            Channels = channels;
        }

        /// <summary>音频帧数（每帧含 Channels 个采样）。</summary>
        public int FrameCount => Count / (2 * Channels);
    }
}