using System.Threading.Channels;
using Maki.Api.Dtos;
using Maki.Api.Hubs;
using Maki.Core.Entities;
using Maki.Core.Http;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Persists download queue items and feeds their ids to the worker via a channel.
/// Singleton; DB access goes through short-lived scopes.
/// </summary>
public class DownloadQueueService(IServiceScopeFactory scopeFactory, TimeProvider time) : IDownloadCooldown
{
    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>();

    // Shared scraper cooldown. When a source rate-limits us, all scraper workers back
    // off until this instant instead of hammering the site and failing every download.
    // Written under the lock, read lock-free via Interlocked so page fetches stay cheap.
    private readonly Lock _cooldownLock = new();
    private long _cooldownUntilTicks;
    private int _consecutiveRateLimits;
    private static readonly TimeSpan MaxCooldown = TimeSpan.FromMinutes(15);

    public ChannelReader<int> Reader => _channel.Reader;

    /// <summary>How long scraper workers should still wait before their next attempt.</summary>
    public TimeSpan CooldownRemaining()
    {
        var remaining = Interlocked.Read(ref _cooldownUntilTicks) - time.GetUtcNow().UtcDateTime.Ticks;
        return remaining > 0 ? TimeSpan.FromTicks(remaining) : TimeSpan.Zero;
    }

    TimeSpan IDownloadCooldown.Remaining() => CooldownRemaining();

    /// <summary>Waits out the current cooldown, if any. Re-checks because it can be extended mid-wait.</summary>
    public async Task WaitAsync(CancellationToken ct = default)
    {
        TimeSpan remaining;
        while ((remaining = CooldownRemaining()) > TimeSpan.Zero)
        {
            await Task.Delay(remaining, time, ct);
        }
    }

    /// <summary>
    /// Backs off all scraper downloads after a rate-limit hit. Honors the server's
    /// Retry-After when present, otherwise uses an escalating delay (30s → 15m) that
    /// grows with consecutive hits. Never shortens an already-longer cooldown.
    /// Returns the instant downloads may resume.
    /// </summary>
    public DateTime EnterRateLimitCooldown(TimeSpan? retryAfter)
    {
        lock (_cooldownLock)
        {
            var now = time.GetUtcNow().UtcDateTime;
            var currentUntil = Interlocked.Read(ref _cooldownUntilTicks);
            var alreadyCoolingDown = currentUntil > now.Ticks;

            TimeSpan duration;
            if (retryAfter is { } ra && ra > TimeSpan.Zero)
            {
                duration = ra < MaxCooldown ? ra : MaxCooldown;
            }
            else if (alreadyCoolingDown)
            {
                // We're already backing off and the server gave no Retry-After, so this 429 came
                // from a download that was still in flight when the cooldown started — the same
                // incident, not a fresh one. Escalating on it would double the wait for every
                // parallel page that was already mid-request.
                return new DateTime(currentUntil, DateTimeKind.Utc);
            }
            else
            {
                var n = ++_consecutiveRateLimits;
                var seconds = Math.Min(30 * Math.Pow(2, Math.Min(n - 1, 5)), MaxCooldown.TotalSeconds);
                duration = TimeSpan.FromSeconds(seconds);
            }

            var until = now.Add(duration).Ticks;
            if (until <= currentUntil)
            {
                return new DateTime(currentUntil, DateTimeKind.Utc);
            }

            Interlocked.Exchange(ref _cooldownUntilTicks, until);
            return new DateTime(until, DateTimeKind.Utc);
        }
    }

    /// <summary>Resets the escalating-backoff counter once a download succeeds again.</summary>
    public void ClearRateLimitBackoff()
    {
        lock (_cooldownLock)
        {
            _consecutiveRateLimits = 0;
        }
    }

    /// <summary>Queues a chapter for download from its best (lowest-priority-value) enabled mapping.</summary>
    public async Task<DownloadQueueItem?> EnqueueChapterAsync(int chapterId, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();

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
            QueuedAt = time.GetUtcNow().UtcDateTime
        };
        db.DownloadQueue.Add(item);
        await db.SaveChangesAsync(ct);

        await _channel.Writer.WriteAsync(item.Id, ct);
        return item;
    }

    /// <summary>Re-signals an existing queue item (startup recovery, manual retry).</summary>
    public ValueTask SignalAsync(int queueItemId, CancellationToken ct = default) =>
        _channel.Writer.WriteAsync(queueItemId, ct);

    private static readonly TimeSpan MaxRetryBackoff = TimeSpan.FromHours(6);

    /// <summary>
    /// Escalating backoff for the automatic Failed-item retry sweep: 5m → 10m → 20m ... capped at
    /// 6h. Mirrors the shape of <see cref="EnterRateLimitCooldown"/> but keyed per-item off
    /// <c>RetryCount</c> rather than queue-wide, since a Failed item's cause (bad chapter, dead
    /// source) isn't necessarily a rate limit.
    /// </summary>
    public DateTime NextRetryAttempt(int retryCount)
    {
        var seconds = Math.Min(
            300 * Math.Pow(2, Math.Max(retryCount - 1, 0)),
            MaxRetryBackoff.TotalSeconds);
        return time.GetUtcNow().UtcDateTime.AddSeconds(seconds);
    }

    /// <summary>
    /// Re-queues Failed scraper items whose backoff has elapsed and whose attempt count is still
    /// under <paramref name="maxAttempts"/>. Torrent items are excluded — they're tracked
    /// externally by <c>CompletedDownloadJob</c> against qBittorrent, and re-signalling one
    /// wouldn't resubmit the grab. Returns the number re-queued.
    /// </summary>
    public async Task<int> RequeueEligibleFailuresAsync(int maxAttempts, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        var events = scope.ServiceProvider.GetRequiredService<EventBroadcaster>();

        var now = time.GetUtcNow().UtcDateTime;
        var eligible = await db.DownloadQueue
            .Include(q => q.Chapter)
            .Include(q => q.Series)
            .Include(q => q.SourceMapping)
            .Where(q => q.Protocol == AcquisitionProtocol.Scraper &&
                        q.Status == QueueStatus.Failed &&
                        q.RetryCount < maxAttempts &&
                        (q.NextAttempt == null || q.NextAttempt <= now))
            .ToListAsync(ct);

        foreach (var item in eligible)
        {
            item.Status = QueueStatus.Queued;
            item.ErrorMessage = null;
        }

        await db.SaveChangesAsync(ct);

        foreach (var item in eligible)
        {
            await SignalAsync(item.Id, ct);
            if (item.Series is { } series)
            {
                await events.QueueUpdated(QueueItemDto.FromEntity(
                    item, item.Chapter, series, item.SourceMapping?.SourceName ?? "?"));
            }
        }

        return eligible.Count;
    }
}
