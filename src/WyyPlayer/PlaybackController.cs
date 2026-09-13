using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using WyyPlayer.Api;
using WyyPlayer.Core.Playback;

namespace WyyPlayer
{
    /// <summary>
    /// 播放控制的薄适配层：把 UI 的「请求」翻译成状态迁移，
    /// 实际的 AL/文件动作仍由 WyyPlayerMod 的 tick 循环执行（保证主线程）。
    /// UI 只跟本类打交道，不触碰 OpenAlStreamer。
    /// </summary>
    public sealed class PlaybackController
    {
        private readonly PlaybackStateMachine _sm = new();
        private readonly ILogger _log;

        public PlaybackController(ILogger log) { _log = log; }

        public PlaybackStatus Status => _sm.Status;
        public SongKey? CurrentSong  => _sm.CurrentSong;
        /// <summary>当前曲目的字符串键（UI/日志用）。</summary>
        public string? CurrentSongId => _sm.CurrentSong?.ToString();
        public string? CurrentTitle { get; private set; }

        /// <summary>播放队列与模式（单曲循环/顺序/随机）。由 UI 填充、由模组在流末尾取下一首。</summary>
        public PlayQueue Queue { get; } = new();

        private readonly Dictionary<SongKey, string> _titles = new();

        /// <summary>把搜索结果登记为「id → 显示名」，供队列推进时显示歌名（否则只能显示 id）。</summary>
        public void RememberTitles(IEnumerable<(SongKey Key, string Title)> items)
        {
            foreach (var (key, title) in items)
                if (!string.IsNullOrEmpty(title)) _titles[key] = title;
        }

        /// <summary>取显示名；没有登记过则回退为 id 形式。</summary>
        public string TitleFor(SongKey song)
            => _titles.TryGetValue(song, out var t) ? t : song.NativeId;

        /// <summary>由 UI 在选歌时同步显示名（不经过队列）。</summary>
        public void SetTitle(string? title) => CurrentTitle = title;

        /// <summary>已播秒数与总秒数（由模组每 tick 从解码器/播放器同步进来，仅供 UI 显示）。</summary>
        public double PlayedSeconds { get; set; }
        public double TotalSeconds { get; set; }

        /// <summary>请求播放某首歌。返回 false 表示当前正在忙（需先停止）。</summary>
        public bool RequestSong(SongKey song, string? title = null)
        {
            if (!_sm.TryBegin(song))
            {
                _log.Notification($"[vscloudmusic] 当前正在播放/暂停/准备中，先停止再切歌（忽略请求 {song}）");
                return false;
            }
            CurrentTitle = title;
            return true;
        }

        public void RequestStop() => _sm.MarkStopped();
        public void RequestPause() => _sm.MarkPaused();
        public void RequestResume() => _sm.MarkPlaying();
        public void NotifyPlaying() => _sm.MarkPlaying();
        /// <summary>
        /// 上报播放失败，必须携带失败所属曲目 songId。下载/准备的完成事件来自后台线程，
        /// 经主线程消息队列投递时用户可能早已 RequestStop 并请求了新歌 —— 若不校验身份，
        /// 旧歌的失败会把新歌的 Preparing（或 Playing）误打成 Failed，而 StartPlayback 的
        /// 防串台守卫会因此丢弃新歌的合法开播（新歌永远不响）。校验规则与 StartPlayback 同源：
        /// 失败只允许标记在「仍是当前曲目」上，身份不匹配即静默丢弃。本类只在主线程被调用，
        /// 此处读 _sm.CurrentSongId 是线程安全的。
        /// </summary>
        public void NotifyFailed(string reason, SongKey song)
        {
            if (_sm.CurrentSong != song) return;
            _log.Warning($"[vscloudmusic] 播放失败：{reason}");
            _sm.MarkFailed(reason);
        }

        /// <summary>
        /// 由模组在「硬件已释放」的结束路径调用：状态回到可接受新请求的 Stopped。
        /// 注意与 Task 1 契约的配合：本任务从不调用 MarkEnded —— 流末尾的决策只发生在
        /// 非 Playing 状态（Playing 时 WantsLoop 恒为真、走循环重开，不会落到这里），
        /// 而 MarkEnded 在那些状态下是静默 no-op；因此这里用无条件且明确定义的 MarkStopped。
        /// </summary>
        public void NotifyStopped() => _sm.MarkStopped();

        /// <summary>流末尾是否应当重播（由模组在 tick 里询问）。</summary>
        public bool WantsLoop => _sm.WantsLoop;
    }
}