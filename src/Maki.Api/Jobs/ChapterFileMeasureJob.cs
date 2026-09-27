using Maki.Api.Services;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Measures chapter files nothing has measured yet (<see cref="ChapterFileMeasureService"/>). Runs
/// on a timer and is also fired after a library or torrent import, which add files that are
/// stamped with a tier but never opened.
/// </summary>
[DisallowConcurrentExecution]
public class ChapterFileMeasureJob(
    ChapterFileMeasureService measure, ILogger<ChapterFileMeasureJob> logger) : IJob
{
    public static readonly JobKey Key = new("chapter-file-measure");

    public async Task Execute(IJobExecutionContext context)
    {
        if (!await measure.RunAsync(context.CancellationToken))
        {
            logger.LogDebug("Chapter file measurement skipped; one is already running");
        }
    }

    /// <summary>
    /// Queues a run without waiting for it. Never throws: a missed trigger only means the files wait
    /// for the next scheduled pass.
    /// </summary>
    public static async Task TriggerAsync(ISchedulerFactory schedulerFactory, ILogger logger)
    {
        try
        {
            var scheduler = await schedulerFactory.GetScheduler();
            await scheduler.TriggerJob(Key);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not trigger chapter file measurement");
        }
    }
}
