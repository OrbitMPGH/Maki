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
    private const int ConcurrentChapters = 2;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);

        var workers = Enumerable.Range(0, ConcurrentChapters)
            .Select(i => WorkerLoopAsync(i, stoppingToken))
            .ToArray();

        await Task.WhenAll(workers);
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
            }
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
