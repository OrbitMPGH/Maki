using Maki.Core.Entities;

namespace Maki.Core.Naming;

/// <summary>
/// Turns a naming format into an actual folder or file name. The formats themselves are admin
/// settings, so anything inside the API reaches this through <c>NamingService</c>, which knows how
/// to read them; the no-format overloads here exist for code (and tests) that only wants Maki's
/// defaults.
///
/// <para>
/// The <c>.cbz</c> extension is appended here rather than being part of the format: a format that
/// could omit or change it would produce archives the reader and every external tool refuse to
/// open, and there is nothing to gain from allowing it.
/// </para>
/// </summary>
public static class FileNameBuilder
{
    public static string BuildChapterFileName(Series series, Chapter chapter) =>
        BuildChapterFileName(series, chapter, NamingDefaults.ChapterFormat);

    public static string BuildChapterFileName(Series series, Chapter chapter, string format) =>
        NamingFormatter.Format(format, new NamingContext(series, chapter)) + NamingDefaults.ChapterExtension;

    /// <summary>Path of the chapter file relative to the root folder.</summary>
    public static string BuildRelativePath(Series series, Chapter chapter) =>
        BuildRelativePath(series, chapter, NamingDefaults.ChapterFormat);

    /// <summary>
    /// Uses the series' stored <see cref="Series.FolderName"/>, not the folder format: the folder a
    /// series already lives in is a fact, and re-deriving it here would send a download into a
    /// folder that doesn't exist whenever the format changed after the series was added.
    /// </summary>
    public static string BuildRelativePath(Series series, Chapter chapter, string format) =>
        Path.Combine(series.FolderName, BuildChapterFileName(series, chapter, format));

    public static string BuildSeriesFolderName(Series series) =>
        BuildSeriesFolderName(series, NamingDefaults.SeriesFolderFormat);

    public static string BuildSeriesFolderName(Series series, string format) =>
        NamingFormatter.Format(format, new NamingContext(series));
}
