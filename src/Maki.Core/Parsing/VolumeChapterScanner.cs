using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Maki.Core.Parsing;

/// <summary>
/// Reads the chapter numbers a volume/compilation CBZ actually contains by looking
/// at the names of the image files inside it. Scanlation compilations name their
/// pages with the source chapter, e.g.
/// "Boyish Girlfriend - c049 (v05) - p113 [web] [Manga UP!] [Oak].png" is a page of
/// chapter 49. When a volume CBZ carries no volume metadata to range-match the chapter
/// rows against, these embedded markers are the ground truth for which chapters are present.
/// </summary>
public static partial class VolumeChapterScanner
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".avif", ".bmp"
    };

    // A chapter marker in a page name: "c049", "c049.5", "ch049", "chapter 49", "c. 49".
    // The page ("p113") and volume ("v05") markers start with other letters, so a
    // "c" not preceded by another letter and followed by digits is unambiguous; the
    // letter lookbehind keeps "Arc049"/"Comic" from reading as chapters.
    [GeneratedRegex(@"(?<![a-z])c(?:h(?:apter)?)?\.?\s*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ChapterMarker();

    /// <summary>
    /// Distinct chapter numbers found in the image-file names of a CBZ, ascending.
    /// Never throws — an unreadable or markerless archive yields an empty list.
    /// </summary>
    public static IReadOnlyList<decimal> ScanCbz(string cbzPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(cbzPath);
            var names = archive.Entries
                .Where(e => ImageExtensions.Contains(Path.GetExtension(e.Name)))
                .Select(e => e.FullName);
            return ChaptersInNames(names);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Pure extraction used by <see cref="ScanCbz"/>; testable without a real archive.</summary>
    public static IReadOnlyList<decimal> ChaptersInNames(IEnumerable<string> imageNames)
    {
        var found = new SortedSet<decimal>();
        foreach (var name in imageNames)
        {
            foreach (Match match in ChapterMarker().Matches(name))
            {
                if (decimal.TryParse(
                        match.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
                {
                    found.Add(number);
                }
            }
        }

        return found.ToList();
    }

    /// <summary>
    /// Total page count plus, in reading order, the zero-based page index at which each
    /// embedded chapter marker first appears. Used to translate a page-read count for the
    /// whole archive into "which chapter within it has been fully read". Never throws — an
    /// unreadable archive yields (0, []).
    /// </summary>
    public static (int TotalPages, IReadOnlyList<(decimal Chapter, int PageIndex)> Boundaries) ScanCbzBoundaries(
        string cbzPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(cbzPath);
            var names = archive.Entries
                .Where(e => ImageExtensions.Contains(Path.GetExtension(e.Name)))
                .Select(e => e.FullName)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return (names.Count, BoundariesInNames(names));
        }
        catch
        {
            return (0, []);
        }
    }

    /// <summary>Pure extraction used by <see cref="ScanCbzBoundaries"/>; names must already be in page/reading order.</summary>
    public static IReadOnlyList<(decimal Chapter, int PageIndex)> BoundariesInNames(IReadOnlyList<string> orderedImageNames)
    {
        var boundaries = new List<(decimal Chapter, int PageIndex)>();
        decimal? last = null;
        for (var i = 0; i < orderedImageNames.Count; i++)
        {
            var match = ChapterMarker().Match(orderedImageNames[i]);
            if (!match.Success ||
                !decimal.TryParse(
                    match.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
            {
                continue;
            }

            if (number != last)
            {
                boundaries.Add((number, i));
                last = number;
            }
        }

        return boundaries;
    }
}
