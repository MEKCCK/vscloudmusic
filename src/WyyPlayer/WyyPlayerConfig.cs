using Vintagestory.API.Client;
using WyyPlayer.Core.Playback;

namespace WyyPlayer
{
    /// <summary>
    /// 模组设置，用官方推荐的 <c>StoreModConfig</c> / <c>LoadModConfig</c> 落盘
    /// （照 refs/vssurvivalmod/Systems/Handbook/Tutorial/TutorialBase.cs 的用法）。
    ///
    /// 之前用的是自己写的一个 playmode.txt 纯文本文件 —— 那也是能跑的，但偏离官方约定：
    /// 官方设置统一落在 ModConfig 目录下、统一由引擎负责序列化与容错，
    /// 玩家要手改或备份时位置也一致。
    /// </summary>
    internal sealed class WyyPlayerConfig
    {
        /// <summary>播放模式。</summary>
        public PlayMode Mode { get; set; } = PlayMode.Sequential;

        /// <summary>
        /// HUD 位置：lefttop / righttop / leftbottom / rightbottom。
        ///
        /// 默认左上角：右上角被原版小地图占着，放那里会叠上去，
        /// 而且会让小地图下方那个坐标/方位 HUD 跟着错位（实测反馈）。
        /// </summary>
        public string HudPosition { get; set; } = "lefttop";

        /// <summary>
        /// 播放音量（0-100）。默认 50 —— 网易的源本身已是响度最大化的
        /// （峰值逼近 0 dBFS、波峰因数仅约 13 dB），而浏览器/流媒体播放器普遍做
        /// 响度归一化（约 -14 LUFS），实际电平比我们低不少。同一条音轨我们放出来
        /// 更响、低音更冲，接低音炮时最容易过载 —— 所以默认留出余量并允许玩家调。
        /// </summary>
        public int Volume { get; set; } = 50;


        private const string FileName = "vscloudmusic.json";

        /// <summary>读取设置。文件不存在或损坏时返回默认值，绝不让配置问题阻断模组加载。</summary>
        public static WyyPlayerConfig Load(ICoreClientAPI api)
        {
            try
            {
                return api.LoadModConfig<WyyPlayerConfig>(FileName) ?? new WyyPlayerConfig();
            }
            catch
            {
                // 配置损坏不该阻断加载 —— 用默认值继续，下次保存会覆盖掉坏文件。
                return new WyyPlayerConfig();
            }
        }

        public void Save(ICoreClientAPI api)
        {
            try
            {
                api.StoreModConfig(this, FileName);
            }
            catch
            {
                // 存不下设置不是致命错误（例如磁盘只读）：不影响本次会话的可用性。
            }
        }

        public EnumDialogArea ResolveHudPosition() => HudPosition?.ToLowerInvariant() switch
        {
            "lefttop" => EnumDialogArea.LeftTop,
            "leftbottom" => EnumDialogArea.LeftBottom,
            "rightbottom" => EnumDialogArea.RightBottom,
            "righttop" => EnumDialogArea.RightTop,
            _ => EnumDialogArea.LeftTop,
        };
    }
}
