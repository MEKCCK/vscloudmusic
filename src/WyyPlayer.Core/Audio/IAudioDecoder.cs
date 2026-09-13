using System;

namespace WyyPlayer.Core.Audio
{
    /// <summary>
    /// 音频解码器契约。宿主只依赖这个接口，具体用哪一个解码后端由工厂决定。
    ///
    /// 之所以抽象出来：解码器是可替换的（内置的纯 C# NLayer，或外部的 ffmpeg），
    /// 而且实测两者输出的 PCM 确实不完全一致 —— 内置解码器在一份 250 秒的样本里
    /// 于 136.17 秒处出现了一整帧（1152 帧，约 26ms）的错位，外部解码器则没有。
    /// 有了这层抽象，切换后端不必改动播放链路。
    ///
    /// 线程约定：解码在后台线程进行；实现不得触碰任何游戏 API。
    /// </summary>
    public interface IAudioDecoder : IDisposable
    {
        int SampleRate { get; }
        int Channels { get; }

        /// <summary>总时长。未知时返回 <see cref="TimeSpan.Zero"/>。</summary>
        TimeSpan Duration { get; }

        /// <summary>
        /// 读取下一块 PCM 到 <paramref name="buffer"/>。
        /// 返回 false 表示流已结束（不是错误）。块大小由实现决定，但必须是完整音频帧。
        /// </summary>
        bool TryReadChunk(byte[] buffer, out PcmChunk chunk);
    }
}
