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
    private static partial Regex TagGroups();

    // The lookbehind is "\b that also breaks on an underscore": older scanlation sets name every
    // file "Narutaru_vol.03", and _ is a word character, so \b found no boundary in front of the
    // marker and not one of them parsed. Any other letter or digit in front still blocks the
    // match, which is what keeps "Revolution" out of the volume pattern.
    [GeneratedRegex(@"(?<![a-z0-9])v(?:ol(?:ume)?)?\.?[\s_]*(\d+)(?:\s*-\s*(?:v(?:ol)?\.?[\s_]*)?(\d+))?", RegexOptions.IgnoreCase)]
    private static partial Regex VolumePattern();

    [GeneratedRegex(@"(?<![a-z0-9])ch(?:apter)?\.?[\s_]*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ChapterPattern();

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
        var stripped = TagGroups().Replace(name, string.Empty).Trim();
        var parsed = ParseCleanedName(stripped);
        return parsed.IsRecognized ? parsed : ParseCleanedName(name);
    }

    private static ParsedReleaseFile ParseCleanedName(string stripped)
    {
        // Explicit chapter marker wins ("Chapter 0001", "Ch. 10.5").
        var chapter = ChapterPattern().Match(stripped);
        if (chapter.Success)
        {
            var volumeForChapter = VolumePattern().Match(stripped);
            return new ParsedReleaseFile(
                decimal.Parse(chapter.Groups[1].Value, CultureInfo.InvariantCulture),
                volumeForChapter.Success ? int.Parse(volumeForChapter.Groups[1].Value, CultureInfo.InvariantCulture) : null,
                null);
        }

        // Volume marker ("v01", "v01-02", "Vol. 3").
        var volume = VolumePattern().Match(stripped);
        if (volume.Success)
        {
            return new ParsedReleaseFile(
                null,
                int.Parse(volume.Groups[1].Value, CultureInfo.InvariantCulture),
                volume.Groups[2].Success ? int.Parse(volume.Groups[2].Value, CultureInfo.InvariantCulture) : null);
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
