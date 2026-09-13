using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using WyyPlayer.Core.Net.Models;
using WyyPlayer.Core.Playback;
using WyyPlayer.Api;
using WyyPlayer.Core.Providers;
using System.Threading;
using Cairo;

namespace WyyPlayer.Ui
{
    /// <summary>
    /// 播放器面板：搜索、点选播放、播放控制、进度显示。
    ///
    /// 生命周期要点（已核实）：
    /// - GuiDialog.ToggleKeyCombinationCode 是 **abstract 只读属性**，必须 override，
    ///   绝不能在构造函数里赋值。引擎据此处理热键开合。
    /// - GuiDialog **没有** 任何 Compose* 虚方法：组装在构造函数里做，赋值给 SingleComposer。
    /// - 线程约定：本类的所有 composer 操作都在主线程（GUI 回调本来就是主线程）；
    ///   搜索必须在后台线程发起，结果经 EnqueueMainThreadTask 回主线程才渲染。
    /// - UI 只与 PlaybackController 和 SongPlayer 对话，绝不触碰 OpenAlStreamer。
    /// </summary>
    internal sealed class MusicPanelDialog : GuiDialog
    {
        public const string HotkeyCode = "vscloudmusicpanel";
        /// <summary>热键在设置界面里显示的名字。存语言键，注册时再 Lang.Get（照官方写法：
        /// capi.Input.RegisterHotKey(code, Lang.Get("..."), GlKeys.X, ...)）。</summary>
        public const string HotkeyNameKey = "vscloudmusic:hotkey-open-panel";

        // 热键用 L 而不是 M：原版 M 是世界地图、Ctrl+M 是宏编辑器，都会冲突。
        // 注册后可在「设置 → 控制」里自行改键。
        public const GlKeys HotkeyKey = GlKeys.L;

        private const string LinkPrefix = "song://";
        /// <summary>每页结果数。原来写死 12 且 offset 恒为 0，所以永远只能搜出 12 首。</summary>
        private const int MaxResults = 50;

        /// <summary>结果区可见高度（像素）。内容高于它即出现滚动条。</summary>
        private const int ResultsViewHeight = 280;

        /// <summary>
        /// 结果区在面板内的基准 Y（与 Compose 里的 Fixed(10, ResultViewY, ...) 必须一致）。
        ///
        /// 滚动时要把 richtext 平移到「基准 - value」，**不是**「0 - value」：
        /// 后者会把它提到 y=0，比裁剪窗口高出一整个基准 Y，顶部若干行因此被挡在窗外
        /// （实测表现为「歌单从 4 号才开始显示」—— 70px ÷ 每行约 20px ≈ 3.5 行）。
        /// 官方的 GuiDialogStoryGenFailed 写的是 fixedY = 10 - value，那个 10 是它自己的
        /// 内边距补偿，不能直接照抄。
        /// </summary>
        /// <summary>
        /// 结果区在面板内的基准 Y（与 Compose 里的 Fixed(10, ResultViewY, ...) 必须一致）。
        /// </summary>
        private const int ResultViewY = 70;

        /// <summary>
        /// 二维码区域顶部需要让出的高度：**一行说明文字**。
        ///
        /// 登录时结果区顶行显示「请扫描二维码」之类的说明，二维码与它共用同一块区域。
        /// 取值 = 标题行高本身，因此二维码上沿**精确贴合标题下沿**（不留空隙）；
        /// 行高从字体量取，换字号时不会失准。
        /// </summary>
        private static readonly int QrTopInset =
            (int)Math.Round(CairoFont.WhiteSmallText().GetFontExtents().Height);

        private readonly ICoreClientAPI _capi;
        private readonly PlaybackController _controller;
        private readonly NowPlayingHud _hud;
        private readonly MusicProviderRegistry _providers;
        private readonly Action<SongKey, string?> _onPlayRequested;
        private readonly Action<int> _onStepRequested;
        private readonly Action<bool> _onRadioFeed;
        private readonly Action<int> _onVolumeChanged;
        private readonly Action<PlayMode> _onModeChanged;

        private readonly List<SongInfo> _results = new();
        private int _index = -1;
        private string _keyword = "";
        private bool _searching;
        /// <summary>当前列表区显示的是什么 —— 点击链接时要按这个解释 href。</summary>
        private enum ListView { Search, Queue, Playlists, Providers, Recommend, Radio }
        private ListView _view = ListView.Search;
        /// <summary>上次渲染时标为「正在播放」的曲目 id —— 变了才重绘列表。</summary>
        private SongKey? _lastMarkedSong;
        /// <summary>列表为空时显示的话术，切歌重绘时要原样复用。</summary>
        private string _resultsEmptyMessage = "没有内容";
        private readonly List<CollectionInfo> _playlists = new();

        /// <summary>
        /// 推荐/电台已取到的列表缓存。
        /// 没有它时每次切回该视图都会重新请求，玩家会看到「刚听的列表没了、换了一批」
        /// （实测反馈）。缓存后切回只是重新渲染，内容不变。
        /// </summary>
        private readonly List<SongInfo> _recommendCache = new();
        private readonly List<SongInfo> _radioCache = new();

        /// <summary>
        /// 当前播放是否来自电台列表（与告知模组的电台续取状态同源）。
        /// 用于判断切回电台视图时该沿用旧列表还是轮换新的一批。
        /// </summary>
        private bool _radioFeedActive;

        public override string ToggleKeyCombinationCode => HotkeyCode;

        public MusicPanelDialog(ICoreClientAPI capi, PlaybackController controller,
            NowPlayingHud hud, MusicProviderRegistry providers, Action<SongKey, string?> onPlayRequested,
            Action<int> onStepRequested, Action<bool> onRadioFeed,
            Action<int> onVolumeChanged, int initialVolume,
            Action<PlayMode> onModeChanged)
            : base(capi)
        {
            _capi = capi;
            _controller = controller;
            _hud = hud;
            _providers = providers;
            _onPlayRequested = onPlayRequested;
            _onStepRequested = onStepRequested;
            _onRadioFeed = onRadioFeed;
            _onVolumeChanged = onVolumeChanged;
            _onModeChanged = onModeChanged;

            // 完全照搬官方模组的对话框写法（refs/vsessentialsmod/Gui/GuiDialogLogViewer.cs
            // 与 refs/vssurvivalmod/Gui/GuiDialogBlockEntityCommand.cs）。三个关键步骤一个都不能少：
            //
            //   1. 背景用 ElementBounds.Fill，但**必须**设 BothSizing = FitToChildren
            //      并把所有子元素 WithChildren(...) 登记进去；
            //   2. 根的尺寸由 ElementStdBounds.AutosizedMainDialog 推出；
            //   3. 所有元素必须写在 BeginChildElements(bgBounds) / EndChildElements() 之间。
            //
            // 之前漏掉这三步（给了 Fill 背景、却没 FitToChildren/WithChildren/BeginChildElements），
            // autosize 的根配一个没有子元素的 Fill 背景会让布局算出畸形尺寸，
            // 客户端在加载阶段直接原生崩溃（stderr: malloc(): corrupted top size）。
            // 当时误判成「1.22.7 不支持这套 API」而撤回，实际是自己没照原版写。
            var headerBounds   = ElementBounds.Fixed(10, 10, 540, 22);
            var searchBounds   = ElementBounds.Fixed(10, 36, 356, 28);
            var btnSearchBnd   = ElementBounds.Fixed(372, 36, 70, 28);
            var btnMoreBounds  = ElementBounds.Fixed(448, 36, 70, 28);
            // 结果区做成可滚动：内容 bounds 会被上下平移，所以必须套一层固定不动的裁剪窗口，
            // 否则长列表会画到面板外面去。滚动条单独占右侧一条。
            // 照 refs/vssurvivalmod/Gui/GuiDialogJournal.cs 的做法（容器 + BeginClip + 滚动条）。
            // 注意：位于 BeginClip 内的元素是**相对裁剪容器**定位的，而裁剪容器自身
            // 已经落在 ResultViewY 上。若这里再写 ResultViewY，就会被叠加一次 ——
            // 实测富文本比滚动条低了整整 70px（= 3 行 × 23px），表现为
            // 「首行与滚动条顶部不对齐」且「拉到底还有 3 行够不到」。
            // 因此裁剪区内的元素一律用 (0,0) 相对定位。
            var resultsBounds   = ElementBounds.Fixed(0, 0, 518, ResultsViewHeight); // 可滚动内容（相对裁剪区）
            var clippingBounds  = ElementBounds.Fixed(10, ResultViewY, 518, ResultsViewHeight); // 裁剪窗口（不动）
            // 二维码必须用**独立**的 bounds：richtext 会把自己撑高、滚动时又改 fixedY，
            // 两个元素共用同一个 ElementBounds 实例会互相带偏。
            // ⚠ 二维码元素在 BeginClip **之外**，因此它的坐标是相对**面板背景**的，
            // 而不是相对裁剪容器 —— 必须写完整的 (10, ResultViewY)，不能像裁剪区内的
            // 元素那样用 (0,0)。写错会让它整整高出/低于一个 ResultViewY（实测差 70px）。
            // 判断依据：元素在 BeginClip 里就用 (0,0)，在外面就用 (10, ResultViewY)。
            var qrBounds        = ElementBounds.Fixed(10, ResultViewY, 518, ResultsViewHeight);
            var scrollbarBounds = ElementBounds.Fixed(532, ResultViewY, 16, ResultsViewHeight);
            var progressBounds = ElementBounds.Fixed(10, 358, 540, 22);
            var btnPrevBounds  = ElementBounds.Fixed(10, 382, 72, 28);
            var btnPauseBounds = ElementBounds.Fixed(88, 382, 84, 28);
            var btnNextBounds  = ElementBounds.Fixed(178, 382, 72, 28);
            var btnModeBounds  = ElementBounds.Fixed(240, 382, 130, 28);
            // 按钮放不下第二排的入口，分成两行：第一行是播放控制，第二行是内容入口。
            var btnRecommendBounds = ElementBounds.Fixed(0, 412, 80, 28);
            var btnRadioBounds     = ElementBounds.Fixed(84, 412, 80, 28);
            var btnListsBounds = ElementBounds.Fixed(168, 412, 72, 28);
            var btnQueueBounds = ElementBounds.Fixed(244, 412, 72, 28);
            var btnProvidersBounds = ElementBounds.Fixed(320, 412, 72, 28);
            // 音量：面板此前完全没有音量控制（增益硬编码），而失真与电平直接相关。
            var volumeLabelBounds = ElementBounds.Fixed(0, 444, 44, 22);
            var volumeBounds      = ElementBounds.Fixed(48, 442, 232, 28);
            // 已知问题提示：放在面板底部，玩家一眼能看到，不必去翻文档。
            var noticeBounds      = ElementBounds.Fixed(0, 476, 540, 20);
            // 次声波高通：低音过载（功放被超低频吃满）时的对症手段。
            var subLabelBounds = ElementBounds.Fixed(292, 444, 66, 22);
            var subBounds      = ElementBounds.Fixed(360, 442, 180, 28);

            // 2. 背景：包住所有子元素，尺寸由子元素决定。
            //
            // 关键：这里登记的是 **clippingBounds（尺寸固定的裁剪窗口）**，
            // 绝**不能**登记 resultsBounds —— GuiElementRichtext 会把自己的 bounds
            // 撑到内容实际高度（官方正是靠这点取内容高度），而 bgBounds 是 FitToChildren，
            // 登记了它就会让整个面板背景跟着长到内容高度：
            // 长列表（几百首）一开就把面板拉成几千像素高，且关掉重开时那个被撑大的
            // fixedHeight 成了新的「定义值」，于是越开越长。
            // 官方 GuiDialogLogViewer 同样只登记 clippingBounds，不登记文本 bounds。
            var bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;
            bgBounds.WithChildren(headerBounds, searchBounds, btnSearchBnd, btnMoreBounds,
                clippingBounds, qrBounds, scrollbarBounds, progressBounds,
                btnPrevBounds, btnPauseBounds, btnNextBounds,
                btnModeBounds, btnRecommendBounds, btnRadioBounds,
                btnListsBounds, btnQueueBounds, btnProvidersBounds,
                volumeLabelBounds, volumeBounds, noticeBounds);

            // 3. 根：居中，尺寸由背景撑开
            var dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.CenterMiddle);

            SingleComposer = _capi.Gui
                .CreateCompo("vscloudmusic-panel", dialogBounds)
                .AddShadedDialogBG(bgBounds, true)
                .AddDialogTitleBar(Lang.Get("vscloudmusic:panel-title"), OnTitleBarClose)
                .BeginChildElements(bgBounds)
                    .AddDynamicText("", CairoFont.WhiteDetailText(), headerBounds, "header")
                    .AddTextInput(searchBounds, OnKeywordChanged, CairoFont.TextInput(), "search")
                    .AddSmallButton(Lang.Get("vscloudmusic:search"), OnSearchClicked, btnSearchBnd,
                        EnumButtonStyle.Normal, "btnSearch")
                    .AddSmallButton(Lang.Get("vscloudmusic:more"), OnMoreClicked, btnMoreBounds,
                        EnumButtonStyle.Normal, "btnMore")
                    .BeginClip(clippingBounds)
                        .AddRichtext("", CairoFont.WhiteSmallText(), resultsBounds,
                            OnResultClicked, "results")
                    .EndClip()
                    .AddDynamicCustomDraw(qrBounds, new DrawDelegateWithBounds(DrawQr), "qr")
                    .AddVerticalScrollbar(OnScrollResults, scrollbarBounds, "scrollbar")
                    .AddDynamicText("", CairoFont.WhiteSmallText(), progressBounds, "progress")
                    .AddSmallButton(Lang.Get("vscloudmusic:prev"), OnPrevClicked, btnPrevBounds,
                        EnumButtonStyle.Normal, "btnPrev")
                    .AddSmallButton(Lang.Get("vscloudmusic:btn-play-pause"), OnPauseClicked, btnPauseBounds,
                        EnumButtonStyle.Normal, "btnPause")
                    .AddSmallButton(Lang.Get("vscloudmusic:next"), OnNextClicked, btnNextBounds,
                        EnumButtonStyle.Normal, "btnNext")
                    .AddSmallButton(Lang.Get("vscloudmusic:mode-button"), OnModeClicked, btnModeBounds,
                        EnumButtonStyle.Normal, "btnMode")
                    .AddSmallButton(Lang.Get("vscloudmusic:playlists"), OnPlaylistsClicked, btnListsBounds,
                        EnumButtonStyle.Normal, "btnLists")
                    .AddSmallButton(Lang.Get("vscloudmusic:queue"), OnQueueClicked, btnQueueBounds,
                        EnumButtonStyle.Normal, "btnQueue")
                    .AddSmallButton(Lang.Get("vscloudmusic:providers"), OnProvidersClicked,
                        btnProvidersBounds, EnumButtonStyle.Normal, "btnProviders")
                    // 推荐/电台按**活跃音源的能力位**决定是否出现：
                    // 不支持就直接不显示按钮，而不是点了才说不能用。
                    .AddIf(HasCapability(ProviderCapabilities.Recommendations))
                        .AddSmallButton(Lang.Get("vscloudmusic:recommend"), OnRecommendClicked,
                            btnRecommendBounds, EnumButtonStyle.Normal, "btnRecommend")
                    .EndIf()
                    .AddIf(HasCapability(ProviderCapabilities.Radio))
                        .AddSmallButton(Lang.Get("vscloudmusic:radio"), OnRadioClicked,
                            btnRadioBounds, EnumButtonStyle.Normal, "btnRadio")
                    .EndIf()
                    .AddStaticText(Lang.Get("vscloudmusic:volume"), CairoFont.WhiteSmallText(),
                        volumeLabelBounds)
                    .AddSlider(OnVolumeChanged, volumeBounds, "volume")
                    .AddStaticText(Lang.Get("vscloudmusic:known-issues"),
                        CairoFont.WhiteDetailText().WithColor(GuiStyle.ErrorTextColor), noticeBounds)
                .EndChildElements()
                .Compose();

            // AddSlider 只登记元素，取值范围要在组装后设置。
            SingleComposer.GetSlider("volume")?.SetValues(initialVolume, 0, 100, 5, "%");
        }

        /// <summary>原版对话框标题栏的关闭按钮。</summary>
        private void OnTitleBarClose() => TryClose();

        public override void OnGuiOpened()
        {
            base.OnGuiOpened();

            // 分辨率 / GUI 缩放变化后，引擎会 MarkAllDialogsForRecompose() 把每个对话框
            // 重新 Compose。重排会按元素的**定义值**重算 bounds，而我们在滚动时手动改过
            // richtext 的 fixedY、滚动条也保存着自己的位置 —— 两边就此失同步，表现为
            // 内容画到裁剪窗外（畸形）。这里照官方 GuiDialogWorldMap.OnGuiOpened 的做法
            // 显式重排一次，并把滚动状态重新下发，确保二者一致。
            var comp = SingleComposer?.GetRichtext("results");
            if (comp != null)
            {
                SingleComposer!.ReCompose();
                comp.Bounds.fixedY = ResultViewY;
                comp.Bounds.CalcWorldBounds();
                var sb = SingleComposer.GetScrollbar("scrollbar");
                if (sb != null)
                {
                    var content = (float)Math.Max(ResultsViewHeight,
                        Math.Max(comp.Bounds.fixedHeight, comp.TotalHeight));
                    sb.SetHeights(ResultsViewHeight, content);
                    sb.SetScrollbarPosition(0);
                }
            }
            // 面板打开时隐藏右上角状态，避免同一信息显示两遍
            _hud.SetPanelOpen(true);
            RefreshHeader();
        }

        public override void OnGuiClosed()
        {
            base.OnGuiClosed();
            _hud.SetPanelOpen(false);
        }

        public override void OnRenderGUI(float deltaTime)
        {
            base.OnRenderGUI(deltaTime);
            // 进度由模组每 tick 同步进 controller；这里只做显示，无网络/无 AL。
            RefreshProgress();
            RefreshCurrentMark();
            PollLogin();
        }

        /// <summary>
        /// 切歌后把列表里的「正在播放」标记挪过去。
        ///
        /// 只在**当前曲目 id 变化**时重绘，绝不每帧重绘 —— 歌单动辄几百首，
        /// 每帧重建一次富文本会直接掉帧。歌单列表视图不含歌曲，无需处理。
        /// </summary>
        private void RefreshCurrentMark()
        {
            if (!IsOpened()) return;
            // 搜索/取歌单进行中：此刻列表区显示的是「搜索中…」之类的提示，
            // 重绘会把它覆盖成旧内容，反而让人以为卡住了。等结果回来自然会重绘。
            if (_searching) return;

            var now = _controller.CurrentSong;
            if (now == _lastMarkedSong) return;
            _lastMarkedSong = now;

            // 表头（「已登录 · 正在播放：<歌名>」）只在开面板与点模式时刷过，
            // 切歌时没人更新它 —— 实测随机模式下点下一首，歌名还停在上一次。
            // 这里既然是「当前曲目变了」的唯一检测点，顺手把表头也刷新。
            RefreshHeader();

            // 列表里的 ▶ 标记同样要跟着走。
            // 凡是「歌曲列表」的视图都要重绘：搜索、推荐、电台 —— 之前只放行 Search，
            // 导致推荐页/电台页切歌时 ▶ 停在原地（实测反馈）。不含歌曲的视图才跳过。
            switch (_view)
            {
                case ListView.Queue:
                    RenderQueue();
                    break;
                case ListView.Playlists:
                case ListView.Providers:
                    break;
                default:
                    RenderResults(_resultsEmptyMessage);
                    break;
            }
        }

        // ---------------- 搜索 ----------------

        /// <summary>当前音源是否已登录。未注册任何音源时为 false。</summary>
        private bool IsLoggedIn => _providers.Active?.Account.IsLoggedIn == true;

        private void OnKeywordChanged(string text) => _keyword = text ?? "";

        private bool OnSearchClicked()
        {
            var keyword = _keyword.Trim();
            if (keyword.Length == 0 || _searching)
            {
                // 静默早退最坑人：按钮看起来正常、点了却毫无反应。两种原因要分开报。
                Diag($"搜索被跳过：关键词长度={keyword.Length}，_searching={_searching}");
                return true;
            }

            Diag($"开始搜索「{keyword}」");

            _searching = true;
            SetResultsText(Lang.Get("vscloudmusic:searching"));

            // 搜索必须离开主线程；结果回主线程再渲染。
            _ = Task.Run(async () =>
            {
                try
                {
                    var provider = _providers.Active;
                    if (provider == null) return;
                    var result = await provider.SearchAsync(keyword, MaxResults, 0, CancellationToken.None)
                        .ConfigureAwait(false);
                    _capi.Event.EnqueueMainThreadTask(
                        () => ShowResults(keyword, result), "vscloudmusic-search");
                }
                catch (Exception e)
                {
                    // 只记类型与消息，绝不把任何凭据带进 UI 或日志
                    var msg = e.GetType().Name;
                    _capi.Event.EnqueueMainThreadTask(
                        () => ShowSearchFailed(keyword, msg), "vscloudmusic-search-fail");
                }
            });

            return true;
        }

        /// <summary>
        /// 「更多」：接着已搜出的数量继续取下一页，**追加**到现有结果之后。
        /// 分页必须带 offset —— 原来 SearchAsync 把 offset 写死成 0，所以每次
        /// 都拿回同一页（表现为「永远只有 12 首」）。
        /// </summary>
        private bool OnMoreClicked()
        {
            var keyword = _keyword.Trim();
            if (keyword.Length == 0 || _searching) return true;

            // 只有「当前列表就是搜索结果」时才允许翻页：
            // 歌单列出的内容也占用同一个列表区，对它翻页会得到风马牛不相及的结果。
            if (_view != ListView.Search || _results.Count == 0)
            {
                Diag("更多被跳过：当前列表不是搜索结果");
                return true;
            }

            _searching = true;
            SetResultsText(Lang.Get("vscloudmusic:loading-more"));
            var offset = _results.Count;

            _ = Task.Run(async () =>
            {
                try
                {
                    var provider = _providers.Active;
                    if (provider == null) return;
                    var result = await provider.SearchAsync(keyword, MaxResults, offset, CancellationToken.None)
                        .ConfigureAwait(false);
                    _capi.Event.EnqueueMainThreadTask(
                        () => AppendResults(keyword, result), "vscloudmusic-more");
                }
                catch (Exception e)
                {
                    var msg = e.GetType().Name;
                    _capi.Event.EnqueueMainThreadTask(
                        () => { _searching = false; Diag($"加载更多失败：{msg}"); RenderResults(_resultsEmptyMessage); },
                        "vscloudmusic-more-fail");
                }
            });
            return true;
        }

        /// <summary>把新一页追加到现有结果末尾（与 ShowResults 的区别：不清空、不重置 _index）。</summary>
        private void AppendResults(string keyword, SearchPage result)
        {
            var added = result?.Songs?.Count ?? 0;
            Diag($"「{keyword}」第 {(_results.Count / MaxResults) + 1} 页返回 {added} 首（已有 {_results.Count}）");

            if (added == 0)
            {
                _searching = false;
                RenderResults(Lang.Get("vscloudmusic:more-empty", _results.Count));
                return;
            }

            // 去重：分页边界上 API 可能重复返回同一首
            foreach (var song in result!.Songs)
                if (!_results.Exists(x => x.Key == song.Key)) _results.Add(song);

            RenderResults(_resultsEmptyMessage);
        }

        private void ShowResults(string keyword, SearchPage result)
        {
            Diag($"搜索「{keyword}」返回 {result?.Songs?.Count ?? -1} 首");
            _view = ListView.Search;
            _results.Clear();
            if (result?.Songs != null) _results.AddRange(result.Songs);
            _index = -1;
            RenderResults(Lang.Get("vscloudmusic:search-empty", keyword));
        }

        /// <summary>
        /// 把 <c>_results</c> 渲染到列表区。**它是列表内容的唯一真相来源** ——
        /// 搜索、歌单、之后的任何列表都必须先填 _results 再调这里渲染，
        /// 不要再另造一份渲染路径（之前就是因为给 ShowResults 传了空 SearchResult，
        /// 它内部 _results.Clear() 把刚灌好的歌单又清空了，表现为「队列里有歌、列表却空白」）。
        /// </summary>
        private void RenderResults(string emptyMessage)
        {
            _searching = false;
            _resultsEmptyMessage = emptyMessage;
            // 本方法渲染的永远是「歌曲 id 列表」（无论来源是搜索还是歌单），
            // 所以点击解释为歌曲。放在这里而不是各调用方，是为了只有一处需要推理 ——
            // 一旦 _view 与实际内容不符（例如加载歌单后仍停留在 Playlists），
            // 点歌就会走错分支（队列视图分支会绕过 PlayIndex，导致队列不被重建）。
            // **绝不在这里改 _view**：视图由调用方设定。
            // 这里曾硬写 _view = ListView.Search，导致推荐/电台这类复用本方法渲染的视图
            // 一渲染就被改回 Search —— 于是 PlayIndex 里「是否来自电台」的判断永远为 false，
            // 电台列表每次切回都轮换（实测反馈）。同类「两份真相」问题本项目已出现多次。

            if (_results.Count == 0)
            {
                SetResultsText(emptyMessage);
                return;
            }

            var current = _controller.CurrentSong;
            var sb = new StringBuilder();
            for (int i = 0; i < _results.Count; i++)
            {
                var s = _results[i];
                // 标出正在播放的那首：这个列表同时也是「当前歌单」，不标的话
                // 切歌之后根本看不出播到哪儿了（用户反馈）。
                var mark = s.Key == current ? "▶ " : "";
                sb.Append($"<a href=\"{LinkPrefix}{s.Key}\">{mark}{i + 1}. {Escape(s.Name)} — {Escape(s.Artist)}</a><br>");
            }
            SetResultsText(sb.ToString());
        }

        private void ShowSearchFailed(string keyword, string errorType)
        {
            _searching = false;
            SetResultsText(Lang.Get("vscloudmusic:search-failed", keyword, errorType));
        }

        private void OnResultClicked(LinkTextComponent link)
        {
            var href = link?.Href;
            if (string.IsNullOrEmpty(href)) return;

            // 音源 / 登录 / 退出：与歌曲、歌单链接共用同一个富文本控件，按前缀区分
            if (href.StartsWith(UseLinkPrefix, StringComparison.Ordinal))
            {
                _providers.SetActive(href.Substring(UseLinkPrefix.Length));
                RenderProviders();
                return;
            }
            if (href.StartsWith(LoginLinkPrefix, StringComparison.Ordinal))
            {
                BeginLogin(href.Substring(LoginLinkPrefix.Length));
                return;
            }
            if (href.StartsWith(LogoutLinkPrefix, StringComparison.Ordinal))
            {
                ConfirmLogout(href.Substring(LogoutLinkPrefix.Length));
                return;
            }

            // 歌单链接与歌曲链接共用同一个富文本控件，按前缀区分
            if (href.StartsWith(ListLinkPrefix, StringComparison.Ordinal))
            {
                var collectionId = href.Substring(ListLinkPrefix.Length);
                var pl = _playlists.Find(x => x.Id == collectionId);
                if (pl != null) LoadCollection(collectionId, pl.Name);
                return;
            }

            if (!href.StartsWith(LinkPrefix, StringComparison.Ordinal)) return;
            if (!SongKey.TryParse(href.Substring(LinkPrefix.Length), out var songKey)) return;

            // 队列视图里点某首：只切歌，**不要**用当前列表重灌队列，
            // 否则点队列中一首会把队列重置成「当前列表」（那正是队列自身）。
            if (_view == ListView.Queue)
            {
                _onPlayRequested(songKey, _controller.TitleFor(songKey));
                return;
            }

            var picked = _results.FindIndex(s => s.Key == songKey);
            if (picked >= 0) _index = picked;
            var title = picked >= 0 ? $"{_results[picked].Name} — {_results[picked].Artist}" : null;
            PlayIndex(songKey, title);
        }

        // ---------------- 播放控制 ----------------

        private bool OnPauseClicked()
        {
            var before = _controller.Status;
            switch (before)
            {
                case PlaybackStatus.Playing:
                    _controller.RequestPause();
                    break;
                case PlaybackStatus.Paused:
                    _controller.RequestResume();
                    break;
                default:
                    // 停止/失败/空闲：这是一个**死路**——面板里没有第二个「播放」按钮，
                    // 停止后再也无法从面板恢复播放（实测日志里反复出现「Stopped → Stopped」，
                    // 按钮点了毫无反应）。把本按钮做成真正的播放/暂停开关：只要还有当前曲目，
                    // 就重新请求播放它。
                    var song = _controller.CurrentSong;
                    if (song == null)
                    {
                        Diag("播放键：没有当前曲目，无操作");
                        break;
                    }
                    Diag($"播放键：{before} → 重播当前曲 {song.Value}");
                    PlayIndex(song.Value, _controller.CurrentTitle);
                    return true;
            }
            Diag($"暂停键：{before} → {_controller.Status}");
            return true;
        }

        /// <summary>循环切换播放模式：单曲循环 → 顺序 → 随机。</summary>
        private bool OnModeClicked()
        {
            var next = _controller.Queue.Mode switch
            {
                PlayMode.RepeatOne => PlayMode.Sequential,
                PlayMode.Sequential => PlayMode.Shuffle,
                // 心动模式已移除：它是单个音源的能力（依赖服务端推荐接口），
                // 做成全局播放模式等于假设所有音源都支持，而其他音源不一定有。
                _ => PlayMode.RepeatOne,
            };
            _controller.Queue.Mode = next;
            Diag($"模式 → {ModeName(next)}（状态 {_controller.Status}）");
            _onModeChanged(next);      // 交给模组持久化
            RefreshHeader();
            return true;
        }

        /// <summary>
        /// 临时诊断输出（本类无 logger，走 capi）。定位「按钮点了没反应」类问题时，
        /// 静态阅读很容易得出错误结论 —— 直接看点击到底有没有到达处理函数、
        /// 以及在哪一个早退分支上被吞掉，比继续推断快得多。
        /// </summary>
        private void Diag(string msg) =>
            _capi.Logger.Notification($"[vscloudmusic-ui] {msg}");

        private static string ModeName(PlayMode mode) => mode switch
        {
            PlayMode.RepeatOne => Lang.Get("vscloudmusic:mode-repeat-one"),
            PlayMode.Sequential => Lang.Get("vscloudmusic:mode-sequential"),
            _ => Lang.Get("vscloudmusic:mode-shuffle"),
        };

        // ---------------- 音源与账户 ----------------

        /// <summary>当前进行中的登录流程。同一时刻只允许一个 —— 界面上也只有一块二维码位置。</summary>
        private ILoginFlow? _loginFlow;
        private string? _loginProviderId;
        private DateTime _lastLoginPollUtc = DateTime.MinValue;

        /// <summary>宿主侧的轮询节流。流程内部还会再节流一次，这里只是别让每帧都派任务。</summary>
        private static readonly TimeSpan LoginPollInterval = TimeSpan.FromMilliseconds(400);

        private const string UseLinkPrefix = "use://";
        private const string LoginLinkPrefix = "login://";
        private const string LogoutLinkPrefix = "logout://";

        /// <summary>活跃音源是否声明了某能力。界面据此决定入口是否出现。</summary>
        private bool HasCapability(ProviderCapabilities cap)
        {
            var active = _providers.Active;
            return active != null && (active.Capabilities & cap) != 0;
        }

        private bool OnRecommendClicked()
        {
            _view = ListView.Recommend;
            if (RestoreFromCache(_recommendCache, "推荐")) return true;
            LoadContentList(Lang.Get("vscloudmusic:loading-recommend"),
                p => p.GetRecommendedSongsAsync(CancellationToken.None), "推荐", _recommendCache);
            return true;
        }

        private bool OnRadioClicked()
        {
            _view = ListView.Radio;

            // 规则（玩家预期）：
            // - 电台**还在播** → 切走再切回应看到刚才那份列表（沿用缓存）
            // - 中途去听了别的（歌单/搜索/推荐）→ 此时回到电台应轮换新的一批，
            //   因为「上一次那批电台」在语义上已经结束了
            Diag($"电台视图：电台播放中={_radioFeedActive}，缓存={_radioCache.Count} 首");
            if (_radioFeedActive && RestoreFromCache(_radioCache, "电台")) return true;

            _radioCache.Clear();
            LoadContentList(Lang.Get("vscloudmusic:loading-radio"),
                p => p.GetRadioSongsAsync(CancellationToken.None), "电台", _radioCache);
            return true;
        }

        /// <summary>
        /// 用缓存恢复该视图的列表，返回是否已恢复（false 表示需要去取）。
        /// 恢复时把 ▶ 游标定位到正在播放的那首，否则切回来会显示成从未播过。
        /// </summary>
        private bool RestoreFromCache(List<SongInfo> cache, string label)
        {
            if (cache.Count == 0) return false;

            _results.Clear();
            _results.AddRange(cache);

            var current = _controller.CurrentSong;
            _index = current == null ? 0 : Math.Max(0, _results.FindIndex(x => x.Key == current.Value));
            Diag($"{label}：使用缓存 {cache.Count} 首（index={_index}）");
            RenderResults(Lang.Get("vscloudmusic:content-empty", label));
            return true;
        }

        /// <summary>
        /// 把「推荐/电台」这类一次性批量结果填进列表。
        /// 走的是与搜索/歌单同一套 _results + RenderResults，因此点歌、▶ 标记、
        /// 灌队列的行为完全一致，不需要另写一条渲染路径。
        /// </summary>
        private void LoadContentList(string loadingText,
            System.Func<IMusicProvider, Task<IReadOnlyList<SongInfo>>> fetch, string label,
            List<SongInfo> cache)
        {
            if (_searching) return;
            var provider = _providers.Active;
            if (provider == null) return;

            _searching = true;
            SetResultsText(loadingText);

            _ = Task.Run(async () =>
            {
                IReadOnlyList<SongInfo> songs;
                try { songs = await fetch(provider).ConfigureAwait(false); }
                catch (Exception e)
                {
                    var msg = e.GetType().Name;
                    _capi.Event.EnqueueMainThreadTask(
                        () => { _searching = false; Diag($"{label}加载失败：{msg}"); SetResultsText(""); },
                        "vscloudmusic-content-fail");
                    return;
                }

                _capi.Event.EnqueueMainThreadTask(() =>
                {
                    _searching = false;
                    cache.Clear();
                    cache.AddRange(songs);

                    _results.Clear();
                    _results.AddRange(songs);
                    _index = songs.Count > 0 ? 0 : -1;
                    Diag($"{label}返回 {songs.Count} 首（已缓存，切回不会重取）");
                    RenderResults(Lang.Get("vscloudmusic:content-empty", label));
                }, "vscloudmusic-content");
            });
        }

        /// <summary>音量滑块。改动即写配置并立刻生效（不等下一首）。</summary>
        private bool OnVolumeChanged(int value)
        {
            _onVolumeChanged(value);
            return true;
        }

        private bool OnProvidersClicked()
        {
            _view = ListView.Providers;
            RenderProviders();
            return true;
        }

        /// <summary>
        /// 列出已注册的音源、各自登录态与可用的账户操作。
        /// 不支持登录的音源（Capabilities 里没有 Authentication）干脆不显示按钮，
        /// 而不是显示一个点了没反应的按钮 —— 能力位的意义就在这里。
        /// </summary>
        private void RenderProviders()
        {
            var all = _providers.All;
            if (all.Count == 0)
            {
                ShowQr(false);
                SetResultsText(Lang.Get("vscloudmusic:no-providers"));
                return;
            }

            var active = _providers.Active;
            var sb = new StringBuilder();
            foreach (var p in all)
            {
                var mark = ReferenceEquals(p, active) ? "▶ " : "";
                var acc = p.Account.IsLoggedIn
                    ? Lang.Get("vscloudmusic:logged-in")
                    : Lang.Get("vscloudmusic:logged-out");
                var who = p.Account.IsLoggedIn && !string.IsNullOrEmpty(p.Account.DisplayName)
                    ? $"（{Escape(p.Account.DisplayName)}）"
                    : "";

                sb.Append($"<a href=\"{UseLinkPrefix}{p.Id}\">{mark}{Escape(p.DisplayName)} — {acc}{who}</a>");

                if ((p.Capabilities & ProviderCapabilities.Authentication) != 0)
                {
                    sb.Append(p.Account.IsLoggedIn
                        ? $"  <a href=\"{LogoutLinkPrefix}{p.Id}\">[{Lang.Get("vscloudmusic:logout")}]</a>"
                        : $"  <a href=\"{LoginLinkPrefix}{p.Id}\">[{Lang.Get("vscloudmusic:login")}]</a>");
                }
                sb.Append("<br>");
            }

            ShowQr(false);
            SetResultsText(sb.ToString());
        }

        /// <summary>
        /// 结果区在「富文本列表」与「二维码贴图」之间切换。
        /// 两者同占一块区域：二维码必须足够大才扫得动，与列表并排会两边都难受。
        /// </summary>
        private void ShowQr(bool show)
        {
            // 不用 Visible —— GuiElement 基类上没有这个成员（文档与反射都确认过）。
            // 改成：显示二维码时把列表文字清空。两者同占结果区，清空后不会叠在一起；
            // 二维码本身由 DrawQr 在 _qrTexture 为空时不画任何东西。
            if (show) SetResultsText("");
        }

        /// <summary>二维码绘制。矩阵已在登录开始时编码成贴图，这里只负责贴上去。</summary>
        private bool _qrDrawLogged;

        /// <summary>
        /// 二维码绘制。**回调是画进 Cairo 表面的，不是 OpenGL。**
        ///
        /// GuiElementCustomDraw 的语义（签名即证据）：
        ///   ComposeElements(Context, ImageSurface) —— 回调在**组装时**执行一次，结果烤成纹理（texId）
        ///   Redraw()                               —— 之后要靠它重新生成
        /// 所以这里不能调 Render2DTexture（那是 OpenGL，与 Cairo 表面无关），
        /// 而是直接用 Cairo 图元画格子；而且因为只在组装时画一次，
        /// 几百个方块的开销完全可接受 —— 设计文档 §4.3 就是这么定的。
        ///
        /// 登录状态变化后必须调 Redraw()，否则元素仍显示旧的一次绘制结果。
        /// </summary>
        private void DrawQr(Context ctx, ImageSurface surface, ElementBounds bounds)
        {
            var matrix = _loginFlow?.QrMatrix;
            if (matrix == null) return;

            var modules = matrix.Length;
            if (modules == 0) return;

            // 正方形居中等比缩放，并**留出余量**：取 min(宽,高) 会正好塞满结果区高度，
            // 边缘容易被面板背景/内缩切掉（实测表现为「二维码缺一块」）。
            // 可用高度要扣掉给说明文字留的那一行，否则加上偏移后会超出可见区底部。
            // ⚠ 坐标空间：bounds 系列已是**缩放后的屏幕坐标**，而 QrTopInset 是
            // **未缩放单位** —— 必须经 GuiElement.scaled() 换算，否则在 GUIScale ≠ 1
            // 时偏移量完全不对（这正是上一版「还是偏高」的原因）。
            var inset = GuiElement.scaled(QrTopInset);
            var availH = bounds.InnerHeight - inset;
            var side = Math.Min(bounds.InnerWidth, Math.Max(60, availH)) * 0.92;

            // 水平居中。
            var x0 = bounds.drawX + (bounds.InnerWidth - side) / 2;

            // 纵向：先让开标题一行，再**整体上移图片高度的 1/3**。
            // 玩家实测：仅让开标题那一行时，二维码仍比预期低约 1/3 张图的高度
            // （说明自绘回调里的坐标系与 drawY 并不完全等价 —— 这一点与本题已知的
            //  「裁剪区内外坐标基准不同」是同一类问题，实测值优先于推算）。
            var y0 = bounds.drawY + inset - side / 3;

            Diag($"[二维码定位] 元素drawY={bounds.drawY:0} 高{bounds.InnerHeight:0} " +
                 $"| inset未缩放={QrTopInset} 缩放后={inset:0} | 可用高{availH:0} 边长{side:0} " +
                 $"| 画在y={y0:0} 底={y0 + side:0} " +
                 $"| 列表drawY={SingleComposer?.GetRichtext("results")?.Bounds.drawY:0} " +
                 $"列表高={SingleComposer?.GetRichtext("results")?.Bounds.InnerHeight:0}");

            // 静默区（4 模块）：规范要求，缺了扫码器常识别失败。
            const int Quiet = 4;
            var total = modules + Quiet * 2;
            var cell = side / total;

            // 先铺白底（含静默区）：二维码必须黑在白上，透明底在暗色面板上会扫不动。
            ctx.SetSourceRGBA(1, 1, 1, 1);
            ctx.Rectangle(x0, y0, side, side);
            ctx.Fill();

            ctx.SetSourceRGBA(0, 0, 0, 1);
            for (int my = 0; my < modules; my++)
            {
                var row = matrix[my];
                for (int mx = 0; mx < modules && mx < row.Length; mx++)
                {
                    if (!row[mx]) continue;
                    ctx.Rectangle(
                        x0 + (mx + Quiet) * cell,
                        y0 + (my + Quiet) * cell,
                        cell, cell);
                }
            }
            ctx.Fill();

            if (!_qrDrawLogged)
            {
                _qrDrawLogged = true;
                Diag($"DrawQr：元素 drawY={bounds.drawY:0} 高{bounds.InnerHeight:0} | "
                       + $"面板 drawY={SingleComposer?.Bounds.drawY:0} 高{SingleComposer?.Bounds.InnerHeight:0} | "
                       + $"scaled(ResultViewY)={GuiElement.scaled(ResultViewY):0} | 画在 y={y0:0}");
            }
        }

        /// <summary>
        /// 电台续取拿到新歌后由模组回调：把新歌追加进电台列表缓存。
        ///
        /// 不加这一步的话，模组侧的队列在源源不断补货，而玩家在电台视图看到的
        /// 永远是最初那 3 首 —— 界面与实际队列脱节。
        /// </summary>
        public void AppendRadioSongs(IReadOnlyList<SongInfo> songs)
        {
            if (songs.Count == 0) return;

            foreach (var song in songs)
                if (!_radioCache.Exists(x => x.Key == song.Key)) _radioCache.Add(song);

            // 只有当前正看着电台才重绘；在别的视图里静默更新缓存即可。
            if (_view == ListView.Radio && !_searching)
            {
                _results.Clear();
                _results.AddRange(_radioCache);
                RenderResults(Lang.Get("vscloudmusic:content-empty", "电台"));
            }
        }

        private void BeginLogin(string providerId)
        {
            var provider = _providers.Get(providerId);
            if (provider == null) return;
            if ((provider.Capabilities & ProviderCapabilities.Authentication) == 0) return;

            DisposeLogin();

            _loginProviderId = providerId;
            Diag($"发起登录：{providerId}");
            SetResultsText(Lang.Get("vscloudmusic:requesting-qr"));

            _ = Task.Run(async () =>
            {
                ILoginFlow? flow = null;
                string? err = null;
                try
                {
                    flow = await provider.BeginLoginAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception e) { err = e.GetType().Name; }

                _capi.Event.EnqueueMainThreadTask(() =>
                {
                    if (err != null)
                    {
                        SetResultsText(Lang.Get("vscloudmusic:login-failed", err));
                        return;
                    }
                    _loginFlow = flow;
                    var rows = flow?.QrMatrix?.Length ?? 0;
                    Diag($"二维码：矩阵 {rows}x{rows}，说明「{flow?.Instruction}」");
                    ShowQr(rows > 0);
                    // 自绘回调只在组装时跑一次，必须显式要求重画，否则仍显示空白。
                    SingleComposer?.GetCustomDraw("qr")?.Redraw();
                    SetResultsText(flow?.Instruction ?? "");
                }, "vscloudmusic-login-begin");
            });
        }

        private void ConfirmLogout(string providerId)
        {
            var provider = _providers.Get(providerId);
            if (provider == null) return;

            DisposeLogin();
            SetResultsText(Lang.Get("vscloudmusic:logging-out"));

            _ = Task.Run(async () =>
            {
                try { await provider.LogoutAsync(CancellationToken.None).ConfigureAwait(false); }
                catch { /* 退出登录失败也要刷新界面：本地凭据已清 */ }
                _capi.Event.EnqueueMainThreadTask(RenderProviders, "vscloudmusic-logout");
            });
        }

        /// <summary>宿主侧轮询：流程自己也会按服务端状态节流，这里只是控制派任务的频率。</summary>
        private void PollLogin()
        {
            var flow = _loginFlow;
            if (flow == null) return;
            if (DateTime.UtcNow - _lastLoginPollUtc < LoginPollInterval) return;
            _lastLoginPollUtc = DateTime.UtcNow;

            _ = Task.Run(async () =>
            {
                LoginPollState state;
                try { state = await flow.PollAsync(CancellationToken.None).ConfigureAwait(false); }
                catch { return; }
                _capi.Event.EnqueueMainThreadTask(() => OnLoginState(flow, state), "vscloudmusic-login-poll");
            });
        }

        private void OnLoginState(ILoginFlow flow, LoginPollState state)
        {
            // 迟到的回包：玩家可能已经重新发起或取消了登录
            if (!ReferenceEquals(flow, _loginFlow)) return;

            switch (state)
            {
                case LoginPollState.Pending:
                    SetResultsText(flow.Instruction);
                    break;

                case LoginPollState.Succeeded:
                    DisposeLogin();
                    _view = ListView.Providers;
                    RenderProviders();
                    break;

                case LoginPollState.Expired:
                case LoginPollState.Failed:
                    DisposeLogin();
                    _view = ListView.Providers;
                    RenderProviders();
                    SetResultsText(flow.Instruction);
                    break;
            }
        }

        /// <summary>结束登录并释放二维码贴图（持有显存，必须显式释放）。</summary>
        private void DisposeLogin()
        {
            _loginFlow = null;
            _loginProviderId = null;
            _qrDrawLogged = false;
            ShowQr(false);
            SingleComposer?.GetCustomDraw("qr")?.Redraw();
        }

        /// <summary>显示当前播放队列（用户要的「当前播放列表」）。</summary>
        private bool OnQueueClicked()
        {
            _view = ListView.Queue;
            RenderQueue();
            return true;
        }

        /// <summary>渲染播放队列，标出正在播放的那首。切成独立方法是为了能被「切歌后自动刷新」复用。</summary>
        private void RenderQueue()
        {
            var q = _controller.Queue;
            if (q.Items.Count == 0)
            {
                SetResultsText(Lang.Get("vscloudmusic:queue-empty"));
                return;
            }

            var current = q.Current;
            var sb = new StringBuilder();
            for (int i = 0; i < q.Items.Count; i++)
            {
                var item = q.Items[i];
                var mark = item == current ? "▶ " : "　";
                sb.Append($"<a href=\"{LinkPrefix}{item}\">{mark}{i + 1}. {Escape(_controller.TitleFor(item))}</a><br>");
            }
            SetResultsText(sb.ToString());
        }

        /// <summary>列出当前账号的歌单；点一个就把它整张灌进队列并开始播放。</summary>
        private bool OnPlaylistsClicked()
        {
            if (_searching) return true;

            if (!IsLoggedIn)
            {
                SetResultsText(Lang.Get("vscloudmusic:playlists-need-login"));
                return true;
            }

            _searching = true;
            SetResultsText(Lang.Get("vscloudmusic:loading-playlists"));

            _ = Task.Run(async () =>
            {
                try
                {
                    var provider = _providers.Active;
                    if (provider == null) return;
                    var lists = await provider.GetUserCollectionsAsync(CancellationToken.None).ConfigureAwait(false);
                    _capi.Event.EnqueueMainThreadTask(() => ShowPlaylists(lists), "vscloudmusic-lists");
                }
                catch (Exception e)
                {
                    var msg = e.GetType().Name;
                    _capi.Event.EnqueueMainThreadTask(
                        () => { _searching = false; SetResultsText(Lang.Get("vscloudmusic:playlists-failed", msg)); },
                        "vscloudmusic-lists-fail");
                }
            });

            return true;
        }

        private void ShowPlaylists(IReadOnlyList<CollectionInfo> lists)
        {
            Diag($"歌单列表返回 {lists?.Count ?? -1} 个（_results 仍为 {_results.Count}）");
            _searching = false;
            _view = ListView.Playlists;
            _playlists.Clear();
            if (lists != null) _playlists.AddRange(lists);

            if (_playlists.Count == 0)
            {
                SetResultsText(Lang.Get("vscloudmusic:playlists-empty"));
                return;
            }

            var sb = new StringBuilder();
            for (int i = 0; i < _playlists.Count; i++)
            {
                var pl = _playlists[i];
                // 歌单用不同前缀，避免与歌曲 id 混淆
                sb.Append($"<a href=\"list://{pl.Id}\">{i + 1}. {Escape(pl.Name)} ({pl.TrackCount})</a><br>");
            }
            SetResultsText(sb.ToString());
        }

        private const string ListLinkPrefix = "list://";

        /// <summary>点歌单 → 后台取该歌单全部曲目 → 回主线程灌入队列并播放第一首。</summary>
        private void LoadCollection(string collectionId, string name)
        {
            if (_searching) { Diag($"点歌单被跳过：_searching 卡在 true（当前 _results={_results.Count}）"); return; }
            _searching = true;
            SetResultsText(Lang.Get("vscloudmusic:loading-playlist", name));

            _ = Task.Run(async () =>
            {
                try
                {
                    var provider = _providers.Active;
                    if (provider == null) return;
                    var tracks = await provider.GetCollectionTracksAsync(collectionId, CancellationToken.None).ConfigureAwait(false);
                    _capi.Event.EnqueueMainThreadTask(() => StartPlaylist(name, tracks), "vscloudmusic-pl");
                }
                catch (Exception e)
                {
                    var msg = e.GetType().Name;
                    _capi.Event.EnqueueMainThreadTask(
                        () => { _searching = false; SetResultsText(Lang.Get("vscloudmusic:playlist-failed", msg)); },
                        "vscloudmusic-pl-fail");
                }
            });
        }

        private void StartPlaylist(string name, IReadOnlyList<SongInfo> tracks)
        {
            Diag($"歌单「{name}」返回 {tracks?.Count ?? -1} 首 → 覆盖 _results");
            _searching = false;
            if (tracks == null || tracks.Count == 0)
            {
                SetResultsText(Lang.Get("vscloudmusic:playlist-empty", name));
                return;
            }

            _view = ListView.Search;   // 歌单曲目按普通歌曲列表处理（点歌走 PlayIndex）
            _results.Clear();
            _results.AddRange(tracks);
            _index = 0;

            // 先把歌单内容渲染出来（_results 已是真相来源）。
            // 队列由下面的 PlayIndex 负责灌 —— 它按 _results + _index 重建队列，
            // 这里再自己 SetItems 是重复的。
            RenderResults(Lang.Get("vscloudmusic:playlist-empty", name));

            var first = tracks[0];
            PlayIndex(first.Key, first.Name);
        }

        private bool OnNextClicked() => Step(1);
        private bool OnPrevClicked() => Step(-1);

        /// <summary>
        /// 上一首/下一首。
        ///
        /// **交给模组走队列**，不能再用本类的 _results + _index 加减 ——
        /// 那等于把「下一首」写死成列表顺序，随机模式完全失效：
        /// 玩家在随机模式下点下一首却总是顺序的那一首（实测反馈）。
        /// 队列才知道模式，也知道随机模式下哪些还没播过。
        /// </summary>
        private bool Step(int delta)
        {
            Diag($"{delta:+0;-0} → 交给队列（模式 {_controller.Queue.Mode}）");
            _onStepRequested(delta);
            return true;
        }

        /// <summary>
        /// 切歌统一入口。注意 Task 1 的状态机契约：**暂停中的曲目必须先显式停止**
        /// 才能开始新歌（暂停时仍然占着全游戏唯一的立体声源，TryBegin 会从 Paused 拒绝）。
        /// 因此这里按状态决定是否先 RequestStop —— 不能只调 RequestSong 了事。
        /// </summary>
        private void PlayIndex(SongKey song, string? title)
        {
            Diag($"PlayIndex {song}（状态 {_controller.Status}，列表 {_results.Count} 首，index={_index}）");

            // 只有从「电台」列表点歌才让队列进入电台续取模式；
            // 从搜索/歌单/推荐点歌都关掉它，否则队列见底时会莫名又续一批电台歌。
            _radioFeedActive = _view == ListView.Radio;
            _onRadioFeed(_radioFeedActive);
            if (_controller.Status == PlaybackStatus.Paused) _controller.RequestStop();

            // 把当前搜索结果灌进播放队列：顺序/随机模式的「下一首从哪来」全靠它。
            // startIndex 指向被点的那首，这样「下一首」天然从它往下走。
            if (_results.Count > 0)
            {
                var keys = new List<SongKey>(_results.Count);
                var titles = new List<(SongKey, string)>(_results.Count);
                foreach (var r in _results)
                {
                    keys.Add(r.Key);
                    titles.Add((r.Key, $"{r.Name} — {r.Artist}"));
                }
                _controller.RememberTitles(titles);
                _controller.Queue.SetItems(keys, _index >= 0 ? _index : 0);
            }

            _onPlayRequested(song, title);
        }

        // ---------------- 显示刷新 ----------------

        private void RefreshHeader()
        {
            var who = IsLoggedIn
                ? Lang.Get("vscloudmusic:logged-in")
                : Lang.Get("vscloudmusic:logged-out");
            var title = string.IsNullOrEmpty(_controller.CurrentTitle)
                ? Lang.Get("vscloudmusic:nothing-playing")
                : _controller.CurrentTitle!;

            // 表头必须写明**当前音源名**：多音源并存时「搜出来的是哪一家、
            // 歌单是谁的」全靠这里分辨，否则切换后极易困惑。
            var source = _providers.Active?.DisplayName ?? "";
            SetText("header", Lang.Get("vscloudmusic:header", source, who, title));
            // 按钮文案随状态变：停在 Stopped/Failed 时它实际是「播放」，标成「暂停」会让人
            // 以为按钮失效（本按钮现在确实能从停止状态重新开播）。
        }

        private void RefreshProgress()
        {
            var status = _controller.Status switch
            {
                PlaybackStatus.Preparing => Lang.Get("vscloudmusic:status-loading"),
                PlaybackStatus.Playing   => Lang.Get("vscloudmusic:status-playing"),
                PlaybackStatus.Paused    => Lang.Get("vscloudmusic:status-paused"),
                PlaybackStatus.Failed    => Lang.Get("vscloudmusic:status-failed"),
                PlaybackStatus.Stopped   => Lang.Get("vscloudmusic:status-stopped"),
                _                        => Lang.Get("vscloudmusic:status-idle"),
            };

            var text = _controller.CurrentSongId == null
                ? ""
                : $"{status}  {Fmt(_controller.PlayedSeconds)} / {Fmt(_controller.TotalSeconds)}";

            // 播放模式也放在这一行：按钮文字无法动态更新，状态类信息统一由动态文本承载。
            SetText("progress", $"{text} · {Lang.Get("vscloudmusic:mode", ModeName(_controller.Queue.Mode))}");
        }

        private static string Fmt(double seconds)
        {
            if (seconds < 0 || double.IsNaN(seconds)) seconds = 0;
            var t = TimeSpan.FromSeconds(seconds);
            return $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
        }

        /// <summary>
        /// 更新动态文本。
        ///
        /// 必须用 GuiElementDynamicText.SetNewText —— 这是官方唯一支持的「文字变化」途径。
        /// 原先用的是 AddStaticText + SetValue：SetValue 只改字段，**不会重绘 Cairo 纹理**，
        /// 所以按钮/文字只在重新 Compose 时才变（表现为「反复开关才刷新，开一次刷一次」）。
        /// forceRedraw 必须为 true，否则内容变了也不重烤。
        /// </summary>
        private void SetText(string key, string text)
        {
            var el = SingleComposer?.GetDynamicText(key);
            if (el == null) return;
            if (el.GetText() == text) return;   // 脏检查：避免每帧重烤纹理
            el.SetNewText(text, false, true, false);
        }

        // 注：这里原来有个 SetButton(key, text) 动态改按钮文字，已删除。
        // GuiElementTextButton 的文字在 compose 时烤进纹理，官方模组从不动态改它
        // （只用 Enabled/Visible）；动态内容一律走 AddDynamicText。
        // 播放状态与播放模式现在都显示在 "progress" 这条动态文本里。

        /// <summary>
        /// 重设结果富文本。注意 SetNewText 必须同时给出字体与点击回调 ——
        /// AddRichtext 时注册的回调不会自动延续到这里，漏传会导致点击链接无反应。
        /// </summary>
        private void SetResultsText(string vtml)
        {
            var el = SingleComposer?.GetRichtext("results");
            if (el == null) return;
            el.SetNewText(vtml, CairoFont.WhiteSmallText(), OnResultClicked);


            // 内容高度由 richtext 自己算出来（TotalHeight），不用我们估。
            // SetHeights 收「可见高度、内容总高度」两个值 —— 相同则滚动条自动禁用。
            var sb = SingleComposer!.GetScrollbar("scrollbar");
            if (sb != null)
            {
                // 行程必须覆盖**内容真实高度**，否则拉到最底仍有几行够不到（实测反馈）。
                // 取两者的较大值：Bounds.fixedHeight 由 richtext 撑高时可能滞后一拍
                // （SetNewText 之后立刻读到的仍是上一次的值），TotalHeight 是它自己算的
                // 内容高度；取大者才能保证一定能滚到底。
                var content = (float)Math.Max(ResultsViewHeight,
                    Math.Max(el.Bounds.fixedHeight, el.TotalHeight));
                sb.SetHeights(ResultsViewHeight, content);
                sb.SetScrollbarPosition(0);   // 换了一批内容就回到顶部，否则会停在上一次的偏移上
            }
            OnScrollResults(0);
        }

        /// <summary>
        /// 滚动回调：把结果区的 bounds 整体上下平移（照官方 GuiDialogJournal 的
        /// OnNewScrollbarvalue：fixedY = -value 后必须 CalcWorldBounds 才生效）。
        /// </summary>
        private void OnScrollResults(float value)
        {
            var el = SingleComposer?.GetRichtext("results");
            if (el == null) return;
            // 相对裁剪区定位，故滚动位移就是单纯的 -value，不再叠加基准 Y。
            el.Bounds.fixedY = 0 - value;
            el.Bounds.CalcWorldBounds();
        }

        /// <summary>
        /// 歌名/歌手来自服务端，必须转义后再喂给 VTML 富文本 ——
        /// 否则名字里出现尖括号就会破坏排版甚至注入标签。
        /// </summary>
        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }
    }
}
