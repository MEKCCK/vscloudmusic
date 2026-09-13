using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WyyPlayer.Core.Net.Models;

namespace WyyPlayer.Core.Net
{
    public sealed class NeteaseClient
    {
        private readonly NeteaseHttp _http;

        /// <summary>
        /// 补取曲目详情的批大小。太大 URL 会超长、太小请求数会暴涨；
        /// 500 首一批是这类接口的常见量级。
        /// </summary>
        private const int DetailBatchSize = 500;

        public NeteaseClient(NeteaseHttp http) { _http = http; }

        public bool IsLoggedIn => !string.IsNullOrEmpty(_http.GetCookie("MUSIC_U"));

        /// <summary>搜索歌曲。无需登录。</summary>
        /// <param name="offset">分页起点。「更多」翻页时传已有结果数。</param>
        public async Task<SearchResult> SearchAsync(string keyword, int limit = 30,
            int offset = 0, CancellationToken ct = default)
        {

            // 走明文 API：加密通道实测有 30~40% 空 body 静默限流，明文为 6/6。
            var raw = await _http.PostApiAsync("/api/search/get", new Dictionary<string, string>
            {
                ["s"] = keyword,
                ["type"] = "1",
                ["limit"] = limit.ToString(),
                ["offset"] = offset.ToString(),
            }, ct).ConfigureAwait(false);

            var parsed = JsonConvert.DeserializeObject<RawSearchResponse>(raw);
            var result = new SearchResult { TotalCount = parsed?.Result?.SongCount ?? 0 };

            if (parsed?.Result?.Songs == null) return result;

            foreach (var s in parsed.Result.Songs)
            {
                var artists = new List<string>();
                if (s.Artists != null)
                    foreach (var a in s.Artists) artists.Add(a.Name);

                result.Songs.Add(new SongBrief
                {
                    Id = s.Id,
                    Name = s.Name,
                    Artist = string.Join(" / ", artists),
                    Album = s.Album?.Name ?? "",
                });
            }

            return result;
        }

        /// <summary>申请登录二维码的 key。走 EAPI（与参考实现对端点的 crypto 选择一致）。</summary>
        public async Task<string> CreateQrKeyAsync(CancellationToken ct = default)
        {
            var raw = await _http.PostApiAsync("/api/login/qrcode/unikey",
                new Dictionary<string, string> { ["type"] = "3" }, ct)
                .ConfigureAwait(false);

            var parsed = JsonConvert.DeserializeObject<RawLoginQrKeyResponse>(raw);
            if (parsed == null || string.IsNullOrEmpty(parsed.Unikey))
                throw new InvalidOperationException($"申请二维码 key 失败，响应: {raw}");

            return parsed.Unikey;
        }

        /// <summary>二维码内容字符串，需渲染成二维码给用户扫。</summary>
        public static string BuildQrContent(string unikey) =>
            $"https://music.163.com/login?codekey={unikey}";

        /// <summary>轮询二维码状态。确认后 MUSIC_U 会由 HttpResponse 的 Set-Cookie 写入 cookie 容器。
        /// 走 EAPI —— 参考实现 login_qr_check 为 eapi；实测 WEAPI 在确认步被风控拒绝（码 8821）。</summary>
        public async Task<QrPollResult> PollQrAsync(string unikey, CancellationToken ct = default)
        {
            var body = JsonConvert.SerializeObject(new { key = unikey, type = 3 });
            var raw = await _http.PostEapiAsync("/api/login/qrcode/client/login", body, ct)
                .ConfigureAwait(false);

            return ParseQrPoll(raw);
        }

        /// <summary>
        /// 解析二维码轮询响应。契约见 <see cref="QrPollResult"/>：
        /// 解析失败/空 body → <c>Unknown</c> + <c>RawCode=0</c>；已建模码（800/801/802/803）→ 对应
        /// <see cref="QrState"/>；未建模码（如实测的 8821）→ <c>Unknown</c> 但保留原始码与服务端消息。
        /// 内部可见以便纯函数单元测试，生产调用方仍走 <see cref="PollQrAsync"/>。
        /// </summary>
        internal static QrPollResult ParseQrPoll(string raw)
        {
            RawLoginQrPollResponse? parsed;
            try
            {
                parsed = JsonConvert.DeserializeObject<RawLoginQrPollResponse>(raw);
            }
            catch (JsonException)
            {
                parsed = null;
            }

            if (parsed == null)
                return new QrPollResult(QrState.Unknown, 0, "", "");

            var state = parsed.Code switch
            {
                800 => QrState.Expired,
                801 => QrState.Waiting,
                802 => QrState.Scanned,
                803 => QrState.Confirmed,
                _ => QrState.Unknown,
            };

            return new QrPollResult(state, parsed.Code, parsed.Message, parsed.Nickname);
        }

        /// <summary>把设计文档的 level 映射为 br（非 v1 端点按码率选音质）。</summary>
        public static int ToBitrate(string level) => level switch
        {
            "standard" => 128000,
            "higher"   => 192000,
            "exhigh"   => 320000,
            "lossless" => 999000,
            _          => 320000,
        };

        /// <summary>
        /// 取音频播放地址。level 见设计文档：standard / higher / exhigh / lossless。
        ///
        /// 主选 /api/song/enhance/player/url 走**明文 API**（实测 6/6；加密通道有 30~40%
        /// 空 body 静默限流）。不要改成 v1 端点 —— v1 在参考实现中用 xeapi，需要 X25519，
        /// 而 .NET 10 原生不支持。主选取不到 URL 时，用 download/url/v1 兜底；
        /// 该兜底端点明文参数报「参数错误」，故仍走 eapi。
        /// </summary>
        public async Task<string?> GetSongUrlAsync(long songId, string level = "exhigh",
            CancellationToken ct = default)
        {
            // 主选：按码率
            var primaryRaw = await _http.PostApiAsync("/api/song/enhance/player/url",
                new Dictionary<string, string>
                {
                    ["ids"] = $"[{songId}]",
                    ["br"] = ToBitrate(level).ToString(),
                }, ct).ConfigureAwait(false);

            var primary = JsonConvert.DeserializeObject<RawSongUrlResponse>(primaryRaw);
            if (primary is { Data.Count: > 0 })
            {
                var url = primary.Data[0].Url;
                if (!string.IsNullOrEmpty(url)) return url;
            }

            // 兜底：按 level
            var fallbackBody = JsonConvert.SerializeObject(new
            {
                id = songId,
                level,
                immerseType = "c51",
            });

            var fallbackRaw = await _http.PostEapiAsync("/api/song/enhance/download/url/v1", fallbackBody, ct)
                .ConfigureAwait(false);

            return ParseFallbackUrl(fallbackRaw);
        }

        /// <summary>
        /// 解析兜底端点 /api/song/enhance/download/url/v1 的响应。
        ///
        /// 该端点的 data 形态不固定（实测为对象，部分场景为数组），按 data 的 token 类型分支：
        /// 数组或对象任一形态下「url 缺失/为空」都返回 null（VIP 无权限、已下架等合法无地址结果）；
        /// data 缺失或为 null（如纯码响应 code:-105）同样视为无地址。
        /// 畸形 body（非 JSON / data 非对象也非数组）仍以 JsonException 暴露，与「无地址」可区分，
        /// 不吞成 null 以免丢失诊断信息。
        /// 内部可见以便纯函数单元测试（ParseQrPoll 同款先例）。
        /// </summary>
        internal static string? ParseFallbackUrl(string fallbackRaw)
        {
            var root = JObject.Parse(fallbackRaw);   // 非 JSON/非对象 → JsonException 原样上抛
            var data = root["data"];

            if (data is JArray array)
            {
                var first = array.First;
                if (first is JObject item) return UrlFromToken(item["url"]);
                if (first is null) return null;      // 空数组 → 无地址
                throw new JsonException(
                    $"兜底端点 data 数组元素不是对象（{first.Type}）: {Truncate(fallbackRaw)}");
            }

            if (data is JObject obj) return UrlFromToken(obj["url"]);

            if (data is null or JValue { Type: JTokenType.Null })
                return null;                         // data 键缺失或 data:null → 无地址

            throw new JsonException(
                $"兜底端点 data 既非对象也非数组（{data.Type}）: {Truncate(fallbackRaw)}");
        }

        private static string? UrlFromToken(JToken? url)
        {
            if (url is null) return null;
            var s = url.ToString();
            return string.IsNullOrEmpty(s) ? null : s;
        }

        private static string Truncate(string raw, int max = 200) =>
            raw.Length <= max ? raw : raw.Substring(0, max) + "…";

        /// <summary>取 LRC 歌词原文。取不到返回 null。参数与参考实现 lyric_new.js 对齐。</summary>
        public async Task<string?> GetLyricAsync(long songId, CancellationToken ct = default)
        {
            var body = JsonConvert.SerializeObject(new
            {
                id = songId,
                cp = false,
                tv = 0,
                lv = 0,
                rv = 0,
                kv = 0,
                yv = 0,
                ytv = 0,
                yrv = 0,
            });

            var raw = await _http.PostApiAsync("/api/song/lyric/v1", new Dictionary<string, string>
            {
                ["id"] = songId.ToString(),
                ["lv"] = "0", ["kv"] = "0", ["tv"] = "0",
            }, ct).ConfigureAwait(false);
            var parsed = JsonConvert.DeserializeObject<RawLyricResponse>(raw);
            return parsed?.Lrc?.Lyric;
        }

        /// <summary>
        /// 查询当前登录态。未登录返回 null。
        /// 注意用 weapi —— 登录状态接口在参考实现里是 weapi（不是 eapi），
        /// 这也是验证「MUSIC_U 能否跨域送达到 weapi 请求」的关键路径。
        /// </summary>
        public async Task<AccountInfo?> GetLoginStatusAsync(CancellationToken ct = default)
        {
            var raw = await _http.PostApiAsync("/api/nuser/account/get",
                new Dictionary<string, string>(), ct).ConfigureAwait(false);

            return ParseAccountStatus(raw);
        }

        /// <summary>
        /// 解析登录状态响应并映射为 <see cref="AccountInfo"/>；未登录返回 null。
        ///
        /// 已登录判定（同时成立）：code == 200 且 account/profile 至少一个存在。
        /// 未登录形态：code 200 但 account/profile 均 null；或非 200 code（常见 301）。
        /// 防御：非 200 code 即使带字段也返回 null —— 无效会话不得报告为已登录，
        /// 宁可在灰色地带误报未登录，不可误报已登录。
        ///
        /// 不可解析的 body（空串 / 非 JSON，如网关错误页）按未登录处理，不抛异常：
        /// 该服务对请求偶发限流且返回空 body（与 ParseQrPoll 遇到的现象一致），
        /// 「登录状态」的消费方（探针 status 命令）需要干净的 null 而非崩溃。
        /// 这与 ParseFallbackUrl 对畸形 body 抛异常的策略不同 —— 那里异常用于区分
        /// 「无地址」与「实现缺陷」；这里 null 本身就是合法的业务答案，且服务器对
        /// 空体的限流模式已被既有测试反复证实。
        /// 内部可见以便纯函数单元测试（ParseQrPoll/ParseFallbackUrl 同款先例）。
        /// </summary>
        internal static AccountInfo? ParseAccountStatus(string raw)
        {
            RawAccountResponse? parsed;
            try
            {
                parsed = JsonConvert.DeserializeObject<RawAccountResponse>(raw);
            }
            catch (JsonException)
            {
                parsed = null;
            }

            if (parsed == null || parsed.Code != 200) return null;
            if (parsed.Account == null && parsed.Profile == null) return null;

            return new AccountInfo
            {
                UserId = parsed.Account?.Id ?? parsed.Profile?.UserId ?? 0,
                Nickname = parsed.Profile?.Nickname ?? "",
                VipType = (parsed.Account?.VipType ?? parsed.Profile?.VipType ?? 0) != 0,
            };
        }

        /// <summary>持久化登录凭据所需的 cookie 串。</summary>
        public string ExportCookie() => _http.CookieHeader;

        /// <summary>
        /// 通知服务端登出（使会话失效）。**成功与否都必须继续清除本地凭据** ——
        /// 网络失败不应让玩家卡在「登不出去」的状态。
        /// </summary>
        public async Task LogoutAsync(CancellationToken ct = default)
        {
            try
            {
                await _http.PostApiAsync("/api/logout",
                    new Dictionary<string, string>(), ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 服务端登出失败不致命：本地凭据照清，下次登录会重新签发会话。
            }
        }

        /// <summary>
        /// 取单曲封面 URL（/api/v3/song/detail，weapi）。
        ///
        /// 为什么不用搜索结果：实测 WEAPI 搜索响应的 album 只有 picId、没有 picUrl，
        /// 而网易封面地址带一段服务端生成的加密串，客户端无法由 picId 反推。
        /// 所以封面必须在播放时补一次详情请求（按歌曲 id 缓存即可，每首只请求一次）。
        /// 参数 c 是一个 **JSON 字符串**（不是对象），照原样传。
        /// </summary>
        public async Task<string?> GetSongCoverUrlAsync(long songId, CancellationToken ct = default)
        {
            var c = JsonConvert.SerializeObject(new[] { new { id = songId } });
            var raw = await _http.PostApiAsync("/api/v3/song/detail",
                new Dictionary<string, string> { ["c"] = c }, ct).ConfigureAwait(false);

            RawSongDetailResponse? parsed;
            try { parsed = JsonConvert.DeserializeObject<RawSongDetailResponse>(raw); }
            catch (JsonException) { return null; }

            var url = parsed?.Songs is { Count: > 0 } ? parsed.Songs[0].Album?.PicUrl : null;
            return string.IsNullOrEmpty(url) ? null : url;
        }

        // ---------------- 推荐 / 电台 ----------------

        /// <summary>
        /// 每日推荐（/api/v3/discovery/recommend/songs，明文 API）。
        /// 该端点的歌曲字段是 **ar/al**（实测），与搜索端点的 artists/album 不同。
        /// </summary>
        public async Task<IReadOnlyList<SongBrief>> GetDailyRecommendationsAsync(
            CancellationToken ct = default)
        {
            var raw = await _http.PostApiAsync("/api/v3/discovery/recommend/songs",
                new Dictionary<string, string>(), ct).ConfigureAwait(false);

            RawDailyRecommendResponse? parsed;
            try { parsed = JsonConvert.DeserializeObject<RawDailyRecommendResponse>(raw); }
            catch (JsonException) { return Array.Empty<SongBrief>(); }

            var songs = parsed?.Data?.DailySongs;
            if (parsed?.Code != 200 || songs == null) return Array.Empty<SongBrief>();

            var result = new List<SongBrief>(songs.Count);
            foreach (var d in songs)
            {
                var artists = new List<string>();
                if (d.Artists != null) foreach (var a in d.Artists) artists.Add(a.Name);
                result.Add(new SongBrief
                {
                    Id = d.Id,
                    Name = d.Name,
                    Artist = string.Join(" / ", artists),
                    Album = d.Album?.Name ?? "",
                    CoverUrl = d.Album?.PicUrl ?? "",
                });
            }
            return result;
        }

        /// <summary>
        /// 私人 FM（/api/v1/radio/get，明文 API）：一批「电台」歌曲。
        /// 实测该端点的歌曲字段是 **artists/album**，与每日推荐的 ar/al 又不同。
        /// </summary>
        public async Task<IReadOnlyList<SongBrief>> GetRadioSongsAsync(CancellationToken ct = default)
        {
            var raw = await _http.PostApiAsync("/api/v1/radio/get",
                new Dictionary<string, string>(), ct).ConfigureAwait(false);

            RawRadioResponse? parsed;
            try { parsed = JsonConvert.DeserializeObject<RawRadioResponse>(raw); }
            catch (JsonException) { return Array.Empty<SongBrief>(); }

            if (parsed?.Data == null) return Array.Empty<SongBrief>();

            var result = new List<SongBrief>(parsed.Data.Count);
            foreach (var d in parsed.Data)
            {
                var artists = new List<string>();
                if (d.Artists != null) foreach (var a in d.Artists) artists.Add(a.Name);
                result.Add(new SongBrief
                {
                    Id = d.Id,
                    Name = d.Name,
                    Artist = string.Join(" / ", artists),
                    Album = d.Album?.Name ?? "",
                    CoverUrl = d.Album?.PicUrl ?? "",
                });
            }
            return result;
        }

        // ---------------- 歌单 ----------------

        /// <summary>取当前登录用户的歌单列表。未登录返回空列表。</summary>
        public async Task<IReadOnlyList<PlaylistBrief>> GetUserPlaylistsAsync(
            CancellationToken ct = default)
        {
            var empty = (IReadOnlyList<PlaylistBrief>)Array.Empty<PlaylistBrief>();

            var info = await GetLoginStatusAsync(ct).ConfigureAwait(false);
            if (info == null || info.UserId == 0) return empty;

            // 明文 API 必须显式带 uid（实测不带 uid 返回 code 400）
            var raw = await _http.PostApiAsync("/api/user/playlist", new Dictionary<string, string>
            {
                ["uid"] = info.UserId.ToString(),
                ["limit"] = "50",
                ["offset"] = "0",
            }, ct).ConfigureAwait(false);

            RawUserPlaylistResponse? parsed;
            try { parsed = JsonConvert.DeserializeObject<RawUserPlaylistResponse>(raw); }
            catch (JsonException) { return empty; }

            if (parsed?.Code != 200 || parsed.Playlist == null) return empty;

            var list = new List<PlaylistBrief>(parsed.Playlist.Count);
            foreach (var p in parsed.Playlist)
                list.Add(new PlaylistBrief { Id = p.Id, Name = p.Name, TrackCount = p.TrackCount });
            return list;
        }

        /// <summary>
        /// 取歌单的歌曲列表。
        ///
        /// 注意两个已实测的细节：
        /// 1) 端点是 eapi（不是明文 API —— 早期文档写错了）；
        /// 2) 该端点的歌曲字段是 **ar/al**，而**搜索**端点用的是 artists/album ——
        ///    同一份数据在不同端点字段名不同，不能混用。
        ///
        /// 参数 n 决定 tracks[] 里带回多少首详情；trackIds[] 始终是全量。
        /// 超出 n 的部分只有 id、没有标题（显示时回退为 #id）。
        /// </summary>
        public async Task<IReadOnlyList<SongBrief>> GetPlaylistTracksAsync(long playlistId,
            int maxDetail = 100, CancellationToken ct = default)
        {
            var empty = (IReadOnlyList<SongBrief>)Array.Empty<SongBrief>();

            // 明文 API 可用（6/6）——这是我最初文档里的写法，中途按参考实现改成 eapi 反而变差
            var raw = await _http.PostApiAsync("/api/v6/playlist/detail", new Dictionary<string, string>
            {
                ["id"] = playlistId.ToString(),
                ["n"] = maxDetail.ToString(),
                ["s"] = "8",
            }, ct).ConfigureAwait(false);

            RawPlaylistDetailResponse? parsed;
            try { parsed = JsonConvert.DeserializeObject<RawPlaylistDetailResponse>(raw); }
            catch (JsonException) { return empty; }

            var pl = parsed?.Playlist;
            if (parsed?.Code != 200 || pl?.TrackIds == null) return empty;

            // tracks[] 只有前 n 首带详情，trackIds[] 才是全量 —— 先用 tracks[] 建映射，
            // 剩下的（实测超过 100 首之后）分批补取详情，否则 101 首起只能显示 #id。
            var titles = new Dictionary<long, string>();
            var covers = new Dictionary<long, string>();
            if (pl.Tracks != null)
            {
                foreach (var t in pl.Tracks)
                {
                    var artists = new List<string>();
                    if (t.Artists != null)
                        foreach (var a in t.Artists) artists.Add(a.Name);
                    titles[t.Id] = artists.Count > 0
                        ? $"{t.Name} — {string.Join(" / ", artists)}"
                        : t.Name;
                    if (!string.IsNullOrEmpty(t.Album?.PicUrl)) covers[t.Id] = t.Album!.PicUrl;
                }
            }

            // 补全缺失部分：把还没标题的 id 分批发给歌曲详情端点。
            // 参照引用实现 playlist_track_all 的做法（先取全量 trackIds，再按批查详情）。
            var missing = new List<long>();
            foreach (var ti in pl.TrackIds)
                if (!titles.ContainsKey(ti.Id)) missing.Add(ti.Id);

            for (int i = 0; i < missing.Count; i += DetailBatchSize)
            {
                ct.ThrowIfCancellationRequested();
                var batch = missing.GetRange(i, Math.Min(DetailBatchSize, missing.Count - i));

                // c 是 **JSON 字符串**（不是对象），与 GetSongCoverUrlAsync 同一口径
                var c = JsonConvert.SerializeObject(batch.Select(id => new { id }));
                string rawBatch;
                try
                {
                    rawBatch = await _http.PostApiAsync("/api/v3/song/detail",
                        new Dictionary<string, string> { ["c"] = c }, ct).ConfigureAwait(false);
                }
                catch (Exception) { break; }   // 补详情失败不该让整个歌单报废：其余仍可按 id 播放

                RawSongDetailResponse? parsedBatch;
                try { parsedBatch = JsonConvert.DeserializeObject<RawSongDetailResponse>(rawBatch); }
                catch (JsonException) { break; }

                if (parsedBatch?.Songs == null) break;
                foreach (var d in parsedBatch.Songs)
                {
                    var artists = new List<string>();
                    if (d.Artists != null)
                        foreach (var a in d.Artists) artists.Add(a.Name);
                    titles[d.Id] = artists.Count > 0
                        ? $"{d.Name} — {string.Join(" / ", artists)}"
                        : d.Name;
                    if (!string.IsNullOrEmpty(d.Album?.PicUrl)) covers[d.Id] = d.Album!.PicUrl;
                }
            }

            var result = new List<SongBrief>(pl.TrackIds.Count);
            foreach (var ti in pl.TrackIds)
            {
                result.Add(new SongBrief
                {
                    Id = ti.Id,
                    Name = titles.TryGetValue(ti.Id, out var title) ? title : $"#{ti.Id}",
                    Artist = "",
                    Album = "",
                    CoverUrl = covers.TryGetValue(ti.Id, out var cover) ? cover : "",
                });
            }
            return result;
        }

        // ---------------- 心动模式（智能播放） ----------------

        private long _likedPlaylistId;

        /// <summary>
        /// 解析「我喜欢的音乐」歌单 id（specialType == 5）。心动模式必须要一个真实歌单上下文：
        /// 实测传 playlistId=0 会被服务端回绝（400 歌单不存在）。结果缓存在实例上。
        /// 未登录或取不到时返回 0。
        /// </summary>
        public async Task<long> GetLikedPlaylistIdAsync(CancellationToken ct = default)
        {
            if (_likedPlaylistId != 0) return _likedPlaylistId;

            var info = await GetLoginStatusAsync(ct).ConfigureAwait(false);
            if (info == null || info.UserId == 0) return 0;

            // 明文 API 必须显式带 uid（实测不带 uid 返回 code 400）
            var raw = await _http.PostApiAsync("/api/user/playlist", new Dictionary<string, string>
            {
                ["uid"] = info.UserId.ToString(),
                ["limit"] = "50",
                ["offset"] = "0",
            }, ct).ConfigureAwait(false);

            RawUserPlaylistResponse? parsed;
            try { parsed = JsonConvert.DeserializeObject<RawUserPlaylistResponse>(raw); }
            catch (JsonException) { return 0; }

            if (parsed?.Code != 200 || parsed.Playlist == null) return 0;

            foreach (var p in parsed.Playlist)
            {
                if (p.SpecialType == 5)   // 5 = 我喜欢的音乐
                {
                    _likedPlaylistId = p.Id;
                    return _likedPlaylistId;
                }
            }

            return 0;
        }

        /// <summary>
        /// 心动模式：以 <paramref name="seedSongId"/> 为种子取一批相似的歌，返回歌曲 id 列表。
        /// 取不到时返回空列表（调用方据此停止，不要空转重试）。
        ///
        /// 为何需要歌单：<c>/api/playmode/intelligence/list</c> 实测必须有真实 playlistId，
        /// 否则返回 400「歌单不存在」。因此先解析「我喜欢的音乐」歌单；
        /// 解析不到（未登录）则直接返回空。
        /// </summary>
        public async Task<IReadOnlyList<long>> GetHeartbeatSongsAsync(long seedSongId,
            int count = 20, CancellationToken ct = default)
        {
            var empty = Array.Empty<long>();

            var playlistId = await GetLikedPlaylistIdAsync(ct).ConfigureAwait(false);
            if (playlistId == 0) return empty;

            var body = JsonConvert.SerializeObject(new
            {
                songId = seedSongId,
                type = "fromPlayOne",
                playlistId,
                startMusicId = seedSongId,
                count,
            });

            var raw = await _http.PostEapiAsync("/api/playmode/intelligence/list", body, ct)
                .ConfigureAwait(false);

            RawIntelligenceResponse? parsed;
            try { parsed = JsonConvert.DeserializeObject<RawIntelligenceResponse>(raw); }
            catch (JsonException) { return empty; }

            if (parsed?.Code != 200 || parsed.Data == null) return empty;

            var ids = new List<long>();
            foreach (var item in parsed.Data)
            {
                // 实测每项的 id 即歌曲 id（与 songInfo.id 相同）；两者都容错，取非零者。
                var id = item.Id != 0 ? item.Id : item.SongInfo?.Id ?? 0;
                if (id != 0 && !ids.Contains(id)) ids.Add(id);
            }
            return ids;
        }
    }
}