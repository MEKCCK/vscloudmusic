using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.Common;
using WyyPlayer.Audio;
using WyyPlayer.Core.Audio;
using WyyPlayer.Core.Playback;
using WyyPlayer.Core.Storage;
using WyyPlayer.Ui;
using WyyPlayer.Api;
using WyyPlayer.Core.Providers;
using System.Threading;

namespace WyyPlayer
{
    public class WyyPlayerMod : ModSystem
    {
        // 当前播放的缓存文件路径：首次由 StartPlayback 在「缓存命中或下载完成」后写入；
        // 循环重开复用同一路径（缓存文件在本会话内不变，不切歌）。
        private string? _currentPath;

        private ICoreClientAPI? _api;
        private long _tickListenerId;
        private OpenAlStreamer? _streamer;
        private Mp3Decoder? _decoder;
        // 当前播放的文件流由本模组持有并关闭：NLayer 的 MpegFile(Stream) 构造函数置
        // _closeStream=false，Mp3Decoder.Dispose() 不会关闭传入的流（字符串构造才传 true）。
        // 因此流必须登记到字段，随 Teardown 或下一轮重启释放，否则每轮循环泄漏一个文件描述符。
        private Stream? _audioStream;
        private readonly byte[] _pcmBuffer = new byte[16384];
        private bool _startedPlaying;
        private bool _eosReached;
        private DateTime _lastProgressUtc;
        // 上一次 OnTick 的墙钟时刻：用于识别「tick 根本没运行」的空档（见 OnTick 顶部）。
        private DateTime _lastTickUtc;
        private bool _tornDown;
        private DateTime _lastDrainDiag = DateTime.MinValue;
        /// <summary>上一次同步给 AL 的状态。只在它变化时才调 Pause/Resume —— 逐 tick 无条件调会
        /// 把自然播完后自转入 STOPPED 的源反复点回 PLAYING，导致队列永远排不空、无法自动切歌。</summary>
        private PlaybackStatus _lastSyncedStatus = PlaybackStatus.Idle;

        // 游戏自身音乐音量（设置键 musicLevel）。我们播放时压低到 0、停止后恢复原值，
        // 否则两首曲子叠在一起 —— 实测日志里游戏音乐（radianceandrust.ogg）与我们的
        // 音乐同时在响，听起来就是“炸”。
        // 用设置键而不是停轨道：ICoreClientAPI 只暴露了 CurrentMusicTrack，
        // 没有可用的音乐引擎本体，而且游戏随时会自己起下一首。
        private float _savedMusicLevel = -1f;
        private bool _musicLevelUnavailable;

        // 播放状态机的薄适配：UI 的请求经它翻译成状态迁移，实际 AL/文件动作仍在本类的
        // tick 循环里执行（保证主线程）。Task 3+ 的 UI 只跟它打交道，不触碰 OpenAlStreamer。
        private PlaybackController? _controller;
        private WyyPlayerConfig _config = new();

        // 右上角常驻播放状态 HUD：注册 ≠ 渲染（RegisterDialog 只把对话框插入 LoadedGuis，
        // 不打开；渲染只遍历 OpenedGuis 且以 opened 字段为门槛），因此 NowPlayingHud 在
        // 构造函数末尾自行 TryOpen（vanilla HudEntityNameTags 同款）。面板打开时仅清空文字。
        private NowPlayingHud? _nowPlayingHud;
        private CoverArtCache? _coverCache;

        private MusicPanelDialog? _panel;

        // 网络/下载与缓存的会话：只在启动时准备歌曲（后台线程），播放在主线程 tick 里。
        /// <summary>已注册的音乐源。附属模组通过 <see cref="RegisterProvider"/> 往里加。</summary>
        private readonly MusicProviderRegistry _providers = new();
        private NeteaseProvider? _netease;

        /// <summary>
        /// 当前队列是否由「电台」在喂。电台是**可持续续取**的流：
        /// 队列见底时宿主自动再取一批，而不是让玩家手动点。
        /// 只由面板在从电台列表点歌时置位；从搜索/歌单点歌会关掉它。
        /// </summary>
        private bool _radioFeed;
        /// <summary>电台续取是否进行中 —— 防止连续几首都处于「临近队尾」时重复发请求。</summary>
        private bool _radioFetching;
        /// <summary>电台提前量：剩余不足此数就开始续取，等真正需要时数据已经在了。</summary>
        private const int RadioPrefetchLookahead = 2;

        // 无进展看门狗：距上次「有新 PCM 入队」超过此值即强制释放。
        // 绝不按「播放时长」兜底 —— 循环播放下播放时长无上限，只有失速才是故障。
        // 健康的循环播放里，两次入队之间的最大间隔 ≤ 一个队列容量的播放时长
        // （16 块 × 16384B ≈ 1.5s）+ 一个 tick（20ms），60s 有约 40 倍余量；
        // 若队列彻底卡死（源不再消费）、解码器永久失败或循环重启失败，
        // 也在 60s 的「tick 实际运行时长」内保证释放 —— 保留 Task 4 评审要求的兜底，
        // 触发依据从「墙钟」改为「进展」、再从「墙钟进展」改为「tick 时长进展」。
        private static readonly TimeSpan WatchdogStall = TimeSpan.FromSeconds(60);

        // tick 之间墙钟间隔的「不可能阈值」：注册周期只有 20ms，正常（哪怕偶尔慢帧）
        // 的两次 OnTick 间隔也远小于 2s。超过 2s 只可能是客户端暂停、主线程长时间
        // 卡顿或系统休眠 —— 这段时间压根没有 tick 运行，看门狗不得把它算作失速。
        private static readonly TimeSpan WatchdogMaxTickGap = TimeSpan.FromSeconds(2);

        /// <summary>本模组是纯客户端（音频 + UI），在专用服务端加载毫无意义。</summary>
        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

        public override void StartClientSide(ICoreClientAPI api)
        {
            _api = api;
            Mod.Logger.Notification("[vscloudmusic] 模组已加载");

            OpenAlProbe.Run(api);
            if (!OpenAlProbe.LastSourceCreated)
            {
                Mod.Logger.Error("[vscloudmusic] OpenAL 不可用");
                return;
            }

            var dataDir = api.GetOrCreateDataPath("vscloudmusic");
            var credPath = Path.Combine(dataDir, "credentials.txt");
            var store = new CredentialStore(credPath);
            var cookie = store.Load();
            Mod.Logger.Notification(cookie == null
                ? "[vscloudmusic] 未找到已保存的凭据 —— 需要先登录"
                : "[vscloudmusic] 已加载已保存的凭据");

            // 内置音源：网易云。凭据/缓存目录沿用原来的位置，玩家无感迁移。
            _netease = new NeteaseProvider(dataDir, cookie, new GameLogAdapter(Mod.Logger));
            _providers.Register(_netease);
            _controller = new PlaybackController(Mod.Logger);
            // 设置持久化走官方的 LoadModConfig（重启后保留播放模式与 HUD 位置）。
            _config = WyyPlayerConfig.Load(api);
            _controller.Queue.Mode = _config.Mode;

            // 右上角状态 HUD：构造即完成组装 + TryOpen（vanilla HudEntityNameTags 模式）。
            // 不再显式 RegisterDialog —— TryOpen 内部在 !LoadedGuis.Contains(this) 时会先自动
            // 注册；RegisterDialog 本身只做 z-order 插入（不进入 OpenedGuis，不渲染），
            // 面板打开与否由 SetPanelOpen 控制。
            // 封面缓存：后台下载 + SkiaSharp 解码成 BitmapRef，HUD 只做绘制。
            // 与 HUD 同生命周期 —— HUD 一销毁就必须释放位图（非托管内存）。
            _coverCache = new CoverArtCache(api, _providers);

            // HUD 位置：默认右上角。HudClockPatch 把位置做成设置项，我们也允许玩家改，
            // 但不需要单独的配置文件 —— 直接读游戏设置里的 vscloudmusicHudPosition。
            _nowPlayingHud = new NowPlayingHud(api, _controller, _coverCache, _config.ResolveHudPosition());

            // 播放器面板：热键 L 开合（M 与世界地图冲突）。
            // ToggleKeyCombinationCode 在 GuiDialog 上是 abstract 只读属性，面板类里已 override，
            // 引擎据此自行处理开合 —— 不需要自己写按键回调。
            api.Input.RegisterHotKey(
                MusicPanelDialog.HotkeyCode, Lang.Get(MusicPanelDialog.HotkeyNameKey),
                MusicPanelDialog.HotkeyKey, HotkeyType.HelpAndOverlays,
                false, false, false);

            _panel = new MusicPanelDialog(api, _controller, _nowPlayingHud, _providers,
                (song, title) => ApplyRequest(song, title),
                delta => StepQueue(delta),
                radio => SetRadioFeed(radio),
                vol => { _config.Volume = vol; _config.Save(api); ApplyVolume(vol); },
                _config.Volume,
                mode => { _config.Mode = mode; _config.Save(api); });
            // 面板同样需要进入 OpenedGuis 才能被渲染；但面板由热键切换，
            // 不在启动时打开 —— 这里只注册，开合由引擎的 ToggleKeyCombinationCode 负责。
            api.Gui.RegisterDialog(_panel);

            // 不再自动播放：玩家一进世界不该突然响音乐，播放一律由 UI 请求触发。
            Mod.Logger.Notification(
                $"[vscloudmusic] 等待 UI 请求播放（不再自动播放）；按 {MusicPanelDialog.HotkeyKey} 打开播放器面板");

            _tickListenerId = api.Event.RegisterGameTickListener(OnTick, 20);
        }

        /// <summary>
        /// 供**附属模组**注册额外的音乐源（QQ 音乐等）。
        ///
        /// 用法（附属模组自己的 ModSystem.StartClientSide 里）：
        /// <code>
        /// var wyy = api.ModLoader.GetModSystem&lt;WyyPlayerMod&gt;();
        /// wyy.RegisterProvider(new MyQqMusicProvider());
        /// </code>
        /// 模组的发现与加载顺序由 VS 自己的模组系统负责（modinfo.json 的 dependencies），
        /// 不需要本模组做插件加载。第一个注册的音源自动成为活跃源。
        /// </summary>
        public void RegisterProvider(IMusicProvider provider)
        {
            if (provider == null) return;
            _providers.Register(provider);
            Mod.Logger.Notification($"[vscloudmusic] 已注册音乐源：{provider.Id}（{provider.DisplayName}）");
        }

        /// <summary>已注册的音乐源（只读）。供附属模组或 UI 查询。</summary>
        public MusicProviderRegistry Providers => _providers;

        /// <summary>
        /// UI 层请求播放的入口（Task 4/5/6 经此触发）：状态机先行 —— 忙（含暂停）时拒绝，
        /// 「暂停中切歌」必须先 RequestStop 再请求（Task 1 契约，拒绝是有意的）；
        /// 随后完整释放旧播放器、后台准备素材、回主线程开播。所有调用发生在主线程；
        /// 下载在后台；AL 动作只在主线程。
        /// </summary>
        public void ApplyRequest(SongKey song, string? title)
        {
            if (_controller == null) return;

            // 无条件先停。状态机在忙（Preparing/Playing/Paused）时会拒绝 TryBegin，而 UI 层
            // 从不会自己先调 RequestStop —— 结果是「播放中点另一首歌完全没反应」。
            // 实测日志里这个分支被连续刷了一屏（「忽略请求 1951343695」…）。
            // MarkStopped 是任意状态都合法的重置；下载中的过期完成事件由
            // NotifyFailed/StartPlayback 的曲目身份校验丢弃，不会串台。
            _controller.RequestStop();

            if (!_controller.RequestSong(song, title)) return;   // 理论不可达，保留作安全网

            // 切歌 = 先完整 Teardown 释放，再重建。绝不允许同时存在两个 OpenAlStreamer。
            Teardown();

            // 下载/缓存检查在后台，完成后回主线程开播（AL 调用必须只在主线程）。
            // 这是 fire-and-forget 任务：任何逃逸的异常都会成为「未观测任务异常」被静默吞掉
            // （零日志、模组悄悄不开播）。因此整段包进 try/catch —— 覆盖缓存命中路径
            // （_cache.Has/_cache.GetPath 在 PrepareAsync 内未包壳）、并发卸载时 _songPlayer
            // 释放/null 化的竞态，以及引擎事件 API 已释放的晚到完成。
            _ = Task.Run(async () =>
            {
                try
                {
                    var player = _netease;
                    if (player == null)
                    {
                        // 任务起步前模组已卸载（Dispose 已把字段置 null）：不触碰任何状态，安静返回。
                        return;
                    }

                    var path = await player.PrepareLocalFileAsync(song, AudioQuality.ExHigh, CancellationToken.None).ConfigureAwait(false);
                    if (path == null)
                    {
                        // 取不到素材（未登录/VIP 限制/已下架/网络失败，SongPlayer 已记录原因）：
                        // 状态机回到 Failed，让「再试一次」成为可能，不再永久卡死。
                        // 身份校验：投递到主线程时用户可能已停止并请求新歌，旧歌的失败不得
                        // 污染新歌的 Preparing/Playing（NotifyFailed 内的 CurrentSongId 同源守卫）。
                        EnqueueMainThread(
                            () => _controller?.NotifyFailed(Lang.Get("vscloudmusic:cannot-load-audio"), song),
                            "vscloudmusic-failed");
                        return;
                    }

                    EnqueueMainThread(() => StartPlayback(path, song), "vscloudmusic-start");
                }
                catch (Exception e)
                {
                    // 能到达此 catch 的异常只有缓存命中 IO、_songPlayer 释放竞态（NRE）、
                    // 引擎事件 API 已释放 —— 均不含凭据内容，仅记类型与消息。凭据永不进日志。
                    Mod.Logger.Error(
                        $"[vscloudmusic] 后台准备失败（{e.GetType().Name}: {e.Message}），放弃开播");
                    // 同上：背景任务投递的失败必须携带自身曲目身份，防止陈旧失败污染新歌。
                    EnqueueMainThread(() => _controller?.NotifyFailed("后台准备失败", song), "vscloudmusic-failed");
                }
            });
        }

        /// <summary>
        /// 回主线程执行的统一入口。用 _api 字段而非 StartClientSide 的参数快照：
        /// Dispose() 会把 _api 置 null，判空即识别「准备完成后游戏已卸载」的晚到完成，
        /// 不再 EnqueueMainThreadTask（StartPlayback 内的守卫是第二道保险）。
        /// </summary>
        private void EnqueueMainThread(Action action, string name)
        {
            var api = _api;
            if (api != null) api.Event.EnqueueMainThreadTask(action, name);
        }

        /// <summary>主线程：从缓存文件建立解码器与播放器，沿用已验证的释放/看门狗机制。</summary>
        private void StartPlayback(string? path, SongKey song)
        {
            if (_api == null) return;                 // 卸载后的晚到完成：不触碰已释放的引擎事件 API
            if (_tornDown) _tornDown = false;         // 修复：失败/停止后开始新歌时复位，不再永久卡死

            // 防串台：机器已不在「这首歌的准备中」时（被停止、或在准备期间又切了别的歌），
            // 丢弃本次完成，绝不覆盖正在播放的硬件 —— 否则会同时存在两个解码器 / 两个
            // OpenAlStreamer（全游戏仅 1 个立体声源）。
            if (_controller == null
                || _controller.Status != PlaybackStatus.Preparing
                || _controller.CurrentSong != song)
            {
                Mod.Logger.Notification("[vscloudmusic] 丢弃晚到的开播请求（播放状态已变化）");
                return;
            }

            if (path == null)
            {
                Mod.Logger.Error("[vscloudmusic] 无素材可播，放弃");
                FailPlayback("无素材可播");
                return;
            }

            // 登记为当前播放路径：RestartForLoop 在文件播完后据此重开缓存文件（不切歌）。
            _currentPath = path;

            try
            {
                _audioStream = File.OpenRead(path);
                _decoder = new Mp3Decoder(_audioStream);
                // OpenAlStreamer 构造也纳入 try（既有缺陷修复：构造失败会泄漏已打开的解码器
                // 与文件流，且看门狗因 _streamer == null 永不触发）。
                _streamer = new OpenAlStreamer(_decoder.SampleRate, _decoder.Channels, Mod.Logger);
            }
            catch (Exception e)
            {
                Mod.Logger.Error($"[vscloudmusic] 打开缓存/创建播放器失败（{e.GetType().Name}）");
                FailPlayback("打开缓存或创建播放器失败");   // Teardown 关闭已登记的解码器与文件流
                return;
            }

            // 新一首歌：进度归零，否则进度条会从上一首的位置开始。
            _streamer.ResetProgress();
            _streamer.SetVolume(Math.Clamp(_config.Volume / 100f, 0f, 1f));
 
            // 新一轮播放的起始状态复位：否则上一轮遗留的 _startedPlaying/_eosReached 会在
            // 首个 tick 就把全新解码器误判为「已放完」，白做一次循环重启（这就是
            // 既有缺陷「_tornDown 不可复位」的同一根源：一切状态都随新一轮归零）。
            _startedPlaying = false;
            _eosReached = false;
            _lastProgressUtc = DateTime.UtcNow;
            _lastTickUtc = DateTime.UtcNow;

            _controller.NotifyPlaying();   // Preparing → Playing（WantsLoop 由此为真）

            // 唯一有权威的「这首歌真的开始放了」的时刻 —— 把队列游标对齐过来。
            // 不能依赖面板灌队列时算出的索引：在队列视图里点歌走的是直通路径，
            // 游标不会更新，于是「下一首」会跳到错误位置（Folia 用反查索引避免这一点）。
            _controller.Queue.SyncTo(song);

            Mod.Logger.Notification(
                $"[vscloudmusic] 开始播放：采样率={_decoder.SampleRate} 声道={_decoder.Channels}，" +
                $"时长={_decoder.Duration.TotalSeconds:0}s");
        }

        /// <summary>
        /// 主线程回调：解码并填充 AL 缓冲队列。
        /// 所有 AL 调用都只发生在这里，保证线程一致。
        /// </summary>
        private void OnTick(float dt)
        {
            // 看门狗计时只看「tick 实际运行」的时间。客户端暂停（或主线程长时间卡顿、
            // 系统休眠）期间 tick 不运行，恢复后的首个 tick 会观测到远大于注册周期
            // (20ms) 的墙钟缺口 —— 这段「根本没在 tick」的时间不算失速：把
            // _lastProgressUtc 同步前移缺口量，看门狗测的便是 tick 时长而非墙钟时长。
            // dt 是引擎提供的且可能被钳制，因此用本模组自己记录的两次 OnTick 墙钟间隔
            // 来判断，不依赖 dt 的数值。
            var now = DateTime.UtcNow;
            if (_lastTickUtc != default && now - _lastTickUtc > WatchdogMaxTickGap)
            {
                _lastProgressUtc += now - _lastTickUtc;
            }
            _lastTickUtc = now;

            if (_controller == null || _streamer == null || _decoder == null) return;

            // 暂停/继续、进度同步：这两件事只在主线程做，与 AL 调用同一线程。
            SyncPlaybackState();

            // 电台补货：每 tick 检查一次。判据是「从当前曲目往后已经没有下一首」，
            // 也就是玩家按下一首会走到队尾、或一首播完需要自动往下切的那一刻 ——
            // 与网易云官方客户端一致（它也是到那时才补一批）。
            // 电台接口每次只给 3 首，所以这一步是持续播放的前提。
            MaybeRefillRadio();

            // 是否重播由状态机决定（WantsLoop 仅在 Playing 为真）：上一轮确实放完
            // （已开始播放、解码到流末尾、AL 队列排空、源停止）→ 状态机要求重播则
            // 重建解码器进入下一轮（streamer——唯一的 AL source——保持不变）；
            // 不要求重播则整体释放，状态回到可接受新请求的 Stopped。
            if (_startedPlaying && _eosReached
                && _streamer.QueuedCount == 0 && !_streamer.IsPlaying)
            {
                if (!_controller.WantsLoop)
                {
                    // 暂停/停止等非 Playing 状态下队列排空 ≠ 自然播完（暂停时队列也会排空，
                    // 这是区分二者的关键）。用 NotifyStopped（MarkStopped，无条件且明确定义）
                    // 而非 MarkEnded —— MarkEnded 只在 Playing 有效，按 Task 1 契约在此不可用。
                    Teardown();
                    _controller.NotifyStopped();
                    return;   // 硬件已释放，本 tick 剩余逻辑必须跳过（_streamer 已为 null）
                }

                // 自然播完：不再无脑重播同一文件，交给**播放队列**决定下一首。
                if (!AdvanceQueue()) return;   // 已在方法内处理完（重建/切歌/停止）
            }

            // 填满队列直到 满/流结束/错误；循环次数受 queueSize 上限约束，不会失控。
            // 流结束后不再填充：避免每释放一个槽位就重跑 GenBuffer + 探测 EOF。
            if (!_eosReached)
            {
                try
                {
                    while (true)
                    {
                        var result = _streamer.QueueChunkSafe(_decoder, _pcmBuffer);
                        if (result == QueueChunkResult.Queued)
                        {
                            _startedPlaying = true;
                            // 看门狗：只有「新 PCM 成功入队」才算实质进展，重置失速时限。
                            // QueueFull/EndOfStream/Error 一律不重置 —— 一个卡死的队列会
                            // 一直返回 QueueFull，若因此无限续命，看门狗就形同虚设。
                            _lastProgressUtc = DateTime.UtcNow;
                            continue;
                        }
                        if (result == QueueChunkResult.EndOfStream)
                        {
                            _eosReached = true;
                            Mod.Logger.Notification("[vscloudmusic] 解码器已到流末尾");
                        }
                        break;
                    }
                }
                catch (Exception e)
                {
                    // 解码/入队抛异常（如坏文件）：不重试、不空转，干净释放，状态机置 Failed。
                    Mod.Logger.Error(
                        $"[vscloudmusic] 解码/入队异常（{e.GetType().Name}: {e.Message}），整体释放");
                    FailPlayback("解码或入队异常");
                    return;
                }
            }

            _streamer.Pump();

            // 临时诊断：流已结束时，每秒打一次四个推进条件，定位为何不自动切下一首。
            // （静态阅读已排除了看门狗、MarkEnded、状态机等，只剩这四个值的实际取值未知。）
            if (_eosReached && _startedPlaying
                && DateTime.UtcNow - _lastDrainDiag >= TimeSpan.FromSeconds(1))
            {
                _lastDrainDiag = DateTime.UtcNow;
                Mod.Logger.Notification(
                    $"[vscloudmusic-drain] 队列={_streamer.QueuedCount} 正在播放={_streamer.IsPlaying} " +
                    $"状态={_controller.Status} WantsLoop={_controller.WantsLoop}");
            }

            // 无进展看门狗：不是「播放时长上限」，而是「tick 运行时长的失速上限」。
            // 只要循环播放健康，每一轮都会至少入队一块 PCM 并重置时限，永不触发；
            // 只有队列卡死（源不再消费缓冲）、解码器永久失败或循环重启失败时，
            // 才会在 tick 持续运行的情况下长时间没有任何块入队 —— 此时保证释放。
            // 暂停等「tick 不运行」的空档已在 OnTick 顶部扣除，不会误触发。
            if (!_tornDown && DateTime.UtcNow - _lastProgressUtc >= WatchdogStall)
            {
                Mod.Logger.Warning(
                    $"[vscloudmusic] 播放无进展看门狗触发：{WatchdogStall.TotalSeconds:0}s 没有任何新 PCM 入队" +
                    "（解码或 AL 停滞），强制释放");
                FailPlayback("播放无进展（看门狗）");
            }
        }




        /// <summary>
        /// 播放期间把游戏自身音乐静音。基于设置键 musicLevel（与游戏自己的音乐滑条同一个键）。
        /// 只在本模组确实在播/暂停时生效，且在任何释放路径与 Dispose 里恢复原值 ——
        /// 改的是玩家的持久化设置，必须复原，否则玩家下次启动会发现游戏音乐莫名没了。
        /// </summary>
        private void SuppressGameMusic(bool suppress)
        {
            if (_musicLevelUnavailable || _api == null) return;

            try
            {
                var floats = _api.Settings.Float;
                if (suppress)
                {
                    if (_savedMusicLevel < 0f)
                    {
                        _savedMusicLevel = floats["musicLevel"];
                        Mod.Logger.Notification(
                            $"[vscloudmusic] 播放期间静音游戏音乐（原值 {_savedMusicLevel:0.00}）");
                    }
                    if (floats["musicLevel"] != 0f) floats["musicLevel"] = 0f;
                }
                else if (_savedMusicLevel >= 0f)
                {
                    floats["musicLevel"] = _savedMusicLevel;
                    Mod.Logger.Notification(
                        $"[vscloudmusic] 已恢复游戏音乐音量（{_savedMusicLevel:0.00}）");
                    _savedMusicLevel = -1f;
                }
            }
            catch (Exception e)
            {
                // 键不存在/类型不符（如整数而非浮点）：只告警一次，之后不再尝试，
                // 绝不因此影响播放本身。
                _musicLevelUnavailable = true;
                Mod.Logger.Warning(
                    $"[vscloudmusic] 无法通过 musicLevel 静音游戏音乐（{e.GetType().Name}），" +
                    "请手动把游戏音乐音量调到 0");
            }
        }

        /// <summary>
        /// 把控制器的状态同步到播放器：
        /// 1) 暂停/继续 —— 用 AL 的源级暂停，**不能用 Stop()**（那会清空队列，变成「停止」而非「暂停」）；
        /// 2) 进度 —— 由已播字节数估算秒数推给 UI（VBR 下会有偏差，进度条不要求帧级精确）。
        /// 本方法只在主线程（OnTick）被调用，与所有 AL 调用同一线程。
        /// </summary>
        private void SyncPlaybackState()
        {
            if (_controller == null || _streamer == null || _decoder == null) return;

            // 只在**状态发生变化**时驱动 AL，绝不逐 tick 无条件调用。
            //
            // 这里曾经是 `case Playing: _streamer.Resume(); break;` —— 致命的：
            // 最后一块 PCM 播完时 OpenAL 会把源置为 STOPPED，而状态机的 Status 仍是
            // Playing（没有任何人把自然播完回写成 Stopped）。于是下一个 tick（20ms 后）
            // 这里的 Resume() 又把源点回 PLAYING（反复重播最后那块约 92ms 的缓冲）。
            // 后果是 IsPlaying 永远为 true、缓冲永远排不空，而紧随其后的推进判断
            // 要求「QueuedCount==0 && !IsPlaying」—— 因此一首放完后停在那里不切下一首。
            //
            // 实测诊断（每秒一次）持续 30s 恒为：队列=1 正在播放=True 状态=Playing。
            // 改为转换驱动后，源能真正走到 STOPPED，推进判断才可能成立。
            var status = _controller.Status;
            if (status != _lastSyncedStatus)
            {
                switch (status)
                {
                    case PlaybackStatus.Paused:
                        _streamer.Pause();
                        break;
                    case PlaybackStatus.Playing:
                        // 仅从「暂停」恢复时才需要 Resume。新一首歌的开播由
                        // OpenAlStreamer.QueueChunkSafe 在首块入队时自行 SourcePlay，
                        // 这里再插一脚反而会在无缓冲时把源点亮。
                        if (_lastSyncedStatus == PlaybackStatus.Paused) _streamer.Resume();
                        break;
                }
                _lastSyncedStatus = status;
            }

            // 我们确实在播（或暂停）时静音游戏自身音乐，否则两首叠在一起。
            var active = status == PlaybackStatus.Playing || status == PlaybackStatus.Paused;
            SuppressGameMusic(active);

            // 每秒字节数 = 采样率 × 声道数 × 2（16 位 PCM）
            var bytesPerSecond = (double)_decoder.SampleRate * _decoder.Channels * 2;
            _controller.PlayedSeconds = bytesPerSecond > 0
                ? _streamer.PlayedBytes / bytesPerSecond
                : 0;
            _controller.TotalSeconds = _decoder.Duration.TotalSeconds;
        }

        /// <summary>
        /// 自然播完一首后的推进。由播放队列决定下一首：
        /// - 单曲循环 → 重开同一文件（走已验证的 RestartForLoop，不发网络请求）
        /// - 顺序/随机 → 取下一首；到末尾则停止
        /// - （曾经的「心动模式」已移除：它是单个音源的能力，不适合做成全局模式）
        ///
        /// 返回值：true = 已安排好（调用方可继续本 tick），false = 已释放或已交后台，调用方必须立即返回。
        /// </summary>
        /// <summary>
        /// 由面板告知「当前播放是否来自电台列表」。电台开启后，队列见底会自动续取。
        /// </summary>
        /// <summary>把配置里的音量（0-100）下发给正在播放的流式播放器。</summary>
        private void ApplyVolume(int percent)
        {
            var gain = Math.Clamp(percent / 100f, 0f, 1f);
            _streamer?.SetVolume(gain);
            Mod.Logger.Notification($"[vscloudmusic] 音量设为 {percent}%（AL 增益 {gain:0.00}）");
        }

        public void SetRadioFeed(bool on)
        {
            _radioFeed = on;
            if (!on) _radioFetching = false;
        }

        /// <summary>
        /// 电台队列见底时补货。tick 与 AdvanceQueue 共用同一判据，避免两处各写一套。
        /// </summary>
        private void MaybeRefillRadio()
        {
            var controller = _controller;
            if (controller == null || !_radioFeed) return;
            // 还有下一首就不补 —— 到队尾才补，与官方客户端的行为一致。
            if (controller.Queue.RemainingAfterCurrent > 0) return;
            StartRadioRefill(advanceAfter: true);
        }

        /// <summary>
        /// 电台续取：后台再取一批 FM 歌曲并 **Append 到队尾**，全程不释放正在播放的硬件。
        /// 这样队列见底时数据已经在手里，听不到断点；等放完再取会先释放硬件、
        /// 再等一次网络往返，中间必然留一段静默。
        /// </summary>
        private void StartRadioRefill(bool advanceAfter = false)
        {
            var provider = _providers.Active;
            if (provider == null || (provider.Capabilities & ProviderCapabilities.Radio) == 0) return;
            if (_radioFetching) return;
            _radioFetching = true;

            _ = Task.Run(async () =>
            {
                System.Collections.Generic.IReadOnlyList<SongInfo> songs;
                try { songs = await provider.GetRadioSongsAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception e)
                {
                    Mod.Logger.Warning($"[vscloudmusic] 电台续取失败（{e.GetType().Name}）");
                    EnqueueMainThread(() => _radioFetching = false, "vscloudmusic-radio-retry");
                    return;
                }

                EnqueueMainThread(() =>
                {
                    _radioFetching = false;
                    if (_controller == null) return;

                    if (songs.Count == 0)
                    {
                        // 取不到就如实停止，不空转重试。
                        Mod.Logger.Warning("[vscloudmusic] 电台没有取到新歌，停止播放");
                        Teardown();
                        _controller.NotifyStopped();
                        return;
                    }

                    var keys = new System.Collections.Generic.List<SongKey>(songs.Count);
                    var titles = new System.Collections.Generic.List<(SongKey, string)>(songs.Count);
                    foreach (var song in songs)
                    {
                        keys.Add(song.Key);
                        titles.Add((song.Key, song.ToString()));
                    }
                    _controller.Queue.Append(keys);
                    _controller.RememberTitles(titles);
                    Mod.Logger.Notification(
                        $"[vscloudmusic] 电台续取 {songs.Count} 首（余 {_controller.Queue.RemainingAfterCurrent} 首）");

                    // 同步给面板，否则界面上的电台列表永远停在最初那批。
                    _panel?.AppendRadioSongs(songs);
                }, "vscloudmusic-radio-refill");
            });
        }

        /// <summary>
        /// 手动切歌（面板的「上一首/下一首」）。
        ///
        /// **必须走队列**，不能像面板原先那样用自己的列表索引 +1/-1 ——
        /// 「下一首是谁」是由播放模式决定的：顺序是列表下一首、随机是未播过的随机一首、
        /// 面板那份索引只表示「玩家点了列表里的哪一首」，
        /// 与播放顺序无关。
        /// </summary>
        public void StepQueue(int delta)
        {
            var controller = _controller;
            var queue = controller?.Queue;
            if (controller == null || queue == null) return;

            var target = delta > 0 ? queue.Next() : queue.Previous();

            if (target != null)
            {
                Mod.Logger.Notification($"[vscloudmusic] 手动切歌（{queue.Mode}，{delta:+0;-0}）→ {target.Value}");
                ApplyRequest(target.Value, controller.TitleFor(target.Value));
                return;
            }

            // 队尾按「下一首」：电台模式下补一批，补完接着播 ——
            // 否则玩家按了没反应，得再按一次（实测反馈）。
            if (delta > 0 && _radioFeed)
            {
                Mod.Logger.Notification("[vscloudmusic] 电台队尾按下一首，补货后续播");
                StartRadioRefill(advanceAfter: true);
            }
        }

        private bool AdvanceQueue()
        {
            if (_controller == null) return false;
            var queue = _controller.Queue;

            // 单曲循环：不重建下载，直接重开缓存文件（已验证路径，无网络）
            if (queue.Mode == PlayMode.RepeatOne)
                return RestartForLoop();

            // 电台：临近队尾就提前续取（与 tick 里的 MaybeRefillRadio 同一判据）。
            MaybeRefillRadio();

            var next = queue.Next();
            if (next != null)
            {
                Mod.Logger.Notification($"[vscloudmusic] 队列推进到 {next.Value}（{queue.Mode}）");
                ApplyRequest(next.Value, _controller.TitleFor(next.Value));
                return false;   // 已转交后台准备 + 主线程重开，本 tick 结束
            }

            if (_radioFeed)
            {
                // 队尾且电台开启：续取已在上面发起，但可能还没回来。
                // 这里再要一批（若已有请求在飞则直接返回），回来后由续取逻辑接上；
                // 若续取失败会自行收敛到停止，不会静默卡住。
                StartRadioRefill();
                return false;
            }

            Mod.Logger.Notification($"[vscloudmusic] 播放列表已到末尾（{queue.Mode}），停止");
            Teardown();
            _controller.NotifyStopped();
            return false;
        }


        private bool RestartForLoop()
        {
            // 先释放上一轮：解码器 + 其文件流，避免循环期间累积未关闭的文件描述符与内部缓冲。
            // 顺序与 Teardown 一致：解码器在前、文件流在后（二者 Dispose 均幂等，见 Teardown 说明）。
            _decoder?.Dispose();
            _decoder = null;
            _audioStream?.Dispose();
            _audioStream = null;

            Mp3Decoder replacement;
            try
            {
                var currentPath = _currentPath;
                if (currentPath == null)
                {
                    Mod.Logger.Error("[vscloudmusic] 循环重启失败：当前播放路径未初始化，整体释放");
                    FailPlayback("循环重启失败：当前播放路径未初始化");
                    return false;
                }

                // 打开即登记到字段：即使 new Mp3Decoder 抛异常（Minor #4），
                // 已打开的文件流也在字段上、由 Teardown 一并关闭，绝不泄漏。
                _audioStream = File.OpenRead(currentPath);
                replacement = new Mp3Decoder(_audioStream);
            }
            catch (Exception e)
            {
                Mod.Logger.Error(
                    $"[vscloudmusic] 循环重启失败：无法打开缓存文件（{e.GetType().Name}: {e.Message}），整体释放");
                FailPlayback("循环重启失败：无法打开缓存文件");   // _audioStream 已登记在字段上，由 Teardown 关闭
                return false;
            }

            // 立刻验证新解码器能产出数据：产不出就整体释放，绝不让一个坏解码器在
            // 下一轮里反复入队失败、空洞轮转。探测消耗的那一小块 PCM 不会入队
            // （≤ 一个 16KB 块，单次循环损失可忽略，且每轮重新打开文件，不累积）。
            try
            {
                if (!replacement.TryReadChunk(_pcmBuffer, out _))
                {
                    Mod.Logger.Error(
                        "[vscloudmusic] 循环重启失败：新解码器无数据可产出，整体释放");
                    replacement.Dispose();   // 未登记的替换解码器在此显式释放
                    FailPlayback("循环重启失败：新解码器无数据可产出");   // 已登记的文件流由 Teardown 关闭
                    return false;
                }
            }
            catch (Exception e)
            {
                Mod.Logger.Error(
                    $"[vscloudmusic] 循环重启失败：新解码器读取异常（{e.GetType().Name}: {e.Message}），整体释放");
                replacement.Dispose();   // 未登记的替换解码器在此显式释放
                FailPlayback("循环重启失败：新解码器读取异常");   // 已登记的文件流由 Teardown 关闭
                return false;
            }

            _decoder = replacement;
            _eosReached = false;
            Mod.Logger.Notification("[vscloudmusic] 循环播放");
            return true;
        }

        /// <summary>播放失败：整体释放硬件，并把状态机置为 Failed —— 之后仍可再次请求新歌（不再卡死）。</summary>
        private void FailPlayback(string reason)
        {
            Teardown();
            // 主线程同步失败（无素材/打开失败/解码异常/看门狗/循环重启）只属于「当前曲目」：
            // 身份取状态机当前值，NotifyFailed 的同源校验恒等通过，行为不变；该校验真正拦截
            // 的是后台任务投递下来的陈旧失败（见 ApplyRequest 的两个入口）。
            if (_controller is { } c && c.CurrentSong is { } song) c.NotifyFailed(reason, song);
        }

        /// <summary>释放解码器与播放器。结束路径与 Dispose 共用，杜绝单一路径失败造成的资源泄漏。</summary>
        private void Teardown()
        {
            if (_tornDown) return;

            // 释放顺序：解码器在前、文件流在后。安全依据：
            // 1) Mp3Decoder.Dispose() 幂等（_disposed 守卫），且其内部 NLayer 的 MpegFile(Stream)
            //    _closeStream=false，Dispose() 不会替我们关闭底层流 —— 流必须由本模组关闭；
            // 2) FileStream.Dispose() 幂等，重复调用是无害空操作、不会抛异常。
            // 因此「先解析器、后文件」既不会双释放也不会抛出，顺序可安全重复（_tornDown 防重入）。
            Mod.Logger.Notification("[vscloudmusic] 释放解码器、文件流与流式播放器");
            // 释放前先把游戏音乐音量还回去：这一步必须在任何提前返回之前。
            SuppressGameMusic(false);
            _decoder?.Dispose();
            _decoder = null;
            _audioStream?.Dispose();
            _audioStream = null;
            _streamer?.Dispose();
            _streamer = null;
            _tornDown = true;
        }

        public override void Dispose()
        {
            // 游戏卸载/退出：无条件注销 tick 监听，绝不让监听器与播放资源泄漏到进程结束。
            _api?.Event.UnregisterGameTickListener(_tickListenerId);
            _api = null;

            Teardown();               // 解码器 → 文件流 → OpenAlStreamer
            _netease?.Dispose();   // NeteaseHttp + SongCache
            _netease = null;
            // 显式释放 HUD：IGuiAPI 没有 UnregisterDialog，GuiDialog 的清理路径只有
            // Dispose()（Task 4 的面板同样在 base.Dispose() 之前释放）。幂等：再进 Dispose 无害。
            _nowPlayingHud?.Dispose();
            _nowPlayingHud = null;
            // 面板与 HUD 同理：IGuiAPI 没有 UnregisterDialog，Dispose() 是唯一清理路径。
            _panel?.Dispose();
            _panel = null;

            // HUD 持有 BitmapRef（非托管位图），必须随模组一起释放
            _nowPlayingHud?.Dispose();
            _nowPlayingHud = null;
            _coverCache?.Dispose();
            _coverCache = null;
            base.Dispose();
        }
    }
}