using WyyPlayer.Core.Playback;
using Xunit;
using WyyPlayer.Api;

namespace WyyPlayer.Core.Tests
{
    public class PlaybackStateTests
    {
        // 状态机现在以带源标识的 SongKey 记录当前曲目（原先用裸 long）。
        private static SongKey K(long id) => new("test", id.ToString());

        [Fact]
        public void 初始为空闲且无当前歌曲()
        {
            var sm = new PlaybackStateMachine();
            Assert.Equal(PlaybackStatus.Idle, sm.Status);
            Assert.Null(sm.CurrentSong);
            Assert.False(sm.IsBusy);
        }

        [Fact]
        public void 播放中请求新歌会被拒绝_必须先停止()
        {
            var sm = new PlaybackStateMachine();
            Assert.True(sm.TryBegin(K(1)));
            sm.MarkPlaying();

            // 播放中直接再请求必须失败 —— 否则会出现两个 AL 源/两个解码器
            Assert.False(sm.TryBegin(K(2)));
            Assert.Equal(K(1), sm.CurrentSong);
            Assert.Equal(PlaybackStatus.Playing, sm.Status);
        }

        [Fact]
        public void 停止后可以请求新歌()
        {
            var sm = new PlaybackStateMachine();
            sm.TryBegin(K(1)); sm.MarkPlaying(); sm.MarkStopped();

            Assert.True(sm.TryBegin(K(2)));
            Assert.Equal(K(2), sm.CurrentSong);
            Assert.Equal(PlaybackStatus.Preparing, sm.Status);
        }

        [Fact]
        public void 失败后状态回到可请求_不再卡死()
        {
            // 这是修复既有缺陷的核心：_tornDown 一旦置位永不复位
            var sm = new PlaybackStateMachine();
            sm.TryBegin(K(1)); sm.MarkPlaying(); sm.MarkFailed("网络错误");

            Assert.Equal(PlaybackStatus.Failed, sm.Status);
            Assert.True(sm.TryBegin(K(2)));            // 失败后必须能再试
            Assert.Equal(K(2), sm.CurrentSong);
        }

        [Fact]
        public void 暂停与播放可互转_且暂停时仍占用当前歌()
        {
            var sm = new PlaybackStateMachine();
            sm.TryBegin(K(7)); sm.MarkPlaying();

            sm.MarkPaused();
            Assert.Equal(PlaybackStatus.Paused, sm.Status);
            Assert.Equal(K(7), sm.CurrentSong);

            sm.MarkPlaying();
            Assert.Equal(PlaybackStatus.Playing, sm.Status);
        }

        [Fact]
        public void 暂停中不得被误判为已结束()
        {
            // 既有缺陷：暂停时队列排空，看起来与「流末尾」一样，会触发从头重播
            var sm = new PlaybackStateMachine();
            sm.TryBegin(K(1)); sm.MarkPlaying(); sm.MarkPaused();

            Assert.False(sm.WantsLoop);
        }

        [Fact]
        public void 仅播放中且自然结束才循环()
        {
            var sm = new PlaybackStateMachine();
            sm.TryBegin(K(1)); sm.MarkPlaying();
            Assert.True(sm.WantsLoop);

            sm.MarkEnded();
            Assert.Equal(PlaybackStatus.Stopped, sm.Status);
        }

        [Fact]
        public void 准备中不算忙完_但也不可并发请求()
        {
            var sm = new PlaybackStateMachine();
            sm.TryBegin(K(1));
            Assert.True(sm.IsBusy);
            Assert.False(sm.TryBegin(K(2)));
        }

        // ---- 修复轮：暂停仍持有 AL 源，不能直接切歌 ----

        [Fact]
        public void 暂停中请求新歌会被拒绝_必须先停止()
        {
            var sm = new PlaybackStateMachine();
            sm.TryBegin(K(1)); sm.MarkPlaying(); sm.MarkPaused();

            // 暂停仍占着唯一的立体声源，必须算 busy
            Assert.Equal(PlaybackStatus.Paused, sm.Status);
            Assert.Equal(K(1), sm.CurrentSong);
            Assert.True(sm.IsBusy);

            // 直接 TryBegin 必须被拒绝 —— 否则会出现两个并存 AL 源
            Assert.False(sm.TryBegin(K(2)));
            Assert.Equal(K(1), sm.CurrentSong);
            Assert.Equal(PlaybackStatus.Paused, sm.Status);

            // 显式停止释放源后，才能开始新歌
            sm.MarkStopped();
            Assert.True(sm.TryBegin(K(2)));
            Assert.Equal(K(2), sm.CurrentSong);
            Assert.Equal(PlaybackStatus.Preparing, sm.Status);
        }

        [Fact]
        public void 准备中停止_下载中途跳过也合法()
        {
            var sm = new PlaybackStateMachine();
            sm.TryBegin(K(1));
            Assert.Equal(PlaybackStatus.Preparing, sm.Status);

            // 下载还在路上就停止/切走：从 Preparing 直接落 Stopped
            sm.MarkStopped();
            Assert.Equal(PlaybackStatus.Stopped, sm.Status);

            Assert.True(sm.TryBegin(K(2)));
            Assert.Equal(PlaybackStatus.Preparing, sm.Status);
        }

        [Fact]
        public void 准备中失败_可再试_并记录原因()
        {
            var sm = new PlaybackStateMachine();
            sm.TryBegin(K(1));
            Assert.Equal(PlaybackStatus.Preparing, sm.Status);

            sm.MarkFailed("下载失败");
            Assert.Equal(PlaybackStatus.Failed, sm.Status);
            Assert.Equal("下载失败", sm.FailureReason);

            Assert.True(sm.TryBegin(K(2)));
            Assert.Equal(PlaybackStatus.Preparing, sm.Status);
        }

        [Fact]
        public void 暂停时队列排空不算结束()
        {
            // 动机：暂停时 AL 队列排空，与「流末尾」看起来一模一样；
            // 状态机本身必须挡住 MarkEnded，不能把暂停误判为自然结束。
            var sm = new PlaybackStateMachine();
            sm.TryBegin(K(1)); sm.MarkPlaying(); sm.MarkPaused();

            sm.MarkEnded();
            Assert.Equal(PlaybackStatus.Paused, sm.Status);
            Assert.Equal(K(1), sm.CurrentSong);
        }

        [Fact]
        public void 停止空闲失败时播放回调不复活状态()
        {
            var sm = new PlaybackStateMachine();

            sm.MarkPlaying();
            Assert.Equal(PlaybackStatus.Idle, sm.Status);

            sm.TryBegin(K(1)); sm.MarkPlaying(); sm.MarkStopped();
            sm.MarkPlaying();
            Assert.Equal(PlaybackStatus.Stopped, sm.Status);

            sm.TryBegin(K(2)); sm.MarkPlaying(); sm.MarkFailed("网络错误");
            sm.MarkPlaying();
            Assert.Equal(PlaybackStatus.Failed, sm.Status);
        }

        [Fact]
        public void 失败原因可供UI显示_下次成功开始时清除()
        {
            var sm = new PlaybackStateMachine();
            sm.TryBegin(K(1)); sm.MarkPlaying(); sm.MarkFailed("加载超时");

            Assert.Equal(PlaybackStatus.Failed, sm.Status);
            Assert.Equal("加载超时", sm.FailureReason);

            sm.TryBegin(K(2));
            Assert.Null(sm.FailureReason);
            Assert.Equal(PlaybackStatus.Preparing, sm.Status);
        }

        [Fact]
        public void 准备中不应循环()
        {
            var sm = new PlaybackStateMachine();
            sm.TryBegin(K(1));
            Assert.Equal(PlaybackStatus.Preparing, sm.Status);
            Assert.False(sm.WantsLoop);
        }
    }
}