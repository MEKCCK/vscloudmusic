using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WyyPlayer.Api;
using WyyPlayer.Core.Audio;
using WyyPlayer.Core.Net;
using WyyPlayer.Core.Net.Models;
using WyyPlayer.Core.Storage;

namespace WyyPlayer.Core.Providers
{
    /// <summary>
    /// 网易云音乐音源。把既有的 <see cref="NeteaseClient"/> 包成宿主认识的
    /// <see cref="IMusicProvider"/>，是本模组内置的第一个（也是目前唯一一个）音源。
    ///
    /// 与旧 <c>SongPlayer</c> 的分工变化：旧类同时管「取地址」和「下载缓存」，
    /// 且只服务网易一家。现在**取地址属于音源**（各家协议不同），
    /// **下载/缓存属于宿主**（与音源无关）—— 附属模组只需实现取地址。
    ///
    /// 线程约定：所有方法都可能被后台线程调用；本类不触碰任何游戏 API。
    /// </summary>
    public sealed class NeteaseProvider : IMusicProvider
    {
        public const string ProviderId = "netease";

        private readonly ILog _log;
        private readonly CredentialStore _credentials;
        private readonly NeteaseHttp _http;
        private readonly NeteaseClient _client;
        private readonly SongCache _cache;

        private IProviderContext? _context;
        private ProviderAccount _account = ProviderAccount.Anonymous();

        /// <param name="configDir">该音源专属目录：凭据与音频缓存都放在这里。</param>
        public NeteaseProvider(string configDir, string? cookieHeader, ILog log)
        {
            _log = log;
            Directory.CreateDirectory(configDir);

            _credentials = new CredentialStore(Path.Combine(configDir, "credentials.txt"));
            _http = new NeteaseHttp(cookieHeader);
            _client = new NeteaseClient(_http);
            _cache = new SongCache(Path.Combine(configDir, "cache"));

            _account = cookieHeader == null
                ? ProviderAccount.Anonymous("未登录")
                : new ProviderAccount { IsLoggedIn = true, StatusMessage = "已登录" };
        }

        public string Id => ProviderId;

        public string DisplayName => "网易云";

        public ProviderCapabilities Capabilities =>
            ProviderCapabilities.Search
            | ProviderCapabilities.Playback
            | ProviderCapabilities.UserLibrary
            | ProviderCapabilities.SimilarSongs
            | ProviderCapabilities.Authentication
            | ProviderCapabilities.QrLogin
            | ProviderCapabilities.Recommendations
            | ProviderCapabilities.Radio;

        public ProviderAccount Account => _account;

        public void Initialize(IProviderContext context) => _context = context;

        // ---------------- 浏览 ----------------

        public async Task<SearchPage> SearchAsync(string keyword, int limit, int offset,
            CancellationToken ct)
        {
            var r = await _client.SearchAsync(keyword, limit, offset, ct).ConfigureAwait(false);
            var list = new List<SongInfo>(r.Songs.Count);
            foreach (var s in r.Songs) list.Add(ToSongInfo(s));
            return new SearchPage { Songs = list, TotalCount = r.TotalCount };
        }

        public async Task<IReadOnlyList<CollectionInfo>> GetUserCollectionsAsync(CancellationToken ct)
        {
            var lists = await _client.GetUserPlaylistsAsync(ct).ConfigureAwait(false);
            var result = new List<CollectionInfo>(lists.Count);
            foreach (var p in lists)
                result.Add(new CollectionInfo
                {
                    Id = p.Id.ToString(),
                    Name = p.Name,
                    TrackCount = p.TrackCount,
                });
            return result;
        }

        public async Task<IReadOnlyList<SongInfo>> GetCollectionTracksAsync(string collectionId,
            CancellationToken ct)
        {
            if (!long.TryParse(collectionId, out var id))
                return Array.Empty<SongInfo>();

            var tracks = await _client.GetPlaylistTracksAsync(id, ct: ct).ConfigureAwait(false);
            var result = new List<SongInfo>(tracks.Count);
            foreach (var t in tracks) result.Add(ToSongInfo(t));
            return result;
        }

        public async Task<IReadOnlyList<SongInfo>> GetRecommendedSongsAsync(CancellationToken ct)
        {
            var songs = await _client.GetDailyRecommendationsAsync(ct).ConfigureAwait(false);
            return ToSongInfos(songs);
        }

        public async Task<IReadOnlyList<SongInfo>> GetRadioSongsAsync(CancellationToken ct)
        {
            var songs = await _client.GetRadioSongsAsync(ct).ConfigureAwait(false);
            return ToSongInfos(songs);
        }

        private static IReadOnlyList<SongInfo> ToSongInfos(IReadOnlyList<SongBrief> songs)
        {
            var result = new List<SongInfo>(songs.Count);
            foreach (var s in songs) result.Add(ToSongInfo(s));
            return result;
        }

        // ---------------- 播放 ----------------

        public Task<string?> GetStreamUriAsync(SongKey song, AudioQuality quality,
            CancellationToken ct)
            => _client.GetSongUrlAsync(ParseNativeId(song), ToLevel(quality), ct);

        public Task<string?> GetCoverUrlAsync(SongKey song, CancellationToken ct)
            => _client.GetSongCoverUrlAsync(ParseNativeId(song), ct);

        public async Task<SongInfo?> GetSongInfoAsync(SongKey song, CancellationToken ct)
        {
            // 搜索/歌单接口带回来的信息已足够显示；这里只在需要时补封面。
            // 时长由解码器在播放时给出，故不在这个接口里造数据。
            var cover = await _client.GetSongCoverUrlAsync(ParseNativeId(song), ct)
                .ConfigureAwait(false);
            return new SongInfo
            {
                Key = song,
                Name = song.NativeId,
                CoverUrl = cover ?? "",
            };
        }

        public async Task<IReadOnlyList<SongKey>> GetSimilarSongsAsync(SongKey seed, int count,
            CancellationToken ct)
        {
            var ids = await _client.GetHeartbeatSongsAsync(ParseNativeId(seed), count, ct)
                .ConfigureAwait(false);
            var result = new List<SongKey>(ids.Count);
            foreach (var id in ids) result.Add(new SongKey(ProviderId, id.ToString()));
            return result;
        }

        /// <summary>
        /// 取可播放的**本地** mp3 路径：命中缓存不发任何网络请求，否则取地址后下载。
        /// 返回 null 表示取不到（未登录 / VIP 限制 / 已下架 / 网络失败）。
        ///
        /// 这是宿主侧职责（与音源无关）的预留接口 —— 目前放在这里是因为缓存目录
        /// 由本音源管理；后续宿主接管下载时原样上移即可。
        /// </summary>
        public async Task<string?> PrepareLocalFileAsync(SongKey song, AudioQuality quality,
            CancellationToken ct)
        {
            if (_cache.Has(song)) return _cache.GetPath(song);

            var url = await GetStreamUriAsync(song, quality, ct).ConfigureAwait(false);
            if (url == null) return null;

            try
            {
                return await _cache.DownloadAsync(song, url, ct).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _log.Error($"[vscloudmusic] 下载失败（{e.GetType().Name}）");
                return null;
            }
        }

        // ---------------- 账户 ----------------

        public async Task<ILoginFlow> BeginLoginAsync(CancellationToken ct)
        {
            var flow = new NeteaseQrLoginFlow(_client, this, _log);
            await flow.StartAsync(ct).ConfigureAwait(false);
            return flow;
        }

        public async Task LogoutAsync(CancellationToken ct)
        {
            try { await _client.LogoutAsync(ct).ConfigureAwait(false); }
            catch (Exception e) { _log.Warning($"[vscloudmusic] 服务端登出失败（{e.GetType().Name}），仍清除本地凭据"); }

            _credentials.Clear();
            _account = ProviderAccount.Anonymous("已退出登录");
            _log.Notification("[vscloudmusic] 已退出登录并清除本地凭据");
        }

        /// <summary>登录成功后由登录流程回调：保存凭据并更新账户状态。</summary>
        internal void OnLoginSucceeded(string nickname)
        {
            var cookie = _client.ExportCookie();
            _credentials.Save(cookie);
            _account = new ProviderAccount
            {
                IsLoggedIn = true,
                DisplayName = nickname,
                StatusMessage = "已登录",
            };
            _log.Notification($"[vscloudmusic] 登录成功：{nickname}");
        }

        public void Dispose()
        {
            _cache.Dispose();
            _http.Dispose();
        }

        // ---------------- 内部 ----------------

        private static long ParseNativeId(SongKey song) =>
            long.TryParse(song.NativeId, out var id) ? id : 0;

        private static string ToLevel(AudioQuality q) => q switch
        {
            AudioQuality.Standard => "standard",
            AudioQuality.Higher => "higher",
            AudioQuality.Lossless => "lossless",
            _ => "exhigh",
        };

        private static SongInfo ToSongInfo(SongBrief s) => new()
        {
            Key = new SongKey(ProviderId, s.Id.ToString()),
            Name = s.Name,
            Artist = s.Artist,
            Album = s.Album,
            CoverUrl = s.CoverUrl,
        };
    }
}
