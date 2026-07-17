using Mangarr.Api.Dtos;
using Mangarr.Api.Hubs;
using Mangarr.Core.Configuration;
using Mangarr.Core.Entities;
using Mangarr.Data;
using Microsoft.EntityFrameworkCore;

namespace Mangarr.Api.Services;

/// <summary>
/// Consumes the download queue channel with a bounded number of concurrent
/// chapter workers. On startup, in-flight items from a previous run are reset
/// to Queued and re-signaled.
/// </summary>
public class DownloadWorkerHostedService(
    DownloadQueueService queue,
    IServiceScopeFactory scopeFactory,
    ILogger<DownloadWorkerHostedService> logger) : BackgroundService
{
    private const int DefaultConcurrentChapters = 2;
    private const int MaxConcurrentChapters = 8;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);

        var concurrency = await ResolveConcurrencyAsync(stoppingToken);
        var workers = Enumerable.Range(0, concurrency)
            .Select(i => WorkerLoopAsync(i, stoppingToken))
            .ToArray();

        await Task.WhenAll(workers);
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
        var db = scope.ServiceProvider.GetRequiredService<MangarrDbContext>();

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

    private async Task WorkerLoopAsync(int workerId, CancellationToken ct)
    {
        await foreach (var queueItemId in queue.Reader.ReadAllAsync(ct))
        {
            try
            {
                // A source rate-limited us: hold every scraper worker until the shared
                // cooldown elapses instead of retrying straight into another 429.
                await WaitOutCooldownAsync(workerId, ct);

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
            var db = scope.ServiceProvider.GetRequiredService<MangarrDbContext>();

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
            await db.SaveChangesAsync(ct);

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

    private async Task WaitOutCooldownAsync(int workerId, CancellationToken ct)
    {
        TimeSpan remaining;
        var logged = false;
        while ((remaining = queue.CooldownRemaining()) > TimeSpan.Zero)
        {
            if (!logged)
            {
                logger.LogInformation(
                    "Worker {Worker} pausing {Seconds:0}s for rate-limit cooldown",
                    workerId, remaining.TotalSeconds);
                logged = true;
            }

            await Task.Delay(remaining, ct);
        }
    }
}
