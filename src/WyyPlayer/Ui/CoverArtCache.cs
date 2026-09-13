using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using WyyPlayer.Api;
using WyyPlayer.Core.Providers;
using System.Threading;

namespace WyyPlayer.Ui
{
    /// <summary>
    /// 封面图缓存：按歌曲 id 取回封面 URL → 下载图片字节 → 解码成 <see cref="BitmapRef"/>。
    ///
    /// 线程约定：下载与解码都在后台线程（SkiaSharp 解码是纯托管 CPU 工作，不碰 GL，
    /// 因此安全）；只有最终交给 HUD 绘制时才在主线程消费 BitmapRef。
    /// 每首歌曲最多下载一次；失败记负缓存，避免反复打网络。
    ///
    /// BitmapRef 持有非托管位图内存，Dispose 时必须逐个释放，否则会泄漏。
    /// </summary>
    internal sealed class CoverArtCache : IDisposable
    {
        /// <summary>
        /// 请求图床按此尺寸出图。网易图床（p*.music.126.net）支持 ?param=WyH 由服务端缩放，
        /// 比我们自己下大图再裁好得多：省流量、省解码、省内存。
        /// </summary>
        private const string ThumbParam = "?param=200y200";

        private readonly ILogger _logger;
        private readonly HttpClient _http;
        private readonly MusicProviderRegistry _providers;

        private readonly object _lock = new();
        private readonly Dictionary<SongKey, BitmapRef> _ready = new();
        private readonly HashSet<SongKey> _inFlight = new();
        private readonly HashSet<SongKey> _failed = new();

        private SongKey? _currentSong;
        private BitmapRef? _current;
        private bool _disposed;

        public CoverArtCache(ICoreClientAPI capi, MusicProviderRegistry providers)
        {
            _logger = capi.Logger;
            _providers = providers;
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 WyyPlayer/1.0");
        }

        /// <summary>
        /// 主线程每帧调用：切到指定歌曲，未缓存则触发一次后台补取。
        /// 必须是廉价的 —— 已处理过的 id 走三个集合的查找后立刻返回。
        /// </summary>
        public void Track(SongKey song)
        {
            if (_disposed) return;

            if (_currentSong != song)
            {
                _currentSong = song;
                lock (_lock) { _current = _ready.TryGetValue(song, out var b) ? b : null; }
            }
            else if (_current == null)
            {
                // 同一首歌：缓存可能刚补齐
                lock (_lock) { if (_ready.TryGetValue(song, out var b)) _current = b; }
            }

            if (_current != null) return;

            lock (_lock)
            {
                if (_ready.ContainsKey(song) || _inFlight.Contains(song) || _failed.Contains(song))
                    return;
                _inFlight.Add(song);
            }
            _ = Task.Run(() => FetchAsync(song));
        }

        /// <summary>当前应绘制的封面；未就绪时为 null（HUD 画占位框）。仅主线程读取。</summary>
        public BitmapRef? Current => _current;

        private async Task FetchAsync(SongKey song)
        {
            try
            {
                var provider = _providers.Resolve(song);
                if (provider == null) { Settle(song, null); return; }
                var url = await provider.GetCoverUrlAsync(song, CancellationToken.None).ConfigureAwait(false);
                if (string.IsNullOrEmpty(url)) { Settle(song, null); return; }

                // 仅对网易图床追加缩放参数，避免给未知 CDN 送奇怪查询串
                if (url.Contains("music.126.net", StringComparison.OrdinalIgnoreCase))
                    url += ThumbParam;

                byte[] bytes = await _http.GetByteArrayAsync(url).ConfigureAwait(false);
                if (bytes.Length == 0) { Settle(song, null); return; }

                // BitmapExternal 内部用 SkiaSharp 解码（游戏自带 SkiaSharp.dll + libSkiaSharp.so），
                // 因此无需我们额外引图像库。
                var bmp = new BitmapExternal(bytes, bytes.Length, _logger);
                if (bmp.Width <= 0 || bmp.Height <= 0) { bmp.Dispose(); Settle(song, null); return; }

                Settle(song, bmp);
            }
            catch (Exception e)
            {
                // 封面失败绝不能影响播放。只记异常类型：URL 里可能带签名参数，不进日志。
                _logger.Debug($"[vscloudmusic] 封面获取失败 {song}：{e.GetType().Name}");
                Settle(song, null);
            }
        }

        private void Settle(SongKey song, BitmapRef? bmp)
        {
            lock (_lock)
            {
                _inFlight.Remove(song);
                if (bmp == null) { _failed.Add(song); return; }
                if (_disposed) { bmp.Dispose(); return; }
                _ready[song] = bmp;
                if (_currentSong == song) _current = bmp;
            }
            if (bmp != null) _logger.Debug($"[vscloudmusic] 封面就绪 {song}（{bmp.Width}x{bmp.Height}）");
        }

        public void Dispose()
        {
            List<BitmapRef> all;
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                all = new List<BitmapRef>(_ready.Values);
                _ready.Clear();
                _inFlight.Clear();
                _failed.Clear();
                _current = null;
            }
            foreach (var b in all) { try { b.Dispose(); } catch { /* 卸载期忽略 */ } }
            try { _http.Dispose(); } catch { /* 卸载期忽略 */ }
        }
    }
}
