using Mangarr.Metadata.MangaBaka;
using Quartz;

namespace Mangarr.Api.Jobs;

/// <summary>
/// Keeps the local MangaBaka database dump current. The dump is published nightly at
/// 00:00 UTC; runs every 6 hours but short-circuits on the published SHA1, so repeat
/// runs cost one tiny checksum request. Also triggerable on demand from settings.
/// </summary>
[DisallowConcurrentExecution]
public class MangaBakaDumpRefreshJob(
    MangaBakaDumpService dumpService,
    ILogger<MangaBakaDumpRefreshJob> logger) : IJob
{
    public static readonly JobKey Key = new("mangabaka-dump");

    public async Task Execute(IJobExecutionContext context)
    {
        try
        {
            await dumpService.RefreshAsync(context.CancellationToken);
        }
        catch (Exception ex)
        {
            // Health check surfaces prolonged staleness; the next run retries.
            logger.LogWarning(ex, "MangaBaka dump refresh failed");
        }
    }
}
