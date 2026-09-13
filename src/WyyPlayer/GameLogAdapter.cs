using Vintagestory.API.Common;
using WyyPlayer.Core;

namespace WyyPlayer
{
    /// <summary>
    /// 把游戏的 <see cref="ILogger"/> 适配成 Core 的 <see cref="ILog"/>。
    ///
    /// Core 禁止引用游戏程序集（可离线单测的前提），所以不能直接用 ILogger；
    /// 这层适配器是唯一的耦合点，只有 4 个方法。
    /// </summary>
    internal sealed class GameLogAdapter : ILog
    {
        private readonly ILogger _log;

        public GameLogAdapter(ILogger log) => _log = log;

        public void Notification(string message) => _log.Notification(message);
        public void Warning(string message) => _log.Warning(message);
        public void Error(string message) => _log.Error(message);
        public void Debug(string message) => _log.Debug(message);
    }
}
