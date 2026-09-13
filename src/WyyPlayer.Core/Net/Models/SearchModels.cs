using System.Collections.Generic;
using Newtonsoft.Json;

namespace WyyPlayer.Core.Net.Models
{
    public sealed class SongBrief
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string Artist { get; set; } = "";
        public string Album { get; set; } = "";

        /// <summary>
        /// 封面图 URL。注意：**搜索接口不返回封面**（其实测响应里 album 只有 picId，
        /// 而网易的封面地址无法由 picId 推导），所以搜索结果这里恒为空；
        /// 真正可用的来源是 /api/v3/song/detail 的 al.picUrl，播放时按需补取。
        /// </summary>
        public string CoverUrl { get; set; } = "";

        public override string ToString() => $"{Name} — {Artist} [{Id}]";
    }

    public sealed class SearchResult
    {
        public List<SongBrief> Songs { get; } = new();
        public int TotalCount { get; set; }
    }

    internal sealed class RawSearchResponse
    {
        [JsonProperty("code")] public int Code { get; set; }
        [JsonProperty("result")] public RawSearchResult? Result { get; set; }
    }

    internal sealed class RawSearchResult
    {
        [JsonProperty("songCount")] public int SongCount { get; set; }
        [JsonProperty("songs")] public List<RawSong>? Songs { get; set; }
    }

    internal sealed class RawSong
    {
        [JsonProperty("id")] public long Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; } = "";
        // 实测 WEAPI /api/search/get 返回的键是 artists/album（非 ar/al）。
        // ar/al 是 /api/cloudsearch/pc 等接口的键，此处以实际响应为准。
        [JsonProperty("artists")] public List<RawArtist>? Artists { get; set; }
        [JsonProperty("album")] public RawAlbum? Album { get; set; }
    }

    internal sealed class RawArtist
    {
        [JsonProperty("name")] public string Name { get; set; } = "";
    }

    internal sealed class RawAlbum
    {
        [JsonProperty("name")] public string Name { get; set; } = "";
        /// <summary>封面地址。搜索接口不含此键（实测），歌曲详情/歌单详情才含。</summary>
        [JsonProperty("picUrl")] public string PicUrl { get; set; } = "";
    }
}