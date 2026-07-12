using Mangarr.Api.Services;
using Quartz;

namespace Mangarr.Api.Jobs;

/// <summary>
/// Scrobble tick: runs every minute and lets <see cref="ScrobbleService.TickAsync"/>
/// decide whether the configured interval has elapsed (so interval changes apply
/// without a restart). The sync-now endpoint triggers this job with force=true.
/// </summary>
[DisallowConcurrentExecution]
public class ScrobbleJob(ScrobbleService scrobbler, ILogger<ScrobbleJob> logger) : IJob
{
    public static readonly JobKey Key = new("scrobble");
    public const string ForceKey = "force";

    public async Task Execute(IJobExecutionContext context)
    {
        try
        {
            var force = context.MergedJobDataMap.GetBooleanValue(ForceKey);
            await scrobbler.TickAsync(force, context.CancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Scrobble sync failed");
        }
    }
}
