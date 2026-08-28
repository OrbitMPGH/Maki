using Maki.Api.Services;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Clears the reader thumbnail and source-preview caches and re-downloads series posters.
/// Manual only: there is no trigger for it, the System settings card fires it by key.
/// <para>
/// A job rather than a fire-and-forget task off the request so it is registered on Quartz's
/// shutdown path — the pass can run for minutes over a large library, and
/// <c>QuartzShutdownInterrupter</c> is what lets a restart end it cleanly mid-run.
/// </para>
/// </summary>
[DisallowConcurrentExecution]
public class ImageCacheRebuildJob(
    ImageCacheRebuildService rebuild, ILogger<ImageCacheRebuildJob> logger) : IJob
{
    public static readonly JobKey Key = new("image-cache-rebuild");

    /// <summary>Job-data flag: re-download every poster instead of only the missing/unreadable ones.</summary>
    public const string ForceKey = "force";

    public async Task Execute(IJobExecutionContext context)
    {
        var force = context.MergedJobDataMap.TryGetValue(ForceKey, out var flag) && flag is true;
        logger.LogInformation("Image cache rebuild starting (force: {Force})", force);
        if (!await rebuild.RunAsync(force, context.CancellationToken))
        {
            logger.LogDebug("Image cache rebuild skipped; one is already running");
        }
    }
}
