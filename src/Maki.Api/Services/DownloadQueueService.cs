using System.Collections.Concurrent;
using System.Threading.Channels;
using Maki.Api.Dtos;
using Maki.Api.Hubs;
using Maki.Core.Entities;
using Maki.Core.Http;
using Maki.Data;
using Microsoft.Data.Sqlite;
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
    // _inFlight counts owners rather than just recording one: ClaimNextAsync registers before its
    // conditional flip, so two workers racing on one candidate both hold a registration until the
    // loser hands its back. With a presence set, that hand-back un-owned the row for the winner, and
    // the next sweep re-queued a download that was still running.
    private readonly ConcurrentDictionary<int, int> _inFlight = new();
    private readonly ConcurrentDictionary<int, byte> _resolving = new();

    /// <summary>
    /// Rows re-pinned while their previous resolve still owned them. That resolve drives them again
    /// on the way out, instead of leaving them Resolving until the orphan sweep comes round.
    /// </summary>
    private readonly ConcurrentDictionary<int, int> _resolveAgain = new();

    // A queue item can be cleared while a worker is fetching pages or a detached resolve is
    // finding a source. Its database row is cancelled or removed, but that alone cannot interrupt
    // the already-running request, which could otherwise write RateLimited or Completed back over
    // the cleared state. Each live owner shares one cancellation source per item so Clear stops
    // every local owner, including the rare two-worker 404 fallback overlap.
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _workCancellations = new();

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

    /// <summary>Cancellation token shared by every local owner of a queue item.</summary>
    public CancellationToken WorkCancellationToken(int queueItemId) =>
        _workCancellations.GetOrAdd(queueItemId, _ => new CancellationTokenSource()).Token;

    /// <summary>Stops any worker or source resolution currently handling this queue item.</summary>
    public void CancelWork(int queueItemId)
    {
        if (_workCancellations.TryGetValue(queueItemId, out var cancellation))
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The worker settled between TryGetValue and Cancel, so there is nothing left to stop.
            }
        }
    }

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
    /// Restores a cooldown persisted on a RateLimited row, so a restart right after a 429 doesn't
    /// send the next request straight back to the source. Never shortens a longer one.
    /// </summary>
    public void RestoreCooldown(string sourceName, DateTime until)
    {
        lock (_cooldownLock)
        {
            if (!_cooldowns.TryGetValue(sourceName, out var state))
            {
                state = new TrackerCooldown();
                _cooldowns[sourceName] = state;
            }

            if (until.Ticks > state.UntilTicks)
            {
                state.UntilTicks = until.Ticks;
            }
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
    public async Task EnqueueRepairAsync(MakiDbContext db, int operationId, Chapter chapter,
        ResolvedChapterSource source, int userId, CancellationToken ct)
    {
        var item = new DownloadQueueItem
        {
            ChapterId = chapter.Id, SeriesId = chapter.SeriesId, SourceMappingId = source.Mapping.Id,
            SourceChapterId = source.SourceChapterId, HealthOperationId = operationId,
            Origin = DownloadOrigin.HealthRepair, QueuedByUserId = userId, Status = QueueStatus.Queued,
            QueuedAt = time.GetUtcNow().UtcDateTime, SortOrder = await NextSortOrderAsync(db, ct)
        };
        db.DownloadQueue.Add(item);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (IsUniqueViolation(e))
        {
            db.Entry(item).State = EntityState.Detached;
            throw new InvalidOperationException("A download is already queued for this chapter");
        }
        await SignalAsync(item.Id, ct);
    }

    /// <param name="Queued">New rows, in the order their chapters were given.</param>
    /// <param name="Error">
    /// A catalogue key for why some chapters were not queued (a series with no enabled mapping, or
    /// under a health review, or a chapter that no longer exists). The rest are still queued.
    /// </param>
    public sealed record BulkEnqueueResult(IReadOnlyList<DownloadQueueItem> Queued, string? Error);

    /// <summary>
    /// Queues many chapters at once with the same rules as <see cref="EnqueueChapterAsync"/>, minus the
    /// pin: one context, one query per check over the whole set and one save, rather than a scope, a
    /// handful of queries, a MAX(SortOrder) scan and a commit per chapter. Queue order follows
    /// <paramref name="chapterIds"/>. Chapters already active are skipped.
    /// </summary>
    public async Task<BulkEnqueueResult> EnqueueChaptersAsync(
        IReadOnlyList<int> chapterIds,
        DownloadOrigin origin,
        int? queuedByUserId,
        CancellationToken ct = default)
    {
        var ids = chapterIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new BulkEnqueueResult([], null);
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();

        var seriesOf = await db.Chapters
            .Where(c => ids.Contains(c.Id))
            .Select(c => new { c.Id, c.SeriesId })
            .ToDictionaryAsync(c => c.Id, c => c.SeriesId, ct);

        string? error = null;
        var missing = ids.Where(id => !seriesOf.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            error = EnqueueRefusedException.ChapterGone;
        }

        var seriesIds = seriesOf.Values.Distinct().ToList();
        var reviewed = await db.HealthFiles
            .Where(f => f.SeriesId != null && seriesIds.Contains(f.SeriesId.Value) &&
                        db.HealthOperations.Any(o => o.FileId == f.Id && o.Status != "completed" &&
                                                     o.Status != "failed" && o.Status != "cancelled"))
            .Select(f => f.SeriesId!.Value)
            .Distinct()
            .ToListAsync(ct);
        if (reviewed.Count > 0)
        {
            error = EnqueueRefusedException.HealthReview;
        }

        var mapped = await sourceResolver.SeriesWithEnabledMappingAsync(db, seriesIds, ct);
        if (seriesIds.Any(id => !mapped.Contains(id)))
        {
            error = EnqueueRefusedException.NoMapping;
        }

        var active = (await db.DownloadQueue
                .Where(q => q.ActiveChapterId != null && ids.Contains(q.ActiveChapterId.Value))
                .Select(q => q.ActiveChapterId!.Value)
                .ToListAsync(ct))
            .ToHashSet();

        var toQueue = ids
            .Where(id => seriesOf.TryGetValue(id, out var seriesId) && !reviewed.Contains(seriesId) &&
                         mapped.Contains(seriesId) && !active.Contains(id))
            .ToList();
        if (toQueue.Count == 0)
        {
            return new BulkEnqueueResult([], error);
        }

        var now = time.GetUtcNow().UtcDateTime;
        var sortOrder = await NextSortOrderAsync(db, ct);
        var items = toQueue.Select((chapterId, i) => new DownloadQueueItem
        {
            SeriesId = seriesOf[chapterId],
            ChapterId = chapterId,
            Protocol = AcquisitionProtocol.Scraper,
            Status = QueueStatus.Resolving,
            QueuedAt = now,
            SortOrder = sortOrder + i,
            Origin = origin,
            QueuedByUserId = queuedByUserId
        }).ToList();

        db.DownloadQueue.AddRange(items);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (IsUniqueViolation(e))
        {
            // Something else queued one of these between the check and the insert. Rare enough that
            // falling back to the per-chapter path, which settles each race on its own, is fine.
            foreach (var item in items)
            {
                db.Entry(item).State = EntityState.Detached;
            }

            var queued = new List<DownloadQueueItem>();
            foreach (var chapterId in toQueue)
            {
                try
                {
                    if (await EnqueueChapterAsync(chapterId, ct, origin, queuedByUserId) is { } item)
                    {
                        queued.Add(item);
                    }
                }
                catch (EnqueueRefusedException ex)
                {
                    error = ex.Key;
                }
            }

            return new BulkEnqueueResult(queued, error);
        }

        foreach (var item in items)
        {
            _ = ResolveAndActivateAsync(item.Id, item.ChapterId!.Value, CancellationToken.None);
        }

        return new BulkEnqueueResult(items, error);
    }

    // On a queue insert only the unique index on ActiveChapterId can raise this.
    private static bool IsUniqueViolation(DbUpdateException e) =>
        e.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 or 1555 };

    /// <param name="preferMappingId">
    /// Pins the download to one of the series' enabled source mappings, e.g. a user picking a
    /// specific source copy of the chapter from the compare view. Tried first regardless of priority
    /// when this item (re-)resolves — see <see cref="ChapterSourceResolver.ResolveAsync"/>. Persisted
    /// on the row (<see cref="DownloadQueueItem.PreferredMappingId"/>) so it survives resolution
    /// happening later, possibly across a restart.
    /// <para>
    /// Also changes the dedupe outcome: a pinned request for a chapter that is already queued but not
    /// yet actively fetching (still <see cref="QueueStatus.Resolving"/>, <see cref="QueueStatus.Queued"/>,
    /// or <see cref="QueueStatus.RateLimited"/>) overrides that row's preference in place and re-resolves
    /// it, rather than being dropped like a second plain enqueue is. An item already past that point
    /// (fetching pages, downloading, etc.) is not cheap to redirect mid-flight, so the existing row is
    /// returned unchanged instead.
    /// </para>
    /// </param>
    /// <param name="replaceInfo">
    /// The chapter's existing file is to be replaced through the upgrade gate rather than overwritten.
    /// Stored on the new row, and on an existing row the pin is applied to.
    /// </param>
    public async Task<DownloadQueueItem?> EnqueueChapterAsync(
        int chapterId,
        CancellationToken ct = default,
        DownloadOrigin origin = DownloadOrigin.Unknown,
        int? queuedByUserId = null,
        int? preferMappingId = null,
        Maki.Core.Quality.UpgradeInfo? replaceInfo = null)
    {
        var replaceJson = replaceInfo?.Serialize();
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();

        var chapter = await db.Chapters.FirstOrDefaultAsync(c => c.Id == chapterId, ct)
            ?? throw new EnqueueRefusedException(EnqueueRefusedException.ChapterGone, $"Chapter {chapterId} not found");
        if (await db.HealthOperations.AnyAsync(o => db.HealthFiles.Any(f => f.Id == o.FileId && f.SeriesId == chapter.SeriesId)
            && o.Status != "completed" && o.Status != "failed" && o.Status != "cancelled", ct))
            throw new EnqueueRefusedException(EnqueueRefusedException.HealthReview, "A health review is active for this series");

        var existing = await db.DownloadQueue.FirstOrDefaultAsync(q => q.ActiveChapterId == chapterId, ct);
        if (existing is not null)
        {
            return await OnAlreadyActiveAsync(existing);
        }

        // Cheap, DB-only check: a series with literally no enabled mapping can be rejected
        // synchronously (same as before), without waiting on the per-chapter network lookup below.
        if (!await sourceResolver.HasEnabledMappingAsync(db, chapter.SeriesId, ct))
        {
            throw new EnqueueRefusedException(EnqueueRefusedException.NoMapping, "Series has no enabled source mappings");
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
            QueuedByUserId = queuedByUserId,
            PreferredMappingId = preferMappingId,
            UpgradeInfoJson = replaceJson
        };
        db.DownloadQueue.Add(item);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (IsUniqueViolation(e))
        {
            // Lost a race with another enqueue (a double click, or a manual download against
            // SmartDownloadJob) between the check above and this insert.
            db.Entry(item).State = EntityState.Detached;
            var winner = await db.DownloadQueue.FirstOrDefaultAsync(q => q.ActiveChapterId == chapterId, ct);
            if (winner is null)
            {
                throw;
            }
            return await OnAlreadyActiveAsync(winner);
        }

        // Detached from the request that enqueued it — CancellationToken.None, own scope inside —
        // since resolution can and should outlive the HTTP request that triggered it.
        _ = ResolveAndActivateAsync(item.Id, chapterId, CancellationToken.None);

        return item;

        async Task<DownloadQueueItem?> OnAlreadyActiveAsync(DownloadQueueItem active)
        {
            if (preferMappingId is null)
            {
                return null;
            }

            // Conditional, because a worker can claim the row between the read above and this write,
            // and a plain save would drag an active download back to Resolving. Already fetching or
            // downloading means the pin is not applied: the caller sees the row's own
            // PreferredMappingId still differs from what it asked for. A replacement also makes the row the
            // caller's own, so an automatic upgrade the user overrides stops being automatic.
            var replacing = replaceJson is not null;
            var replaced = replacing ? Maki.Core.Quality.UpgradeInfo.Parse(active.UpgradeInfoJson) : null;
            var repinned = await db.DownloadQueue
                .Where(q => q.Id == active.Id &&
                            (q.Status == QueueStatus.Resolving || q.Status == QueueStatus.Queued ||
                             q.Status == QueueStatus.RateLimited))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(q => q.PreferredMappingId, preferMappingId)
                    .SetProperty(q => q.SourceMappingId, (int?)null)
                    .SetProperty(q => q.SourceChapterId, (string?)null)
                    .SetProperty(q => q.Status, QueueStatus.Resolving)
                    .SetProperty(q => q.ErrorKey, (string?)null)
                    .SetProperty(q => q.ErrorParamsJson, (string?)null)
                    .SetProperty(q => q.ErrorMessage, (string?)null)
                    .SetProperty(q => q.UpgradeInfoJson, q => replaceJson ?? q.UpgradeInfoJson)
                    .SetProperty(q => q.Origin, q => replacing ? origin : q.Origin)
                    .SetProperty(q => q.QueuedByUserId, q => replacing ? queuedByUserId : q.QueuedByUserId), ct);
            await db.Entry(active).ReloadAsync(ct);

            // The scan's `enqueued` memo would otherwise keep that candidate closed after the user
            // dropped it for another source.
            if (repinned > 0 && replaced is { Force: false, AttemptId: > 0 } automatic)
            {
                await db.UpgradeAttempts.Where(a => a.Id == automatic.AttemptId).ExecuteDeleteAsync(ct);
            }

            if (repinned > 0)
            {
                _resolveAgain[active.Id] = chapterId;
                _ = ResolveAndActivateAsync(active.Id, chapterId, CancellationToken.None);
            }
            return active;
        }
    }

    /// <summary>
    /// Queues a replacement for a chapter's existing file, pinned to the mapping and source chapter the
    /// upgrade scan probed. Unlike <see cref="EnqueueChapterAsync"/> it never touches an existing row:
    /// null when the chapter has any live queue row, no longer has the file the scan judged, or its
    /// series is under a health review. Sorted after everything already queued, so an upgrade never
    /// jumps ahead of a missing chapter.
    /// </summary>
    public async Task<DownloadQueueItem?> EnqueueUpgradeAsync(int chapterId, int mappingId, string sourceChapterId,
        Maki.Core.Quality.UpgradeInfo info, int? queuedByUserId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();

        var chapter = await db.Chapters.AsNoTracking().FirstOrDefaultAsync(c => c.Id == chapterId, ct);
        if (chapter?.ChapterFileId is not { } fileId || fileId != info.ChapterFileId)
        {
            return null;
        }

        if (await db.DownloadQueue.AnyAsync(q => q.ChapterId == chapterId &&
                q.Status != QueueStatus.Completed && q.Status != QueueStatus.Failed && q.Status != QueueStatus.Cancelled, ct))
        {
            return null;
        }

        if (await db.HealthOperations.AnyAsync(o => db.HealthFiles.Any(f => f.Id == o.FileId && f.SeriesId == chapter.SeriesId)
            && o.Status != "completed" && o.Status != "failed" && o.Status != "cancelled", ct))
        {
            return null;
        }

        var item = new DownloadQueueItem
        {
            SeriesId = chapter.SeriesId,
            ChapterId = chapterId,
            Protocol = AcquisitionProtocol.Scraper,
            Status = QueueStatus.Queued,
            QueuedAt = time.GetUtcNow().UtcDateTime,
            SortOrder = await NextSortOrderAsync(db, ct),
            Origin = DownloadOrigin.Upgrade,
            QueuedByUserId = queuedByUserId,
            SourceMappingId = mappingId,
            PreferredMappingId = mappingId,
            SourceChapterId = sourceChapterId,
            UpgradeInfoJson = info.Serialize()
        };
        db.DownloadQueue.Add(item);
        await db.SaveChangesAsync(ct);
        await SignalAsync(item.Id, ct);
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
        _resolveAgain.TryRemove(itemId, out _);

        var workCancellation = WorkCancellationToken(itemId);

        try
        {
            // The gate is taken *after* registering in _resolving, so a row waiting its turn still
            // reads as owned and the orphan sweep leaves it alone instead of starting a second one.
            await _resolveGate.WaitAsync(workCancellation);
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, workCancellation);
                deadline.CancelAfter(ResolveDeadline);
                await ResolveAndActivateCoreAsync(itemId, chapterId, deadline.Token);
            }
            finally
            {
                _resolveGate.Release();
            }
        }
        catch (OperationCanceledException) when (workCancellation.IsCancellationRequested)
        {
            // Cleared from the queue. The controller has already settled or removed the row.
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The deadline, not a shutdown. Settle the row as failed so the retry sweep can pick it
            // back up on a backoff; leaving it Resolving would strand it until the next restart.
            logger.LogWarning("Resolving queue item {Id} exceeded {Minutes} min; failing it",
                itemId, ResolveDeadline.TotalMinutes);
            await TryFailResolveAsync(itemId, "error.download.resolveTimedOut");
        }
        catch (OperationCanceledException)
        {
            // Shutdown. Startup recovery resumes the row.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Resolving queue item {Id} failed outside its own handling", itemId);
            await TryFailResolveAsync(itemId, "error.download.unexpected");
        }
        finally
        {
            _resolving.TryRemove(itemId, out _);
            ReleaseWorkCancellation(itemId);

            if (_resolveAgain.TryRemove(itemId, out var againChapterId))
            {
                _ = ResolveAndActivateAsync(itemId, againChapterId, CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Last-resort settle for a resolve that blew up before it could record its own failure. Fresh
    /// scope, since the one that threw may hold a broken DbContext. Best-effort: if this fails too
    /// the DB is unreachable, and the orphan sweep will re-drive the row.
    /// </summary>
    private async Task TryFailResolveAsync(int itemId, string key)
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
            item.SetError(key);
            item.RetryCount++;
            item.NextAttempt = NextRetryAttempt(item.RetryCount);
            await db.SaveChangesAsync();
            await ReportFailureAsync(scope, item, key);

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
        if (item.HealthOperationId != null)
        {
            item.Status = QueueStatus.Failed;
            item.SetError("error.download.repairNeedsReview");
            await db.SaveChangesAsync(ct);
            return;
        }

        var chapter = await db.Chapters.FirstOrDefaultAsync(c => c.Id == chapterId, ct);
        if (chapter is null)
        {
            item.Status = QueueStatus.Failed;
            item.SetError("error.download.chapterGone");
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Removed (e.g. QueueController.Remove) while this was resolving. Nothing left to update.
                return;
            }

            await ReportFailureAsync(scope, item, "error.download.chapterGone");
            if (item.Series is { } gone)
            {
                await events.QueueUpdated(QueueItemDto.FromEntity(item, null, gone, "?"));
            }

            return;
        }

        string sourceNameForBroadcast;
        var pin = item.PreferredMappingId;
        while (true)
        {
            ResolvedChapterSource? resolved = null;
            SourceRateLimitedException? rateLimited = null;
            try
            {
                resolved = await sourceResolver.ResolveAsync(db, chapter, pin, ct, onlyPreferred: pin != null);
            }
            catch (SourceRateLimitedException ex)
            {
                rateLimited = ex;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Resolving queue item {Id} failed", itemId);
            }

            // A pick can land while this resolve is already running, and the enqueue then leaves the
            // row to this call. The write lock makes the re-read and the save one step, so a pick
            // either shows up here and is resolved again, or lands after and re-resolves on its own.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var latestPin = await db.DownloadQueue
                .Where(q => q.Id == itemId)
                .Select(q => q.PreferredMappingId)
                .FirstOrDefaultAsync(ct);
            if (latestPin != pin)
            {
                pin = latestPin;
                continue;
            }

            if (resolved is not null)
            {
                var cooldownUntil = CooldownUntil(resolved.Mapping.SourceName);

                item.SourceMappingId = resolved.Mapping.Id;
                item.SourceChapterId = resolved.SourceChapterId;
                item.Status = cooldownUntil is null ? QueueStatus.Queued : QueueStatus.RateLimited;
                item.NextAttempt = cooldownUntil;
                if (cooldownUntil is null)
                {
                    item.ClearError();
                }
                else
                {
                    item.SetError("error.download.rateLimited", new { source = resolved.Mapping.SourceName });
                }
                sourceNameForBroadcast = resolved.Mapping.SourceName;
            }
            else if (rateLimited is not null)
            {
                // The same handling as a rate limit mid-download: back the source off and park the row
                // without spending an attempt. With no mapping set it is claimable once NextAttempt
                // passes, and the processor resolves it again then.
                var until = EnterRateLimitCooldown(rateLimited.SourceName, rateLimited.RetryAfter);
                item.Status = QueueStatus.RateLimited;
                item.NextAttempt = until;
                item.SetError("error.download.rateLimited", new { source = rateLimited.SourceName });
                sourceNameForBroadcast = rateLimited.SourceName;
                logger.LogWarning("Rate limited by {Source} resolving queue item {Id}; backing off until {Until:o}",
                    rateLimited.SourceName, itemId, until);
            }
            else
            {
                item.Status = QueueStatus.Failed;
                item.SetError(pin is null ? "error.download.unexpected" : "error.download.pickedSourceUnavailable");
                item.RetryCount++;
                item.NextAttempt = NextRetryAttempt(item.RetryCount);
                sourceNameForBroadcast = "?";
            }

            try
            {
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Removed (e.g. QueueController.Remove) while this was resolving. Nothing left to update.
                return;
            }
            break;
        }

        if (item.Status is QueueStatus.Queued or QueueStatus.RateLimited)
        {
            await _channel.Writer.WriteAsync(item.Id, ct);
        }
        else if (item.Status == QueueStatus.Failed && item.ErrorKey is { } failedKey)
        {
            await ReportFailureAsync(scope, item, failedKey);
        }

        if (item.Series is { } series)
        {
            await events.QueueUpdated(QueueItemDto.FromEntity(item, chapter, series, sourceNameForBroadcast));
        }
    }

    /// <summary>
    /// Counts a resolve failure into the series' open download batch, the way the processor does for
    /// its own failures. Unreported, the id stays pending until the stale sweep and the summary comes
    /// an hour late with the failure listed as unfinished. Resolved from the scope rather than the
    /// constructor so the tests that build this service by hand need not supply one.
    /// </summary>
    private static async Task ReportFailureAsync(IServiceScope scope, DownloadQueueItem item, string key)
    {
        if (scope.ServiceProvider.GetService<DownloadBatchNotifier>() is { } batches)
        {
            await batches.FailedAsync(item.SeriesId, item.Id, key);
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
            // NextAttempt is the persisted half of a cooldown: the in-memory one is gone after a
            // restart, and a row parked RateLimited must still wait out its own time.
            var now = time.GetUtcNow().UtcDateTime;
            var cooling = CoolingDownSources();
            var candidate = await db.DownloadQueue
                .Where(q => q.Protocol == AcquisitionProtocol.Scraper &&
                            (q.Status == QueueStatus.Queued || q.Status == QueueStatus.RateLimited) &&
                            (q.NextAttempt == null || q.NextAttempt <= now) &&
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
            // orphan and re-queue out from under the worker about to run it. The cancellation source
            // is registered here too, for the same reason: a clear-queue request that lands between
            // the flip and CancelWork's TryGetValue must always find a token to cancel, never a gap.
            _inFlight.AddOrUpdate(candidate.Id, 1, (_, owners) => owners + 1);
            WorkCancellationToken(candidate.Id);

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
                ReleaseWorkCancellation(queueItemId);
                return;
            }
        }
    }

    private void ReleaseWorkCancellation(int queueItemId)
    {
        if (_inFlight.ContainsKey(queueItemId) || _resolving.ContainsKey(queueItemId))
        {
            return;
        }

        if (_workCancellations.TryRemove(queueItemId, out var cancellation))
        {
            cancellation.Dispose();
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

        var requeued = 0;
        if (orphanedDownloads.Count > 0)
        {
            // The status is re-checked in the same statement: a worker can settle a row between the
            // snapshot above and this write, and updating by id alone dragged a Completed row back
            // to Queued and downloaded it again.
            requeued = await db.DownloadQueue
                .Where(q => orphanedDownloads.Contains(q.Id) &&
                            (q.Status == QueueStatus.FetchingPages ||
                             q.Status == QueueStatus.Downloading ||
                             q.Status == QueueStatus.Validating ||
                             q.Status == QueueStatus.Packaging ||
                             q.Status == QueueStatus.Importing))
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
            var rows = await db.DownloadQueue
                .Where(q => unresolvable.Contains(q.Id) && q.Status == QueueStatus.Resolving)
                .ToListAsync(ct);
            foreach (var row in rows)
            {
                row.Status = QueueStatus.Failed;
                row.SetError("error.download.noChapterToResolve");
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

        return requeued + orphanedResolves.Count;
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

    /// <summary>Failures no retry can change, which <see cref="RequeueEligibleFailuresAsync"/> never picks up.</summary>
    public static readonly string[] PermanentErrorKeys = ["error.download.upgradeTargetGone"];

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
                        q.HealthOperationId == null &&
                        q.RetryCount < maxAttempts &&
                        (q.ErrorKey == null || !PermanentErrorKeys.Contains(q.ErrorKey)) &&
                        (q.NextAttempt == null || q.NextAttempt <= now) &&
                        (q.ChapterId == null || !db.DownloadQueue.Any(o => o.ActiveChapterId == q.ChapterId)))
            .ToListAsync(ct);

        // A chapter can hold several failed rows from separate enqueues, and only one may go active.
        eligible = eligible
            .GroupBy(q => q.ChapterId is { } chapterId ? (long)chapterId : -q.Id)
            .Select(g => g.MaxBy(q => q.Id)!)
            .ToList();

        foreach (var item in eligible)
        {
            // Land straight in RateLimited, not a "Queued" that never explains itself, if this
            // item's tracker is already cooling down from some other item's rate limit.
            var cooldownUntil = item.SourceMapping is { } mapping ? CooldownUntil(mapping.SourceName) : null;
            item.Status = cooldownUntil is null ? QueueStatus.Queued : QueueStatus.RateLimited;
            item.NextAttempt = cooldownUntil;
            if (cooldownUntil is null)
            {
                item.ClearError();
            }
            else
            {
                item.SetError("error.download.rateLimited", new { source = item.SourceMapping!.SourceName });
            }
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

/// <summary>An enqueue refused for a reason the caller can show, with its catalogue key.</summary>
public sealed class EnqueueRefusedException(string key, string message) : InvalidOperationException(message)
{
    public const string ChapterGone = "error.download.chapterGone";
    public const string HealthReview = "error.download.healthReviewActive";
    public const string NoMapping = "error.download.noEnabledMapping";

    public string Key { get; } = key;
}
