using System.Collections.Concurrent;
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
    ChapterSourceResolver sourceResolver,
    ILogger<DownloadQueueService> logger) : IDownloadCooldown
{
    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>();

    // Ids currently owned by a worker (claimed, not yet settled) and ids currently being resolved by
    // a detached ResolveAndActivateAsync. An in-flight *status* on a row whose id is in neither set
    // means nobody is actually working on it any more — the only way to tell a genuinely slow
    // download apart from one whose owner died, without inventing a heartbeat column. Registered
    // inside ClaimNextAsync/ResolveAndActivateAsync rather than by the caller, and *before* the
    // status flip, so there is no window for SweepOrphanedAsync to see the row unowned.
    //
    // _inFlight counts owners rather than just recording one: ChapterDownloadProcessor's 404 fallback
    // parks the row back in Queued while the worker is still recursing on it, so a second worker can
    // legitimately claim the same id. With a presence set, whichever finished first un-owned the row
    // for the other, and the next sweep re-queued a download that was still running.
    private readonly ConcurrentDictionary<int, int> _inFlight = new();
    private readonly ConcurrentDictionary<int, byte> _resolving = new();

    // Every enqueue starts a detached resolve, and a bulk enqueue (adding a series, "search missing",
    // a monitored refresh) starts one per chapter at once. Each resolve lists a source's catalog, so
    // an unbounded fan-out put hundreds of listings into one source's shared rate limiter at the same
    // instant; every one of them then aged out against its HttpClient timeout instead of returning,
    // the whole batch failed, and RetryFailedDownloadsJob re-queued it to do the same thing again.
    // SourceChapterListCache collapses the per-series duplicates; this bounds what is left, so a
    // 40-series refresh resolves a few at a time rather than all at once.
    private const int MaxConcurrentResolves = 3;
    private readonly SemaphoreSlim _resolveGate = new(MaxConcurrentResolves, MaxConcurrentResolves);

    // A resolve that never returns is worse than one that fails: the row stays Resolving, its id
    // stays in _resolving, and SweepOrphanedAsync therefore treats it as still being worked on and
    // never re-drives it. Nothing downstream guarantees termination on its own — the per-request
    // HttpClient timeouts don't bound a source that pages through hundreds of requests — so cap the
    // whole resolve. Generous, because a legitimate multi-mapping resolve behind a 1 req/s limiter
    // is genuinely slow; the point is that it ends.
    private static readonly TimeSpan ResolveDeadline = TimeSpan.FromMinutes(15);

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

    /// <summary>
    /// Snapshot of the sources currently cooling down, for <see cref="ClaimNextAsync"/> to exclude in
    /// SQL. Usually empty, which is the case the query is optimized for.
    /// </summary>
    private List<string> CoolingDownSources()
    {
        lock (_cooldownLock)
        {
            var nowTicks = time.GetUtcNow().UtcDateTime.Ticks;
            return _cooldowns.Where(pair => pair.Value.UntilTicks > nowTicks).Select(pair => pair.Key).ToList();
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
    /// <para>
    /// Every caller runs this detached, so nothing awaits it and nothing would ever observe a throw:
    /// an exception escaping here used to leave the row in Resolving forever, with only a restart to
    /// move it. The wrapper settles the row instead, and registers the id so
    /// <see cref="SweepOrphanedAsync"/> can tell a resolve still running from one that vanished.
    /// </para>
    /// </summary>
    public async Task ResolveAndActivateAsync(int itemId, int chapterId, CancellationToken ct)
    {
        // Also the guard against two resumes racing on the same row (startup recovery plus a sweep).
        if (!_resolving.TryAdd(itemId, 0))
        {
            return;
        }

        try
        {
            // The gate is taken *after* registering in _resolving, so a row waiting its turn still
            // reads as owned and the orphan sweep leaves it alone instead of starting a second one.
            await _resolveGate.WaitAsync(ct);
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(ResolveDeadline);
                await ResolveAndActivateCoreAsync(itemId, chapterId, deadline.Token);
            }
            finally
            {
                _resolveGate.Release();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The deadline, not a shutdown. Settle the row as failed so the retry sweep can pick it
            // back up on a backoff; leaving it Resolving would strand it until the next restart.
            logger.LogWarning("Resolving queue item {Id} exceeded {Minutes} min; failing it",
                itemId, ResolveDeadline.TotalMinutes);
            await TryFailResolveAsync(itemId, $"Timed out finding a source after {ResolveDeadline.TotalMinutes:0} minutes");
        }
        catch (OperationCanceledException)
        {
            // Shutdown. Startup recovery resumes the row.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Resolving queue item {Id} failed outside its own handling", itemId);
            await TryFailResolveAsync(itemId, ex.Message);
        }
        finally
        {
            _resolving.TryRemove(itemId, out _);
        }
    }

    /// <summary>
    /// Last-resort settle for a resolve that blew up before it could record its own failure. Fresh
    /// scope, since the one that threw may hold a broken DbContext. Best-effort: if this fails too
    /// the DB is unreachable, and the orphan sweep will re-drive the row.
    /// </summary>
    private async Task TryFailResolveAsync(int itemId, string error)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();

            var item = await db.DownloadQueue
                .Include(q => q.Chapter)
                .Include(q => q.Series)
                .FirstOrDefaultAsync(q => q.Id == itemId);
            if (item is null || item.Status != QueueStatus.Resolving)
            {
                return;
            }

            item.Status = QueueStatus.Failed;
            item.ErrorMessage = error;
            item.RetryCount++;
            item.NextAttempt = NextRetryAttempt(item.RetryCount);
            await db.SaveChangesAsync();

            // Same as every other settle path: without this the queue page keeps rendering the row
            // as Resolving until something unrelated repaints it, so the failure reads as a hang.
            if (item.Series is { } series)
            {
                var events = scope.ServiceProvider.GetRequiredService<EventBroadcaster>();
                await events.QueueUpdated(QueueItemDto.FromEntity(item, item.Chapter, series, "?"));
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not mark queue item {Id} as failed after a broken resolve", itemId);
        }
    }

    private async Task ResolveAndActivateCoreAsync(int itemId, int chapterId, CancellationToken ct)
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
    /// <para>
    /// A claimable item can legitimately have no mapping: resolution failing leaves the row Failed with
    /// <c>SourceMappingId</c> still null, and <see cref="RequeueEligibleFailuresAsync"/> then flips it
    /// back to Queued. Deleting a mapping nulls the column too (the FK is <c>SetNull</c>). Such an item
    /// belongs to no tracker, so no cooldown can apply to it — it is always claimable, and
    /// <c>ChapterDownloadProcessor</c> re-resolves the mapping on dispatch. Treating the null as a
    /// dictionary key instead threw out of the worker loop and silently killed every worker.
    /// </para>
    /// </summary>
    public async Task<int?> ClaimNextAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            // Cooling-down trackers are excluded in SQL rather than by materializing the queue and
            // filtering in memory. Every worker runs this on every wake — five seconds apart, for
            // the life of the process — so on a queue of a few thousand items the old form turned a
            // single-row pick into a repeated full scan of the table, and SQLite's writer lock made
            // that contend with the very status updates the pipeline needs to make progress.
            var cooling = CoolingDownSources();
            var candidate = await db.DownloadQueue
                .Where(q => q.Protocol == AcquisitionProtocol.Scraper &&
                            (q.Status == QueueStatus.Queued || q.Status == QueueStatus.RateLimited) &&
                            (q.SourceMapping == null || !cooling.Contains(q.SourceMapping.SourceName)))
                .OrderBy(q => q.SortOrder)
                .ThenBy(q => q.QueuedAt)
                .Select(q => new { q.Id })
                .FirstOrDefaultAsync(ct);

            if (candidate is null)
            {
                // Either nothing queued, or every remaining item's tracker is cooling down.
                return null;
            }

            // Registered before the flip, not after: the sweep only has the status and these two
            // dictionaries to go on, so an id registered a moment late is one it can read as an
            // orphan and re-queue out from under the worker about to run it.
            _inFlight.AddOrUpdate(candidate.Id, 1, (_, owners) => owners + 1);

            int claimed;
            try
            {
                claimed = await db.DownloadQueue
                    .Where(q => q.Id == candidate.Id &&
                                (q.Status == QueueStatus.Queued || q.Status == QueueStatus.RateLimited))
                    .ExecuteUpdateAsync(s => s.SetProperty(q => q.Status, QueueStatus.FetchingPages), ct);
            }
            catch
            {
                // The flip may or may not have landed, but this call owns nothing either way.
                ReleaseClaim(candidate.Id);
                throw;
            }

            if (claimed == 1)
            {
                return candidate.Id;
            }

            // Lost the race to another worker on this candidate; hand the registration back before
            // requerying, or this id would look owned for the rest of the process's life.
            ReleaseClaim(candidate.Id);
        }

        return null;
    }

    /// <summary>
    /// Hands one claim on an item back once the worker is done with it, whatever the outcome. Must be
    /// called in a <c>finally</c> — an id left registered is one <see cref="SweepOrphanedAsync"/>
    /// will never rescue. Drops the row from the owner table only when the *last* claim on it goes,
    /// since the 404 fallback can leave two workers legitimately holding the same id.
    /// </summary>
    public void ReleaseClaim(int queueItemId)
    {
        while (_inFlight.TryGetValue(queueItemId, out var owners))
        {
            if (owners > 1)
            {
                if (_inFlight.TryUpdate(queueItemId, owners - 1, owners))
                {
                    return;
                }
            }
            // Remove-if-still-this-count, so a claim taken between the read and the removal isn't lost.
            else if (((ICollection<KeyValuePair<int, int>>)_inFlight).Remove(new(queueItemId, owners)))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Re-queues rows that carry an in-flight status but have no owner: the worker or the detached
    /// resolve task that held them died without settling them. Startup recovery covers the process
    /// having restarted; this covers the same thing happening while the process keeps running, which
    /// otherwise leaves an item reading "Fetching" indefinitely with nothing to move it along.
    /// Returns how many rows were moved.
    /// </summary>
    public async Task<int> SweepOrphanedAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();

        // Torrent items are tracked externally by CompletedDownloadJob and have no worker here, so
        // their statuses must be left alone.
        var active = await db.DownloadQueue
            .Where(q => q.Protocol == AcquisitionProtocol.Scraper &&
                        (q.Status == QueueStatus.Resolving ||
                         q.Status == QueueStatus.FetchingPages ||
                         q.Status == QueueStatus.Downloading ||
                         q.Status == QueueStatus.Validating ||
                         q.Status == QueueStatus.Packaging ||
                         q.Status == QueueStatus.Importing))
            .Select(q => new { q.Id, q.Status, q.ChapterId })
            .ToListAsync(ct);

        var orphanedResolves = active
            .Where(q => q.Status == QueueStatus.Resolving && !_resolving.ContainsKey(q.Id))
            .ToList();
        var orphanedDownloads = active
            .Where(q => q.Status != QueueStatus.Resolving && !_inFlight.ContainsKey(q.Id))
            .Select(q => q.Id)
            .ToList();

        if (orphanedDownloads.Count > 0)
        {
            await db.DownloadQueue
                .Where(q => orphanedDownloads.Contains(q.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(q => q.Status, QueueStatus.Queued), ct);

            foreach (var id in orphanedDownloads)
            {
                await SignalAsync(id, ct);
            }
        }

        // A Resolving row has no mapping yet, so it isn't claimable — re-drive resolution rather than
        // flipping it to Queued. One with no chapter can never resolve against anything, so fail it
        // instead of sweeping the same row every tick forever.
        var unresolvable = orphanedResolves.Where(q => q.ChapterId is null).Select(q => q.Id).ToList();
        if (unresolvable.Count > 0)
        {
            // Counted as an attempt and given a backoff like any other failure. Failing it with
            // RetryCount 0 and no NextAttempt puts it straight back in RequeueEligibleFailuresAsync's
            // sights — the same job run would flip it to Queued again, and the row would cycle
            // between the two passes forever instead of settling.
            var rows = await db.DownloadQueue.Where(q => unresolvable.Contains(q.Id)).ToListAsync(ct);
            foreach (var row in rows)
            {
                row.Status = QueueStatus.Failed;
                row.ErrorMessage = "Queue item has no chapter to resolve";
                row.RetryCount++;
                row.NextAttempt = NextRetryAttempt(row.RetryCount);
            }

            await db.SaveChangesAsync(ct);
        }

        foreach (var item in orphanedResolves)
        {
            if (item.ChapterId is { } chapterId)
            {
                _ = ResolveAndActivateAsync(item.Id, chapterId, CancellationToken.None);
            }
        }

        return orphanedDownloads.Count + orphanedResolves.Count;
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
