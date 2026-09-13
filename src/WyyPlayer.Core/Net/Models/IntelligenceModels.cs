using System.Collections.Generic;
using Newtonsoft.Json;

namespace WyyPlayer.Core.Net.Models
{
    /// <summary>用户歌单列表（用于解析「我喜欢的音乐」歌单 id，心动模式需要它）。</summary>
    internal sealed class RawUserPlaylistResponse
    {
        [JsonProperty("code")] public int Code { get; set; }
        [JsonProperty("playlist")] public List<RawPlaylistItem>? Playlist { get; set; }
    }

    internal sealed class RawPlaylistItem
    {
        [JsonProperty("id")] public long Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; } = "";
        /// <summary>5 = 我喜欢的音乐（网易云的固定 specialType）。</summary>
        [JsonProperty("specialType")] public int SpecialType { get; set; }
        [JsonProperty("trackCount")] public int TrackCount { get; set; }
    }

    /// <summary>
    /// 心动模式（智能播放）响应。实测结构：
    /// <code>{"code":200,"message":"SUCCESS","data":[{"id":557905,"songInfo":{...},"alg":...,"recommended":...}, ...]}</code>
    /// 每项的 <c>id</c> 即歌曲 id（与 <c>songInfo.id</c> 相同）。
    /// 注意 <c>count</c> 只是建议值：实测传 5 仍返回 151 条。
    /// </summary>
    internal sealed class RawIntelligenceResponse
    {
        [JsonProperty("code")] public int Code { get; set; }
        [JsonProperty("message")] public string Message { get; set; } = "";
        [JsonProperty("data")] public List<RawIntelligenceItem>? Data { get; set; }
    }

    internal sealed class RawIntelligenceItem
    {
        [JsonProperty("id")] public long Id { get; set; }
        [JsonProperty("songInfo")] public RawIntelligenceSongInfo? SongInfo { get; set; }
    }

    internal sealed class RawIntelligenceSongInfo
    {
        [JsonProperty("id")] public long Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; } = "";
    }
}

namespace WyyPlayer.Core.Net.Models
{
    /// <summary>歌单详情（/api/v6/playlist/detail，eapi）。trackIds 是全量歌曲 id，tracks 只含前若干首的详情。</summary>
    internal sealed class RawPlaylistDetailResponse
    {
        [JsonProperty("code")] public int Code { get; set; }
        [JsonProperty("playlist")] public RawPlaylistDetail? Playlist { get; set; }
    }

    internal sealed class RawPlaylistDetail
    {
        [JsonProperty("id")] public long Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("trackCount")] public int TrackCount { get; set; }
        [JsonProperty("trackIds")] public List<RawTrackId>? TrackIds { get; set; }
        [JsonProperty("tracks")] public List<RawPlaylistTrack>? Tracks { get; set; }
    }

    internal sealed class RawTrackId
    {
        [JsonProperty("id")] public long Id { get; set; }
    }

    internal sealed class RawPlaylistTrack
    {
        [JsonProperty("id")] public long Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("ar")] public List<RawArtist>? Artists { get; set; }
        /// <summary>歌单详情端点的专辑键是 al（与搜索端点的 album 不同）。</summary>
        [JsonProperty("al")] public RawAlbum? Album { get; set; }
    }
}

namespace WyyPlayer.Core.Net.Models
{
    /// <summary>歌单简要信息（UI 用）。</summary>
    public sealed class PlaylistBrief
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public int TrackCount { get; set; }

        public override string ToString() => $"{Name}（{TrackCount} 首）";
    }
}

namespace WyyPlayer.Core.Net.Models
{
    internal sealed class RawSongDetailResponse
    {
        [JsonProperty("code")] public int Code { get; set; }
        [JsonProperty("songs")] public List<RawSongDetail>? Songs { get; set; }
    }

    internal sealed class RawSongDetail
    {
        [JsonProperty("id")] public long Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; } = "";
        /// <summary>歌曲详情端点的歌手键是 ar（与歌单详情一致）。</summary>
        [JsonProperty("ar")] public List<RawArtist>? Artists { get; set; }
        [JsonProperty("al")] public RawAlbum? Album { get; set; }
    }
}

namespace WyyPlayer.Core.Net.Models
{
    /// <summary>
    /// 每日推荐（/api/v3/discovery/recommend/songs）。
    /// 实测该端点的歌曲字段是 ar/al（与歌单详情同源），而不是搜索端点的 artists/album。
    /// </summary>
    internal sealed class RawDailyRecommendResponse
    {
        [JsonProperty("code")] public int Code { get; set; }
        [JsonProperty("data")] public RawDailyRecommendData? Data { get; set; }
    }

    internal sealed class RawDailyRecommendData
    {
        [JsonProperty("dailySongs")] public List<RawDailySong>? DailySongs { get; set; }
    }

    internal sealed class RawDailySong
    {
        [JsonProperty("id")] public long Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("ar")] public List<RawArtist>? Artists { get; set; }
        [JsonProperty("al")] public RawAlbum? Album { get; set; }
    }

    /// <summary>
    /// 私人 FM（/api/v1/radio/get）。实测该端点用的是 artists/album，
    /// 与每日推荐的 ar/al 又不同 —— 三个接口三种结构。
    /// </summary>
    internal sealed class RawRadioResponse
    {
        [JsonProperty("data")] public List<RawRadioSong>? Data { get; set; }
    }

    internal sealed class RawRadioSong
    {
        [JsonProperty("id")] public long Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("artists")] public List<RawArtist>? Artists { get; set; }
        [JsonProperty("album")] public RawAlbum? Album { get; set; }
    }
}
