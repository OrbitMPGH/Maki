using Maki.Api.Services;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Digests chapter files for KOReader's progress sync (<see cref="KoreaderDocumentIndexer"/>). Runs on
/// a timer, and a sync for a document nobody recognises fires it early, so a file copied to the
/// device some other way resolves on a later sync.
/// </summary>
[DisallowConcurrentExecution]
public class KoreaderDocumentIndexJob(KoreaderDocumentIndexer indexer) : IJob
{
    public static readonly JobKey Key = new("koreader-document-index");

    private static readonly TimeSpan TriggerAtMostEvery = TimeSpan.FromMinutes(10);
    private static long _lastTriggerTicks;

    public async Task Execute(IJobExecutionContext context) => await indexer.RunAsync(context.CancellationToken);

    /// <summary>Queues a run without waiting for it, at most once per ten minutes. Never throws.</summary>
    public static async Task TriggerSoonAsync(ISchedulerFactory schedulerFactory, ILogger logger)
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastTriggerTicks);
        if (now - last < TriggerAtMostEvery.Ticks || Interlocked.CompareExchange(ref _lastTriggerTicks, now, last) != last)
        {
            return;
        }

        try
        {
            var scheduler = await schedulerFactory.GetScheduler();
            await scheduler.TriggerJob(Key);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not trigger the KOReader document index");
        }
    }
}
