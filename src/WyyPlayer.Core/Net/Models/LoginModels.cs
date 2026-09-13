using Newtonsoft.Json;

namespace WyyPlayer.Core.Net.Models
{
    public enum QrState
    {
        Unknown = 0,
        Expired = 800,
        Waiting = 801,
        Scanned = 802,
        Confirmed = 803,
    }

    /// <summary>
    /// 二维码轮询结果。区分两种失败模式的契约：
    /// - <c>State == Unknown &amp;&amp; RawCode == 0</c>：无法解析响应（空 body / 非 JSON / 服务端限流的空响应）。
    /// - <c>State == Unknown &amp;&amp; RawCode != 0</c>：服务端返回了我们未建模的 code（如实测的 8821），
    ///   <see cref="Message"/> 保留服务端对此状态的解释。
    /// 已建模的 code（800/801/802/803）映射为对应 <see cref="QrState"/>，<see cref="RawCode"/> 为原始 code。
    /// </summary>
    public readonly record struct QrPollResult(QrState State, int RawCode, string Message, string Nickname);

    internal sealed class RawLoginQrKeyResponse
    {
        [JsonProperty("code")] public int Code { get; set; }
        [JsonProperty("unikey")] public string Unikey { get; set; } = "";
    }

    internal sealed class RawLoginQrPollResponse
    {
        [JsonProperty("code")] public int Code { get; set; }
        [JsonProperty("message")] public string Message { get; set; } = "";
        [JsonProperty("nickname")] public string Nickname { get; set; } = "";
    }
}