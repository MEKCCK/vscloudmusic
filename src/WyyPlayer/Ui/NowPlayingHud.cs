using System;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using WyyPlayer.Core.Playback;
using WyyPlayer.Api;

namespace WyyPlayer.Ui
{
    /// <summary>
    /// 常驻播放状态 HUD：封面 + 歌名 + 进度条 + 时间。面板打开时整体隐藏
    /// （这些信息面板里都有，重复显示只会挡住原版 UI）。
    ///
    /// 布局与绘制方式参照成熟模组 HudClockPatch（反编译 1.1.2 核实）：
    ///   1) 根用 ElementStdBounds.AutosizedMainDialog.WithAlignment(位置) 自动撑开；
    ///   2) 子元素用 ElementBounds.Fixed 排布，背景用 FitToChildren 的 Fill 包住全部子元素；
    ///   3) 封面是任意位图，只能走自定义绘制（AddStaticCustomDraw + SurfaceDrawImage）；
    ///   4) 由 RegisterGameTickListener 定时重组，而不是每帧 —— 每帧重排会掉帧。
    ///
    /// HudElement 继承自 GuiDialog，composer / CairoFont / 布局全部可复用。
    /// </summary>
    internal sealed class NowPlayingHud : HudElement
    {
        private const int CoverSize = 64;    // 封面边长
        private const int TextWidth = 200;   // 文字区宽度
        private const int BarHeight = 6;     // 进度条高度
        private const int Pad = 6;           // 外padding
        private const int LineGap = 2;

        private readonly PlaybackController _controller;
        private readonly CoverArtCache _covers;
        private readonly ICoreClientAPI _capi;
        private readonly EnumDialogArea _position;

        private bool _panelOpen;
        private long _tickListener;

        // 脏检查缓存：显示输入全都没变时不重组（重组会重排文字，代价高）
        private PlaybackStatus _lastStatus;
        private string _lastTitle = "";
        private SongKey? _lastSong;
        private PlayMode _lastMode;
        private bool _lastHadCover;

        public NowPlayingHud(ICoreClientAPI capi, PlaybackController controller,
            CoverArtCache covers, EnumDialogArea position) : base(capi)
        {
            _capi = capi;
            _controller = controller;
            _covers = covers;
            _position = position;

            // 本 API 版本的 HudElement.DialogType 是只读 override，恒返回 HUD ——
            // 基类已保证类型，无需也不能赋值。

            Compose();

            // 250ms 重组一次：进度以秒计，250ms 足够顺滑，又远低于每帧重排的代价。
            _tickListener = capi.Event.RegisterGameTickListener(OnTick, 250);
        }

        /// <summary>面板开合：面板开时隐藏 HUD，关时恢复。</summary>
        public void SetPanelOpen(bool open)
        {
            if (_panelOpen == open) return;
            _panelOpen = open;
            // 立即重排，不等下一个 tick
            Compose();
        }

        private void OnTick(float dt)
        {
            if (_controller.CurrentSong is { } cur) _covers.Track(cur);

            if (_panelOpen) return;

            // 只有「换歌 / 换标题 / 状态变化 / 模式变化 / 封面就绪」才需要重排，
            // 进度推进**不再是重排的理由** —— 动态文本与 Statbar 都能就地更新。
            var hadCover = _covers.Current != null;
            var needsRecompose = _controller.Status != _lastStatus
                || _controller.CurrentSong != _lastSong
                || !string.Equals(_controller.CurrentTitle, _lastTitle)
                || _controller.Queue.Mode != _lastMode
                || hadCover != _lastHadCover;

            _lastStatus = _controller.Status;
            _lastSong = _controller.CurrentSong;
            _lastTitle = _controller.CurrentTitle ?? "";
            _lastMode = _controller.Queue.Mode;
            _lastHadCover = hadCover;

            if (needsRecompose) Compose();
            else PushValues();
        }

        private void Compose()
        {
            var show = !_panelOpen && _controller.CurrentSongId != null;

            if (!show)
            {
                // 绝不能组装一个「没有子元素」的 autosize 对话框：引擎在算尺寸时抛
                // 「Cant build bounds from children elements, there are no children!」，
                // 而且每帧刷一次。正确做法是直接关掉 —— HudElement 支持 TryClose/TryOpen。
                //
                // 注：本 API 版本上只注册不打开的对话框永远不会被渲染（每帧渲染只遍历
                // OpenedGuis），所以「想要它出现」就必须 TryOpen —— 见下方。
                if (IsOpened()) TryClose();
                return;
            }

            if (SingleComposer == null)
            {
                // 照搬 HudClockPatch：自动撑开的根 + 指定对齐
                var dialogBounds = ElementStdBounds.AutosizedMainDialog
                    .WithAlignment(_position)
                    .WithFixedPadding(Pad);
                SingleComposer = _capi.Gui.CreateCompo("vscloudmusic-hud", dialogBounds);
            }
            else
            {
                SingleComposer.Clear(SingleComposer.Bounds);
            }

            int textX = CoverSize + Pad;
            int textH = (CoverSize - BarHeight - LineGap) / 2;

            var cover = ElementBounds.Fixed(0, 0, CoverSize, CoverSize);
            var title = ElementBounds.Fixed(textX, 0, TextWidth, textH);
            var time = ElementBounds.Fixed(textX, textH, TextWidth, textH);
            var bar = ElementBounds.Fixed(textX, CoverSize - BarHeight, TextWidth, BarHeight);

            // ElementBounds.Fill 是属性（每次取都新建实例），赋值给局部再改是安全的
            var bg = ElementBounds.Fill;
            bg.BothSizing = ElementSizing.FitToChildren;
            bg.WithChildren(cover, title, time, bar);

            SingleComposer
                .AddShadedDialogBG(bg, false, 0.0, 0.6f)
                .AddDynamicText("", CairoFont.WhiteSmallText(), title, "title")
                .AddDynamicText("", CairoFont.WhiteDetailText(), time, "time")
                .AddStaticCustomDraw(cover, new DrawDelegateWithBounds(DrawCover))
                // 进度条用官方控件 GuiElementStatbar（官方 HudBosshealthBars 的做法），
                // 不再自己用 Cairo 画矩形：样式/缩放由引擎保证，而且 SetValues 更新数值
                // 不需要重排 —— 这正是「每秒整体重排」该被替换掉的原因。
                .AddStatbar(bar, GuiStyle.HealthBarColor, "progressbar")
                .Compose();

            PushValues();

            // 有内容才打开（原版 HudEntityNameTags 同样在构造末尾 TryOpen）。
            if (!IsOpened()) TryOpen();
        }

        /// <summary>
        /// 把当前状态下发到三个元素。全部走「不重排」的更新方式：
        /// 动态文本 SetNewText、进度条 SetValues —— 因此 HUD 组装一次就够了，
        /// 不需要像原来那样每隔一段时间整体重排。
        /// </summary>
        private void PushValues()
        {
            var composer = SingleComposer;
            if (composer == null) return;

            var titleEl = composer.GetDynamicText("title");
            if (titleEl != null)
            {
                var t = BuildTitle();
                if (titleEl.GetText() != t) titleEl.SetNewText(t, false, true, false);
            }

            var timeEl = composer.GetDynamicText("time");
            if (timeEl != null)
            {
                var t = BuildSubText();
                if (timeEl.GetText() != t) timeEl.SetNewText(t, false, true, false);
            }

            var barEl = composer.GetStatbar("progressbar");
            if (barEl != null)
            {
                var total = (float)Math.Max(0, _controller.TotalSeconds);
                var played = (float)Math.Max(0, _controller.PlayedSeconds);
                if (total > 0) barEl.SetValues(Math.Min(played, total), 0, total);
            }
        }

        private string BuildTitle()
        {
            var title = string.IsNullOrEmpty(_controller.CurrentTitle)
                ? $"#{_controller.CurrentSongId}"
                : _controller.CurrentTitle!;
            if (title.Length > 26) title = title.Substring(0, 25) + "…";   // 固定宽度，超长截断
            return title;
        }

        private string BuildSubText()
        {
            var state = _controller.Status switch
            {
                PlaybackStatus.Preparing => Lang.Get("vscloudmusic:status-loading"),
                PlaybackStatus.Paused    => Lang.Get("vscloudmusic:status-paused"),
                PlaybackStatus.Failed    => Lang.Get("vscloudmusic:status-failed"),
                PlaybackStatus.Stopped   => Lang.Get("vscloudmusic:status-stopped"),
                _                        => null,
            };
            var mode = _controller.Queue.Mode switch
            {
                PlayMode.RepeatOne => Lang.Get("vscloudmusic:mode-repeat-one"),
                PlayMode.Sequential => Lang.Get("vscloudmusic:mode-sequential"),
                PlayMode.Shuffle => Lang.Get("vscloudmusic:mode-shuffle"),
                _ => Lang.Get("vscloudmusic:mode-heartbeat"),
            };

            var total = _controller.TotalSeconds;
            var clock = total > 0
                ? $"{Fmt(_controller.PlayedSeconds)}/{Fmt(total)} "
                : "";
            return state != null ? $"{clock}{state}·{mode}" : $"{clock}{mode}";
        }

        private static string Fmt(double sec)
        {
            if (sec < 0) sec = 0;
            var t = (int)sec;
            return $"{t / 60}:{t % 60:00}";
        }

        /// <summary>封面：用 SurfaceDrawImage 把 BitmapRef 贴到 Cairo 表面上。</summary>
        private void DrawCover(Context ctx, ImageSurface surface, ElementBounds b)
        {
            var x = (int)b.drawX;
            var y = (int)b.drawY;
            var w = (int)b.InnerWidth;
            var h = (int)b.InnerHeight;

            var bmp = _covers.Current;
            if (bmp != null)
            {
                try
                {
                    // 必须限定命名空间：Cairo 里也有同名的 SurfaceDrawImage
                    Vintagestory.API.Common.SurfaceDrawImage.Image(surface, bmp, x, y, w, h);
                    return;
                }
                catch (Exception e)
                {
                    // 绘制失败不能拖垮 HUD 渲染循环：退回占位框
                    _capi.Logger.Debug($"[vscloudmusic] 封面绘制失败：{e.GetType().Name}");
                }
            }

            // 占位框：让「还在下载」和「布局错位」能一眼区分
            ctx.SetSourceRGBA(1, 1, 1, 0.10);
            ctx.Rectangle(x, y, w, h);
            ctx.Fill();
        }


        // 以下覆写全部照官方 HUD 的写法（refs/vssurvivalmod/Gui/HudBosshealthBars.cs）。
        // 这些不是装饰：它们决定 HUD 会不会抢焦点、抢鼠标、被热键意外开合。

        /// <summary>HUD 不允许被热键开合（官方 HUD 一律返回 null）。</summary>
        public override string? ToggleKeyCombinationCode => null;

        /// <summary>输入优先级：HUD 取 1，保证它在普通对话框之前拿到判断机会。</summary>
        public override double InputOrder => 1;

        public override bool ShouldReceiveKeyboardEvents() => false;
        public override bool ShouldReceiveMouseEvents() => false;
        public override bool CaptureAllInputs() => false;

        /// <summary>HUD 不可聚焦 —— 否则它会抢走键盘焦点，使玩家在游戏里按键失灵。</summary>
        public override bool Focusable => false;

        /// <summary>不可聚焦，因此焦点变化无事可做（官方同样留空覆写）。</summary>
        protected override void OnFocusChanged(bool on) { }

        /// <summary>不可点击：鼠标事件不落到 HUD 上（官方同样留空覆写）。</summary>
        public override void OnMouseDown(MouseEvent args) { }

        public override void Dispose()
        {
            if (_tickListener != 0)
            {
                _capi.Event.UnregisterGameTickListener(_tickListener);
                _tickListener = 0;
            }
            base.Dispose();
        }
    }
}
