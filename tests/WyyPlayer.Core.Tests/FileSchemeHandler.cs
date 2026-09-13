using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace WyyPlayer.Core.Tests
{
    /// <summary>
    /// 测试专用：支持 file:// 的 HttpMessageHandler（离线、不触网）。
    /// net10.0 默认的 SocketsHttpHandler 不支持 file scheme，而 SongCacheTests
    /// 依赖 file URL 离线验证「流式写盘 → .part → 原子改名」的全过程，故把本处理器
    /// 注入 SongCache 构造参数。非 file scheme 原样委派给 SocketsHttpHandler；
    /// 生产代码（WyyPlayer.Core）不包含任何 scheme 特定逻辑。
    /// </summary>
    public sealed class FileSchemeHandler : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _inner = new HttpMessageInvoker(new SocketsHttpHandler());

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri;
            if (uri != null && uri.IsFile)
            {
                try
                {
                    var stream = new FileStream(uri.LocalPath, FileMode.Open, FileAccess.Read,
                        FileShare.Read, 81920, FileOptions.Asynchronous);
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        RequestMessage = request,
                        Content = new StreamContent(stream)
                    };
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // 与真实 HttpClient 一致：传输层失败抛 HttpRequestException（内嵌原始异常）
                    throw new HttpRequestException($"无法读取 {uri.LocalPath}", ex);
                }
            }

            return await _inner.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}