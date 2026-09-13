using System;
using System.Text;
using OpenTK.Audio.OpenAL;
using Vintagestory.API.Client;

namespace WyyPlayer.Audio
{
    /// <summary>
    /// 探测模组能否在游戏已有的 OpenAL 上下文上调用 AL。
    /// 不创建设备或上下文 —— 这是本方案成立的前提。
    /// </summary>
    public static class OpenAlProbe
    {
        public static bool LastSourceCreated { get; private set; }

        public static string Run(ICoreClientAPI api)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[vscloudmusic] ===== OpenAL 探针开始 =====");

            try
            {
                sb.AppendLine($"AL_VERSION  : {Safe(() => AL.Get(ALGetString.Version))}");
                sb.AppendLine($"AL_VENDOR   : {Safe(() => AL.Get(ALGetString.Vendor))}");
                sb.AppendLine($"AL_RENDERER : {Safe(() => AL.Get(ALGetString.Renderer))}");
            }
            catch (Exception e)
            {
                sb.AppendLine($"读取 AL 字符串异常: {e.GetType().Name}: {e.Message}");
            }

            try
            {
                var ctx = ALC.GetCurrentContext();
                sb.AppendLine($"当前上下文句柄: {ctx.Handle}");
                sb.AppendLine($"上下文是否为空  : {ctx.Handle == IntPtr.Zero}");
            }
            catch (Exception e)
            {
                sb.AppendLine($"ALC.GetCurrentContext 异常: {e.GetType().Name}: {e.Message}");
            }

            try
            {
                int src = AL.GenSource();
                var err = AL.GetError();
                LastSourceCreated = src != 0 && err == ALError.NoError;
                sb.AppendLine($"AL.GenSource 返回: {src}（0 表示失败）");
                sb.AppendLine($"AL.GetError 返回: {err}");

                if (src != 0)
                {
                    AL.DeleteSource(src);
                    sb.AppendLine("已释放探针 source");
                }
            }
            catch (Exception e)
            {
                sb.AppendLine($"AL.GenSource 异常: {e.GetType().Name}: {e.Message}");
                LastSourceCreated = false;
            }

            sb.AppendLine($"结论: {(LastSourceCreated ? "成功 —— OpenAL 直通可行" : "失败 —— 需改用 OGG 注入备用路线")}");
            sb.AppendLine("[vscloudmusic] ===== OpenAL 探针结束 =====");

            string report = sb.ToString();
            api.Logger.Notification(report);
            return report;
        }

        private static string Safe(Func<string> f)
        {
            try { return f() ?? "(null)"; }
            catch (Exception e) { return $"<异常 {e.GetType().Name}>"; }
        }
    }
}