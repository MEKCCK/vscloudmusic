using WyyPlayer.Api;

namespace WyyPlayer.Core.Playback
{
    public enum PlaybackStatus
    {
        Idle,
        Preparing,
        Playing,
        Paused,
        Stopped,
        Failed,
    }

    /// <summary>
    /// 播放状态机。纯逻辑、无线程、无 IO —— 因此可以脱离游戏单测。
    ///
    /// 存在意义：既有的模组是「循环播放单个文件」结构，没有停止/切歌入口，
    /// 且 _tornDown 一旦置位永不复位（一首歌失败后再也无法播下一首）。
    /// 这里把「什么时候允许开始一首新歌」显式化，避免并发持有两个 AL 源
    /// （全游戏只有 1 个立体声源，并发持有会饿死游戏自身音频）。
    ///
    /// 线程契约：本类【不是线程安全的】。它只允许在游戏主线程上调用 ——
    /// tick 回调与 GUI 回调都在主线程运行，模组侧不得在其他线程触碰此实例。
    /// </summary>
    public sealed class PlaybackStateMachine
    {
        public PlaybackStatus Status { get; private set; } = PlaybackStatus.Idle;
        public SongKey? CurrentSong { get; private set; }

        /// <summary>最近一次失败的原因（供 UI 展示）；成功开始新歌后清空。不含任何凭据信息。</summary>
        public string? FailureReason { get; private set; }

        /// <summary>
        /// 是否正占着唯一的立体声源：准备中（下载/解码进行时）、播放中、暂停中
        /// （暂停只是冻结播放位置，AL 源仍被持有）。
        /// </summary>
        public bool IsBusy => Status == PlaybackStatus.Preparing
            || Status == PlaybackStatus.Playing
            || Status == PlaybackStatus.Paused;

        /// <summary>
        /// 只有 空闲/已停止/已失败 三种状态才允许开始新歌。
        /// 暂停（Paused）仍持有 AL 源，必须先显式 MarkStopped() 释放，不能直接切歌。
        /// </summary>
        public bool TryBegin(SongKey song)
        {
            if (IsBusy) return false;
            CurrentSong = song;
            Status = PlaybackStatus.Preparing;
            FailureReason = null;
            return true;
        }

        public void MarkPlaying()
        {
            if (Status == PlaybackStatus.Preparing || Status == PlaybackStatus.Paused)
                Status = PlaybackStatus.Playing;
        }

        public void MarkPaused()
        {
            if (Status == PlaybackStatus.Playing) Status = PlaybackStatus.Paused;
        }

        public void MarkStopped()
        {
            Status = PlaybackStatus.Stopped;
        }

        public void MarkFailed(string reason)
        {
            // 失败必须回到「可以再试」的状态，而不是永久卡死。
            Status = PlaybackStatus.Failed;
            FailureReason = reason;
        }

        /// <summary>
        /// 流自然结束。只有 Playing 才算「自然结束」：暂停时 AL 队列排空在视觉上
        /// 与流末尾无法区分，若此时放行 MarkEnded 就会把暂停误判为结束（丢歌）。
        /// 因此从 Paused 等其余状态调用是 no-op。
        /// </summary>
        public void MarkEnded()
        {
            if (Status == PlaybackStatus.Playing) Status = PlaybackStatus.Stopped;
        }

        /// <summary>
        /// 是否应当自动重播。
        /// 只有「正在播放」才算 —— 暂停时 AL 队列会排空，看起来与流末尾一模一样，
        /// 若据此重播就会出现「暂停后从头开始」的怪异行为。
        /// </summary>
        public bool WantsLoop => Status == PlaybackStatus.Playing;
    }
}