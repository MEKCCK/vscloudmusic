using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using WyyPlayer.Api;

namespace WyyPlayer.Core.Audio
{
    /// <summary>
    /// 音频下载与本地缓存。
    ///
    /// 只缓存**字节**，绝不缓存 URL —— 网易云的音频地址是带签名的，响应里
    /// expi 约 1200 秒即过期，缓存 URL 必然导致后续播放 403。
    ///
    /// 落盘走「临时 .part 文件 + 原子改名」，中途失败不会留下可被误认为
    /// 完整缓存的文件。
    /// </summary>
    public sealed class SongCache : IDisposable
    {
        private readonly string _dir;
        private readonly HttpClient _client;
        private readonly TimeSpan _timeout;

        /// <param name="handler">可选的 HttpMessageHandler 注入点（供测试使用）。
        /// 为 null 时使用系统默认 HttpClient 行为，只接受 http(s) 等受支持 scheme ——
        /// 非 http(s) 的 URL（如 file://）会被拒绝，保持安全默认。</param>
        public SongCache(string cacheDir, TimeSpan? downloadTimeout = null,
            HttpMessageHandler? handler = null)
        {
            if (string.IsNullOrWhiteSpace(cacheDir))
                throw new ArgumentException("缓存目录不能为空", nameof(cacheDir));

            _dir = cacheDir;
            Directory.CreateDirectory(_dir);
            _timeout = downloadTimeout ?? TimeSpan.FromMinutes(5);

            // 超时用「每次下载各自的 CancellationToken」控制，而不是 HttpClient.Timeout：
            // 后者无法按请求调整，而 12MB 的曲子在慢速网络下会超过默认的 100 秒。
            // handler 为 null 时走系统默认 HttpClient 行为（只接受 http(s) 等受支持
            // scheme，file:// 等非 http(s) 会被拒绝）；测试通过注入自定义处理器
            // 离线验证流式写盘路径。
            _client = handler is null
                ? new HttpClient { Timeout = Timeout.InfiniteTimeSpan }
                : new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        }

        /// <summary>
        /// 缓存文件名。**必须净化 SongKey**：它的字符串形式是 <c>netease:5257138</c>，
        /// 而冒号在 Windows 文件名里是非法字符 —— 直接用会导致 Windows 上写文件失败。
        /// 只保留字母数字与连字符。
        /// </summary>
        internal static string FileNameFor(SongKey song)
        {
            var raw = song.ToString();
            var chars = new char[raw.Length];
            for (int i = 0; i < raw.Length; i++)
            {
                var c = raw[i];
                chars[i] = (char.IsLetterOrDigit(c) || c == '-') ? c : '_';
            }
            return new string(chars) + ".mp3";
        }

        public string GetPath(SongKey song) => Path.Combine(_dir, FileNameFor(song));

        public bool Has(SongKey song)
        {
            var p = GetPath(song);
            return File.Exists(p) && new FileInfo(p).Length > 0;
        }

        /// <summary>下载到缓存；已缓存则直接返回既有路径，不重复下载。</summary>
        public async Task<string> DownloadAsync(SongKey song, string url,
            CancellationToken ct = default)
        {
            var target = GetPath(song);
            if (Has(song)) return target;

            // 每次调用用独立的临时名：并发下载同一首歌时，共用 {id}.mp3.part 会因
            // File.Create 默认 FileShare.None 而让后到者抛 IOException。
            // 名字以 .part 结尾（Clear 的 *.part 清理能覆盖、失败清理能匹配），
            // 且不以 .mp3 结尾（Has/TotalBytes 的 *.mp3 匹配绝不会命中它）。
            var part = Path.Combine(_dir, $"{FileNameFor(song)}.{Guid.NewGuid():N}.part");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);

            try
            {
                using var res = await _client
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);
                res.EnsureSuccessStatusCode();

                // 流式写盘：不把整首曲子读进内存（上一版 GetBytesAsync 会）。
                await using (var src = await res.Content.ReadAsStreamAsync(cts.Token)
                                 .ConfigureAwait(false))
                await using (var dst = File.Create(part))
                {
                    await src.CopyToAsync(dst, 81920, cts.Token).ConfigureAwait(false);
                }

                // 原子改名：只有完整下载完成才会出现目标文件
                File.Move(part, target, overwrite: true);
                return target;
            }
            catch
            {
                // 失败清理半成品，绝不留 .part
                try { if (File.Exists(part)) File.Delete(part); } catch { /* 清理失败不掩盖原异常 */ }
                throw;
            }
        }

        public long TotalBytes()
        {
            long sum = 0;
            foreach (var f in Directory.GetFiles(_dir, "*.mp3"))
                sum += new FileInfo(f).Length;
            return sum;
        }

        public void Clear()
        {
            foreach (var f in Directory.GetFiles(_dir, "*.mp3")) File.Delete(f);
            foreach (var f in Directory.GetFiles(_dir, "*.part")) File.Delete(f);
        }

        public void Dispose() => _client.Dispose();
    }
}