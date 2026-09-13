using System.Collections.Generic;
using Newtonsoft.Json;

namespace WyyPlayer.Core.Net.Models
{
    /// <summary>主选端点 /api/song/enhance/player/url 的响应：data 是数组。</summary>
    internal sealed class RawSongUrlResponse
    {
        [JsonProperty("code")] public int Code { get; set; }
        [JsonProperty("data")] public List<RawSongUrlItem>? Data { get; set; }
    }

    internal sealed class RawSongUrlItem
    {
        [JsonProperty("id")] public long Id { get; set; }
        [JsonProperty("url")] public string? Url { get; set; }
        [JsonProperty("br")] public int Bitrate { get; set; }
        [JsonProperty("size")] public long Size { get; set; }
        [JsonProperty("level")] public string? Level { get; set; }
        [JsonProperty("fee")] public int Fee { get; set; }
    }
}