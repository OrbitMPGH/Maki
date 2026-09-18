using Maki.Api.Services;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Anime-signal tick: fires hourly and lets <see cref="AnimeSignalSyncService.TickAsync"/> decide
/// whether the configured interval has elapsed, so interval changes apply without a restart. The
/// opt-in endpoint triggers this job with force=true.
/// </summary>
[DisallowConcurrentExecution]
public class AnimeSignalJob(AnimeSignalSyncService signals, ILogger<AnimeSignalJob> logger) : IJob
{
    public static readonly JobKey Key = new("anime-signals");
    public const string ForceKey = "force";

    public async Task Execute(IJobExecutionContext context)
    {
        try
        {
            var force = context.MergedJobDataMap.GetBooleanValue(ForceKey);
            await signals.TickAsync(force, context.CancellationToken);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // Shutdown, not a failure.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Anime signal sync failed");
        }
    }
}
