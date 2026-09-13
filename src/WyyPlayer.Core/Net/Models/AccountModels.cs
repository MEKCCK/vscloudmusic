using Newtonsoft.Json;

namespace WyyPlayer.Core.Net.Models
{
    /// <summary>已登录账号的公开信息。未登录时登录状态接口返回 null。</summary>
    public sealed class AccountInfo
    {
        public long UserId { get; set; }
        public string Nickname { get; set; } = "";
        public bool VipType { get; set; }
    }

    /// <summary>
    /// /api/nuser/account/get 的响应。account/profile 位于响应顶层，不在 data 包装内；
    /// 未登录时典型形态为 code 200 且两者均 null，或纯码 301。
    /// </summary>
    internal sealed class RawAccountResponse
    {
        [JsonProperty("code")] public int Code { get; set; }
        [JsonProperty("account")] public RawAccountAccount? Account { get; set; }
        [JsonProperty("profile")] public RawAccountProfile? Profile { get; set; }
    }

    internal sealed class RawAccountAccount
    {
        [JsonProperty("id")] public long Id { get; set; }
        [JsonProperty("vipType")] public int VipType { get; set; }
    }

    internal sealed class RawAccountProfile
    {
        [JsonProperty("userId")] public long UserId { get; set; }
        [JsonProperty("nickname")] public string Nickname { get; set; } = "";
        [JsonProperty("vipType")] public int VipType { get; set; }
    }
}