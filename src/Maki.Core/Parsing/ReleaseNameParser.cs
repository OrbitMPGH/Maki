using System.Globalization;
using System.Text.RegularExpressions;

namespace Maki.Core.Parsing;

/// <summary>
/// What a library CBZ file name parsed into. A file is either a chapter file
/// (Number set), a volume compilation (Volume set, optionally VolumeEnd for
/// ranges like "v01-02"), or unrecognized (nothing set).
/// </summary>
public record ParsedReleaseFile(decimal? Number, int? Volume, int? VolumeEnd)
{
    public bool IsChapter => Number is not null;
    public bool IsVolume => Number is null && Volume is not null;
    public bool IsRecognized => Number is not null || Volume is not null;

    /// <summary>
    /// Trimmed contents of every parenthesised or bracketed group in the file name, in order of
    /// appearance: "(2024) (Digital) (1r0n)" becomes ["2024", "Digital", "1r0n"]. Read by
    /// <c>ReleaseTags</c> to tell a release's year, group and digital flag apart. Defaults to
    /// empty so the positional constructor keeps working for callers that don't care about tags.
    /// </summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Last chapter of a range like "c049.1-057"; null for a single chapter.</summary>
    public decimal? NumberEnd { get; init; }
}

/// <summary>
/// Parses release-style names found in existing libraries:
///   folders: "Dandadan (Digital) (1r0n)", "Title [J-Novel Club] [Group]", "Title (2023-2026) (...)"
///   files:   "Title Chapter 0001.cbz", "Dandadan 148 (2024) (Digital) (1r0n).cbz",
///            "Title 049.1 (...).cbz", "Title v01 (Digital-Compilation) (Oak).cbz",
///            "Title v01-02 (2022) (Digital) (1r0n) (f).cbz", "Title_vol.03.rar",
///            "Title (v01).cbz", "Title vol.1" (no extension)
/// </summary>
public static partial class ReleaseNameParser
{
    [GeneratedRegex(@"\s*[\(\[][^\)\]]*[\)\]]")]
    internal static partial Regex TagGroups();

    // The lookbehind is "\b that also breaks on an underscore": older scanlation sets name every
    // file "Narutaru_vol.03", and _ is a word character, so \b found no boundary in front of the
    // marker and not one of them parsed. Any other letter or digit in front still blocks the
    // match, which is what keeps "Revolution" out of the volume pattern.
    [GeneratedRegex(@"(?<![a-z0-9])v(?:ol(?:ume)?)?\.?[\s_]*(\d+)(?:\s*-\s*(?:v(?:ol)?\.?[\s_]*)?(\d+))?", RegexOptions.IgnoreCase)]
    internal static partial Regex VolumePattern();

    // The "h" is optional because a bare "c049" is the scanlation convention, and this has to read
    // the same marker VolumeChapterScanner reads off the page names inside an archive — the two
    // disagreeing meant an archive whose own name said c001 parsed as nothing at all while its
    // pages parsed fine. The lookbehind is what keeps "Comic" and "Arc049" out. A range takes a bare
    // hyphen only: Maki's own names put " - " between the number and the chapter title, and
    // "Ch.10 - 15 Years Later" is chapter 10, not 10 to 15.
    [GeneratedRegex(@"(?<![a-z0-9])c(?:h(?:apter)?)?\.?[\s_]*(\d+(?:\.\d+)?)(?:-(?:c(?:h(?:apter)?)?\.?)?(\d+(?:\.\d+)?))?", RegexOptions.IgnoreCase)]
    internal static partial Regex ChapterPattern();

    [GeneratedRegex(@"(?:^|[\s_])#?(\d+(?:\.\d+)?)\s*$")]
    private static partial Regex TrailingNumberPattern();

    /// <summary>
    /// The extensions a trailing <c>.something</c> is allowed to be. <c>GetFileNameWithoutExtension</c>
    /// treats any of them as one, so "My Series vol.1" with no extension at all came back as
    /// "My Series vol" and lost the only number it had.
    /// </summary>
    private static readonly string[] ArchiveExtensions =
        [".cbz", ".cbr", ".cb7", ".cbt", ".zip", ".rar", ".7z", ".tar", ".pdf", ".epub"];

    /// <summary>Strips release tags from a folder name, leaving a searchable series title.</summary>
    public static string CleanFolderTitle(string folderName)
    {
        var cleaned = TagGroups().Replace(folderName, string.Empty).Trim();
        return cleaned.Length > 0 ? cleaned : folderName.Trim();
    }

    /// <summary>Parses a CBZ file name (with or without extension) into chapter/volume info.</summary>
    public static ParsedReleaseFile ParseFileName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        var extension = Path.GetExtension(name);
        if (ArchiveExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            name = name[..^extension.Length];
        }

        // Strip release tags first so "(2024)" and "(f)" never read as numbers. A name that says
        // nothing without them is then retried whole, because the only marker it carries may be
        // inside a bracket — "My Series (v01).cbz" had its volume stripped off and was dropped as
        // unrecognized. The retry is deliberately the fallback rather than the first attempt: "(v2)"
        // marks a second scan of a chapter file, and a name that already parses must not pick up a
        // volume from one.
        var tags = ExtractTags(name);
        var stripped = TagGroups().Replace(name, string.Empty).Trim();
        var parsed = ParseCleanedName(stripped);
        if (!parsed.IsRecognized)
        {
            parsed = ParseCleanedName(name);
        }

        return parsed with { Tags = tags };
    }

    /// <summary>The trimmed inner text of every "(...)"/"[...]" group <see cref="TagGroups"/> matches, in order.</summary>
    internal static IReadOnlyList<string> ExtractTags(string name) =>
        TagGroups().Matches(name)
            .Select(m => m.Value.Trim())
            .Select(v => v.Length >= 2 ? v[1..^1].Trim() : v)
            .ToList();

    /// <summary>First and last number of a <see cref="ChapterPattern"/> match; End is null unless it is a real, ascending range.</summary>
    internal static (decimal Start, decimal? End) ChapterRange(Match match)
    {
        var start = decimal.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        if (!match.Groups[2].Success) return (start, null);
        var end = decimal.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        return end > start ? (start, end) : (start, null);
    }

    /// <summary>First and last volume of a <see cref="VolumePattern"/> match; End is null for a single volume.</summary>
    internal static (int Start, int? End) VolumeRange(Match match)
    {
        var start = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return match.Groups[2].Success
            ? (start, int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture))
            : (start, null);
    }

    private static ParsedReleaseFile ParseCleanedName(string stripped)
    {
        // Explicit chapter marker wins ("Chapter 0001", "Ch. 10.5").
        var chapter = ChapterPattern().Match(stripped);
        if (chapter.Success)
        {
            var volumeForChapter = VolumePattern().Match(stripped);
            var (number, numberEnd) = ChapterRange(chapter);
            return new ParsedReleaseFile(
                number,
                volumeForChapter.Success ? int.Parse(volumeForChapter.Groups[1].Value, CultureInfo.InvariantCulture) : null,
                null) { NumberEnd = numberEnd };
        }

        // Volume marker ("v01", "v01-02", "Vol. 3").
        var volume = VolumePattern().Match(stripped);
        if (volume.Success)
        {
            var (start, end) = VolumeRange(volume);
            return new ParsedReleaseFile(null, start, end);
        }

        // Bare trailing number after the title ("Dandadan 148", "Title 049.1").
        var trailing = TrailingNumberPattern().Match(stripped);
        if (trailing.Success)
        {
            return new ParsedReleaseFile(
                decimal.Parse(trailing.Groups[1].Value, CultureInfo.InvariantCulture),
                null,
                null);
        }

        return new ParsedReleaseFile(null, null, null);
    }
}
