# 许可证审查

本文件记录本项目对外部代码的依赖与参考情况，以及「是否存在代码复制」的审查依据。
结论：**未复制任何一行代码**；与外部项目的重合仅为**网易云协议的客观事实**
与不可避免的通用概念。

审查日期：2026-09-14

---

## 一、运行时依赖（均为 MIT，与 GPLv3 兼容）

| 包 | 许可证 | 作者 |
|---|---|---|
| NLayer | MIT | Mark Heath, Andrew Ward |
| Newtonsoft.Json | MIT | James Newton-King |
| Net.Codecrete.QrCodeGenerator | MIT | Manuel Bleichenbacher |

三者均作为独立程序库被引用，未修改其源码。

**游戏程序集**（VintagestoryAPI、OpenTK、cairo-sharp）被引用但**不随本模组分发**，
由玩家自备的游戏提供。

---

## 二、对齐的参考实现：`@neteasecloudmusicapienhanced/api`

**许可证：MIT**（已向 npm registry 核实：`license: MIT`，
仓库 `github.com/NeteaseCloudMusicApiEnhanced/api-enhanced`）。

本项目的加密流程与 HTTP 细节**对齐此实现**，因为它是最完整、且许可证宽松的参考：

- WEAPI / EAPI 的步骤与各端点选用哪一套加密
- EAPI header 字段及其缺省值（`os=pc`、`appver=3.1.17.204416`、
  `osver=Microsoft-Windows-10-Professional-build-19045-64bit`、`channel=netease`）
- UA 选择规则（eapi 默认 iPhone UA，os=osx 时用 Mac Chrome UA）
- `requestId` 的 `epochMillis_4位随机数` 格式

该实现是 **JavaScript**、运行于 Node；本项目是 **C#**、运行于 .NET。
两者不存在逐行对应的可能，本项目为独立编写。

---

## 三、仅参考设计、未复制代码的项目

### AllMusic（`github.com/Coloryr/AllMusic`，GPL-3.0）

参考了「宿主与音乐源分离、以能力位声明所支持功能」这一**架构思路**。

**未复制**其插件机制：AllMusic 用 `MusicApiLoader` 从目录加载 JAR
（`URLClassLoader` + `version` 文件做兼容检查）；本项目**没有插件加载器**，
音乐源由 VS 的模组系统加载后调用 `RegisterProvider` 注册。

接口对比（AllMusic `IMusicApi` 12 个方法 vs 本项目 `IMusicProvider`）：

| AllMusic | 本项目 |
|---|---|
| `reload(File)` | 无（用 `IProviderContext.ConfigDirectory`） |
| `getId()` | `Id` |
| `getMusic(id, player, isList)` | `GetSongInfoAsync(SongKey, ct)` |
| `search(String[])` | `SearchAsync(kw, limit, offset, ct)` |
| `setList(id, sender)` | 无（队列归宿主） |
| `getLyric(id)` | 无（能力位保留未实现） |
| `getPlayUrl(id)` | `GetStreamUriAsync(SongKey, AudioQuality, ct)` |
| `isBusy()` | 无（异步 + CancellationToken） |
| `getMusicId(arg)` | 无 |
| `checkId(id)` | 无（改用 `SongKey` 携带源标识） |
| `command(...)` / `tab(...)` | 无（改用 `ILoginFlow`） |
| — | `Capabilities` / `Account` / `Initialize` / `GetCoverUrlAsync` / `GetUserCollectionsAsync` / `GetCollectionTracksAsync` / `GetSimilarSongsAsync` / `GetRecommendedSongsAsync` / `GetRadioSongsAsync` / `BeginLoginAsync` / `LogoutAsync` |

本项目的 `checkId`（猜归属）与 `command`/`tab`（自由子命令）是**刻意不采用**的
——分别用带源标识的 `SongKey` 与统一的 `ILoginFlow` 取代。

### Folia（`github.com/chthollyphile/folia-major`，AGPL-3.0）

参考了 provider 契约的划分方式与队列推进的若干做法。**未复制代码。**

队列推进对比：

| Folia（TypeScript，`usePlaybackQueueController.ts`） | 本项目（C#） |
|---|---|
| `handleNextTrack` 内**内联**索引算术 | 索引算术封装在独立的 `PlayQueue` 类（`NextSequential`/`NextShuffled`/`Previous`） |
| `loopMode: 'off'\|'one'\|'all'` 字符串 | `PlayMode` 枚举三值 |
| 用 `playQueue.findIndex` 反查游标 | 游标 + `SyncTo` 在开播时对齐 |
| `isFmMode` + `getPersonalFm()`，提前量 `length - 2` | `_radioFeed` + `GetRadioSongsAsync()`，**到队尾才补** |
| `stopAtQueueEnd()` 闭包 | `Teardown()` + `NotifyStopped()` |

两者都是「队列推进」这一必然概念的不同实现，不存在逐行对应。

### netapi（`github.com/Coloryr/netapi`，AGPL-3.0）

参考了网易云接口的调用方式。**未复制代码**，加密实现逐函数比对如下：

| netapi（Java） | 本项目（C#） |
|---|---|
| `Cipher.getInstance("AES/CBC/PKCS5Padding")`，`try/catch` 吞异常 + `printStackTrace` | `Aes.Create()` + 属性赋值 + `TransformFinalBlock`，**异常上抛** |
| `strToHex`：`Integer.toHexString(ch)` **不补零**，逐字符码位转 hex 后当大整数解析 | **无此函数**，直接用 `Encoding.UTF8.GetBytes`（算法不同） |
| RSA 模数**硬编码**为十六进制字符串 | 从 **PEM 公钥解析**（`RSA.ImportSubjectPublicKeyInfo`） |
| `Math.random()` 生成密钥 | `RandomNumberGenerator.GetInt32`（密码学安全） |
| `byteArrToHex` 手写半字节循环 + `hexArray` | `Convert.ToHexString`（BCL） |
| `zFill` 循环 `insert` | `PadLeft(256, '0')` |
| 内联方法 + 返回 `EncResObj` | 泛化原语（`AesCbcBase64`/`AesEcbHexUpper`/`AesCbcDecryptBase64`/`AesEcbDecryptHex`）+ `record struct` 返回 |
| EAPI header 用 `os=android` + 华为浏览器 UA | `os=pc` + iPhone UA（对齐 MIT 的 JS 参考实现） |
| `url.replaceFirst("\\w*api", "weapi")` | 字符串拼接 `"/weapi/" + uri.Replace("api/","")` |
| 无重试 | 串行化 + 250ms 最小间隔 + 4 次指数退避重试（本项目独有） |

---

## 四、重合部分为何不构成复制

唯一的重合是**网易云协议的客观事实**，任何实现都必须一致，属于事实性信息：

- 固定密钥与 IV：`0CoJUm6Qyw8W8jud`、`0102030405060708`、`e82ckenh8dichen8`
- base62 字母表
- EAPI 拼接分隔符 `-36cd479b6b5-` 与模板 `nobody{url}use{data}md5forencrypt`
- RSA 公钥（以 PEM 形式取自 RFC 5280 SubjectPublicKeyInfo）
- 取值受限的 header 字段（`os`/`appver`/`channel` 等）

这些常量在所有网易云接口实现中都相同，且为功能性必需 —— 改变其中任何一个，
请求就会失败。它们不表达任何可受版权保护的创造性选择。

---

## 五、结论

**本项目未复制任何外部项目的代码。** 与 AGPL-3.0 项目（Folia、netapi）的重合
仅为协议事实与通用概念，不构成衍生作品；因此本项目采用 GPL-3.0 不产生许可冲突。

如有异议，欢迎提 issue —— 上述比对依据均可复现。
