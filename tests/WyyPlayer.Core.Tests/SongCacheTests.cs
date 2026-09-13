using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WyyPlayer.Api;
using WyyPlayer.Core.Audio;
using Xunit;

namespace WyyPlayer.Core.Tests
{
    public class SongCacheTests : IDisposable
    {
        // 缓存现在以带源标识的 SongKey 为键（原先用裸 long）。
        private static SongKey K(long id) => new("test", id.ToString());

        private readonly string _dir;

        public SongCacheTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "wyycache-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        [Fact]
        public void 未下载时_Has为false且路径可预期()
        {
            using var cache = new SongCache(_dir);

            Assert.False(cache.Has(K(12345)));
            // 文件名用净化后的 SongKey：冒号在 Windows 文件名里非法，故写成 test_12345.mp3
            Assert.Equal(Path.Combine(_dir, "test_12345.mp3"), cache.GetPath(K(12345)));
        }

        [Fact]
        public async Task 从本地file_URL下载后_内容一致且Has为true()
        {
            // 用 file:// 做离线测试：不触网，但走完整的「流式写盘 + 原子替换」路径。
            // file:// 由测试项目注入的 FileSchemeHandler 提供，生产代码不含 scheme 逻辑。
            var src = Path.Combine(_dir, "src.bin");
            var payload = new byte[64 * 1024];
            new Random(7).NextBytes(payload);
            await File.WriteAllBytesAsync(src, payload);

            using var cache = new SongCache(_dir, handler: new FileSchemeHandler());
            var path = await cache.DownloadAsync(K(999), new Uri(src).AbsoluteUri);

            Assert.True(cache.Has(K(999)));
            Assert.Equal(payload, await File.ReadAllBytesAsync(path));
        }

        [Fact]
        public async Task 重复下载同一首歌_第二次不会重写文件()
        {
            var src = Path.Combine(_dir, "src.bin");
            await File.WriteAllBytesAsync(src, new byte[1024]);

            using var cache = new SongCache(_dir, handler: new FileSchemeHandler());
            var first = await cache.DownloadAsync(K(1), new Uri(src).AbsoluteUri);
            var firstWrite = File.GetLastWriteTimeUtc(first);

            await Task.Delay(30);
            var second = await cache.DownloadAsync(K(1), new Uri(src).AbsoluteUri);

            Assert.Equal(first, second);
            Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(second));
        }

        [Fact]
        public async Task 下载失败时_不留下缓存文件()
        {
            using var cache = new SongCache(_dir, TimeSpan.FromSeconds(5), new FileSchemeHandler());

            await Assert.ThrowsAnyAsync<Exception>(() =>
                cache.DownloadAsync(K(2), "file:///nonexistent/definitely-missing.bin"));

            Assert.False(cache.Has(K(2)));
            Assert.Empty(Directory.GetFiles(_dir, "*.part"));
        }

        [Fact]
        public void Clear_清空缓存()
        {
            // 用缓存自己的命名规则落文件，避免测试硬编码文件名而与实现脱节
            var key = K(1);
            File.WriteAllBytes(Path.Combine(_dir, SongCache.FileNameFor(key)), new byte[10]);
            using var cache = new SongCache(_dir);

            Assert.True(cache.Has(key));
            cache.Clear();
            Assert.False(cache.Has(key));
        }

        [Fact]
        public async Task 未注入处理器时_file_URL被拒绝()
        {
            // 安全默认：不注入 handler 时走系统 HttpClient，非 http(s) scheme 必须被拒绝。
            using var cache = new SongCache(_dir);

            await Assert.ThrowsAnyAsync<Exception>(() =>
                cache.DownloadAsync(K(3), "file:///nonexistent/definitely-missing.bin"));

            Assert.False(cache.Has(K(3)));
            Assert.Empty(Directory.GetFiles(_dir, "*.part"));
        }

        [Fact]
        public async Task 同一首歌并发下载_互不冲突且不留下part文件()
        {
            // 并发下载同一首歌：两次调用共用同一个 {id}.mp3.part 临时名时会因
            // FileShare.None 而抛 IOException。每次调用用独立的临时名可消除此冲突。
            var src = Path.Combine(_dir, "src.bin");
            var payload = new byte[8 * 1024 * 1024];
            new Random(11).NextBytes(payload);
            await File.WriteAllBytesAsync(src, payload);

            var url = new Uri(src).AbsoluteUri;
            using var cache = new SongCache(_dir, handler: new FileSchemeHandler());

            var results = await Task.WhenAll(
                cache.DownloadAsync(K(7), url),
                cache.DownloadAsync(K(7), url));

            Assert.All(results, p => Assert.Equal(cache.GetPath(K(7)), p));
            Assert.True(cache.Has(K(7)));
            Assert.Equal(payload, await File.ReadAllBytesAsync(results[0]));
            Assert.Empty(Directory.GetFiles(_dir, "*.part"));
        }
    }
}