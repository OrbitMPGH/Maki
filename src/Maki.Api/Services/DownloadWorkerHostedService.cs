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
/// </summary>
public class DownloadWorkerHostedService(
    DownloadQueueService queue,
    DownloadBatchNotifier batches,
    IServiceScopeFactory scopeFactory,
    ILogger<DownloadWorkerHostedService> logger) : BackgroundService
{
    private const int DefaultConcurrentChapters = 2;
    private const int MaxConcurrentChapters = 8;
    private static readonly TimeSpan CooldownPollInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);

        var concurrency = await ResolveConcurrencyAsync(stoppingToken);
        var workers = Enumerable.Range(0, concurrency)
            .Select(i => WorkerLoopAsync(i, stoppingToken))
            .Append(PeriodicWakeAsync(stoppingToken))
            .ToArray();

        await Task.WhenAll(workers);
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
            await queue.SignalAsync(0, ct);
        }
    }

    /// <summary>
    /// Reads the configured worker count once. Clamped because each worker is a live scraper
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

            var clamped = Math.Clamp(configured, 1, MaxConcurrentChapters);
            if (clamped != configured)
            {
                logger.LogWarning(
                    "Download concurrency {Configured} out of range; using {Clamped}", configured, clamped);
            }

            return clamped;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read download concurrency setting; using {Default}", DefaultConcurrentChapters);
            return DefaultConcurrentChapters;
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

        foreach (var item in pending)
        {
            item.Status = QueueStatus.Queued;
        }

        await db.SaveChangesAsync(ct);

        foreach (var item in pending)
        {
            await queue.SignalAsync(item.Id, ct);
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
        await foreach (var _ in queue.Reader.ReadAllAsync(ct))
        {
            while (await queue.ClaimNextAsync(ct) is { } queueItemId)
            {
                try
                {
                    // A RateLimited outcome means ChapterDownloadProcessor already parked the item
                    // and started that tracker's cooldown — nothing more for this worker to do. It
                    // loops straight back to ClaimNextAsync, which will skip that tracker in favor of
                    // the next-highest-priority item on a different one.
                    using var scope = scopeFactory.CreateScope();
                    var processor = scope.ServiceProvider.GetRequiredService<ChapterDownloadProcessor>();
                    await processor.ProcessAsync(queueItemId, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
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
            item.ErrorMessage = cause.Message;
            item.RetryCount++;
            item.NextAttempt = queue.NextRetryAttempt(item.RetryCount);
            await db.SaveChangesAsync(ct);

            // This path bypasses ChapterDownloadProcessor.FailAsync, so report the outcome
            // ourselves — an unreported item would hold its batch open until the stale sweep.
            batches.Failed(item.SeriesId, item.Id, cause.Message);

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
