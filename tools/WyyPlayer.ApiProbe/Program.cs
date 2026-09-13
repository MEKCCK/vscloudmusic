using System;
using System.Threading.Tasks;
using WyyPlayer.Core.Net;

namespace WyyPlayer.ApiProbe
{
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            using var http = new NeteaseHttp();
            var client = new NeteaseClient(http);

            // 通用「原始请求」调试命令：把加密链路的实际响应原样打出来。
            // 用于「不猜响应结构」——新增接口时先跑一次看真实字段，再写解析。
            // 用法: raw eapi <path> <json>      或    raw weapi <path> <json>
            if (args.Length >= 4 && args[0] == "raw")
            {
                var credPath = Environment.GetEnvironmentVariable("WYY_CRED_FILE");
                using var rawHttp = string.IsNullOrEmpty(credPath)
                    ? new NeteaseHttp()
                    : new NeteaseHttp(new WyyPlayer.Core.Storage.CredentialStore(credPath).Load());

                var scheme = args[1];
                var path = args[2];
                var body = args[3];

                var resp = scheme == "weapi"
                    ? await rawHttp.PostWeapiAsync(path, body)
                    : await rawHttp.PostEapiAsync(path, body);

                Console.WriteLine(resp);
                return 0;
            }

            if (args.Length > 0 && args[0] == "login")
            {
                var unikey = await client.CreateQrKeyAsync();
                Console.WriteLine("二维码内容: " + NeteaseClient.BuildQrContent(unikey));
                Console.WriteLine("（用手机网易云 App 扫描；此处仅打印内容用于验证）");

                // 轮询预算只算已建模状态（Waiting/Scanned）；Unknown（限流空体或未建模码，如实测
                // 的 8821）不消耗预算，改为指数退避，让服务端风控窗口过去。硬超时 5 分钟兜底。
                var deadline = DateTime.UtcNow.AddMinutes(5);
                const int maxWaitingPolls = 120;
                const int maxDelayMs = 30_000;
                int waitingPolls = 0;
                int delayMs = 2000;

                while (DateTime.UtcNow < deadline)
                {
                    var r = await client.PollQrAsync(unikey);
                    Console.WriteLine($"  [poll #{waitingPolls}] 状态={r.State} 原始码={r.RawCode} 消息=\"{r.Message}\"");

                    if (r.State == WyyPlayer.Core.Net.Models.QrState.Confirmed)
                    {
                        Console.WriteLine($"登录成功，昵称={r.Nickname}");
                        // ⚠ 刻意行为，仅供本地调试：下面会把登录凭据打到 stdout。
                        // 模组本体绝不这样做（凭据不得进入日志）。如果你要贴日志求助，
                        // 请先删掉这一行输出。
                        Console.WriteLine("导出 cookie（凭据！请勿外传）: " + client.ExportCookie());
                        return 0;
                    }
                    if (r.State == WyyPlayer.Core.Net.Models.QrState.Expired)
                    {
                        Console.WriteLine("二维码已过期");
                        return 1;
                    }
                    if (r.State == WyyPlayer.Core.Net.Models.QrState.Unknown)
                    {
                        // 解析失败（RawCode=0，服务端限流空体）或未建模码（RawCode≠0，未知业务状态）：
                        // 退避重试，不消耗正常轮询预算，原始码与消息让失败可诊断。
                        if (delayMs < maxDelayMs) delayMs *= 2;
                        Console.WriteLine($"  响应异常（Unknown），退避 {delayMs}ms 后重试…");
                        await Task.Delay(delayMs);
                        continue;
                    }

                    // Waiting / Scanned —— 正常节奏，恢复默认间隔。
                    delayMs = 2000;
                    waitingPolls++;
                    if (waitingPolls >= maxWaitingPolls) break;
                    await Task.Delay(delayMs);
                }
                Console.WriteLine("超时");
                return 1;
            }

            if (args.Length >= 2 && args[0] == "url")
            {
                string? url;
                try
                {
                    url = await client.GetSongUrlAsync(long.Parse(args[1]));
                }
                catch (Exception e)
                {
                    // 传输层/服务端异常（如 CDN 对匿名请求的偶发 TLS 重置）：暴露为一行诊断 + 非零退出，
                    // 不抛未处理异常。生产路径（SongPlayer.PrepareAsync）同样把这类异常收敛为“取不到地址”。
                    Console.WriteLine($"取不到地址（网络/服务端异常：{e.GetType().Name}）");
                    return 1;
                }
                Console.WriteLine(url == null ? "取不到地址（可能需要 VIP 或已下架）" : url);
                return url == null ? 1 : 0;
            }

            if (args.Length >= 2 && args[0] == "lyric")
            {
                var lrc = await client.GetLyricAsync(long.Parse(args[1]));
                Console.WriteLine(lrc ?? "无歌词");
                return lrc == null ? 1 : 0;
            }

            if (args.Length > 0 && args[0] == "status")
            {
                var store = new WyyPlayer.Core.Storage.CredentialStore(
                    Environment.GetEnvironmentVariable("WYY_CRED_FILE")
                    ?? throw new InvalidOperationException("需要设置 WYY_CRED_FILE 环境变量"));

                using var credHttp = new WyyPlayer.Core.Net.NeteaseHttp(store.Load());
                var credClient = new WyyPlayer.Core.Net.NeteaseClient(credHttp);

                var info = await credClient.GetLoginStatusAsync();
                if (info == null)
                {
                    Console.WriteLine("未登录（或 MUSIC_U 未能送达到 weapi 请求）");
                    return 1;
                }

                Console.WriteLine($"已登录: 昵称={info.Nickname} userId={info.UserId} VIP={info.VipType}");
                return 0;
            }

            var keyword = args.Length > 0 ? args[0] : "周杰伦";
            Console.WriteLine($"搜索: {keyword}");

            var result = await client.SearchAsync(keyword, 10);
            Console.WriteLine($"命中 {result.TotalCount} 条，显示前 {result.Songs.Count} 条：");
            foreach (var s in result.Songs)
                Console.WriteLine("  " + s);

            return result.Songs.Count > 0 ? 0 : 1;
        }
    }
}