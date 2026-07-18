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
///            "Title v01-02 (2022) (Digital) (1r0n) (f).cbz"
/// </summary>
public static partial class ReleaseNameParser
{
    [GeneratedRegex(@"\s*[\(\[][^\)\]]*[\)\]]")]
    private static partial Regex TagGroups();

    [GeneratedRegex(@"\bv(?:ol(?:ume)?)?\.?\s*(\d+)(?:\s*-\s*(?:v(?:ol)?\.?\s*)?(\d+))?", RegexOptions.IgnoreCase)]
    private static partial Regex VolumePattern();

    [GeneratedRegex(@"\bch(?:apter)?\.?\s*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ChapterPattern();

    [GeneratedRegex(@"(?:^|\s)#?(\d+(?:\.\d+)?)\s*$")]
    private static partial Regex TrailingNumberPattern();

    /// <summary>Strips release tags from a folder name, leaving a searchable series title.</summary>
    public static string CleanFolderTitle(string folderName)
    {
        var cleaned = TagGroups().Replace(folderName, string.Empty).Trim();
        return cleaned.Length > 0 ? cleaned : folderName.Trim();
    }

    /// <summary>Parses a CBZ file name (with or without extension) into chapter/volume info.</summary>
    public static ParsedReleaseFile ParseFileName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        // Strip release tags first so "(2024)" and "(f)" never read as numbers.
        var stripped = TagGroups().Replace(name, string.Empty).Trim();

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
