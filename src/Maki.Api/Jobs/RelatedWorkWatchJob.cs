using Maki.Api.Services;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Runs <see cref="RelatedWorkWatchService"/>. Triggered by <see cref="MangaBakaDumpRefreshJob"/> after
/// a new dump installs, and once a day so an install whose trigger a restart swallowed is still covered.
/// </summary>
[DisallowConcurrentExecution]
public class RelatedWorkWatchJob(
    RelatedWorkWatchService watch,
    ILogger<RelatedWorkWatchJob> logger) : IJob
{
    public static readonly JobKey Key = new("related-work-watch");

    public async Task Execute(IJobExecutionContext context)
    {
        try
        {
            await watch.RunAsync(context.CancellationToken);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // Shutdown; the seen set was not saved, so the next run covers the same relations.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Related work check failed");
            context.ReportFailure(ex);
        }
    }
}
