using System.Collections.Generic;
using WyyPlayer.Api;
using WyyPlayer.Core.Playback;
using Xunit;

namespace WyyPlayer.Core.Tests
{
    // 测试用的简短构造：K(1) 等价于 new SongKey("test", "1")。
    // 队列现在以带源标识的 SongKey 为元素，不再接受裸 long。
    internal static class TestKeys
    {
        public static SongKey K(long id) => new("test", id.ToString());
        public static SongKey[] Ks(params long[] ids)
        {
            var arr = new SongKey[ids.Length];
            for (int i = 0; i < ids.Length; i++) arr[i] = K(ids[i]);
            return arr;
        }
    }

    public class PlayQueueTests
    {
        private static PlayQueue Q(PlayMode mode, params long[] ids)
        {
            var q = new PlayQueue { Mode = mode };
            q.SetItems(TestKeys.Ks(ids));
            return q;
        }

        [Fact]
        public void 空队列_所有操作返回null且不抛异常()
        {
            var q = new PlayQueue();
            Assert.Null(q.Current);
            Assert.Null(q.Next());
            Assert.Null(q.Previous());
            Assert.Empty(q.Items);
        }

        [Fact]
        public void 设置列表后_当前为首项()
        {
            var q = Q(PlayMode.Sequential, 1, 2, 3);
            Assert.Equal(TestKeys.K(1), q.Current);
            Assert.Equal(TestKeys.Ks( 1, 2, 3 ), q.Items);
        }

        [Fact]
        public void 单曲循环_始终返回同一首()
        {
            var q = Q(PlayMode.RepeatOne, 7, 8, 9);
            Assert.Equal(TestKeys.K(7), q.Next());
            Assert.Equal(TestKeys.K(7), q.Next());
            Assert.Equal(TestKeys.K(7), q.Current);
        }

        [Fact]
        public void 顺序播放_依次推进并在末尾返回null()
        {
            var q = Q(PlayMode.Sequential, 1, 2, 3);
            Assert.Equal(TestKeys.K(2), q.Next());
            Assert.Equal(TestKeys.K(3), q.Next());
            Assert.Null(q.Next());      // 末尾：返回 null 由调用方决定停止
            Assert.Equal(TestKeys.K(3), q.Current); // 但不改变当前曲
        }

        [Fact]
        public void 随机播放_不重复取歌_取完返回null()
        {
            var q = Q(PlayMode.Shuffle, 1, 2, 3, 4, 5);
            var seen = new HashSet<SongKey> { q.Current!.Value };
            for (int i = 0; i < 4; i++)
            {
                var n = q.Next();
                Assert.NotNull(n);
                Assert.True(seen.Add(n!.Value), "随机播放不得重复取同一首");
            }
            Assert.Null(q.Next());   // 全部取过
        }

        [Fact]
        public void 顺序模式_到末尾返回null_由调用方决定停止或续接()
        {
            var q = Q(PlayMode.Sequential, 1, 2);
            Assert.Equal(TestKeys.K(2), q.Next());
            Assert.Null(q.Next());
        }

        [Fact]
        public void 上一首_退回实际播放历史()
        {
            var q = Q(PlayMode.Sequential, 1, 2, 3);
            q.Next();                 // 播到 2
            q.Next();                 // 播到 3
            Assert.Equal(TestKeys.K(3), q.Current);
            Assert.Equal(TestKeys.K(2), q.Previous());
            Assert.Equal(TestKeys.K(1), q.Previous());
            Assert.Equal(TestKeys.K(1), q.Current);
        }

        [Fact]
        public void 随机播放的上一首_同样是随机取未播过的()
        {
            // 需求变更：随机的「上一首」也随机（玩家期望：A→C→F 之后按上一首
            // 应跳到另一首随机的，而不是沿历史回到 C）。
            // 因此这里断言的是「换了一首且不重复」，而不是「回到刚才那首」。
            var q = Q(PlayMode.Shuffle, 10, 20, 30);
            var first = q.Current!.Value;
            var second = q.Next()!.Value;
            Assert.NotEqual(first, second);

            var prev = q.Previous();
            Assert.NotNull(prev);
            Assert.Equal(prev, q.Current);
            Assert.NotEqual(second, prev!.Value);   // 不能停在原地
        }

        [Fact]
        public void 历史为空时_上一首退化为列表前一首()
        {
            // 面板点列表里的第 2 首会走 SetItems（它会清空历史）。
            // 此时按上一首必须回到第 1 首，而不是因为历史为空就毫无反应。
            var q = new PlayQueue { Mode = PlayMode.Sequential };
            q.SetItems(TestKeys.Ks(10, 20, 30), startIndex: 1);

            Assert.Equal(TestKeys.K(20), q.Current);
            Assert.Equal(TestKeys.K(10), q.Previous());
            Assert.Equal(TestKeys.K(10), q.Current);
        }

        [Fact]
        public void 已在首曲时_上一首返回null()
        {
            var q = new PlayQueue { Mode = PlayMode.Sequential };
            q.SetItems(TestKeys.Ks(10, 20), startIndex: 0);
            Assert.Null(q.Previous());
        }

        [Fact]
        public void 未播放过任何歌时_上一首返回null()
        {
            var q = Q(PlayMode.Sequential, 1, 2);
            Assert.Null(q.Previous());
        }

        [Fact]
        public void Append_可在队尾追加歌曲用于续接()
        {
            var q = Q(PlayMode.Sequential, 1, 2);
            q.Next();                       // 播到 2
            Assert.Null(q.Next());          // 队列尽头

            q.Append(TestKeys.Ks( 3, 4 ));
            Assert.Equal(TestKeys.K(3), q.Next());
            Assert.Equal(TestKeys.K(4), q.Next());
            Assert.Null(q.Next());
        }

        [Fact]
        public void Append_不重复加入已有歌曲()
        {
            var q = Q(PlayMode.Sequential, 1, 2);
            q.Append(TestKeys.Ks( 2, 3 ));   // 2 已存在
            Assert.Equal(TestKeys.Ks( 1, 2, 3 ), q.Items);
        }

        [Fact]
        public void SetItems_重置索引与历史()
        {
            var q = Q(PlayMode.Sequential, 1, 2, 3);
            q.Next(); q.Next();
            q.SetItems(TestKeys.Ks( 100, 200 ));

            Assert.Equal(TestKeys.K(100), q.Current);
            Assert.Null(q.Previous());       // 历史已清空
            Assert.Equal(TestKeys.K(200), q.Next());
        }

        [Fact]
        public void SetItems_起始索引越界时收敛到合法范围()
        {
            var q = new PlayQueue();
            q.SetItems(TestKeys.Ks( 1, 2, 3 ), startIndex: 99);
            Assert.Equal(TestKeys.K(3), q.Current);

            q.SetItems(TestKeys.Ks( 1, 2, 3 ), startIndex: -5);
            Assert.Equal(TestKeys.K(1), q.Current);
        }
    }
}
