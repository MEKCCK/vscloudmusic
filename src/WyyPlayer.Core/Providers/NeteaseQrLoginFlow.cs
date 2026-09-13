using System;
using System.Threading;
using System.Threading.Tasks;
using Net.Codecrete.QrCodeGenerator;
using WyyPlayer.Api;
using WyyPlayer.Core.Net;
using WyyPlayer.Core.Net.Models;

namespace WyyPlayer.Core.Providers
{
    /// <summary>
    /// 网易云扫码登录。
    ///
    /// 分工：宿主负责显示说明与位矩阵、按固定间隔调 <see cref="PollAsync"/>；
    /// 本类负责申请 unikey、生成矩阵、解释轮询状态码。这样登录方式的变化完全被关在这里，
    /// 宿主与 API 契约都不受影响。
    ///
    /// 为什么给**位矩阵**而不是图片：设计文档 §4.3 已定「只算位矩阵，用 GUI 逐格画方块」。
    /// 参考 Folia 是让它的 Node 服务端用 npm 的 qrcode 包生成 base64 PNG，我们没有服务端，
    /// 所以在本进程内编码；用 Net.Codecrete.QrCodeGenerator（零依赖、纯托管）而不是 QRCoder
    /// —— 后者在 net6.0+ 依赖 System.Drawing.Common，那是 Windows 专属包。
    /// </summary>
    internal sealed class NeteaseQrLoginFlow : ILoginFlow
    {
        /// <summary>
        /// 轮询间隔下限。服务端在扫码前会大量返回 801，间隔太密容易触发风控
        /// （实测有 HTTP 200 空 body 的静默限流），2 秒是参考实现与探针一致的节奏。
        /// </summary>
        private static readonly TimeSpan MinPollInterval = TimeSpan.FromSeconds(2);

        /// <summary>Unknown（限流空体或未建模码）时退避，避免风控窗口内空转。</summary>
        private static readonly TimeSpan MaxPollInterval = TimeSpan.FromSeconds(30);

        private readonly NeteaseClient _client;
        private readonly NeteaseProvider _owner;
        private readonly ILog _log;

        private string? _unikey;
        private string _instruction = "正在申请二维码…";
        private TimeSpan _interval = MinPollInterval;
        private DateTime _lastPollUtc = DateTime.MinValue;
        private LoginPollState _state = LoginPollState.Pending;

        public NeteaseQrLoginFlow(NeteaseClient client, NeteaseProvider owner, ILog log)
        {
            _client = client;
            _owner = owner;
            _log = log;
        }

        public string Instruction => _instruction;

        public bool[][]? QrMatrix { get; private set; }

        /// <summary>申请 unikey 并编码成矩阵。由 NeteaseProvider.BeginLoginAsync 调用一次。</summary>
        internal async Task StartAsync(CancellationToken ct)
        {
            try
            {
                _unikey = await _client.CreateQrKeyAsync(ct).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _instruction = $"申请二维码失败（{e.GetType().Name}），请稍后重试";
                _state = LoginPollState.Failed;
                return;
            }

            var content = NeteaseClient.BuildQrContent(_unikey);
            try
            {
                var qr = QrCode.EncodeText(content, QrCode.Ecc.Medium);
                QrMatrix = ToMatrix(qr);
                _instruction = "用手机 App 扫描二维码";
            }
            catch (Exception e)
            {
                // 编码失败不影响已拿到的 unikey：退化为「手动打开链接」也是可用的
                _log.Error($"[vscloudmusic] 二维码编码失败：{e.GetType().Name}");
                _instruction = "二维码生成失败，请改用其它方式登录";
                _state = LoginPollState.Failed;
            }
        }

        public async Task<LoginPollState> PollAsync(CancellationToken ct)
        {
            if (_state != LoginPollState.Pending) return _state;
            if (_unikey == null) return _state;

            // 服务端状态转换很快，但玩家扫码需要时间；本地节流避免把宿主的高频调用
            // 直接变成高频请求（那正是触发风控的原因）。
            var since = DateTime.UtcNow - _lastPollUtc;
            if (since < _interval) return LoginPollState.Pending;
            _lastPollUtc = DateTime.UtcNow;

            QrPollResult r;
            try
            {
                r = await _client.PollQrAsync(_unikey, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                _log.Warning($"[vscloudmusic] 二维码轮询异常（{e.GetType().Name}），继续等待");
                return LoginPollState.Pending;
            }

            switch (r.State)
            {
                case QrState.Confirmed:
                    _owner.OnLoginSucceeded(string.IsNullOrEmpty(r.Nickname) ? "（已登录）" : r.Nickname);
                    _state = LoginPollState.Succeeded;
                    _instruction = "登录成功";
                    break;

                case QrState.Scanned:
                    _interval = MinPollInterval;
                    _instruction = "已扫描，请在手机上确认";
                    break;

                case QrState.Expired:
                    _state = LoginPollState.Expired;
                    _instruction = "二维码已过期，请重新发起登录";
                    break;

                case QrState.Waiting:
                    _interval = MinPollInterval;
                    break;

                default:
                    // Unknown：解析失败（限流空体）或未建模码。退避重试，不当作失败 ——
                    // 实测限流是间歇性的，退避后能恢复。
                    _interval = _interval < MaxPollInterval
                        ? TimeSpan.FromTicks(Math.Min(_interval.Ticks * 2, MaxPollInterval.Ticks))
                        : MaxPollInterval;
                    break;
            }

            return _state;
        }

        /// <summary>把 QrCode 的模块转成 <c>[y][x]</c> 的矩形数组（与 API 契约一致）。</summary>
        private static bool[][] ToMatrix(QrCode qr)
        {
            var size = qr.Size;
            var matrix = new bool[size][];
            for (int y = 0; y < size; y++)
            {
                var row = new bool[size];
                for (int x = 0; x < size; x++) row[x] = qr.GetModule(x, y);
                matrix[y] = row;
            }
            return matrix;
        }
    }
}
