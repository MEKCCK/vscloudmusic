using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace WyyPlayer.Core.Storage
{
    /// <summary>
    /// 把网易云登录凭据（cookie 串）持久化到本机文件。
    ///
    /// 安全约定：
    /// - 该文件等价于账号密码，只允许当前用户读写（Unix 上 0600）。
    /// - 写入走「临时文件 + 原子替换」，避免进程中途退出留下半截文件。
    /// - 临时文件自创建那一刻起即为 0600（UnixCreateMode），凭据字节写入前
    ///   磁盘上便不存在任何放宽权限的凭据文件。
    /// - 本类不做任何日志输出，异常消息也不携带凭据内容。
    /// </summary>
    public sealed class CredentialStore
    {
        private readonly string _path;

        public CredentialStore(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("凭据文件路径不能为空", nameof(path));
            _path = path;
        }

        /// <summary>原子替换使用的临时文件路径。</summary>
        private string TmpPath => _path + ".tmp";

        public bool Exists => File.Exists(_path);

        /// <summary>读取凭据串；不存在或内容为空时返回 null。</summary>
        public string? Load()
        {
            if (!File.Exists(_path)) return null;

            // 读取失败（权限、损坏）按「没有凭据」处理：调用方会退回要求重新登录，
            // 这比抛异常中断启动更可用。但绝不静默返回一个残缺的串 —— 空即 null。
            try
            {
                var text = File.ReadAllText(_path, Encoding.UTF8).Trim();
                return string.IsNullOrEmpty(text) ? null : text;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        public void Save(string cookieHeader)
        {
            if (string.IsNullOrWhiteSpace(cookieHeader))
                throw new ArgumentException("凭据内容不能为空", nameof(cookieHeader));

            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // 崩溃残留的 .tmp 可能带着旧凭据和旧（放宽的）权限，必须先删除再新建。
            // 不能截断复用：UnixCreateMode 只作用于新建 inode，截断会保留旧 inode
            // 的放宽权限，重新打开「先放宽后收紧」的窗口。删除后 CreateNew 若仍失败
            // （极端的并发竞争）会抛 IOException —— 保存失败而非写入一个不可控
            // 权限的文件，是正确的失败方向。
            File.Delete(TmpPath);

            WriteOwnerOnly(TmpPath, cookieHeader);

            // 删+新建后 tmp 自创建即为 0600；此行只在理论兜底路径
            // （非 Windows 非 Unix 且 UnixCreateMode 不可用）上才真正起作用，
            // 属冗余安全网。
            RestrictToOwner(TmpPath);

            // rename(2) 整体替换 inode：目标文件继承 tmp 的 0600。
            File.Move(TmpPath, _path, overwrite: true);

            // 后置收紧：正常路径下已冗余（rename 已带来 0600 inode），保留为纵深防御，
            // 防止未来改动写路径时权限回归。
            RestrictToOwner(_path);
        }

        public void Clear()
        {
            File.Delete(_path);    // 文件不存在时 Delete 是 no-op
            File.Delete(TmpPath);  // 崩溃残留的 .tmp 可能持有最后一份凭据，一并清除
        }

        /// <summary>
        /// 以属主可读写权限写入凭据。Windows 上退化为普通写（依赖用户目录 ACL）；
        /// Unix 上用 FileMode.CreateNew + UnixCreateMode，令文件自落盘即为 0600，
        /// 且与进程 umask 无关（.NET 在创建时会按模式精确应用）。
        /// </summary>
        private static void WriteOwnerOnly(string path, string content)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // FileStreamOptions.UnixCreateMode 在 Windows 上会抛
                // PlatformNotSupportedException，因此仅非 Windows 分支使用它。
                File.WriteAllText(path, content, Encoding.UTF8);
                return;
            }

            try
            {
                var bytes = Encoding.UTF8.GetBytes(content);
                using var fs = new FileStream(path, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                });
                fs.Write(bytes);
                fs.Flush();
            }
            catch (PlatformNotSupportedException)
            {
                // 理论上的非 Windows 非 Unix 平台：退回普通写，由调用处的
                // RestrictToOwner 在移动前兜底收紧。
                File.WriteAllText(path, content, Encoding.UTF8);
            }
        }

        /// <summary>Unix 上收紧到仅属主可读写。Windows 依赖用户目录自身的 ACL。</summary>
        private static void RestrictToOwner(string path)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (PlatformNotSupportedException) { /* 非 Unix 且非 Windows：忽略 */ }
        }
    }
}