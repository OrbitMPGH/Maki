using Maki.Api.Services.Upscaling;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Unloads upscale model sessions ten minutes after their last page. A loaded model is tens of MB
/// resident plus its arena, and the reader only asks for pages while someone is reading.
/// </summary>
[DisallowConcurrentExecution]
public class UpscalerIdleUnloadJob(OnnxPageUpscaler upscaler) : IJob
{
    public static readonly JobKey Key = new("upscaler-idle-unload");

    public static readonly TimeSpan IdleWindow = TimeSpan.FromMinutes(10);

    public Task Execute(IJobExecutionContext context)
    {
        upscaler.ReleaseIfIdle(IdleWindow);
        return Task.CompletedTask;
    }
}
