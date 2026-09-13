using System;

namespace WyyPlayer.Api
{
    /// <summary>
    /// 跨音乐源的歌曲标识：<c>源id:nativeId</c>，例如 <c>netease:5257138</c>。
    ///
    /// 为什么不能只用各家的原始 id：多个音乐源的 id 空间互相独立，数值撞车完全可能，
    /// 而且 QQ 音乐一类的源用的根本不是数字 id。队列里只存原始 id 的话，
    /// 播放时无从知道该向**哪个源**要音频，也可能拿到另一家的同 id 歌曲。
    ///
    /// 因此队列、缓存、控制器一律以本类型为准，原始 id 只在 provider 内部使用。
    /// </summary>
    public readonly struct SongKey : IEquatable<SongKey>
    {
        /// <summary>音乐源标识，由 provider 自己声明（如 "netease"）。比较时忽略大小写。</summary>
        public string ProviderId { get; }

        /// <summary>该音乐源内部的原始 id。</summary>
        public string NativeId { get; }

        public SongKey(string providerId, string nativeId)
        {
            ProviderId = providerId ?? throw new ArgumentNullException(nameof(providerId));
            NativeId = nativeId ?? throw new ArgumentNullException(nameof(nativeId));
        }

        /// <summary>按 <c>源id:nativeId</c> 解析。格式不符返回 false，不抛异常（外部数据不可信）。</summary>
        public static bool TryParse(string? text, out SongKey key)
        {
            key = default;
            if (string.IsNullOrEmpty(text)) return false;
            var i = text!.IndexOf(':');
            if (i <= 0 || i >= text.Length - 1) return false;
            key = new SongKey(text.Substring(0, i), text.Substring(i + 1));
            return true;
        }

        public static SongKey Parse(string text) =>
            TryParse(text, out var k) ? k : throw new FormatException($"不是合法的歌曲键：{text}");

        public bool Equals(SongKey other) =>
            string.Equals(ProviderId, other.ProviderId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(NativeId, other.NativeId, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is SongKey k && Equals(k);

        public override int GetHashCode() =>
            HashCode.Combine(ProviderId.ToLowerInvariant(), NativeId);

        public static bool operator ==(SongKey a, SongKey b) => a.Equals(b);
        public static bool operator !=(SongKey a, SongKey b) => !a.Equals(b);

        /// <summary>序列化形式，可直接持久化到队列/缓存文件名。</summary>
        public override string ToString() => $"{ProviderId}:{NativeId}";
    }
}
