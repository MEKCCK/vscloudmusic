using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WyyPlayer.Api
{
    /// <summary>
    /// 音乐源能力位。宿主据此决定界面显示什么 —— 不支持的能力对应的按钮/入口直接隐藏，
    /// 而不是让玩家点进去才发现不能用。
    ///
    /// 这个设计借自参考实现 AllMusic（IMusicApi）与 Folia（ProviderCapabilities）：
    /// 音源之间能力差别很大，用布尔位声明比让宿主假设「所有源都一样」稳得多。
    /// </summary>
    [Flags]
    public enum ProviderCapabilities
    {
        None = 0,
        Search = 1 << 0,
        Playback = 1 << 1,
        /// <summary>支持读取当前账号的歌单/收藏。</summary>
        UserLibrary = 1 << 2,
        /// <summary>支持按种子歌曲取相似歌曲（如「以这首为种子继续放」）。宿主暂未使用，留给音源自行组织。</summary>
        SimilarSongs = 1 << 3,
        Lyrics = 1 << 4,
        /// <summary>需要登录才能完整使用；对应界面显示账户区。</summary>
        Authentication = 1 << 5,
        /// <summary>支持二维码扫码登录。</summary>
        QrLogin = 1 << 6,
        /// <summary>支持「推荐」页（服务端个性化推荐）。</summary>
        Recommendations = 1 << 7,
        /// <summary>支持「电台」（私人 FM 一类的连续推荐流）。</summary>
        Radio = 1 << 8,
    }

    /// <summary>音质档位。具体码率由各音源自行映射。</summary>
    public enum AudioQuality
    {
        Standard,
        Higher,
        ExHigh,
        Lossless,
    }

    /// <summary>
    /// 一首歌在宿主内的表示。带 <see cref="Key"/>，因此跨音源不会混淆。
    /// </summary>
    public sealed class SongInfo
    {
        public SongKey Key { get; set; }

        public string Name { get; set; } = "";

        /// <summary>艺术家，多个用 " / " 连接（显示用）。</summary>
        public string Artist { get; set; } = "";

        public string Album { get; set; } = "";

        /// <summary>封面图 URL；取不到时为空字符串，宿主画占位框。</summary>
        public string CoverUrl { get; set; } = "";

        /// <summary>时长（秒）。未知为 0。</summary>
        public double DurationSeconds { get; set; }

        public override string ToString() =>
            string.IsNullOrEmpty(Artist) ? Name : $"{Name} — {Artist}";
    }

    /// <summary>一页搜索结果。</summary>
    public sealed class SearchPage
    {
        public IReadOnlyList<SongInfo> Songs { get; set; } = Array.Empty<SongInfo>();

        /// <summary>服务端报告的结果总数，用于「还有更多」的判断。未知为 0。</summary>
        public int TotalCount { get; set; }
    }

    /// <summary>歌单/收藏夹等「一组歌」的简要信息。</summary>
    public sealed class CollectionInfo
    {
        /// <summary>该音源内部的集合 id。</summary>
        public string Id { get; set; } = "";

        public string Name { get; set; } = "";

        public int TrackCount { get; set; }

        /// <summary>封面 URL，可空。</summary>
        public string CoverUrl { get; set; } = "";
    }

    /// <summary>当前账号状态。<see cref="IsLoggedIn"/> 决定界面显示「登录」还是「退出」。</summary>
    public sealed class ProviderAccount
    {
        public bool IsLoggedIn { get; set; }

        /// <summary>显示用昵称；未登录为空。</summary>
        public string DisplayName { get; set; } = "";

        /// <summary>账号在该音源内的唯一标识；仅用于显示与去重，不参与鉴权。</summary>
        public string UserId { get; set; } = "";

        /// <summary>未登录或异常时的原因（给玩家看的一句话）。</summary>
        public string StatusMessage { get; set; } = "";

        public static ProviderAccount Anonymous(string reason = "") =>
            new() { IsLoggedIn = false, StatusMessage = reason };
    }
}
