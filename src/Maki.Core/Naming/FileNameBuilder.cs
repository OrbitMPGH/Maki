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
        BuildChapterFileName(series, chapter, null, false, format);

    /// <summary>
    /// Names a file that backs a span of chapters — a volume compilation — after the whole span
    /// rather than after <paramref name="chapter"/> alone, which would give it the name the real
    /// first chapter of the span wants.
    /// </summary>
    /// <param name="through">Last chapter of the span; null or the same chapter names one chapter.</param>
    /// <param name="wholeVolumes">Whether the span is every known chapter of the volumes it covers.</param>
    public static string BuildChapterFileName(
        Series series, Chapter chapter, Chapter? through, bool wholeVolumes, string format) =>
        BuildChapterFileName(series, chapter, through, wholeVolumes, format, NamingDefaults.ChapterExtension);

    /// <summary>
    /// Same as the four-argument overload, but with the extension an existing file already has
    /// (e.g. renaming a placed PDF), rather than the default <c>.cbz</c> every format assumes.
    /// </summary>
    public static string BuildChapterFileName(
        Series series, Chapter chapter, Chapter? through, bool wholeVolumes, string format,
        string extension) =>
        NamingFormatter.Format(format, new NamingContext(series, chapter, through, wholeVolumes))
        + LanguageSuffix(chapter, format)
        + extension;

    /// <summary>
    /// The language code Maki names files under when the format doesn't say otherwise. Chapter
    /// identity is <c>(Number, Language)</c>, so once a series is synced in two languages there are
    /// two rows wanting chapter 24 — and the default format
    /// (<see cref="NamingDefaults.ChapterFormat"/>) contains no <c>{Chapter Language}</c>, so both
    /// resolve to the same name and the second download overwrites the first.
    /// </summary>
    public const string DefaultLanguage = "en";

    /// <summary>
    /// <c> [es]</c> for a chapter in a language other than <see cref="DefaultLanguage"/>, when the
    /// format carries no <c>{Chapter Language}</c> token of its own to disambiguate with.
    /// <para>
    /// Suffixing only the non-default language, rather than every language once a series has more
    /// than one, is what keeps this from renaming files: every chapter on disk today is English, so
    /// enabling a second language adds new names instead of invalidating existing ones. The cost is
    /// that a series available only in Spanish has <c>[es]</c> on every file.
    /// </para>
    /// </summary>
    private static string LanguageSuffix(Chapter chapter, string format)
    {
        var language = chapter.Language;
        if (string.IsNullOrWhiteSpace(language) ||
            language.Equals(DefaultLanguage, StringComparison.OrdinalIgnoreCase) ||
            format.Contains(LanguageToken, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return $" [{language}]";
    }

    private const string LanguageToken = "{Chapter Language}";

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
