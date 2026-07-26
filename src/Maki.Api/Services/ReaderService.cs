using Maki.Core.Entities;
using Maki.Core.Paths;
using Maki.Data;
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
    /// </summary>
    public async Task<ChapterSlice?> SliceAsync(int chapterId, CancellationToken ct)
    {
        var chapter = await db.Chapters
            .Include(c => c.ChapterFile)
            .FirstOrDefaultAsync(c => c.Id == chapterId, ct);
        if (chapter?.ChapterFile is not { } file)
        {
            return null;
        }

        var series = await db.Series.Include(s => s.RootFolder)
            .FirstOrDefaultAsync(s => s.Id == chapter.SeriesId, ct);
        if (series?.RootFolder is null)
        {
            return null;
        }

        var absolute = LibraryPaths.Resolve(series.RootFolder.Path, file.RelativePath);
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

        var (start, count) = SliceBounds(info, chapter,
            await SharesFileAsync(file.Id, chapter.Id, ct));

        return new ChapterSlice(chapter, series, file.Id, absolute, file.Size, info.Pages, start, count);
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
            if (info.Boundaries[i].Chapter == number)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return (0, info.Pages.Count);
        }

        var start = info.Boundaries[index].PageIndex;
        var end = index + 1 < info.Boundaries.Count ? info.Boundaries[index + 1].PageIndex : info.Pages.Count;
        return (start, Math.Max(0, end - start));
    }

    private async Task<bool> SharesFileAsync(int chapterFileId, int chapterId, CancellationToken ct) =>
        await db.Chapters.AnyAsync(c => c.ChapterFileId == chapterFileId && c.Id != chapterId, ct);

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

    public async Task<ChapterProgress?> ProgressAsync(int chapterId, CancellationToken ct) =>
        await db.ChapterProgress.FirstOrDefaultAsync(p => p.ChapterId == chapterId, ct);

    /// <summary>
    /// Records the reader's position. <paramref name="pageIndex"/> is absolute (never a delta),
    /// so a debounced client may retry or reorder writes freely. Returns true when the chapter
    /// crossed into completion on this call.
    /// </summary>
    public async Task<bool> SaveProgressAsync(ChapterSlice slice, int pageIndex, bool? completed,
        CancellationToken ct)
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
        // Read here, so it is no longer either external or deliberately un-read.
        row.External = false;
        row.UnreadAt = null;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        if (row.Completed && !wasCompleted)
        {
            await OnChapterCompletedAsync(slice.Series, chapter, ct);
            return true;
        }

        return false;
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
        kavitaPush.QueuePush(series.Id, chapter.Number);

        if (chapter.Number is null)
        {
            // A one-shot has no number to raise the high-water mark to — see
            // ReadingProgressService.RecordUnnumberedReadAsync for why inventing one is wrong.
            await progress.RecordUnnumberedReadAsync(series.Id, series.Title, ct);
            return;
        }

        var (maxChapter, maxVolume) = await RecomputeMarksAsync(series.Id, ct);
        await progress.TrackNativeAsync(series.Id, series.Title, maxChapter, maxVolume, ct);
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
