namespace WyyPlayer.Core
{
    /// <summary>
    /// Core 侧的日志抽象。
    ///
    /// 为什么不用游戏的 ILogger：Core 禁止引用任何游戏程序集（可离线单测的前提）。
    /// 模组侧用一个小适配器把 ILogger 包成这个接口即可。
    ///
    /// 铁律：任何凭据（cookie / MUSIC_U / token）都不得经此输出。
    /// </summary>
    public interface ILog
    {
        void Notification(string message);
        void Warning(string message);
        void Error(string message);
        void Debug(string message);
    }
}
