using Maki.Api.Services;
using Maki.Api.Services.Ocr;
using Maki.Api.Services.Upscaling;
using Maki.Core.Io;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Unloads upscale model sessions three minutes after their last page, OCR models after
/// <see cref="PageTextReader.IdleWindow"/>, and credit-detection caches after <see cref="CreditIdleWindow"/>.
/// </summary>
[DisallowConcurrentExecution]
public class UpscalerIdleUnloadJob(
    OnnxPageUpscaler upscaler,
    PageTextReader ocr,
    CreditNoticeStore? creditNotices = null,
    CreditBank? creditBank = null,
    ReaderCreditsCache? readerCredits = null) : IJob
{
    public static readonly JobKey Key = new("upscaler-idle-unload");

    public static readonly TimeSpan IdleWindow = TimeSpan.FromMinutes(3);

    public static readonly TimeSpan CreditIdleWindow = TimeSpan.FromMinutes(5);

    public Task Execute(IJobExecutionContext context)
    {
        var released = upscaler.ReleaseIfIdle(IdleWindow) > 0;
        released |= ocr.ReleaseIfIdle(PageTextReader.IdleWindow);
        released |= creditNotices?.ReleaseIfIdle(CreditIdleWindow) ?? false;
        released |= creditBank?.ReleaseIfIdle(CreditIdleWindow) ?? false;
        released |= readerCredits?.ReleaseIfIdle(CreditIdleWindow) ?? false;
        released |= CreditGroupsService.ReleaseMeasureIfIdle(CreditIdleWindow);
        if (released)
        {
            // CollectAndTrim compacts LOH left by upscale/OCR bursts and hands freed glibc arenas back.
            NativeHeap.CollectAndTrim();
        }

        return Task.CompletedTask;
    }
}
