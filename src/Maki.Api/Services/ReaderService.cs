using Maki.Core.Entities;
using Maki.Core.Paths;
using Maki.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Backs the built-in reader: resolves a chapter to its slice of pages inside a CBZ, and
/// records reading progress into <see cref="ChapterProgress"/> and, through
/// <see cref="ReadingProgressService"/>, into the shared <see cref="ReadingState"/> aggregate.
/// </summary>
public class ReaderService(
    MakiDbContext db,
    ReaderArchiveCache archives,
    ReadingProgressService progress,
    KavitaProgressPusher kavitaPush,
    ILogger<ReaderService> logger)
{
    /// <summary>Where a chapter lives inside its backing archive.</summary>
    public record ChapterSlice(
        Chapter Chapter,
        Series Series,
        int ChapterFileId,
        string ArchivePath,
        long ArchiveSize,
        IReadOnlyList<string> Pages,
        int StartPage,
        int PageCount);

    /// <summary>
    /// Resolves the chapter's pages. Returns null when the chapter is unknown, has no file, or
    /// the file is missing/unreadable on disk.
    /// <para>
    /// Every page and thumbnail request resolves a slice, so this is the hottest path the reader
    /// has — a 200-page chapter with prefetch runs it hundreds of times for one sitting. It is
    /// deliberately <b>one</b> round-trip: chapter, file, series, root folder and the
    /// shares-an-archive test all come back in a single projection. It used to be three separate
    /// queries, which multiplied straight through by the page count.
    /// </para>
    /// </summary>
    public async Task<ChapterSlice?> SliceAsync(int chapterId, CancellationToken ct)
    {
        var row = await db.Chapters
            .Where(c => c.Id == chapterId && c.ChapterFileId != null)
            .Select(c => new
            {
                Chapter = c,
                File = c.ChapterFile!,
                Series = c.Series,
                RootPath = c.Series!.RootFolder!.Path,
                // A volume/compilation archive backs more than one chapter, which is what makes
                // this chapter a slice rather than the whole file.
                SharesFile = db.Chapters.Any(o => o.ChapterFileId == c.ChapterFileId && o.Id != c.Id),
            })
            .FirstOrDefaultAsync(ct);

        if (row?.Series is null || string.IsNullOrEmpty(row.RootPath))
        {
            return null;
        }

        var file = row.File;
        var absolute = LibraryPaths.Resolve(row.RootPath, file.RelativePath);
        if (absolute is null || !File.Exists(absolute))
        {
            logger.LogWarning("Chapter {ChapterId} file is missing: {Path}", chapterId, file.RelativePath);
            return null;
        }

        var info = archives.Get(file.Id, file.Size, absolute);
        if (info.Pages.Count == 0)
        {
            return null;
        }

        var (start, count) = SliceBounds(info, row.Chapter, row.SharesFile);

        return new ChapterSlice(
            row.Chapter, row.Series, file.Id, absolute, file.Size, info.Pages, start, count);
    }

    /// <summary>
    /// The same resolution for a whole set of chapters, in one query.
    /// <para>
    /// The OPDS feed needs a page count for every chapter it lists — OPDS-PSE has no way to
    /// express a chapter of unknown length — and calling <see cref="SliceAsync"/> per row would be
    /// one database round-trip per entry on a feed page. Archive reads still happen per chapter,
    /// but those go through <see cref="ReaderArchiveCache"/> and are warm after the first render.
    /// </para>
    /// <para>
    /// Chapters with no file, a file missing from disk, or an unreadable archive are absent from
    /// the result rather than present with a zero count: the feed drops them instead of offering a
    /// stream that would 404 on page 0.
    /// </para>
    /// </summary>
    public async Task<Dictionary<int, ChapterSlice>> SlicesAsync(
        IReadOnlyCollection<int> chapterIds, CancellationToken ct)
    {
        if (chapterIds.Count == 0)
        {
            return [];
        }

        var rows = await db.Chapters
            .Where(c => chapterIds.Contains(c.Id) && c.ChapterFileId != null)
            .Select(c => new
            {
                Chapter = c,
                File = c.ChapterFile!,
                Series = c.Series,
                RootPath = c.Series!.RootFolder!.Path,
                SharesFile = db.Chapters.Any(o => o.ChapterFileId == c.ChapterFileId && o.Id != c.Id),
            })
            .ToListAsync(ct);

        var slices = new Dictionary<int, ChapterSlice>();
        foreach (var row in rows)
        {
            if (row.Series is null || string.IsNullOrEmpty(row.RootPath))
            {
                continue;
            }

            var absolute = LibraryPaths.Resolve(row.RootPath, row.File.RelativePath);
            if (absolute is null || !File.Exists(absolute))
            {
                continue;
            }

            var info = archives.Get(row.File.Id, row.File.Size, absolute);
            if (info.Pages.Count == 0)
            {
                continue;
            }

            var (start, count) = SliceBounds(info, row.Chapter, row.SharesFile);
            slices[row.Chapter.Id] = new ChapterSlice(
                row.Chapter, row.Series, row.File.Id, absolute, row.File.Size, info.Pages, start, count);
        }

        return slices;
    }

    /// <summary>
    /// The page range a chapter occupies. A volume/compilation CBZ backs several chapters, and
    /// the only ground truth for where each begins is the chapter markers embedded in the page
    /// names. When those markers don't name this chapter the whole archive is served rather than
    /// a guessed range — showing extra pages is recoverable, silently skipping them is not.
    /// </summary>
    private static (int Start, int Count) SliceBounds(
        ReaderArchiveCache.ArchiveInfo info, Chapter chapter, bool sharesFile)
    {
        if (!sharesFile || chapter.Number is not { } number || info.Boundaries.Count == 0)
        {
            return (0, info.Pages.Count);
        }

        var index = -1;
        for (var i = 0; i < info.Boundaries.Count; i++)
        {
            if (info.Boundaries[i].Chapter != number)
            {
                continue;
            }

            if (index >= 0)
            {
                // The chapter's markers are not contiguous — its pages appear in two or more runs
                // (interleaved or out-of-order scanlation names). Any single range would silently
                // drop the other runs, which is the one failure this whole method exists to avoid,
                // so fall back to serving the archive whole.
                return (0, info.Pages.Count);
            }

            index = i;
        }

        if (index < 0)
        {
            return (0, info.Pages.Count);
        }

        var start = info.Boundaries[index].PageIndex;
        var end = index + 1 < info.Boundaries.Count ? info.Boundaries[index + 1].PageIndex : info.Pages.Count;
        return (start, Math.Max(0, end - start));
    }

    /// <summary>The previous/next downloaded chapter of the same series and language.</summary>
    public async Task<(int? Previous, int? Next)> NeighboursAsync(Chapter chapter, CancellationToken ct)
    {
        var siblings = await db.Chapters
            .Where(c => c.SeriesId == chapter.SeriesId && c.Language == chapter.Language && c.ChapterFileId != null)
            .Select(c => new { c.Id, c.Number, c.Volume })
            .ToListAsync(ct);

        var ordered = siblings
            .OrderBy(c => c.Number is null ? 1 : 0)
            .ThenBy(c => c.Number)
            .ThenBy(c => c.Volume)
            .ThenBy(c => c.Id)
            .ToList();

        var at = ordered.FindIndex(c => c.Id == chapter.Id);
        return at < 0
            ? (null, null)
            : (at > 0 ? ordered[at - 1].Id : null,
                at + 1 < ordered.Count ? ordered[at + 1].Id : null);
    }

    /// <summary>
    /// Whose reading this service is recording, taken from the context's own <see cref="DataScope"/> —
    /// deliberately the same object the global query filters read, so the rows this writes and the rows
    /// it can see can never disagree about who owns them. Set by <c>CurrentUserMiddleware</c> for a
    /// normal request and by <c>OpdsController</c> after it resolves a feed token, which is why this
    /// works for both the built-in reader and OPDS page streaming.
    /// </summary>
    private int UserId => db.Scope.UserId;

    public async Task<ChapterProgress?> ProgressAsync(int chapterId, CancellationToken ct) =>
        await db.ChapterProgress.FirstOrDefaultAsync(p => p.ChapterId == chapterId, ct);

    /// <summary>
    /// A single report may not carry more reading time than this, however long the client says it
    /// was away. The built-in reader heartbeats every minute, so anything near this is already a
    /// client that lost connectivity mid-chapter; past it, it is a broken or hostile one, and an
    /// unbounded number here would let one request write an arbitrary figure into Rewind.
    /// </summary>
    private const int MaxSecondsPerReport = 900;

    /// <summary>
    /// How many unreported seconds a chapter may bank before they are appended to the stats log.
    /// Trades row count against how precisely the reading is dated: at five minutes a long sitting
    /// costs a dozen rows an hour and nothing lands in the wrong day.
    /// </summary>
    private const int ReadingTimeFlushSeconds = 300;

    /// <summary>
    /// A client's reading-time report: a <em>delta</em> of active seconds since its last one (never
    /// a total, which is why it is clamped rather than trusted), and whether the sitting just ended.
    /// <para>
    /// <see cref="Final"/> is what keeps an abandoned chapter honest. The banking threshold assumes
    /// another report is coming; when the reader closes the tab or walks away from a chapter it
    /// never finishes, none is, and the remainder would sit unreported until the chapter was
    /// completed — possibly never. A sitting ending is rare enough that flushing it unconditionally
    /// costs nothing in rows.
    /// </para>
    /// </summary>
    public readonly record struct TimeReport(int Seconds, bool Final)
    {
        /// <summary>No time observed: what every path that is not the built-in reader reports.</summary>
        public static TimeReport None => new(0, false);
    }

    /// <summary>
    /// Records the reader's position. <paramref name="pageIndex"/> is absolute (never a delta),
    /// so a debounced client may retry or reorder writes freely. Returns true when the chapter
    /// crossed into completion on this call.
    /// <para>
    /// Only the built-in reader reports a non-empty <paramref name="time"/> — see
    /// <see cref="ChapterProgress.ReadSeconds"/> for why OPDS cannot.
    /// </para>
    /// <para>
    /// Retries once on a lost insert race. The built-in reader debounces to a single writer, but
    /// OPDS page streaming does not: a reading app that prefetches several pages at once fires
    /// several of these concurrently for a chapter with no row yet, they all miss the read below,
    /// and they all insert. One wins on the unique index over <c>ChapterId</c> and the rest would
    /// otherwise throw away the position they were recording. The retry re-reads and updates the
    /// row the winner created.
    /// </para>
    /// </summary>
    public async Task<bool> SaveProgressAsync(ChapterSlice slice, int pageIndex, bool? completed,
        TimeReport time, CancellationToken ct)
    {
        try
        {
            return await SaveProgressCoreAsync(slice, pageIndex, completed, time, ct);
        }
        catch (DbUpdateException e) when (IsUniqueViolation(e))
        {
            logger.LogDebug("Progress insert for chapter {ChapterId} lost a race, retrying",
                slice.Chapter.Id);
            db.ChangeTracker.Clear();
            return await SaveProgressCoreAsync(slice, pageIndex, completed, time, ct);
        }
    }

    // 2067 = SQLITE_CONSTRAINT_UNIQUE, 1555 = SQLITE_CONSTRAINT_PRIMARYKEY. Matched on the
    // *extended* code, never the primary 19, which also covers FK and NOT NULL failures that no
    // retry can fix — the same rule ReadingProgressService follows.
    private static bool IsUniqueViolation(DbUpdateException e) =>
        e.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 or 1555 };

    private async Task<bool> SaveProgressCoreAsync(ChapterSlice slice, int pageIndex, bool? completed,
        TimeReport time, CancellationToken ct)
    {
        var chapter = slice.Chapter;
        var row = await db.ChapterProgress.FirstOrDefaultAsync(p => p.ChapterId == chapter.Id, ct);
        var now = DateTime.UtcNow;
        var wasCompleted = row?.Completed ?? false;

        if (row is null)
        {
            row = new ChapterProgress
            {
                SeriesId = chapter.SeriesId,
                ChapterId = chapter.Id,
                StartedAt = now
            };
            db.ChapterProgress.Add(row);
        }

        // The resume position is free to move backwards; completion is not.
        row.PageIndex = Math.Clamp(pageIndex, 0, Math.Max(0, slice.PageCount - 1));
        row.PageCount = slice.PageCount;
        row.Completed = completed ?? (row.Completed || row.PageIndex >= slice.PageCount - 1);
        row.ReadSeconds += Math.Clamp(time.Seconds, 0, MaxSecondsPerReport);
        // Read here, so it is no longer either external or deliberately un-read.
        row.External = false;
        row.UnreadAt = null;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        if (row.Completed && !wasCompleted)
        {
            // Flush first: the leftover under the threshold is time spent on this chapter, and
            // waiting for a threshold that will never be crossed again would lose it for good.
            await FlushReadingTimeAsync(row, slice.Series, ct);
            await OnChapterCompletedAsync(slice.Series, chapter, ct);
            return true;
        }

        // The threshold assumes another report is coming. On the write that says the sitting is
        // over, none is: a chapter left unfinished would otherwise hold its last few minutes
        // until it was completed, which for an abandoned one is never.
        if (time.Final || row.ReadSeconds - row.ReportedSeconds >= ReadingTimeFlushSeconds)
        {
            await FlushReadingTimeAsync(row, slice.Series, ct);
        }

        return false;
    }

    /// <summary>
    /// Appends the chapter's unreported reading time to the stats log and marks it reported.
    /// <para>
    /// The marker advances even for a fully-incognito series, which emits nothing: leaving the
    /// seconds unreported would bank them, and taking the series back out of incognito would then
    /// dump the whole hidden backlog into Rewind on the next page turn.
    /// </para>
    /// </summary>
    private async Task FlushReadingTimeAsync(ChapterProgress row, Series series, CancellationToken ct)
    {
        var unreported = row.ReadSeconds - row.ReportedSeconds;
        if (unreported <= 0)
        {
            return;
        }

        row.ReportedSeconds = row.ReadSeconds;

        if (series.Incognito != IncognitoMode.Full)
        {
            // KavitaSeriesId stays null: this event only ever comes from the built-in reader, so
            // the local series is always known and there is no unmatched row to aggregate under.
            db.StatsEvents.Add(new StatsEvent
            {
                Type = StatsEventType.ReadingTime,
                UserId = UserId,
                Timestamp = DateTime.UtcNow,
                SeriesId = series.Id,
                SeriesKey = SeriesIdentity.For(series),
                SeriesTitle = series.Title,
                Value = unreported
            });
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Marks a chapter unread: clears the position and completion, and leaves a
    /// <see cref="ChapterProgress.UnreadAt"/> tombstone behind rather than deleting the row.
    /// <para>
    /// The tombstone is what makes the action stick for a Kavita-tracked series. Kavita goes on
    /// reporting the chapter as read, and the scrobble tick marks what Kavita reports, so a deleted
    /// row would simply be recreated within the hour.
    /// </para>
    /// <para>
    /// Deliberately does not lower <see cref="ReadingState"/>: that mark is forward-only, and
    /// un-reading one chapter is not evidence the rest were un-read. Nothing user-visible reads it
    /// any more — read counts come from this table — so it can stay put.
    /// </para>
    /// <para>
    /// <see cref="ChapterProgress.ReadSeconds"/> stays put too, and so does its reported marker.
    /// The time was spent and Rewind has already logged it; zeroing the pair would make the next
    /// read report negative time, and zeroing only the total would re-emit the whole chapter.
    /// </para>
    /// </summary>
    public async Task ClearProgressAsync(int chapterId, CancellationToken ct)
    {
        // Tracked update, not ExecuteUpdate: that bypasses the change tracker, so a context which
        // had already loaded this row would keep serving the pre-clear values — and the next
        // SaveProgressAsync on it would see "no changes" and never write the re-read.
        var row = await db.ChapterProgress.FirstOrDefaultAsync(p => p.ChapterId == chapterId, ct);
        if (row is null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        row.Completed = false;
        row.PageIndex = 0;
        row.UnreadAt = now;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
    }

    private async Task OnChapterCompletedAsync(Series series, Chapter chapter, CancellationToken ct)
    {
        kavitaPush.QueuePush(UserId, series.Id, chapter.Number);

        if (chapter.Number is null)
        {
            // A one-shot has no number to raise the high-water mark to — see
            // ReadingProgressService.RecordUnnumberedReadAsync for why inventing one is wrong.
            await progress.RecordUnnumberedReadAsync(UserId, series.Id, series.Title, ct);
            return;
        }

        var (maxChapter, maxVolume) = await RecomputeMarksAsync(series.Id, ct);
        await progress.TrackNativeAsync(UserId, series.Id, series.Title, maxChapter, maxVolume, ct);
    }

    /// <summary>
    /// Derives the series' high-water marks from scratch out of <see cref="ChapterProgress"/>.
    /// Recomputing rather than incrementing is self-healing and immune to the duplicate chapter
    /// rows a multi-language series carries. A volume only counts once <em>every</em> downloaded
    /// chapter in it is complete, matching how Kavita computes a fully-read volume — a naive
    /// max() would report volume 4 read after a single chapter of it.
    /// </summary>
    private async Task<(double MaxChapter, double MaxVolume)> RecomputeMarksAsync(int seriesId, CancellationToken ct)
    {
        var chapters = await db.Chapters
            .Where(c => c.SeriesId == seriesId && c.ChapterFileId != null)
            .Select(c => new { c.Id, c.Number, c.Volume })
            .ToListAsync(ct);

        var completed = (await db.ChapterProgress
                .Where(p => p.SeriesId == seriesId && p.Completed)
                .Select(p => p.ChapterId)
                .ToListAsync(ct))
            .ToHashSet();

        var maxChapter = chapters
            .Where(c => completed.Contains(c.Id) && c.Number is not null)
            .Select(c => (double)c.Number!.Value)
            .DefaultIfEmpty(0)
            .Max();

        var maxVolume = chapters
            .Where(c => c.Volume is not null)
            .GroupBy(c => c.Volume!.Value)
            .Where(g => g.All(c => completed.Contains(c.Id)))
            .Select(g => (double)g.Key)
            .DefaultIfEmpty(0)
            .Max();

        return (maxChapter, maxVolume);
    }
}
