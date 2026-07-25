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
    public async Task<Marks> TrackKavitaAsync(int kavitaSeriesId, string title, int? seriesId,
        double maxChapter, double maxVolume, CancellationToken ct) =>
        await WithGateAsync(async () =>
        {
            var now = DateTime.UtcNow;
            var state = await db.ReadingStates
                .FirstOrDefaultAsync(r => r.KavitaSeriesId == kavitaSeriesId, ct);

            if (state is null && seriesId is int sid)
            {
                // Adopt the row the built-in reader already owns for this series rather than
                // inserting a second one. Silent, like any first sighting: whatever Kavita has
                // been carrying is history from before Maki watched this series.
                state = await db.ReadingStates
                    .FirstOrDefaultAsync(r => r.SeriesId == sid && r.KavitaSeriesId == null, ct);
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

            return await AdvanceAsync(state, title, seriesId, maxChapter, maxVolume, now, ct);
        }, ct);

    /// <summary>
    /// Records progress made in the built-in reader. Unlike the Kavita path there is no silent
    /// baseline: a native row starts at zero and the very first read emits its delta, because
    /// nothing here predates Maki — the reading demonstrably just happened in it.
    /// </summary>
    public async Task<Marks> TrackNativeAsync(int seriesId, string title,
        double maxChapter, double maxVolume, CancellationToken ct) =>
        await WithGateAsync(async () =>
        {
            var now = DateTime.UtcNow;

            // Ordered, because two Kavita series can legitimately resolve to one local series.
            // Picking the adopted/Kavita-backed row when one exists is deliberate: it is what
            // keeps the optional push-back to Kavita from echoing into a second row.
            var state = await db.ReadingStates
                .Where(r => r.SeriesId == seriesId)
                .OrderByDescending(r => r.UpdatedAt)
                .FirstOrDefaultAsync(ct);

            if (state is null)
            {
                state = new ReadingState
                {
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

            return await AdvanceAsync(state, title, seriesId, maxChapter, maxVolume, now, ct);
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
    public async Task RecordUnnumberedReadAsync(int seriesId, string title, CancellationToken ct) =>
        await WithGateAsync(async () =>
        {
            var now = DateTime.UtcNow;
            var kavitaSeriesId = await db.ReadingStates
                .Where(r => r.SeriesId == seriesId)
                .OrderByDescending(r => r.UpdatedAt)
                .Select(r => r.KavitaSeriesId)
                .FirstOrDefaultAsync(ct);

            db.StatsEvents.Add(new StatsEvent
            {
                Type = StatsEventType.ChaptersRead,
                Timestamp = now,
                SeriesId = seriesId,
                KavitaSeriesId = kavitaSeriesId,
                SeriesTitle = title,
                Value = 1
            });
            await db.SaveChangesAsync(ct);
            return true;
        }, ct);

    /// <summary>Forward-only advance of an existing row, emitting the read events it implies.</summary>
    private async Task<Marks> AdvanceAsync(ReadingState state, string title, int? seriesId,
        double maxChapter, double maxVolume, DateTime now, CancellationToken ct)
    {
        // Forward-only: Kavita rescans, boundary refinement shifts, mark-unread and re-reads in
        // the built-in reader can all move the number backwards — never let that spike (or
        // negate) the stats.
        var chapterDelta = (int)Math.Floor(maxChapter) - (int)Math.Floor(state.MaxChapter);
        var volumeDelta = (int)Math.Floor(maxVolume) - (int)Math.Floor(state.MaxVolume);

        if (chapterDelta > 0)
        {
            db.StatsEvents.Add(new StatsEvent
            {
                Type = StatsEventType.ChaptersRead,
                Timestamp = now,
                SeriesId = seriesId ?? state.SeriesId,
                KavitaSeriesId = state.KavitaSeriesId,
                SeriesTitle = title,
                Value = chapterDelta
            });
        }
        else if (volumeDelta > 0 && Math.Floor(maxChapter) <= 0)
        {
            // Volume-only series (no chapter numbering) — count whole volumes instead.
            db.StatsEvents.Add(new StatsEvent
            {
                Type = StatsEventType.VolumesRead,
                Timestamp = now,
                SeriesId = seriesId ?? state.SeriesId,
                KavitaSeriesId = state.KavitaSeriesId,
                SeriesTitle = title,
                Value = volumeDelta
            });
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
            db.StatsEvents.Add(new StatsEvent
            {
                Type = StatsEventType.SeriesFinished,
                Timestamp = now,
                SeriesId = state.SeriesId,
                KavitaSeriesId = state.KavitaSeriesId,
                SeriesTitle = title
            });
        }

        state.Title = title;
        state.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return new Marks(state.MaxChapter, state.MaxVolume);
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

    // 19 = SQLITE_CONSTRAINT, 2067 = SQLITE_CONSTRAINT_UNIQUE.
    private static bool IsUniqueViolation(DbUpdateException e) =>
        e.InnerException is SqliteException { SqliteErrorCode: 19 };
}
