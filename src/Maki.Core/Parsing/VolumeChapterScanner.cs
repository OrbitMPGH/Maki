using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Maki.Core.Reading;

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
    // A chapter marker in a page name: "c049", "c049.5", "ch049", "chapter 49", "c. 49".
    // The page ("p113") and volume ("v05") markers start with other letters. The lookbehind is
    // ReleaseNameParser's, keeping "Arc049" and the "9c5" of a hashed page name out; the lookahead
    // is extra because page names carry hex hashes ("x1-c3f0...") and tags like "[c2c]", where the
    // digits run straight into another letter.
    [GeneratedRegex(@"(?<![a-z0-9])c(?:h(?:apter)?)?\.?\s*((?>[0-9]+(?:\.[0-9]+)?))(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex ChapterMarker();

    /// <summary>
    /// Distinct chapter numbers found in the image-file names of a CBZ, ascending.
    /// Never throws — an unreadable or markerless archive yields an empty list.
    /// </summary>
    public static IReadOnlyList<decimal> ScanCbz(string cbzPath)
    {
        // A PDF has no page filenames to read markers out of.
        if (ComicFile.IsPdf(cbzPath)) return [];
        try
        {
            using var archive = ZipFile.OpenRead(cbzPath);
            return ChaptersInNames(CbzReader.PageNames(archive));
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
            // One page belongs to one chapter, so only its first marker counts, as in BoundariesInNames.
            var match = ChapterMarker().Match(name);
            if (match.Success && decimal.TryParse(
                    match.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
            {
                found.Add(number);
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
        if (ComicFile.IsPdf(cbzPath)) return (CbzReader.PageNames(cbzPath).Count, []);
        try
        {
            using var archive = ZipFile.OpenRead(cbzPath);
            // Same page order the reader serves — see CbzReader.PageNames.
            var names = CbzReader.PageNames(archive);
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
