using Newtonsoft.Json;

namespace WyyPlayer.Core.Net.Models
{
    internal sealed class RawLyricResponse
    {
        [JsonProperty("code")] public int Code { get; set; }
        [JsonProperty("lrc")] public RawLyricBody? Lrc { get; set; }
    }

    internal sealed class RawLyricBody
    {
        [JsonProperty("lyric")] public string? Lyric { get; set; }
    }
}