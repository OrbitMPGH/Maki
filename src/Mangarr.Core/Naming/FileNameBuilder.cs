using System.Globalization;
using Mangarr.Core.Entities;

namespace Mangarr.Core.Naming;

/// <summary>
/// Builds Kavita-parser-safe CBZ file names:
///   with volume:  "{Series} Vol.3 Ch.24.cbz"
///   volume-less:  "{Series} Ch.10.5.cbz"
///   one-shot:     "{Series}.cbz" (single file named as the series)
/// </summary>
public static class FileNameBuilder
{
    public static string BuildChapterFileName(Series series, Chapter chapter)
    {
        var title = FileNameSanitizer.Sanitize(series.Title);

        if (chapter.IsOneShot || chapter.Number is null)
        {
            var suffix = !string.IsNullOrWhiteSpace(chapter.Title) && chapter.Title != series.Title
                ? $" - {FileNameSanitizer.Sanitize(chapter.Title)}"
                : string.Empty;
            return $"{title}{suffix}.cbz";
        }

        var number = chapter.Number.Value.ToString("0.###", CultureInfo.InvariantCulture);
        return chapter.Volume is int volume
            ? $"{title} Vol.{volume} Ch.{number}.cbz"
            : $"{title} Ch.{number}.cbz";
    }

    /// <summary>Path of the chapter file relative to the root folder.</summary>
    public static string BuildRelativePath(Series series, Chapter chapter)
    {
        return Path.Combine(series.FolderName, BuildChapterFileName(series, chapter));
    }
}
