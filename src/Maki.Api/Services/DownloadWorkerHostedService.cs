using Maki.Api.Dtos;
using Maki.Api.Hubs;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Consumes the download queue channel with a bounded number of concurrent
/// chapter workers. On startup, in-flight items from a previous run are reset
/// to Queued and re-signaled.
/// <para>
/// The worker count and the per-item timeout are re-read on every cooldown poll, so a change in
/// Settings applies within a few seconds. All <see cref="MaxConcurrentChapters"/> loops exist from
/// the start; the ones above the configured count stay parked and read nothing from the channel.
/// </para>
/// </summary>
public class DownloadWorkerHostedService(
    DownloadQueueService queue,
    DownloadBatchNotifier batches,
    IServiceScopeFactory scopeFactory,
    ILogger<DownloadWorkerHostedService> logger) : BackgroundService
{
    private const int DefaultConcurrentChapters = 2;
    private const int MaxConcurrentChapters = 8;
    private const int DefaultItemTimeoutMinutes = 120;
    private static readonly TimeSpan CooldownPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WorkerRestartDelay = TimeSpan.FromSeconds(10);

    private volatile int _concurrency = DefaultConcurrentChapters;

    // Minutes rather than a TimeSpan so it can be volatile; 0 means no limit.
    private volatile int _itemTimeoutMinutes = DefaultItemTimeoutMinutes;

    private TimeSpan ItemTimeout =>
        _itemTimeoutMinutes > 0 ? TimeSpan.FromMinutes(_itemTimeoutMinutes) : Timeout.InfiniteTimeSpan;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);
        await RefreshSettingsAsync(stoppingToken);

        var workers = Enumerable.Range(0, MaxConcurrentChapters)
            .Select(i => SuperviseAsync($"worker {i}", ct => WorkerLoopAsync(i, ct), stoppingToken))
            .Append(SuperviseAsync("cooldown poll", PeriodicWakeAsync, stoppingToken))
            .ToArray();

        await Task.WhenAll(workers);
    }

    /// <summary>
    /// Keeps one loop alive for the life of the service. A loop that throws used to fault its Task
    /// silently: <see cref="Task.WhenAll(Task[])"/> can't surface it while the cooldown poller is
    /// still running, and that poller never finishes, so a dead worker produced no log line and no
    /// host shutdown — the queue simply stopped dispatching until someone restarted the app.
    /// Everything here is retryable (a transient DB error, a bad row), so log it and start over
    /// after a pause rather than losing a worker permanently.
    /// </summary>
    private async Task SuperviseAsync(string name, Func<CancellationToken, Task> loop, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await loop(ct);
                return; // clean exit: the channel completed
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Download {Name} loop faulted; restarting in {Delay}s",
                    name, WorkerRestartDelay.TotalSeconds);
                try
                {
                    await Task.Delay(WorkerRestartDelay, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// A per-tracker cooldown lifting doesn't itself produce a channel signal, so a RateLimited item
    /// parked on a source that just cleared could otherwise sit until unrelated queue activity wakes
    /// a worker. Poking the channel periodically bounds how long that can stall.
    /// </summary>
    private async Task PeriodicWakeAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(CooldownPollInterval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            await RefreshSettingsAsync(ct);
            await queue.SignalAsync(0, ct);
        }
    }

    /// <summary>Both resolvers swallow their own failures, so this never throws out of the poll loop.</summary>
    private async Task RefreshSettingsAsync(CancellationToken ct)
    {
        var concurrency = await ResolveConcurrencyAsync(ct);
        if (concurrency != _concurrency)
        {
            logger.LogInformation("Download concurrency now {Concurrency}", concurrency);
            _concurrency = concurrency;
        }

        _itemTimeoutMinutes = await ResolveItemTimeoutMinutesAsync(ct);
    }

    /// <summary>
    /// Reads the configured worker count. Clamped because each worker is a live scraper
    /// connection — too many is a fast route to a site-wide rate limit, which stalls every
    /// download rather than speeding any up.
    /// </summary>
    private async Task<int> ResolveConcurrencyAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
            var raw = await settings.GetAsync(SettingKeys.DownloadConcurrentChapters, ct);

            if (!int.TryParse(raw, out var configured))
            {
                return DefaultConcurrentChapters;
            }

            // No warning when out of range: this runs every few seconds, and the settings endpoint
            // already refuses values outside 1..MaxConcurrentChapters.
            return Math.Clamp(configured, 1, MaxConcurrentChapters);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read download concurrency setting; using {Default}", DefaultConcurrentChapters);
            return DefaultConcurrentChapters;
        }
    }

    /// <summary>
    /// Reads the per-item wall-clock cap in minutes. A non-positive value means no cap, which is the
    /// escape hatch for somebody whose source is legitimately slower than any number we'd pick.
    /// </summary>
    private async Task<int> ResolveItemTimeoutMinutesAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
            var raw = await settings.GetAsync(SettingKeys.DownloadItemTimeoutMinutes, ct);

            if (!int.TryParse(raw, out var minutes))
            {
                return DefaultItemTimeoutMinutes;
            }

            return Math.Max(minutes, 0);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the download item timeout; using {Default} min",
                DefaultItemTimeoutMinutes);
            return DefaultItemTimeoutMinutes;
        }
    }

    private async Task RecoverAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();

        // Only scraper items go through the page pipeline; torrent items are
        // tracked externally by CompletedDownloadJob and must keep their status.
        var pending = await db.DownloadQueue
            .Where(q => q.Protocol == AcquisitionProtocol.Scraper &&
                        q.Status != QueueStatus.Completed &&
                        q.Status != QueueStatus.Failed &&
                        q.Status != QueueStatus.Cancelled)
            .ToListAsync(ct);

        // Resolving items never got a mapping before the restart — ClaimNextAsync requires one, so
        // flipping them straight to Queued would crash it. Resume resolution for each instead.
        var stillResolving = pending.Where(item => item.Status == QueueStatus.Resolving).ToList();
        var interrupted = pending.Except(stillResolving).ToList();

        foreach (var item in interrupted)
        {
            item.Status = QueueStatus.Queued;
        }

        await db.SaveChangesAsync(ct);

        foreach (var item in interrupted)
        {
            await queue.SignalAsync(item.Id, ct);
        }

        foreach (var item in stillResolving)
        {
            if (item.ChapterId is { } chapterId)
            {
                _ = queue.ResolveAndActivateAsync(item.Id, chapterId, CancellationToken.None);
            }
        }

        if (pending.Count > 0)
        {
            logger.LogInformation("Recovered {Count} queued downloads from previous run", pending.Count);
        }
    }

    /// <summary>
    /// A channel write is just a wake-up, not a specific item — the actual next item is decided by
    /// <see cref="DownloadQueueService.ClaimNextAsync"/> off <c>SortOrder</c>, so a manual reorder
    /// takes effect on the very next dispatch. On each wake, drain every claimable item before
    /// going back to sleep, since several signals can land for work one wake-up already covers.
    /// </summary>
    private async Task WorkerLoopAsync(int workerId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Parked above the configured count: read nothing, so the signal goes to a live worker.
            if (workerId >= _concurrency)
            {
                await Task.Delay(CooldownPollInterval, ct);
                continue;
            }

            if (!await queue.Reader.WaitToReadAsync(ct))
            {
                return; // clean exit: the channel completed
            }

            if (!queue.Reader.TryRead(out _))
            {
                continue;
            }

            // Re-checked per item so lowering the count retires a worker after its current download.
            while (workerId < _concurrency)
            {
                int queueItemId;
                try
                {
                    // Claiming sits in the loop *condition* no more: a throw here (transient DB
                    // error, a row the claim logic can't cope with) escaped the whole method and
                    // killed the worker for good. Drop back to waiting for the next signal instead;
                    // PeriodicWakeAsync guarantees one arrives within CooldownPollInterval.
                    if (await queue.ClaimNextAsync(ct) is not { } claimed)
                    {
                        break;
                    }

                    queueItemId = claimed;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Worker {Worker} could not claim the next queue item", workerId);
                    break;
                }

                var itemTimeout = ItemTimeout;
                try
                {
                    // A RateLimited outcome means ChapterDownloadProcessor already parked the item
                    // and started that tracker's cooldown — nothing more for this worker to do. It
                    // loops straight back to ClaimNextAsync, which will skip that tracker in favor of
                    // the next-highest-priority item on a different one.
                    using var scope = scopeFactory.CreateScope();
                    var processor = scope.ServiceProvider.GetRequiredService<ChapterDownloadProcessor>();

                    // Bounded so one item can never own a worker for the life of the process. The
                    // orphan sweep cannot rescue this case — the row still carries an in-flight
                    // status *and* a live owner, which is exactly what the sweep uses to tell a slow
                    // download from an abandoned one — so the only thing that can end it is the
                    // worker giving up on it.
                    var workCancellation = queue.WorkCancellationToken(queueItemId);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, workCancellation);
                    deadline.CancelAfter(itemTimeout);
                    await processor.ProcessAsync(queueItemId, deadline.Token);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (OperationCanceledException) when (queue.WorkCancellationToken(queueItemId).IsCancellationRequested)
                {
                    // Clear queue cancelled this item. Its row is already removed or marked Cancelled.
                }
                catch (OperationCanceledException)
                {
                    // The per-item deadline, not a shutdown. Fail it so the retry backoff owns what
                    // happens next, and free the worker for the rest of the queue.
                    logger.LogError("Worker {Worker} abandoned queue item {Id} after {Minutes} min",
                        workerId, queueItemId, itemTimeout.TotalMinutes);
                    await TryFailAsync(
                        queueItemId,
                        new TimeoutException($"Download gave up after {itemTimeout.TotalMinutes:0} minutes"),
                        ct);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Worker {Worker} crashed on queue item {Id}", workerId, queueItemId);

                    // ChapterDownloadProcessor fails the item itself for anything thrown inside its
                    // pipeline. Reaching here means the failure escaped that handling — the item load
                    // threw before the try, or FailAsync/CooldownAsync itself did — so the item is
                    // still mid-flight. Without this it would sit "Downloading" forever with no
                    // user-facing error.
                    await TryFailAsync(queueItemId, ex, ct);
                }
                finally
                {
                    // Whatever happened, this worker no longer owns the row. Leaving it registered
                    // would hide it from the orphan sweep for the rest of the process's life.
                    queue.ReleaseClaim(queueItemId);
                }
            }
        }
    }

    /// <summary>
    /// Last-resort fail for an item whose processing blew up outside the processor's own handling.
    /// Uses a fresh scope because the one that threw may hold a broken DbContext. Best-effort: if
    /// even this fails the DB is unreachable, and startup recovery re-queues the item.
    /// </summary>
    private async Task TryFailAsync(int queueItemId, Exception cause, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();

            var item = await db.DownloadQueue
                .Include(q => q.Chapter)
                .Include(q => q.Series)
                .FirstOrDefaultAsync(q => q.Id == queueItemId, ct);

            if (item is null || item.Status is QueueStatus.Completed or QueueStatus.Cancelled or QueueStatus.Failed)
            {
                return;
            }

            item.Status = QueueStatus.Failed;
            item.SetError("error.download.unexpected");
            item.RetryCount++;
            item.NextAttempt = queue.NextRetryAttempt(item.RetryCount);
            await db.SaveChangesAsync(ct);

            // This path bypasses ChapterDownloadProcessor.FailAsync, so report the outcome
            // ourselves — an unreported item would hold its batch open until the stale sweep.
            await batches.FailedAsync(item.SeriesId, item.Id, "error.download.unexpected");

            if (item.Series is { } series)
            {
                var events = scope.ServiceProvider.GetRequiredService<EventBroadcaster>();
                await events.QueueUpdated(QueueItemDto.FromEntity(item, item.Chapter, series, "?"));
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not mark queue item {Id} as failed", queueItemId);
        }
    }

}
