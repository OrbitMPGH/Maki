using Maki.Metadata.MangaBaka;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Keeps the local MangaBaka database dump current. The dump is published nightly at
/// 00:00 UTC; runs every 6 hours but short-circuits on the published SHA1, so repeat
/// runs cost one tiny checksum request. Also triggerable on demand from settings.
/// </summary>
[DisallowConcurrentExecution]
public class MangaBakaDumpRefreshJob(
    MangaBakaDumpService dumpService,
    ISchedulerFactory schedulerFactory,
    ILogger<MangaBakaDumpRefreshJob> logger) : IJob
{
    public static readonly JobKey Key = new("mangabaka-dump");

    public async Task Execute(IJobExecutionContext context)
    {
        try
        {
            var installed = await dumpService.RefreshAsync(context.CancellationToken);
            if (installed)
            {
                // Rail caches were built off the old (or no) dump; re-warm against the new one.
                var scheduler = await schedulerFactory.GetScheduler(context.CancellationToken);
                await scheduler.TriggerJob(DiscoverCacheWarmJob.Key, context.CancellationToken);
            }
        }
        catch (Exception ex)
        {
            // Health check surfaces prolonged staleness; the next run retries.
            logger.LogWarning(ex, "MangaBaka dump refresh failed");
        }
    }
}
