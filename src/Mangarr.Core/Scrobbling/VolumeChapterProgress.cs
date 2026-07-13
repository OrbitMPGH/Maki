using Mangarr.Core.Kavita;

namespace Mangarr.Core.Scrobbling;

/// <summary>
/// Refines Kavita's series-level chapter progress using page boundaries scanned from
/// the local library's own multi-chapter volume archives (<see cref="Parsing.VolumeChapterScanner"/>).
/// Kavita treats a "Volume 1.cbz" containing chapters 1-8 as a single readable unit with
/// one pagesRead counter; this maps that page count back onto the individual chapters
/// Mangarr knows are inside it, so a partially-read volume still advances the scrobbled
/// chapter number instead of waiting for the whole volume to be finished.
/// </summary>
public static class VolumeChapterProgress
{
    /// <param name="TotalPages">Page count of the archive, from the local scan.</param>
    /// <param name="Boundaries">Ascending (chapter number, first page index) pairs found inside it.</param>
    public record ChapterFileBoundaries(
        int TotalPages, IReadOnlyList<(decimal Chapter, int PageIndex)> Boundaries);

    /// <summary>
    /// Raises <paramref name="baseMaxChapter"/> to the highest chapter that a matching
    /// volume's page-read count shows as fully consumed. A chapter counts as fully read
    /// only when the read-page count reaches the start of the next chapter (or the end
    /// of the archive for the last one) — partial pages into a chapter don't advance it.
    /// </summary>
    public static decimal Refine(
        IEnumerable<KavitaProgress.KavitaVolumeDto> volumes,
        IReadOnlyDictionary<int, ChapterFileBoundaries> boundariesByVolume,
        decimal baseMaxChapter)
    {
        var best = baseMaxChapter;
        foreach (var vol in volumes)
        {
            var vnum = vol.MaxNumber ?? vol.Number;
            if (vnum is not { } v || v != Math.Floor(v) ||
                !boundariesByVolume.TryGetValue((int)v, out var b) || b.Boundaries.Count == 0)
            {
                continue;
            }

            var pagesRead = vol.Chapters is { Count: > 0 }
                ? vol.Chapters.Sum(c => c.PagesRead)
                : vol.PagesRead;
            var totalPages = b.TotalPages > 0 ? b.TotalPages : vol.Pages;
            if (totalPages <= 0)
            {
                continue;
            }

            decimal? reached = null;
            for (var i = 0; i < b.Boundaries.Count; i++)
            {
                var endExclusive = i + 1 < b.Boundaries.Count ? b.Boundaries[i + 1].PageIndex : totalPages;
                if (pagesRead >= endExclusive)
                {
                    reached = b.Boundaries[i].Chapter;
                }
            }

            if (reached is { } r && r > best)
            {
                best = r;
            }
        }

        return best;
    }
}
