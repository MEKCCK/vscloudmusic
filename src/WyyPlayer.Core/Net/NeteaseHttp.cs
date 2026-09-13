using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using WyyPlayer.Core.Crypto;

namespace WyyPlayer.Core.Net
{
    /// <summary>
    /// 网易云 HTTP 客户端。负责 cookie 维护、csrf 注入与加密分发。
    /// </summary>
    public sealed class NeteaseHttp : IDisposable
    {
        public const string WeapiDomain = "https://music.163.com";
        // 引用实现（@neteasecloudmusicapienhanced/api）util/config.json 的 APP_CONF.eapiDomain
        public const string EapiDomain = "https://interfacepc.music.163.com";

        private const string DesktopUa =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        // 引用实现为 eapi 选 chooseUserAgent('api', 'iphone')，除非 os === 'osx' 才用 Mac 版 Chrome UA
        private const string EapiIphoneUa = "NeteaseMusic 9.0.90/5038 (iPhone; iOS 16.2; zh_CN)";
        private const string EapiOsxUa =
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

        // processCookieObject 的 osMap['pc']：EAPI 请求在无对应 cookie 时以此为设备默认
        private const string PcOs = "pc";
        private const string PcAppver = "3.1.17.204416";
        private const string PcOsver = "Microsoft-Windows-10-Professional-build-19045-64bit";
        private const string PcChannel = "netease";

        private readonly HttpClient _client;
        private readonly CookieContainer _cookies = new();

        /// <summary>
        /// 所有出网请求串行化。实测网易 CDN（volc-dcdn）在并发/连发时会静默限流：
        /// 返回 HTTP 200 且 Content-Length 为 0（不是错误码，所以容易被误当成“解析失败”）。
        /// 串行 + 最小间隔是比“重试更多次”更有效的对策 —— 重试只能治症状。
        /// </summary>
        private readonly SemaphoreSlim _gate = new(1, 1);
        private DateTime _lastRequestUtc = DateTime.MinValue;
        private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(250);

        private const int MaxAttempts = 4;

        // 按计划约定的构造签名：始终自建 HttpClient 并挂 CookieContainer。
        // 不对外提供注入 HttpClient 的路径 —— 注入的客户端不会经过 CookieContainer，
        // 会导致 MUSIC_U 等登录 cookie 静默丢失。
        public NeteaseHttp(string? cookieHeader = null)
        {
            var handler = new HttpClientHandler
            {
                CookieContainer = _cookies,
                UseCookies = true,
                AutomaticDecompression = DecompressionMethods.All,
            };
            _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

            if (!string.IsNullOrWhiteSpace(cookieHeader))
                ImportCookieHeader(cookieHeader);
        }

        /// <summary>
        /// 当前 cookie，形如 a=1; b=2。同名条目（QR 轮询的 Set-Cookie 跨 path/domain 变体
        /// 在 CookieContainer 中累积）按 <see cref="IsPreferred"/> 每条名只导出一个值，
        /// 保证导出⟶导入⟶导出幂等。
        /// </summary>
        public string CookieHeader
        {
            get
            {
                var all = _cookies.GetAllCookies();
                var best = new Dictionary<string, Cookie>(StringComparer.OrdinalIgnoreCase);
                foreach (Cookie c in all)
                    if (!best.TryGetValue(c.Name, out var cur) || IsPreferred(c, cur))
                        best[c.Name] = c;

                var parts = new List<string>(best.Count);
                foreach (Cookie c in all)
                    if (best.TryGetValue(c.Name, out var b) && ReferenceEquals(b, c))
                        parts.Add($"{c.Name}={c.Value}");
                return string.Join("; ", parts);
            }
        }

        public string? GetCookie(string name)
        {
            Cookie? best = null;
            foreach (Cookie c in _cookies.GetAllCookies())
                if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase) &&
                    (best == null || IsPreferred(c, best)))
                    best = c;
            return best?.Value;
        }

        /// <summary>
        /// 同名 cookie 的偏好规则（CookieHeader 导出与 GetCookie 共用，保证决定性）：
        /// 1) 后写优先 —— TimeStamp 更新者胜（每次 Set-Cookie 都更新/新建条目时间戳）；
        /// 2) 时间戳并列时更具体 path 优先（更长 Path）；
        /// 3) 依 Path、Domain、Value 依次降序兜底，使结果与枚举顺序完全解耦。
        /// 对齐引用实现的扁平 name→value cookie 模型：最后收到的 Set-Cookie 覆盖先前同名值。
        /// </summary>
        internal static bool IsPreferred(Cookie candidate, Cookie current)
        {
            if (candidate.TimeStamp != current.TimeStamp)
                return candidate.TimeStamp > current.TimeStamp;
            if (candidate.Path.Length != current.Path.Length)
                return candidate.Path.Length > current.Path.Length;
            var pathCmp = string.CompareOrdinal(candidate.Path, current.Path);
            if (pathCmp != 0) return pathCmp > 0;
            var domainCmp = StringComparer.OrdinalIgnoreCase.Compare(candidate.Domain, current.Domain);
            if (domainCmp != 0) return domainCmp > 0;
            return string.CompareOrdinal(candidate.Value, current.Value) > 0;
        }

        public void SetCookie(string name, string value)
        {
            _cookies.Add(new Uri(WeapiDomain), new Cookie(name, value) { Domain = ".music.163.com" });
        }

        public void ImportCookieHeader(string header)
        {
            foreach (var seg in header.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = seg.Split('=', 2);
                if (kv.Length != 2) continue;
                SetCookie(kv[0].Trim(), kv[1].Trim());
            }
        }

        public async Task<string> PostWeapiAsync(string uri, string jsonBody, CancellationToken ct = default)
        {
            var csrf = GetCookie("__csrf") ?? "";
            var body = InjectCsrf(jsonBody, csrf);

            var payload = NeteaseCrypto.Weapi(body);
            var url = WeapiDomain + "/weapi/" + uri.TrimStart('/').Replace("api/", "");

            return await ExecuteAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Referrer = new Uri(WeapiDomain);
                req.Headers.TryAddWithoutValidation("User-Agent", DesktopUa);
                req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["params"] = payload.Params,
                    ["encSecKey"] = payload.EncSecKey,
                });
                return req;
            }, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 明文 API（/api/... 普通表单 + cookie，不加密）。
        ///
        /// **这是首选通道**：实测同一环境下加密通道（weapi/eapi）有 30~40% 的空 body
        /// 静默限流，而明文通道在逐个端点各 6 次连测中全部成功（搜索/歌单详情/歌曲详情/
        /// 播放地址/登录状态/我的歌单/歌词/二维码 key 均为 6/6 或 3/3）。
        /// 只有确实要求 eapi header 的端点（心动模式、下载地址 v1）才继续走加密通道。
        /// </summary>
        public async Task<string> PostApiAsync(string path, IDictionary<string, string> form,
            CancellationToken ct = default)
        {
            var url = WeapiDomain + path;
            var snapshot = new Dictionary<string, string>(form);   // 重试要重建请求，先快照

            return await ExecuteAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Referrer = new Uri(WeapiDomain);
                req.Headers.TryAddWithoutValidation("User-Agent", DesktopUa);
                req.Content = new FormUrlEncodedContent(snapshot);
                return req;
            }, ct).ConfigureAwait(false);
        }

        public async Task<string> PostEapiAsync(string uri, string jsonBody, CancellationToken ct = default)
        {
            var url = EapiDomain + "/eapi/" + uri.TrimStart('/').Replace("api/", "");

            return await ExecuteAsync(() =>
            {
                var fields = BuildEapiHeaderFields();
                var withHeader = InjectHeader(jsonBody, HeaderToJson(fields));
                var enc = NeteaseCrypto.Eapi(uri, withHeader);

                var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.TryAddWithoutValidation("User-Agent", ChooseEapiUserAgent(fields));
                // Cookie 头由 header 对象生成（引用实现的 createHeaderCookie），不是 cookie 容器本身
                req.Headers.TryAddWithoutValidation("Cookie", CreateHeaderCookie(fields));
                req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["params"] = enc,
                });
                return req;
            }, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 出网公共路径：串行化 + 最小间隔 + 有界指数退避重试。
        ///
        /// 重试条件为「空 body」—— 这是网易 CDN 静默限流的确定特征（HTTP 200 + len 0，
        /// 实测 volc-dcdn）。空 body 是**可重试**的；一旦拿到非空响应就立即返回，
        /// 无论内容是业务错误码（那是调用方的事，重试也没用）。
        ///
        /// 每次重试都重建 HttpRequestMessage（HttpRequestMessage 不可重发）。
        /// </summary>
        private async Task<string> ExecuteAsync(Func<HttpRequestMessage> build, CancellationToken ct)
        {
            for (int attempt = 1; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                string raw;

                await _gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var since = DateTime.UtcNow - _lastRequestUtc;
                    if (since < MinInterval)
                        await Task.Delay(MinInterval - since, ct).ConfigureAwait(false);
                    _lastRequestUtc = DateTime.UtcNow;

                    using var req = build();
                    using var res = await _client.SendAsync(req, ct).ConfigureAwait(false);
                    raw = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                }
                catch (Exception e) when (attempt < MaxAttempts && IsTransient(e, ct))
                {
                    await BackoffAsync(attempt, ct).ConfigureAwait(false);
                    continue;
                }
                finally
                {
                    _gate.Release();
                }

                if (raw.Length > 0 || attempt >= MaxAttempts) return raw;
                await BackoffAsync(attempt, ct).ConfigureAwait(false);
            }
        }

        /// <summary>瞬时故障（网络/超时）；调用方主动取消不算，必须原样上抛。</summary>
        private static bool IsTransient(Exception e, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return false;
            return e is HttpRequestException or TaskCanceledException or IOException;
        }

        /// <summary>指数退避 300/700/1500ms，叠加抖动避开同步重试。</summary>
        private static Task BackoffAsync(int attempt, CancellationToken ct)
        {
            var ms = attempt switch { 1 => 300, 2 => 700, _ => 1500 };
            ms += Random.Shared.Next(0, 200);
            return Task.Delay(ms, ct);
        }

        public async Task<string> PostPlainAsync(string uri, IDictionary<string, string> form,
            CancellationToken ct = default)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, WeapiDomain + uri);
            req.Headers.Referrer = new Uri(WeapiDomain);
            req.Headers.TryAddWithoutValidation("User-Agent", DesktopUa);
            req.Content = new FormUrlEncodedContent(form);

            using var res = await _client.SendAsync(req, ct).ConfigureAwait(false);
            return await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }

        public async Task<byte[]> GetBytesAsync(string url, CancellationToken ct = default)
        {
            using var res = await _client.GetAsync(url, ct).ConfigureAwait(false);
            res.EnsureSuccessStatusCode();
            return await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }

        // ---------- EAPI header 纯逻辑（内部可见供离线单测，与引用实现逐行对应） ----------

        /// <summary>
        /// 按引用实现 header 字段顺序构造 EAPI header（值为 null 对应 JS undefined）。
        /// 缺省设备字段（os/appver/osver/channel）无对应 cookie 时取 osMap['pc'] 默认。
        /// </summary>
        internal List<KeyValuePair<string, string?>> BuildEapiHeaderFields()
        {
            var now = DateTimeOffset.UtcNow;

            var fields = new List<KeyValuePair<string, string?>>(13)
            {
                new("osver", CookieOr("osver", PcOsver)),
                new("deviceId", GetCookie("deviceId")),                        // 无 cookie 则 null（JS undefined）
                new("os", CookieOr("os", PcOs)),
                new("appver", CookieOr("appver", PcAppver)),
                new("versioncode", CookieOr("versioncode", "140")),
                new("mobilename", GetCookie("mobilename") ?? ""),
                new("buildver", CookieOr("buildver", now.ToUnixTimeSeconds().ToString())),
                new("resolution", CookieOr("resolution", "1920x1080")),
                new("__csrf", GetCookie("__csrf") ?? ""),
                new("channel", CookieOr("channel", PcChannel)),
                new("requestId", GenerateRequestId(now)),
            };

            string? musicU = GetCookie("MUSIC_U");
            if (!string.IsNullOrEmpty(musicU)) fields.Add(new("MUSIC_U", musicU));
            string? musicA = GetCookie("MUSIC_A");
            if (!string.IsNullOrEmpty(musicA)) fields.Add(new("MUSIC_A", musicA));

            return fields;
        }

        private string CookieOr(string name, string fallback)
        {
            var v = GetCookie(name);
            return string.IsNullOrEmpty(v) ? fallback : v;
        }

        private static string ChooseEapiUserAgent(IReadOnlyList<KeyValuePair<string, string?>> fields)
        {
            foreach (var kv in fields)
                if (kv.Key == "os") return kv.Value == "osx" ? EapiOsxUa : EapiIphoneUa;
            return EapiIphoneUa;
        }

        /// <summary>body 内嵌的 header JSON。与 JSON.stringify 一致：null（JS undefined）字段被丢弃。</summary>
        internal static string HeaderToJson(IEnumerable<KeyValuePair<string, string?>> fields)
        {
            var dict = new Dictionary<string, string?>();
            foreach (var kv in fields)
                if (kv.Value != null) dict[kv.Key] = kv.Value;
            return JsonConvert.SerializeObject(dict);
        }

        /// <summary>
        /// 引用实现的 createHeaderCookie：header 每个键值 encodeURIComponent 后以 "; " 连接，
        /// 作为请求的 Cookie 头。值为 null 时按 JS 语义 encodeURIComponent(undefined) === "undefined"。
        /// </summary>
        internal static string CreateHeaderCookie(IEnumerable<KeyValuePair<string, string?>> header)
        {
            var parts = new List<string>();
            foreach (var kv in header)
                parts.Add(EncodeUriComponent(kv.Key) + "=" + EncodeUriComponent(kv.Value ?? "undefined"));
            return string.Join("; ", parts);
        }

        /// <summary>引用实现的 generateRequestId：epochMillis_ + 4 位补零随机数（0000-0999）。</summary>
        internal static string GenerateRequestId(DateTimeOffset now) =>
            $"{now.ToUnixTimeMilliseconds()}_{Random.Shared.Next(1000).ToString().PadLeft(4, '0')}";

        /// <summary>
        /// JS encodeURIComponent 等价实现：Uri.EscapeDataString 之外把 RFC 2396 子分隔符
        /// ! * ' ( ) 还原为原样（encodeURIComponent 不转义它们）。
        /// </summary>
        internal static string EncodeUriComponent(string value) =>
            Uri.EscapeDataString(value)
                .Replace("%21", "!").Replace("%2A", "*")
                .Replace("%27", "'").Replace("%28", "(").Replace("%29", ")");

        private static string InjectCsrf(string json, string csrf)
        {
            if (json == "{}" || string.IsNullOrWhiteSpace(json)) return $"{{\"csrf_token\":\"{csrf}\"}}";
            var trimmed = json.TrimEnd();
            if (!trimmed.EndsWith("}")) return json;
            var head = trimmed.Substring(0, trimmed.Length - 1).TrimEnd();
            var sep = head.EndsWith("{") ? "" : ",";
            return $"{head}{sep}\"csrf_token\":\"{csrf}\"}}";
        }

        private static string InjectHeader(string json, string header)
        {
            var trimmed = json.TrimEnd();
            if (!trimmed.EndsWith("}")) return json;
            var head = trimmed.Substring(0, trimmed.Length - 1).TrimEnd();
            var sep = head.EndsWith("{") ? "" : ",";
            return $"{head}{sep}\"header\":{header}}}";
        }

        public void Dispose()
        {
            _gate.Dispose();
            _client.Dispose();
        }
    }
}