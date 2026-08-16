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
public class DownloadQueueService(
    IServiceScopeFactory scopeFactory,
    TimeProvider time,
    ChapterSourceResolver sourceResolver) : IDownloadCooldown
{
    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>();

    // Per-tracker (source) cooldown. A rate limit on one source only backs that source off — other
    // trackers keep dispatching from the same queue in the meantime. Guarded by _cooldownLock rather
    // than made concurrent-safe per-entry, since rate limits are rare and this is never on a hot path.
    private readonly Lock _cooldownLock = new();
    private readonly Dictionary<string, TrackerCooldown> _cooldowns = new();
    private static readonly TimeSpan MaxCooldown = TimeSpan.FromMinutes(15);

    private class TrackerCooldown
    {
        public long UntilTicks;
        public int ConsecutiveRateLimits;
    }

    public ChannelReader<int> Reader => _channel.Reader;

    /// <summary>How long downloads from this source should still wait before their next attempt.</summary>
    public TimeSpan CooldownRemaining(string sourceName)
    {
        lock (_cooldownLock)
        {
            if (!_cooldowns.TryGetValue(sourceName, out var state))
            {
                return TimeSpan.Zero;
            }

            var remaining = state.UntilTicks - time.GetUtcNow().UtcDateTime.Ticks;
            return remaining > 0 ? TimeSpan.FromTicks(remaining) : TimeSpan.Zero;
        }
    }

    TimeSpan IDownloadCooldown.Remaining(string sourceName) => CooldownRemaining(sourceName);

    /// <summary>The instant this source's cooldown lifts, or null if it isn't currently cooling down.</summary>
    public DateTime? CooldownUntil(string sourceName)
    {
        lock (_cooldownLock)
        {
            if (!_cooldowns.TryGetValue(sourceName, out var state) || state.UntilTicks <= time.GetUtcNow().UtcDateTime.Ticks)
            {
                return null;
            }

            return new DateTime(state.UntilTicks, DateTimeKind.Utc);
        }
    }

    /// <summary>Waits out the given source's current cooldown, if any. Re-checks because it can be extended mid-wait.</summary>
    public async Task WaitAsync(string sourceName, CancellationToken ct = default)
    {
        TimeSpan remaining;
        while ((remaining = CooldownRemaining(sourceName)) > TimeSpan.Zero)
        {
            await Task.Delay(remaining, time, ct);
        }
    }

    /// <summary>
    /// Backs off downloads from <paramref name="sourceName"/> after a rate-limit hit. Honors the
    /// server's Retry-After when present, otherwise uses an escalating delay (30s → 15m) that grows
    /// with that source's consecutive hits. Never shortens an already-longer cooldown for it.
    /// Returns the instant downloads from this source may resume. Other sources are unaffected.
    /// </summary>
    public DateTime EnterRateLimitCooldown(string sourceName, TimeSpan? retryAfter)
    {
        lock (_cooldownLock)
        {
            var now = time.GetUtcNow().UtcDateTime;
            if (!_cooldowns.TryGetValue(sourceName, out var state))
            {
                state = new TrackerCooldown();
                _cooldowns[sourceName] = state;
            }

            var alreadyCoolingDown = state.UntilTicks > now.Ticks;

            TimeSpan duration;
            if (retryAfter is { } ra && ra > TimeSpan.Zero)
            {
                duration = ra < MaxCooldown ? ra : MaxCooldown;
            }
            else if (alreadyCoolingDown)
            {
                // Already backing this source off and it gave no Retry-After, so this 429 came from
                // a download that was still in flight when the cooldown started — the same incident,
                // not a fresh one. Escalating on it would double the wait for every parallel page
                // that was already mid-request.
                return new DateTime(state.UntilTicks, DateTimeKind.Utc);
            }
            else
            {
                var n = ++state.ConsecutiveRateLimits;
                var seconds = Math.Min(30 * Math.Pow(2, Math.Min(n - 1, 5)), MaxCooldown.TotalSeconds);
                duration = TimeSpan.FromSeconds(seconds);
            }

            var until = now.Add(duration).Ticks;
            if (until <= state.UntilTicks)
            {
                return new DateTime(state.UntilTicks, DateTimeKind.Utc);
            }

            state.UntilTicks = until;
            return new DateTime(until, DateTimeKind.Utc);
        }
    }

    /// <summary>Resets the given source's escalating-backoff counter once a download from it succeeds again.</summary>
    public void ClearRateLimitBackoff(string sourceName)
    {
        lock (_cooldownLock)
        {
            if (_cooldowns.TryGetValue(sourceName, out var state))
            {
                state.ConsecutiveRateLimits = 0;
            }
        }
    }

    /// <summary>
    /// Queues a chapter for download. Returns as soon as the row exists — finding which mapping
    /// actually has this chapter means listing each source's catalog over the network, too slow to
    /// make "Download this chapter" or "Search missing" wait on. The item shows up immediately as
    /// <see cref="QueueStatus.Resolving"/>; <see cref="ResolveAndActivateAsync"/> fills in the real
    /// source in the background and flips it to Queued (or RateLimited) once found.
    /// </summary>
    /// <param name="origin">
    /// What triggered this. Recorded on the row because every path funnels through here and is
    /// otherwise indistinguishable afterwards, and because the in-app inbox notifies on automatic
    /// downloads only — somebody who clicked Download watched it happen.
    /// </param>
    /// <param name="queuedByUserId">
    /// Who the download is for, when that is one person. For a request approval that is the
    /// <em>requester</em>, not the admin who approved it.
    /// </param>
    public async Task<DownloadQueueItem?> EnqueueChapterAsync(
        int chapterId,
        CancellationToken ct = default,
        DownloadOrigin origin = DownloadOrigin.Unknown,
        int? queuedByUserId = null)
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

        // Cheap, DB-only check: a series with literally no enabled mapping can be rejected
        // synchronously (same as before), without waiting on the per-chapter network lookup below.
        if (!await sourceResolver.HasEnabledMappingAsync(db, chapter.SeriesId, ct))
        {
            throw new InvalidOperationException("Series has no enabled source mappings");
        }

        var item = new DownloadQueueItem
        {
            SeriesId = chapter.SeriesId,
            ChapterId = chapterId,
            Protocol = AcquisitionProtocol.Scraper,
            Status = QueueStatus.Resolving,
            QueuedAt = time.GetUtcNow().UtcDateTime,
            SortOrder = await NextSortOrderAsync(db, ct),
            Origin = origin,
            QueuedByUserId = queuedByUserId
        };
        db.DownloadQueue.Add(item);
        await db.SaveChangesAsync(ct);

        // Detached from the request that enqueued it — CancellationToken.None, own scope inside —
        // since resolution can and should outlive the HTTP request that triggered it.
        _ = ResolveAndActivateAsync(item.Id, chapterId, CancellationToken.None);

        return item;
    }

    /// <summary>
    /// Finds which enabled mapping actually has this chapter and flips the item from Resolving into
    /// Queued, or straight into RateLimited if that source is already cooling down. Runs detached
    /// from any particular caller — also used by <c>DownloadWorkerHostedService</c> to resume items
    /// that were still Resolving when the app last stopped.
    /// </summary>
    public async Task ResolveAndActivateAsync(int itemId, int chapterId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        var events = scope.ServiceProvider.GetRequiredService<EventBroadcaster>();

        var item = await db.DownloadQueue
            .Include(q => q.Series)
            .FirstOrDefaultAsync(q => q.Id == itemId, ct);

        // Removed, or already settled by something else (e.g. a restart resuming it twice), before
        // this got a chance to run.
        if (item is null || item.Status != QueueStatus.Resolving)
        {
            return;
        }

        var chapter = await db.Chapters.FirstOrDefaultAsync(c => c.Id == chapterId, ct);
        if (chapter is null)
        {
            item.Status = QueueStatus.Failed;
            item.ErrorMessage = "Chapter no longer exists";
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Removed (e.g. QueueController.Remove) while this was resolving. Nothing left to update.
                return;
            }

            if (item.Series is { } gone)
            {
                await events.QueueUpdated(QueueItemDto.FromEntity(item, null, gone, "?"));
            }

            return;
        }

        string sourceNameForBroadcast;
        try
        {
            var resolved = await sourceResolver.ResolveAsync(db, chapter, preferMappingId: null, ct);
            var cooldownUntil = CooldownUntil(resolved.Mapping.SourceName);

            item.SourceMappingId = resolved.Mapping.Id;
            item.SourceChapterId = resolved.SourceChapterId;
            item.Status = cooldownUntil is null ? QueueStatus.Queued : QueueStatus.RateLimited;
            item.NextAttempt = cooldownUntil;
            item.ErrorMessage = cooldownUntil is { } until
                ? $"Rate limited by {resolved.Mapping.SourceName} — retrying after {until.ToLocalTime():HH:mm:ss}"
                : null;
            sourceNameForBroadcast = resolved.Mapping.SourceName;
        }
        catch (Exception ex)
        {
            item.Status = QueueStatus.Failed;
            item.ErrorMessage = ex.Message;
            item.RetryCount++;
            item.NextAttempt = NextRetryAttempt(item.RetryCount);
            sourceNameForBroadcast = "?";
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Removed (e.g. QueueController.Remove) while this was resolving. Nothing left to update.
            return;
        }

        if (item.Status is QueueStatus.Queued or QueueStatus.RateLimited)
        {
            await _channel.Writer.WriteAsync(item.Id, ct);
        }

        if (item.Series is { } series)
        {
            await events.QueueUpdated(QueueItemDto.FromEntity(item, chapter, series, sourceNameForBroadcast));
        }
    }

    /// <summary>Next-to-the-end manual position for a newly created queue item.</summary>
    public static async Task<int> NextSortOrderAsync(MakiDbContext db, CancellationToken ct = default) =>
        (await db.DownloadQueue.MaxAsync(q => (int?)q.SortOrder, ct) ?? 0) + 1;

    /// <summary>Re-signals an existing queue item (startup recovery, manual retry).</summary>
    public ValueTask SignalAsync(int queueItemId, CancellationToken ct = default) =>
        _channel.Writer.WriteAsync(queueItemId, ct);

    /// <summary>
    /// Claims the highest-priority claimable scraper item (lowest <see cref="DownloadQueueItem.SortOrder"/>,
    /// ties broken by <see cref="DownloadQueueItem.QueuedAt"/>) whose source isn't currently cooling down.
    /// Claimable status means <c>Queued</c> (never attempted) or <c>RateLimited</c> (a previous attempt was
    /// throttled — its own tracker's cooldown may have lifted since). Skipping past a cooling-down tracker
    /// to the next-highest-priority item on a different one is the whole point of a per-tracker cooldown:
    /// one rate-limited source shouldn't stall everything else in the queue. The status flip is a
    /// conditional update so two workers racing on the same candidate can't both grab it.
    /// </summary>
    public async Task<int?> ClaimNextAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var candidates = await db.DownloadQueue
                .Where(q => q.Protocol == AcquisitionProtocol.Scraper &&
                            (q.Status == QueueStatus.Queued || q.Status == QueueStatus.RateLimited))
                .OrderBy(q => q.SortOrder)
                .ThenBy(q => q.QueuedAt)
                .Select(q => new { q.Id, SourceName = q.SourceMapping!.SourceName })
                .ToListAsync(ct);

            var candidate = candidates.FirstOrDefault(c => CooldownRemaining(c.SourceName) <= TimeSpan.Zero);
            if (candidate is null)
            {
                // Either nothing queued, or every remaining item's tracker is cooling down.
                return null;
            }

            var claimed = await db.DownloadQueue
                .Where(q => q.Id == candidate.Id &&
                            (q.Status == QueueStatus.Queued || q.Status == QueueStatus.RateLimited))
                .ExecuteUpdateAsync(s => s.SetProperty(q => q.Status, QueueStatus.FetchingPages), ct);

            if (claimed == 1)
            {
                return candidate.Id;
            }

            // Lost the race to another worker on this candidate; requery and try again.
        }

        return null;
    }

    /// <summary>
    /// Sets the manual dispatch order for a batch of active queue items. Ids not currently active
    /// (already completed/cancelled, or unknown) are ignored rather than erroring — the caller is
    /// reordering a snapshot that may have moved on since it was fetched.
    /// </summary>
    public async Task ReorderAsync(IReadOnlyList<int> orderedIds, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();

        var items = await db.DownloadQueue
            .Where(q => orderedIds.Contains(q.Id) &&
                        q.Status != QueueStatus.Completed && q.Status != QueueStatus.Cancelled)
            .ToDictionaryAsync(q => q.Id, ct);

        for (var i = 0; i < orderedIds.Count; i++)
        {
            if (items.TryGetValue(orderedIds[i], out var item))
            {
                item.SortOrder = i;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private static readonly TimeSpan MaxRetryBackoff = TimeSpan.FromHours(6);

    /// <summary>
    /// Escalating backoff for the automatic Failed-item retry sweep: 5m → 10m → 20m ... capped at
    /// 6h. Mirrors the shape of <see cref="EnterRateLimitCooldown"/> but keyed per-item off
    /// <c>RetryCount</c> rather than per-tracker, since a Failed item's cause (bad chapter, dead
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
            // Land straight in RateLimited, not a "Queued" that never explains itself, if this
            // item's tracker is already cooling down from some other item's rate limit.
            var cooldownUntil = item.SourceMapping is { } mapping ? CooldownUntil(mapping.SourceName) : null;
            item.Status = cooldownUntil is null ? QueueStatus.Queued : QueueStatus.RateLimited;
            item.NextAttempt = cooldownUntil;
            item.ErrorMessage = cooldownUntil is { } until
                ? $"Rate limited by {item.SourceMapping!.SourceName} — retrying after {until.ToLocalTime():HH:mm:ss}"
                : null;
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
