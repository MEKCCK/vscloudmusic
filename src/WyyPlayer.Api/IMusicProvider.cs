using System;
using System.Threading;
using System.Threading.Tasks;

namespace WyyPlayer.Api
{
    /// <summary>
    /// 一次交互式登录过程。宿主负责**显示**与**轮询**，具体怎么登由音源决定 ——
    /// 因为各家的登录方式根本不同（网易是扫码、QQ 音乐是另一套跳转/授权），
    /// 定成 LoginAsync(user, password) 这种签名会把所有音源都限制死。
    ///
    /// 参考：AllMusic 的 IMusicApi 用 command(sender, name, args) 让音源自己处理
    /// 任意子命令（登录也能塞进去），但那样宿主无法统一呈现登录界面。这里取两者的中间：
    /// 宿主统一显示「一句话说明 + 可选二维码」，音源决定内容与完成时机。
    /// </summary>
    public interface ILoginFlow
    {
        /// <summary>给玩家看的操作说明，例如「用手机 App 扫描二维码」。</summary>
        string Instruction { get; }

        /// <summary>
        /// 二维码的**位矩阵**：<c>[y][x]</c>，true 表示黑格。不需要二维码的登录方式返回 null。
        ///
        /// 为什么不给图片：设计文档 §4.3 已定「只算位矩阵，用 GUI 逐格画方块」——
        /// 宿主直接把矩阵画成方块即可，任何一侧都不需要图像编码器，也不必让 API 程序集
        /// 依赖图形库。（我最初写成 PNG 字节，是要为此手写一个图像编码器，属于走错路。）
        /// </summary>
        bool[][]? QrMatrix { get; }

        /// <summary>
        /// 查询是否已完成。宿主每隔一段时间调一次，直到返回非 <see cref="LoginPollState.Pending"/>。
        /// 实现方内部自行节流，不必依赖宿主的调用频率。
        /// </summary>
        Task<LoginPollState> PollAsync(CancellationToken ct);
    }

    public enum LoginPollState
    {
        /// <summary>还没完成，继续等。</summary>
        Pending,
        /// <summary>已登录成功，宿主应关闭登录界面并刷新账户区。</summary>
        Succeeded,
        /// <summary>二维码已过期，需要重新发起登录。</summary>
        Expired,
        /// <summary>失败（含玩家取消）。<see cref="ILoginFlow.Instruction"/> 应给出原因。</summary>
        Failed,
    }

    /// <summary>
    /// 宿主提供给音源的运行环境。音源不直接接触游戏 API ——
    /// 它是独立的契约实现，只通过这里拿配置目录与日志。
    /// </summary>
    public interface IProviderContext
    {
        /// <summary>该音源专属的配置目录（已创建）。音源把凭据等写在这里。</summary>
        string ConfigDirectory { get; }

        /// <summary>写日志。音源**绝不可**把凭据写进日志。</summary>
        void Log(string message);
    }

    /// <summary>
    /// 音乐源。附属模组的入口契约 —— 在它自己的 ModSystem.StartClientSide 里
    /// 调 <c>api.ModLoader.GetModSystem&lt;WyyPlayerMod&gt;().RegisterProvider(...)</c> 注册。
    ///
    /// 方法集合参照 AllMusic 的 IMusicApi（getId / checkId / getMusic / search /
    /// getPlayUrl / getLyric / isBusy / reload），并按「宿主统一呈现」的需要做了调整：
    /// 用带源标识的 <see cref="SongKey"/> 取代 checkId 的「猜归属」，用统一的登录流程
    /// 取代 command 的自由子命令。
    ///
    /// 线程约定：所有方法都可能被后台线程调用，实现方必须自己保证线程安全；
    /// **不得**在实现里触碰任何游戏 API。
    /// </summary>
    public interface IMusicProvider
    {
        /// <summary>音源标识，需全局唯一（如 "netease"）。会被写进 SongKey 并持久化。</summary>
        string Id { get; }

        /// <summary>界面上显示的名字。</summary>
        string DisplayName { get; }

        ProviderCapabilities Capabilities { get; }

        /// <summary>宿主在建好配置目录后、使用前调用一次。</summary>
        void Initialize(IProviderContext context);

        /// <summary>当前账号状态。界面每次刷新都会读它，实现应当廉价且不阻塞。</summary>
        ProviderAccount Account { get; }

        /// <summary>搜索。分页由 offset/limit 表达。</summary>
        Task<SearchPage> SearchAsync(string keyword, int limit, int offset, CancellationToken ct);

        /// <summary>取流媒体地址。返回 null 表示不可播（无版权/VIP 限制/已下架）。</summary>
        Task<string?> GetStreamUriAsync(SongKey song, AudioQuality quality, CancellationToken ct);

        /// <summary>
        /// 取封面 URL。返回 null 表示没有。
        /// 与 SearchAsync 分开是因为很多音源的搜索响应里不带封面
        /// （网易云就是如此），必须在播放时补一次详情请求。
        /// </summary>
        Task<string?> GetCoverUrlAsync(SongKey song, CancellationToken ct);

        /// <summary>
        /// 取指定歌曲的完整信息（含封面与时长）。
        /// 队列里只存键，播放时用它补齐显示信息。
        /// </summary>
        Task<SongInfo?> GetSongInfoAsync(SongKey song, CancellationToken ct);

        /// <summary>取当前账号的歌单列表。未声明 <see cref="ProviderCapabilities.UserLibrary"/> 时不会被调用。</summary>
        Task<System.Collections.Generic.IReadOnlyList<CollectionInfo>> GetUserCollectionsAsync(CancellationToken ct);

        /// <summary>取某个歌单的全部曲目。</summary>
        Task<System.Collections.Generic.IReadOnlyList<SongInfo>> GetCollectionTracksAsync(string collectionId, CancellationToken ct);

        /// <summary>
        /// 以某首歌为种子取相似歌曲。未声明 <see cref="ProviderCapabilities.SimilarSongs"/> 时不会被调用。
        /// </summary>
        Task<System.Collections.Generic.IReadOnlyList<SongKey>> GetSimilarSongsAsync(SongKey seed, int count, CancellationToken ct);

        /// <summary>
        /// 取一批推荐歌曲（「推荐」页）。未声明 <see cref="ProviderCapabilities.Recommendations"/>
        /// 时不会被调用 —— 宿主会直接隐藏入口。
        /// </summary>
        Task<System.Collections.Generic.IReadOnlyList<SongInfo>> GetRecommendedSongsAsync(
            CancellationToken ct);

        /// <summary>
        /// 取一批电台歌曲（「电台」页）。与推荐页的区别：电台是**可持续续取**的流，
        /// 队列耗尽时宿主会再来取一批。未声明 <see cref="ProviderCapabilities.Radio"/>
        /// 时不会被调用。
        /// </summary>
        Task<System.Collections.Generic.IReadOnlyList<SongInfo>> GetRadioSongsAsync(
            CancellationToken ct);

        /// <summary>
        /// 发起一次交互式登录，返回已完成初始化的流程（说明与二维码矩阵都已就绪）。
        /// 未声明 <see cref="ProviderCapabilities.Authentication"/> 时不会被调用。
        ///
        /// 为什么是异步：二维码的获取本身要走一次网络（申请 unikey），
        /// 同步签名根本交不出矩阵，只会迫使宿主去轮询「好了没」。
        /// </summary>
        Task<ILoginFlow> BeginLoginAsync(CancellationToken ct);

        /// <summary>退出登录，并清除该音源保存的凭据。未登录时应当是无害的空操作。</summary>
        Task LogoutAsync(CancellationToken ct);
    }
}
