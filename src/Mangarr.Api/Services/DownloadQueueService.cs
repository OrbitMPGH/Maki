using System.Threading.Channels;
using Mangarr.Core.Entities;
using Mangarr.Data;
using Microsoft.EntityFrameworkCore;

namespace Mangarr.Api.Services;

/// <summary>
/// Persists download queue items and feeds their ids to the worker via a channel.
/// Singleton; DB access goes through short-lived scopes.
/// </summary>
public class DownloadQueueService(IServiceScopeFactory scopeFactory)
{
    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>();

    // Shared scraper cooldown. When a source rate-limits us, all scraper workers back
    // off until this instant instead of hammering the site and failing every download.
    private long _cooldownUntilTicks;
    private int _consecutiveRateLimits;
    private static readonly TimeSpan MaxCooldown = TimeSpan.FromMinutes(15);

    public ChannelReader<int> Reader => _channel.Reader;

    /// <summary>How long scraper workers should still wait before their next attempt.</summary>
    public TimeSpan CooldownRemaining()
    {
        var remaining = Interlocked.Read(ref _cooldownUntilTicks) - DateTime.UtcNow.Ticks;
        return remaining > 0 ? TimeSpan.FromTicks(remaining) : TimeSpan.Zero;
    }

    /// <summary>
    /// Backs off all scraper downloads after a rate-limit hit. Honors the server's
    /// Retry-After when present, otherwise uses an escalating delay (30s → 15m) that
    /// grows with consecutive hits. Never shortens an already-longer cooldown.
    /// Returns the instant downloads may resume.
    /// </summary>
    public DateTime EnterRateLimitCooldown(TimeSpan? retryAfter)
    {
        TimeSpan duration;
        if (retryAfter is { } ra && ra > TimeSpan.Zero)
        {
            duration = ra < MaxCooldown ? ra : MaxCooldown;
        }
        else
        {
            var n = Interlocked.Increment(ref _consecutiveRateLimits);
            var seconds = Math.Min(30 * Math.Pow(2, Math.Min(n - 1, 5)), MaxCooldown.TotalSeconds);
            duration = TimeSpan.FromSeconds(seconds);
        }

        var until = DateTime.UtcNow.Add(duration).Ticks;
        long current;
        do
        {
            current = Interlocked.Read(ref _cooldownUntilTicks);
            if (until <= current)
            {
                return new DateTime(current, DateTimeKind.Utc);
            }
        }
        while (Interlocked.CompareExchange(ref _cooldownUntilTicks, until, current) != current);

        return new DateTime(until, DateTimeKind.Utc);
    }

    /// <summary>Resets the escalating-backoff counter once a download succeeds again.</summary>
    public void ClearRateLimitBackoff() => Interlocked.Exchange(ref _consecutiveRateLimits, 0);

    /// <summary>Queues a chapter for download from its best (lowest-priority-value) enabled mapping.</summary>
    public async Task<DownloadQueueItem?> EnqueueChapterAsync(int chapterId, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangarrDbContext>();

        var chapter = await db.Chapters.FirstOrDefaultAsync(c => c.Id == chapterId, ct)
            ?? throw new InvalidOperationException($"Chapter {chapterId} not found");

        var alreadyQueued = await db.DownloadQueue.AnyAsync(q =>
            q.ChapterId == chapterId &&
            q.Status != QueueStatus.Completed &&
            q.Status != QueueStatus.Failed &&
            q.Status != QueueStatus.Cancelled, ct);
        if (alreadyQueued)
        {
            return null;
        }

        var mapping = await db.SourceMappings
            .Where(m => m.SeriesId == chapter.SeriesId && m.Enabled)
            .OrderBy(m => m.Priority)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Series has no enabled source mappings");

        var item = new DownloadQueueItem
        {
            SeriesId = chapter.SeriesId,
            ChapterId = chapterId,
            SourceMappingId = mapping.Id,
            Protocol = AcquisitionProtocol.Scraper,
            Status = QueueStatus.Queued,
            QueuedAt = DateTime.UtcNow
        };
        db.DownloadQueue.Add(item);
        await db.SaveChangesAsync(ct);

        await _channel.Writer.WriteAsync(item.Id, ct);
        return item;
    }

    /// <summary>Re-signals an existing queue item (startup recovery, manual retry).</summary>
    public ValueTask SignalAsync(int queueItemId, CancellationToken ct = default) =>
        _channel.Writer.WriteAsync(queueItemId, ct);
}
