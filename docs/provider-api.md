# 音乐源扩展 API

本模组把「音乐从哪来」与「怎么播放」分开：宿主负责播放、队列、UI 与缓存，
**音乐源只需提供数据**。第三方模组可以实现 `IMusicProvider` 来接入别的服务
（QQ 音乐、酷狗等）。

契约全部在 **`WyyPlayer.Api`** 程序集里 —— 只有接口与数据类，没有实现、
没有网络代码、不引用游戏程序集。你的模组只需依赖它。

---

## 快速开始

```csharp
using WyyPlayer.Api;

internal sealed class MyProvider : IMusicProvider
{
    public string Id => "myprovider";              // 全局唯一，会被写进 SongKey 并持久化
    public string DisplayName => "我的音源";
    public ProviderCapabilities Capabilities => ProviderCapabilities.Search | ProviderCapabilities.Playback;
    public ProviderAccount Account => ProviderAccount.Anonymous();   // 本例不做登录

    public void Initialize(IProviderContext ctx) { /* ctx.ConfigDirectory / ctx.Log */ }

    public async Task<SearchPage> SearchAsync(string kw, int limit, int offset, CancellationToken ct)
    {
        var songs = await MyHttp.Search(kw, limit, offset, ct);
        return new SearchPage { Songs = songs, TotalCount = songs.Count };
    }

    public Task<string?> GetStreamUriAsync(SongKey song, AudioQuality q, CancellationToken ct)
        => MyHttp.ResolveUrl(song.NativeId, ct);

    public Task<string?> GetCoverUrlAsync(SongKey song, CancellationToken ct)
        => MyHttp.ResolveCover(song.NativeId, ct);

    public Task<SongInfo?> GetSongInfoAsync(SongKey song, CancellationToken ct)
        => Task.FromResult<SongInfo?>(null);        // 可选：补全标题/封面

    // 未声明对应能力时不会被调用，可直接抛或返回空。
    public Task<IReadOnlyList<CollectionInfo>> GetUserCollectionsAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<CollectionInfo>>(Array.Empty<CollectionInfo>());
    public Task<IReadOnlyList<SongInfo>> GetCollectionTracksAsync(string id, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<SongInfo>>(Array.Empty<SongInfo>());
    public Task<IReadOnlyList<SongKey>> GetSimilarSongsAsync(SongKey seed, int n, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<SongKey>>(Array.Empty<SongKey>());
    public Task<IReadOnlyList<SongInfo>> GetRecommendedSongsAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<SongInfo>>(Array.Empty<SongInfo>());
    public Task<IReadOnlyList<SongInfo>> GetRadioSongsAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<SongInfo>>(Array.Empty<SongInfo>());

    public Task<ILoginFlow> BeginLoginAsync(CancellationToken ct)
        => throw new NotSupportedException();       // 未声明 Authentication 就不会被调用

    public Task LogoutAsync(CancellationToken ct) => Task.CompletedTask;
}
```

注册：

```csharp
public class MyModSystem : ModSystem
{
    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        var wyy = api.ModLoader.GetModSystem<WyyPlayerMod>();
        wyy.RegisterProvider(new MyProvider());
    }
}
```

并在你自己模组的 `modinfo.json` 里声明依赖：

```json
{ "dependencies": { "game": "", "vscloudmusic": "" } }
```

---

## 能力位：按需声明

`ProviderCapabilities` 决定宿主**显示哪些入口** —— 没声明的能力，对应按钮
根本不会出现，而不是让玩家点进去才发现不能用。

| 位 | 影响的界面 |
|---|---|
| `Search` | 搜索框与搜索按钮 |
| `Playback` | 播放本身 |
| `UserLibrary` | 「歌单」入口 |
| `Recommendations` | 「推荐」入口 |
| `Radio` | 「电台」入口，并在队列见底时自动续取 |
| `SimilarSongs` | 保留给「以某首为种子继续放」；宿主暂未使用 |
| `Authentication` / `QrLogin` | 「登录」「退出」按钮 |

---

## `SongKey`：为什么不用裸 id

```csharp
public readonly struct SongKey
{
    public string ProviderId { get; }   // "netease"
    public string NativeId   { get; }   // "5257138"
}
```

跨音乐源的 id 空间互相独立，数值撞车完全可能；而且并非所有服务都用数字 id。
队列、缓存、控制器一律以 `SongKey` 为准，播放时因此能准确回到
「当初是哪一个源给的这首歌」。

`SongKey.TryParse("netease:5257138", out var key)` 可在持久化后还原。

---

## 登录：`ILoginFlow`

各家的登录方式差别很大（扫码、跳转授权、账号密码），所以契约不是
`LoginAsync(user, password)`，而是**由音源产出「登录流程」，宿主负责显示与轮询**：

```csharp
public interface ILoginFlow
{
    string Instruction { get; }    // 给玩家看的一句话
    bool[][]? QrMatrix { get; }    // 二维码位矩阵（[y][x]，true 为黑格）；不需要则 null
    Task<LoginPollState> PollAsync(CancellationToken ct);
}
```

`BeginLoginAsync` 返回时，说明与二维码**都已就绪**（所以它是异步的 ——
申请二维码本身要走一次网络）。

宿主会按固定间隔调用 `PollAsync`，直到返回 `Succeeded` / `Expired` / `Failed`。
**实现方应自行节流**，不要把宿主的高频调用直接变成高频请求 —— 那正是触发风控的原因。

二维码以**位矩阵**返回而不是图片：宿主直接用 Cairo 画方块，两侧都不需要图像编码器，
`WyyPlayer.Api` 也就不必依赖任何图形库。

---

## 线程约定

- 所有方法**都可能被后台线程调用**，实现方须自行保证线程安全。
- 实现方**不得**触碰任何游戏 API；需要的资源通过 `IProviderContext` 获取
  （`ConfigDirectory` 用于存放凭据等，`Log` 用于输出日志）。
- **绝不可把凭据写进日志。**

---

## 宿主负责什么

实现者不需要关心这些：

- 播放（解码、缓冲、输出）与播放队列
- 音频缓存（按 `SongKey` 落盘）
- 封面下载与贴图
- 播放模式（单曲/顺序/随机）与自动切歌
- 面板、HUD、快捷键、设置持久化

音乐源只需回答「有哪些歌」「这首歌的地址是什么」。

---

## 一个提醒

网易云接口的字段名在不同端点之间并不一致（搜索用 `artists`/`album`，
歌单详情与每日推荐用 `ar`/`al`，私人 FM 又用 `artists`/`album`）。
接入新音源时请以**实际响应**为准，不要照搬别处的字段名 —— 这类坑本项目踩过多次。
