using Maki.Api.Services;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Digests chapter files for KOReader's progress sync (<see cref="KoreaderDocumentIndexer"/>). Runs on
/// a timer, and a sync for a document nobody recognises fires it early when some file has never been
/// indexed, so a file copied to the device some other way resolves on a later sync. Pushes that were
/// waiting for a document id are tried again once a pass has added some, and pushes whose write threw
/// after every pass.
/// </summary>
[DisallowConcurrentExecution]
public class KoreaderDocumentIndexJob(KoreaderDocumentIndexer indexer, KoreaderPushWriter pushes) : IJob
{
    public static readonly JobKey Key = new("koreader-document-index");

    private static readonly TimeSpan TriggerAtMostEvery = TimeSpan.FromMinutes(10);
    private static long _lastTriggerTicks;

    public async Task Execute(IJobExecutionContext context)
    {
        var added = await indexer.RunAsync(context.CancellationToken);
        pushes.RetryWaiting(failedOnly: added == 0);
    }

    /// <summary>
    /// Queues a run without waiting for it, at most once per ten minutes and only when
    /// <paramref name="needed"/> says a run would find something. Never throws.
    /// </summary>
    public static async Task TriggerSoonAsync(ISchedulerFactory schedulerFactory, ILogger logger,
        Func<Task<bool>> needed)
    {
        try
        {
            var now = DateTime.UtcNow.Ticks;
            var last = Interlocked.Read(ref _lastTriggerTicks);
            if (now - last < TriggerAtMostEvery.Ticks || !await needed() ||
                Interlocked.CompareExchange(ref _lastTriggerTicks, now, last) != last)
            {
                return;
            }

            var scheduler = await schedulerFactory.GetScheduler();
            await scheduler.TriggerJob(Key);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not trigger the KOReader document index");
        }
    }
}
