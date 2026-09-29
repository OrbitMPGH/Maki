using Maki.Api.Services;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Runs <see cref="FollowedCreatorReleaseService"/>. Triggered by <see cref="MangaBakaDumpRefreshJob"/>
/// after a new dump installs, since that is the only time new series can appear, and once a day as
/// well so an install whose trigger was lost to a restart is picked up. A run with nothing above the
/// watermark costs one credit-index read.
/// </summary>
[DisallowConcurrentExecution]
public class FollowedCreatorReleaseJob(
    FollowedCreatorReleaseService releases,
    ILogger<FollowedCreatorReleaseJob> logger) : IJob
{
    public static readonly JobKey Key = new("followed-creator-releases");

    public async Task Execute(IJobExecutionContext context)
    {
        try
        {
            await releases.RunAsync(context.CancellationToken);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // Shutdown; the watermark was not moved, so the next run covers the same ids.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Follow release check failed");
        }
    }
}
