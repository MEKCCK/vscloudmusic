using System;
using System.Collections.Generic;
using WyyPlayer.Api;

namespace WyyPlayer.Core.Playback
{
    /// <summary>播放模式。</summary>
    public enum PlayMode
    {
        /// <summary>单曲循环：始终重播当前曲。</summary>
        RepeatOne,
        /// <summary>顺序播放：按列表推进，到末尾停下。</summary>
        Sequential,
        /// <summary>随机播放：不重复地随机取歌，取完停下。</summary>
        Shuffle,
    }

    /// <summary>
    /// 播放队列与播放模式。纯逻辑、无线程、无 IO、无网络 —— 因此可脱离游戏单测。
    ///
    /// 为什么需要它：单曲循环只需重播当前曲，但**顺序播放需要一个有序列表**、
    /// **随机播放需要列表并保证不重复**（取完即止，不重新洗牌）。
    /// 没有队列，前三种模式无从谈起。
    ///
    /// 线程约定：与 PlaybackStateMachine 相同，只允许在游戏主线程使用，不做同步。
    /// </summary>
    public sealed class PlayQueue
    {
        private readonly List<SongKey> _items = new();
        private readonly List<SongKey> _history = new();
        private readonly HashSet<SongKey> _played = new();
        private readonly Random _rng;

        private int _index = -1;

        /// <param name="seed">随机种子，仅供测试注入以得到确定性结果。</param>
        public PlayQueue(int? seed = null)
        {
            _rng = seed.HasValue ? new Random(seed.Value) : new Random();
        }

        public PlayMode Mode { get; set; } = PlayMode.Sequential;

        public IReadOnlyList<SongKey> Items => _items;
        public SongKey? Current => _index >= 0 && _index < _items.Count ? _items[_index] : null;

        /// <summary>当前游标位置；为 -1 表示尚未定位。</summary>
        public int CurrentIndex => _index;

        /// <summary>
        /// 当前曲目之后还剩多少首。仅用于「是否该提前续接」的判断（心动模式）。
        /// </summary>
        public int RemainingAfterCurrent =>
            _index < 0 ? _items.Count : Math.Max(0, _items.Count - _index - 1);

        /// <summary>
        /// 把游标强制对齐到「实际正在播放的这首歌」。
        ///
        /// 为什么必须有这个方法：面板自己持有一份列表索引，而玩家可以从搜索结果、
        /// 歌单列表、队列视图、「上一首/下一首」等多个入口点歌，每次都由面板算出索引
        /// 再灌队列 —— 只要有一个入口漏了同步，游标就会与实际在放的歌脱节，
        /// 「下一首」于是跳到错误的位置。
        ///
        /// 参照成熟播放器（Folia，usePlaybackQueueController）的做法：它的 currentIndex
        /// 每次由当前歌曲反查（playQueue.findIndex(...)），不维护独立游标，因而不会漂移。
        /// 本类需要保留游标（随机模式靠 _played 保证不重复），
        /// 所以在「一首歌真正开始播放」时强制对齐 —— 那是唯一有权威的事实来源。
        /// </summary>
        public void SyncTo(SongKey song)
        {
            var i = _items.IndexOf(song);
            if (i < 0) return;   // 不在队列里（例如直接播放的单曲）：不动游标，避免误跳
            _index = i;
            _played.Add(song);
        }

        /// <summary>重设整个列表。索引收敛到合法范围，历史与「已随机过」记录一并清空。</summary>
        public void SetItems(IEnumerable<SongKey> songs, int startIndex = 0)
        {
            _items.Clear();
            if (songs != null)
            {
                foreach (var song in songs)
                    if (!_items.Contains(song)) _items.Add(song);
            }

            _history.Clear();
            _played.Clear();

            if (_items.Count == 0)
            {
                _index = -1;
                return;
            }

            _index = Math.Clamp(startIndex, 0, _items.Count - 1);
            _played.Add(_items[_index]);
        }

        /// <summary>追加歌曲（心动模式续接用）；已存在的 id 会被跳过。</summary>
        public void Append(IEnumerable<SongKey> songs)
        {
            if (songs == null) return;
            foreach (var song in songs)
                if (!_items.Contains(song)) _items.Add(song);
        }

        /// <summary>取下一首。返回 null 表示「没有下一首了」，由调用方决定停止或续接。</summary>
        public SongKey? Next()
        {
            if (_items.Count == 0) return null;

            return Mode switch
            {
                PlayMode.RepeatOne => Current,
                PlayMode.Shuffle => NextShuffled(),
                _ => NextSequential(),
            };
        }

        /// <summary>
        /// 退回上一首。顺序/单曲模式下按**实际播放历史**回退；
        /// 随机模式下与「下一首」同样随机取一首未播过的（玩家期望，见方法内注释）。
        /// </summary>
        public SongKey? Previous()
        {
            // 随机模式下「上一首」也随机取一首未播过的 —— 与「下一首」同一逻辑。
            // （按播放历史回退是多数播放器的做法，但实测玩家期望随机模式下前后都随机：
            //   从 A→C→F 之后按上一首，期望跳到另一首随机的，而不是回到 C。）
            if (Mode == PlayMode.Shuffle) return NextShuffled();

            // 有播放历史就按历史回退（随机/顺序都已在历史里留下足迹）。
            if (_history.Count > 0)
            {
                var last = _history[^1];
                _history.RemoveAt(_history.Count - 1);
                _index = _items.IndexOf(last);
                return Current;
            }

            // 历史为空时退化为「列表里的前一首」。
            // 这一步不可省：面板点列表里的某一首会调 SetItems，而 SetItems 会清空历史 ——
            // 于是「点第 2 首 → 按上一首」本应回到第 1 首，却因为历史为空直接返回 null、
            // 点了毫无反应（实测反馈）。
            if (_index <= 0) return null;
            _index--;
            return Current;
        }

        private SongKey? NextSequential()
        {
            var next = _index + 1;
            if (next >= _items.Count) return null;   // 到末尾：交给调用方决定停止/续接

            _history.Add(_items[_index]);
            _index = next;
            _played.Add(_items[_index]);
            return Current;
        }

        private SongKey? NextShuffled()
        {
            // 从「还没随机到过」的歌里挑；没有剩余即返回 null（不重新洗牌）
            var candidates = new List<int>();
            for (int i = 0; i < _items.Count; i++)
                if (!_played.Contains(_items[i])) candidates.Add(i);

            if (candidates.Count == 0) return null;

            var pick = candidates[_rng.Next(candidates.Count)];
            _history.Add(_items[_index]);
            _index = pick;
            _played.Add(_items[_index]);
            return Current;
        }
    }
}
