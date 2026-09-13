using System;
using System.Collections.Generic;
using WyyPlayer.Api;

namespace WyyPlayer.Core.Providers
{
    /// <summary>
    /// 已注册音乐源的集合，以及「当前使用哪一个」。
    ///
    /// 参照 AllMusic 的 AllMusicApi（静态表 + 按 id 取用 + 配置里的 defaultApi），
    /// 但这里是**实例**而非静态：静态注册表让测试之间互相污染，也无法在一个进程里
    /// 并存两套配置。
    ///
    /// 线程约定：注册与切换活跃源只在主线程（模组加载、UI 操作）发生；
    /// 查找可能来自后台线程，故用锁保护内部的字典与列表快照。
    /// </summary>
    public sealed class MusicProviderRegistry
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, IMusicProvider> _byId =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly List<IMusicProvider> _ordered = new();
        private string? _activeId;

        /// <summary>按注册顺序列出全部音源（供 UI 呈现）。</summary>
        public IReadOnlyList<IMusicProvider> All
        {
            get { lock (_lock) return _ordered.ToArray(); }
        }

        /// <summary>
        /// 注册一个音源。id 重复时**覆盖**并保留原位置 —— 重复注册最常见的原因是
        /// 附属模组在每次进入世界时都建新实例，覆盖比报错更符合预期。
        /// </summary>
        public void Register(IMusicProvider provider)
        {
            if (provider == null) throw new ArgumentNullException(nameof(provider));

            lock (_lock)
            {
                if (_byId.TryGetValue(provider.Id, out var existing))
                {
                    var i = _ordered.IndexOf(existing);
                    if (i >= 0) _ordered[i] = provider;
                }
                else
                {
                    _ordered.Add(provider);
                }
                _byId[provider.Id] = provider;

                // 第一个注册的自动成为活跃源，避免「注册了却没人用」的空窗。
                _activeId ??= provider.Id;
            }
        }

        public IMusicProvider? Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            lock (_lock) return _byId.TryGetValue(id, out var p) ? p : null;
        }

        public bool IsRegistered(string id)
        {
            lock (_lock) return _byId.ContainsKey(id);
        }

        /// <summary>
        /// 当前活跃音源。id 失效（例如附属模组被卸载）时回退到第一个已注册的，
        /// 绝不返回 null 除非一个都没注册。
        /// </summary>
        public IMusicProvider? Active
        {
            get
            {
                lock (_lock)
                {
                    if (_activeId != null && _byId.TryGetValue(_activeId, out var p)) return p;
                    return _ordered.Count > 0 ? _ordered[0] : null;
                }
            }
        }

        /// <summary>切换活跃音源。未注册的 id 会被忽略并返回 false。</summary>
        public bool SetActive(string id)
        {
            lock (_lock)
            {
                if (!_byId.ContainsKey(id)) return false;
                _activeId = id;
                return true;
            }
        }

        /// <summary>活跃音源的 id；一个都没注册时为 null。</summary>
        public string? ActiveId
        {
            get { lock (_lock) return Active?.Id; }
        }

        /// <summary>
        /// 按 <see cref="SongKey"/> 找归属音源。队列里存的是带源标识的键，
        /// 因此播放时能准确回到「当初是哪一个源给的这首歌」——
        /// 这正是取代 AllMusic 那种 checkId 猜归属的地方。
        /// </summary>
        public IMusicProvider? Resolve(SongKey key) => Get(key.ProviderId);
    }
}
