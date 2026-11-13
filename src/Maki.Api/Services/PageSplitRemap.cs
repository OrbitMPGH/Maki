using Maki.Core.Images;
using Maki.Core.Parsing;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Carries every stored page index of a file's chapters across a split of its tall pages, and back
/// again when an undo puts the original in place: each reader's position (page and the share of it
/// the strip was scrolled to, so a reader resumes in the right piece), the page count snapshot,
/// bookmarks and page notes. Indices are within a chapter's slice, and a volume's slices are found
/// from page-name markers the split keeps, so a slice moves to where its first page's pieces start.
/// Written with <c>ExecuteUpdate</c>, so the save hook neither reads the move as a read (which would
/// set the series to Reading) nor restamps <c>UpdatedAt</c>, which an offline replay compares against.
/// Callers run it in the transaction that records the new bytes.
/// </summary>
public static class PageSplitRemap
{
    /// <param name="pages">
    /// The page names the stored indices refer to now: the archive before the split, or with
    /// <paramref name="undo"/> the split archive an undo is about to replace.
    /// </param>
    /// <returns>How many rows moved.</returns>
    public static async Task<int> ApplyAsync(MakiDbContext db, int fileId, IReadOnlyList<string> pages, PageSplitMap map,
        bool undo, CancellationToken ct)
    {
        if (map.IsEmpty || pages.Count == 0)
        {
            return 0;
        }

        var chapters = await db.Chapters.IgnoreQueryFilters()
            .Where(c => c.ChapterFileId == fileId)
            .Select(c => new { c.Id, c.Number })
            .ToListAsync(ct);
        var info = new ReaderArchiveCache.ArchiveInfo(pages, VolumeChapterScanner.BoundariesInNames(pages));
        var moved = 0;
        foreach (var chapter in chapters)
        {
            var (start, count) = ReaderService.SliceBounds(info, chapter.Number, chapters.Count > 1);
            if (count == 0)
            {
                continue;
            }

            var slice = new Slice(map, start, count, undo);

            var progress = await db.ChapterProgress.IgnoreQueryFilters()
                .Where(p => p.ChapterId == chapter.Id)
                .Select(p => new { p.Id, p.PageIndex, p.PageOffset, p.PageCount })
                .ToListAsync(ct);
            foreach (var row in progress)
            {
                var (page, offset) = slice.Move(row.PageIndex, row.PageOffset);
                var pageCount = row.PageCount > 0 ? slice.NewCount : row.PageCount;
                if ((page, offset, pageCount) == (row.PageIndex, row.PageOffset, row.PageCount))
                {
                    continue;
                }

                moved += await db.ChapterProgress.IgnoreQueryFilters()
                    .Where(p => p.Id == row.Id)
                    .ExecuteUpdateAsync(set => set
                        .SetProperty(p => p.PageIndex, page)
                        .SetProperty(p => p.PageOffset, offset)
                        .SetProperty(p => p.PageCount, pageCount), ct);
            }

            var bookmarks = await db.ReaderBookmarks.IgnoreQueryFilters()
                .Where(b => b.ChapterId == chapter.Id)
                .Select(b => new { b.Id, b.PageIndex })
                .ToListAsync(ct);
            foreach (var row in bookmarks)
            {
                var page = slice.Move(row.PageIndex, 0).Page;
                if (page != row.PageIndex)
                {
                    moved += await db.ReaderBookmarks.IgnoreQueryFilters()
                        .Where(b => b.Id == row.Id)
                        .ExecuteUpdateAsync(set => set.SetProperty(b => b.PageIndex, page), ct);
                }
            }

            var notes = await db.ChapterNotes.IgnoreQueryFilters()
                .Where(n => n.ChapterId == chapter.Id && n.PageIndex != null)
                .Select(n => new { n.Id, PageIndex = n.PageIndex!.Value })
                .ToListAsync(ct);
            foreach (var row in notes)
            {
                var page = slice.Move(row.PageIndex, 0).Page;
                if (page != row.PageIndex)
                {
                    moved += await db.ChapterNotes.IgnoreQueryFilters()
                        .Where(n => n.Id == row.Id)
                        .ExecuteUpdateAsync(set => set.SetProperty(n => n.PageIndex, (int?)page), ct);
                }
            }
        }

        return moved;
    }

    /// <summary>One chapter's slice, at <paramref name="start"/> for <paramref name="count"/> pages of the archive the indices refer to now.</summary>
    internal readonly struct Slice(PageSplitMap map, int start, int count, bool undo)
    {
        private int NewStart => undo ? map.OldIndex(start) : map.NewStart(start);

        public int NewCount => undo
            ? map.OldIndex(start + count - 1) + 1 - map.OldIndex(start)
            : map.NewStart(start + count) - map.NewStart(start);

        /// <summary>A slice-relative page and offset carried over. A page past the slice keeps its distance from the end.</summary>
        public (int Page, double Offset) Move(int page, double offset)
        {
            if (page < 0)
            {
                return (page, offset);
            }

            if (page >= count)
            {
                return (page - count + NewCount, offset);
            }

            var (moved, movedOffset) = undo ? map.Backward(start + page, offset) : map.Forward(start + page, offset);
            return (moved - NewStart, movedOffset);
        }
    }
}
