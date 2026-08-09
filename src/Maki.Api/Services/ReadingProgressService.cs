using Maki.Core.Entities;
using Maki.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Process-wide serialization for <see cref="ReadingState"/> merges. The scrobble sync's own
/// lock only guards the sync against itself, which means nothing to a reader HTTP request
/// arriving on another thread — and the merge is a read-then-write, so interleaving would
/// produce duplicate rows. Registered as a singleton; the unique indexes are the backstop,
/// not the mechanism.
/// </summary>
public sealed class ReadingProgressGate
{
    public SemaphoreSlim Lock { get; } = new(1, 1);
}

/// <summary>
/// The single writer for <see cref="ReadingState"/>, shared by the Kavita scan and the
/// built-in reader. Keeps one forward-only high-water row per series and turns advances into
/// ChaptersRead/VolumesRead/SeriesFinished <see cref="StatsEvent"/>s for Rewind.
/// <para>
/// Merging both sources into one row is what makes double-counting impossible: deltas are
/// computed against the stored mark, so a chapter read in Maki and later re-reported by
/// Kavita yields a delta of zero. Deliberately not based on ScrobbleSyncState (per tracker
/// service: two trackers would double-count, zero trackers would record nothing).
/// </para>
/// </summary>
public class ReadingProgressService(
    MakiDbContext db,
    ReadingProgressGate gate,
    ILogger<ReadingProgressService> logger)
{
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The merged high-water marks after a track call.</summary>
    public readonly record struct Marks(double MaxChapter, double MaxVolume);

    /// <summary>
    /// Records progress observed in Kavita. Returns the <em>merged</em> marks, which may be
    /// ahead of what Kavita reported when the series was also read in Maki's own reader —
    /// callers must scrobble those, not the raw Kavita numbers, or native reading on an
    /// adopted series never reaches the trackers.
    /// </summary>
    /// <param name="userId">
    /// Whose reading this is. Every method here takes it explicitly rather than reading an ambient
    /// current user, because the two callers live in different worlds: the reader runs inside a request
    /// that has one, and the scrobble tick is a background job that walks several. Passing it also
    /// keeps the queries correct under an unrestricted <see cref="DataScope"/>, where the global filter
    /// narrows nothing.
    /// </param>
    public async Task<Marks> TrackKavitaAsync(int userId, int kavitaSeriesId, string title, int? seriesId,
        double maxChapter, double maxVolume, CancellationToken ct) =>
        await WithGateAsync(async () =>
        {
            var now = DateTime.UtcNow;
            var state = await db.ReadingStates
                .FirstOrDefaultAsync(r => r.UserId == userId && r.KavitaSeriesId == kavitaSeriesId, ct);

            if (state is null && seriesId is int sid)
            {
                // Adopt the row the built-in reader already owns for this series rather than
                // inserting a second one. Silent, like any first sighting: whatever Kavita has
                // been carrying is history from before Maki watched this series.
                state = await db.ReadingStates
                    .FirstOrDefaultAsync(
                        r => r.UserId == userId && r.SeriesId == sid && r.KavitaSeriesId == null, ct);
                if (state is not null)
                {
                    state.KavitaSeriesId = kavitaSeriesId;
                    state.MaxChapter = Math.Max(state.MaxChapter, maxChapter);
                    state.MaxVolume = Math.Max(state.MaxVolume, maxVolume);
                    state.Title = title;
                    state.Finished |= await IsSeriesFinishedAsync(seriesId, state.MaxChapter, ct);
                    state.UpdatedAt = now;
                    await db.SaveChangesAsync(ct);
                    logger.LogInformation(
                        "Adopted native reading state for '{Title}' into Kavita series {KavitaId} at chapter {Chapter}",
                        title, kavitaSeriesId, state.MaxChapter);
                    return new Marks(state.MaxChapter, state.MaxVolume);
                }
            }

            if (state is null)
            {
                // First sighting is a silent baseline: everything read before Maki started
                // watching must not land in today's stats. Same for a series already finished.
                db.ReadingStates.Add(new ReadingState
                {
                    UserId = userId,
                    KavitaSeriesId = kavitaSeriesId,
                    SeriesId = seriesId,
                    Title = title,
                    MaxChapter = maxChapter,
                    MaxVolume = maxVolume,
                    Finished = await IsSeriesFinishedAsync(seriesId, maxChapter, ct),
                    LastProgressAt = now,
                    UpdatedAt = now
                });
                await db.SaveChangesAsync(ct);
                return new Marks(maxChapter, maxVolume);
            }

            return await AdvanceAsync(userId, state, title, seriesId, maxChapter, maxVolume, now, ct);
        }, ct);

    /// <summary>
    /// Records progress made in the built-in reader. Unlike the Kavita path there is no silent
    /// baseline: a native row starts at zero and the very first read emits its delta, because
    /// nothing here predates Maki — the reading demonstrably just happened in it.
    /// </summary>
    public async Task<Marks> TrackNativeAsync(int userId, int seriesId, string title,
        double maxChapter, double maxVolume, CancellationToken ct) =>
        await WithGateAsync(async () =>
        {
            var now = DateTime.UtcNow;

            // Ordered by MaxChapter, because two Kavita series can legitimately resolve to one
            // local series and the pick has to be *stable*. UpdatedAt is not: the Kavita pass
            // stamps it on every row it touches each tick, so the row chosen here would flip
            // between reads and the next delta would be measured against a lower mark — the same
            // chapters counted twice into Rewind. The furthest mark also caps the delta.
            var state = await PickAsync(userId, seriesId, ct);

            if (state is null)
            {
                state = new ReadingState
                {
                    UserId = userId,
                    KavitaSeriesId = null,
                    SeriesId = seriesId,
                    Title = title,
                    MaxChapter = 0,
                    MaxVolume = 0,
                    LastProgressAt = now,
                    UpdatedAt = now
                };
                db.ReadingStates.Add(state);
            }

            return await AdvanceAsync(userId, state, title, seriesId, maxChapter, maxVolume, now, ct);
        }, ct);

    /// <summary>
    /// Merges progress that was made <em>elsewhere, before now</em> — the one-off import of read
    /// status out of Kavita — into the high-water row, emitting <b>no</b> <see cref="StatsEvent"/>
    /// at all.
    /// <para>
    /// Silence is the whole point: those chapters were read on unknown dates, and dating them
    /// today would dump a user's entire back catalogue onto one day of Rewind. Rewind counts only
    /// reading Maki actually observed happening — the scrobble job's Kavita deltas and the
    /// built-in reader. Advancing the mark here is still required, though: it is the baseline the
    /// next genuine read is measured against, so without it the first chapter read after an
    /// import would emit a delta of hundreds.
    /// </para>
    /// </summary>
    public async Task ImportSilentAsync(int userId, int seriesId, int? kavitaSeriesId, string title,
        double maxChapter, double maxVolume, CancellationToken ct) =>
        await WithGateAsync(async () =>
        {
            var now = DateTime.UtcNow;
            var state = kavitaSeriesId is int kid
                ? await db.ReadingStates
                    .FirstOrDefaultAsync(r => r.UserId == userId && r.KavitaSeriesId == kid, ct)
                : null;
            state ??= await PickAsync(userId, seriesId, ct);

            if (state is null)
            {
                db.ReadingStates.Add(new ReadingState
                {
                    UserId = userId,
                    KavitaSeriesId = kavitaSeriesId,
                    SeriesId = seriesId,
                    Title = title,
                    MaxChapter = maxChapter,
                    MaxVolume = maxVolume,
                    Finished = await IsSeriesFinishedAsync(seriesId, maxChapter, ct),
                    LastProgressAt = now,
                    UpdatedAt = now
                });
                await db.SaveChangesAsync(ct);
                return true;
            }

            state.KavitaSeriesId ??= kavitaSeriesId;
            state.SeriesId ??= seriesId;
            if (maxChapter > state.MaxChapter || maxVolume > state.MaxVolume)
            {
                state.MaxChapter = Math.Max(state.MaxChapter, maxChapter);
                state.MaxVolume = Math.Max(state.MaxVolume, maxVolume);
                // Deliberately not touching LastProgressAt: this reading didn't happen now, and
                // that field is what Rewind's "dropped series" staleness is measured from.
            }

            state.Finished |= await IsSeriesFinishedAsync(state.SeriesId, state.MaxChapter, ct);
            state.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            return true;
        }, ct);

    /// <summary>
    /// Records a read of a chapter that carries no number (a one-shot), which cannot be
    /// expressed as a high-water mark. Callers must gate this on the sticky
    /// <see cref="ChapterProgress.Completed"/> transition — that flag is the idempotency token.
    /// <para>
    /// Deliberately does NOT touch MaxChapter. There is no number to raise it to, and inventing
    /// one (1, or "highest known + 1") would make SmartDownloadJob mis-count unread chapters and
    /// would falsely mark numbered chapters read in the library's progress ring.
    /// </para>
    /// </summary>
    public async Task RecordUnnumberedReadAsync(int userId, int seriesId, string title, CancellationToken ct) =>
        await WithGateAsync(async () =>
        {
            var now = DateTime.UtcNow;
            // Same stable pick as everywhere else — see PickAsync.
            var kavitaSeriesId = (await PickAsync(userId, seriesId, ct))?.KavitaSeriesId;

            if (!await IsFullIncognitoAsync(seriesId, ct))
            {
                db.StatsEvents.Add(new StatsEvent
                {
                    Type = StatsEventType.ChaptersRead,
                    UserId = userId,
                    Timestamp = now,
                    SeriesId = seriesId,
                    KavitaSeriesId = kavitaSeriesId,
                    SeriesKey = await SeriesKeyAsync(seriesId, title, ct),
                    SeriesTitle = title,
                    Value = 1
                });
            }
            await db.SaveChangesAsync(ct);
            return true;
        }, ct);

    /// <summary>
    /// The one row to treat as a series' reading state, when more than one exists.
    /// <para>
    /// Duplicates per <c>SeriesId</c> are legal — two Kavita series can resolve to one local
    /// series — so every reader of this table has to order, and every one of them has to order
    /// the <em>same</em> way or they disagree about which row a series' progress lives in.
    /// <b>MaxChapter</b> is that key: it is forward-only, hence stable across ticks, and picking
    /// the furthest mark keeps a delta from being measured against a row that lags behind.
    /// <c>UpdatedAt</c> is explicitly wrong here — the Kavita pass restamps it on every row it
    /// touches, so the pick would flip between calls and re-count chapters into Rewind.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Scoped to one user: duplicates per <c>SeriesId</c> are legal <em>within</em> a user, and across
    /// users they are the norm — two readers of the same series each own a row, and picking the
    /// furthest of the two would measure one person's delta against the other's mark.
    /// </remarks>
    private async Task<ReadingState?> PickAsync(int userId, int seriesId, CancellationToken ct) =>
        await db.ReadingStates
            .Where(r => r.UserId == userId && r.SeriesId == seriesId)
            .OrderByDescending(r => r.MaxChapter)
            .ThenByDescending(r => r.Id)
            .FirstOrDefaultAsync(ct);

    /// <summary>Forward-only advance of an existing row, emitting the read events it implies.</summary>
    private async Task<Marks> AdvanceAsync(int userId, ReadingState state, string title, int? seriesId,
        double maxChapter, double maxVolume, DateTime now, CancellationToken ct)
    {
        // Forward-only: Kavita rescans, boundary refinement shifts, mark-unread and re-reads in
        // the built-in reader can all move the number backwards — never let that spike (or
        // negate) the stats.
        var chapterDelta = (int)Math.Floor(maxChapter) - (int)Math.Floor(state.MaxChapter);
        var volumeDelta = (int)Math.Floor(maxVolume) - (int)Math.Floor(state.MaxVolume);
        var fullIncognito = await IsFullIncognitoAsync(seriesId ?? state.SeriesId, ct);
        var seriesKey = await SeriesKeyAsync(seriesId ?? state.SeriesId, title, ct);

        if (chapterDelta > 0)
        {
            if (!fullIncognito)
            {
                db.StatsEvents.Add(new StatsEvent
                {
                    Type = StatsEventType.ChaptersRead,
                    UserId = userId,
                    Timestamp = now,
                    SeriesId = seriesId ?? state.SeriesId,
                    KavitaSeriesId = state.KavitaSeriesId,
                    SeriesKey = seriesKey,
                    SeriesTitle = title,
                    Value = chapterDelta
                });
            }
        }
        else if (volumeDelta > 0 && Math.Floor(maxChapter) <= 0)
        {
            // Volume-only series (no chapter numbering) — count whole volumes instead.
            if (!fullIncognito)
            {
                db.StatsEvents.Add(new StatsEvent
                {
                    Type = StatsEventType.VolumesRead,
                    UserId = userId,
                    Timestamp = now,
                    SeriesId = seriesId ?? state.SeriesId,
                    KavitaSeriesId = state.KavitaSeriesId,
                    SeriesKey = seriesKey,
                    SeriesTitle = title,
                    Value = volumeDelta
                });
            }
        }

        if (maxChapter > state.MaxChapter || maxVolume > state.MaxVolume)
        {
            state.MaxChapter = Math.Max(state.MaxChapter, maxChapter);
            state.MaxVolume = Math.Max(state.MaxVolume, maxVolume);
            state.LastProgressAt = now;
        }

        state.SeriesId = seriesId ?? state.SeriesId;

        if (!state.Finished && await IsSeriesFinishedAsync(state.SeriesId, state.MaxChapter, ct))
        {
            state.Finished = true;
            if (!fullIncognito)
            {
                db.StatsEvents.Add(new StatsEvent
                {
                    Type = StatsEventType.SeriesFinished,
                    UserId = userId,
                    Timestamp = now,
                    SeriesId = state.SeriesId,
                    KavitaSeriesId = state.KavitaSeriesId,
                    SeriesKey = seriesKey,
                    SeriesTitle = title
                });
            }
        }

        state.Title = title;
        state.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return new Marks(state.MaxChapter, state.MaxVolume);
    }

    private async Task<bool> IsFullIncognitoAsync(int? seriesId, CancellationToken ct) =>
        seriesId is int sid && await db.Series.AsNoTracking()
            .Where(s => s.Id == sid).Select(s => s.Incognito).FirstOrDefaultAsync(ct) == IncognitoMode.Full;

    /// <summary>
    /// The durable identity to stamp on an event. Falls back to the title key for a Kavita-only
    /// row, which has no local series to read provider ids off — the same fallback adoption uses,
    /// so the two halves of a removed-and-re-added series still meet.
    /// </summary>
    private async Task<string> SeriesKeyAsync(int? seriesId, string title, CancellationToken ct)
    {
        if (seriesId is not int sid)
        {
            return SeriesIdentity.ForTitle(title);
        }

        var row = await db.Series.AsNoTracking()
            .Where(s => s.Id == sid)
            .Select(s => new { s.Title, s.MangaBakaId, s.MangaDexUuid, s.AniListId, s.MalId })
            .FirstOrDefaultAsync(ct);

        return row is null
            ? SeriesIdentity.ForTitle(title)
            : SeriesIdentity.For(new Series
            {
                Title = row.Title,
                MangaBakaId = row.MangaBakaId,
                MangaDexUuid = row.MangaDexUuid,
                AniListId = row.AniListId,
                MalId = row.MalId
            });
    }

    /// <summary>
    /// "Finished" = the reader reached the highest chapter Maki knows for a series whose
    /// publication status is Completed. Unmatched Kavita series can never finish (no local
    /// chapter list to compare against) — acceptable.
    /// </summary>
    private async Task<bool> IsSeriesFinishedAsync(int? localSeriesId, double maxChapter, CancellationToken ct)
    {
        if (localSeriesId is not int sid || maxChapter <= 0)
        {
            return false;
        }

        var status = await db.Series.Where(s => s.Id == sid)
            .Select(s => (SeriesStatus?)s.Status).FirstOrDefaultAsync(ct);
        if (status != SeriesStatus.Completed)
        {
            return false;
        }

        var highest = await db.Chapters
            .Where(c => c.SeriesId == sid && c.Number != null)
            .OrderByDescending(c => c.Number)
            .Select(c => c.Number)
            .FirstOrDefaultAsync(ct);
        return highest is { } h && h > 0 && Math.Floor(maxChapter) >= Math.Floor((double)h);
    }

    /// <summary>
    /// Runs a merge under the gate, retrying once if a concurrent writer won the race to insert
    /// the same row (the unique index rejects it). The retry re-reads from scratch, so the
    /// forward-only merge simply folds into whatever the winner wrote.
    /// </summary>
    private async Task<T> WithGateAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        if (!await gate.Lock.WaitAsync(GateTimeout, ct))
        {
            throw new TimeoutException("Timed out waiting for the reading-progress lock");
        }

        try
        {
            try
            {
                return await action();
            }
            catch (DbUpdateException e) when (IsUniqueViolation(e))
            {
                logger.LogDebug("Reading-state merge lost a race, retrying: {Error}", e.Message);
                db.ChangeTracker.Clear();
                return await action();
            }
        }
        finally
        {
            gate.Lock.Release();
        }
    }

    // 2067 = SQLITE_CONSTRAINT_UNIQUE, 1555 = SQLITE_CONSTRAINT_PRIMARYKEY. Matched on the
    // *extended* code on purpose: the primary code (19, SQLITE_CONSTRAINT) also covers FK,
    // NOT NULL and CHECK failures, none of which a retry can resolve — retrying those just runs
    // the whole merge a second time before rethrowing the same error.
    private static bool IsUniqueViolation(DbUpdateException e) =>
        e.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 or 1555 };
}
