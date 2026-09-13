using System;
using System.IO;
using System.Runtime.InteropServices;
using WyyPlayer.Core.Storage;
using Xunit;

namespace WyyPlayer.Core.Tests
{
    public class CredentialStoreTests : IDisposable
    {
        private readonly string _dir;

        public CredentialStoreTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "wyycred-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* 清理失败不影响断言 */ }
        }

        private string Path0 => Path.Combine(_dir, "credentials.txt");

        [Fact]
        public void 未保存时_不存在且加载返回null()
        {
            var store = new CredentialStore(Path0);
            Assert.False(store.Exists);
            Assert.Null(store.Load());
        }

        [Fact]
        public void 保存后_能原样读回()
        {
            var store = new CredentialStore(Path0);
            const string Cookie = "MUSIC_U=abc; __csrf=def";

            store.Save(Cookie);

            Assert.True(store.Exists);
            Assert.Equal(Cookie, store.Load());
        }

        [Fact]
        public void 重复保存_是幂等的()
        {
            var store = new CredentialStore(Path0);
            store.Save("MUSIC_U=one");
            store.Save("MUSIC_U=one");

            Assert.Equal("MUSIC_U=one", store.Load());
        }

        [Fact]
        public void 清除后_回到不存在状态()
        {
            var store = new CredentialStore(Path0);
            store.Save("MUSIC_U=abc");
            store.Clear();

            Assert.False(store.Exists);
            Assert.Null(store.Load());
        }

        [Fact]
        public void 空串或null_不写入()
        {
            var store = new CredentialStore(Path0);

            Assert.Throws<ArgumentException>(() => store.Save(""));
            Assert.Throws<ArgumentException>(() => store.Save(null!));
            Assert.False(store.Exists);
        }

        [Fact]
        public void 在Unix上_文件权限恰为属主读写()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

            var store = new CredentialStore(Path0);
            store.Save("MUSIC_U=secret");

            var mode = File.GetUnixFileMode(Path0);
            // 精确断言 0600（不是只查缺失位）——mode 0000 也必须失败。
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        }

        [Fact]
        public void 保存时_崩溃残留的tmp被替换而非复用()
        {
            // 只读残留语义仅 Unix 有意义；Windows 目录 ACL 模型下本测试不适用。
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

            // 模拟上次崩溃：残留一个只读、内容过期的 .tmp。旧实现会试图原地截断
            // 而抛 UnauthorizedAccessException（先放宽后收紧）；新实现先删除再以
            // 0600 新建，保存必须成功，且最终文件必须是全新 inode 的 0600。
            File.WriteAllText(Path0 + ".tmp", "STALE_CREDENTIAL");
            File.SetUnixFileMode(Path0 + ".tmp", UnixFileMode.UserRead);

            var store = new CredentialStore(Path0);
            store.Save("MUSIC_U=fresh");

            Assert.Equal("MUSIC_U=fresh", store.Load());
            Assert.False(File.Exists(Path0 + ".tmp"));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(Path0));
        }

        [Fact]
        public void 清除时_连同崩溃残留的tmp一并删除()
        {
            var store = new CredentialStore(Path0);
            store.Save("MUSIC_U=abc");
            File.WriteAllText(Path0 + ".tmp", "STALE");

            store.Clear();

            Assert.False(store.Exists);
            Assert.Null(store.Load());
            Assert.False(File.Exists(Path0 + ".tmp"));
        }
    }
}