using Maki.Core.Kavita;

namespace Maki.Core.Scrobbling;

/// <summary>
/// Page boundaries scanned from the local library's own multi-chapter volume archives
/// (<see cref="Parsing.VolumeChapterScanner"/>). Kavita treats a "Volume 1.cbz" containing
/// chapters 1-8 as a single readable unit with one pagesRead counter;
/// <see cref="KavitaProgress.Compute"/> uses these to map that count back onto the chapters inside.
/// </summary>
public static class VolumeChapterProgress
{
    /// <param name="TotalPages">Page count of the archive, from the local scan.</param>
    /// <param name="Boundaries">Ascending (chapter number, first page index) pairs found inside it.</param>
    public record ChapterFileBoundaries(
        int TotalPages, IReadOnlyList<(decimal Chapter, int PageIndex)> Boundaries);
}
