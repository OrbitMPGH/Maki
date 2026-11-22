using Maki.Api.Services.Ocr;
using Maki.Api.Services.Upscaling;
using Maki.Core.Io;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Unloads upscale model sessions ten minutes after their last page. A loaded model is tens of MB
/// resident, and the reader only asks for pages while someone is reading. The OCR models that judge
/// credit pages go the same way, after <see cref="PageTextReader.IdleWindow"/>.
/// </summary>
[DisallowConcurrentExecution]
public class UpscalerIdleUnloadJob(OnnxPageUpscaler upscaler, PageTextReader ocr) : IJob
{
    public static readonly JobKey Key = new("upscaler-idle-unload");

    public static readonly TimeSpan IdleWindow = TimeSpan.FromMinutes(3);

    public Task Execute(IJobExecutionContext context)
    {
        var released = upscaler.ReleaseIfIdle(IdleWindow) > 0;
        released |= ocr.ReleaseIfIdle(PageTextReader.IdleWindow);
        if (released)
        {
            // CollectAndTrim compacts LOH left by upscale/OCR bursts and hands freed glibc arenas back.
            NativeHeap.CollectAndTrim();
        }

        return Task.CompletedTask;
    }
}
