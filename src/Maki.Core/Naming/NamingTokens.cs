using System.Globalization;
using Maki.Core.Entities;

namespace Maki.Core.Naming;

/// <summary>Everything a naming token can be resolved against.</summary>
/// <param name="Series">The series being named. Always present.</param>
/// <param name="Chapter">
/// The chapter being named, or null when a series folder is being built — chapter tokens then
/// resolve to empty rather than erroring, so one catalogue serves both formats.
/// </param>
public sealed record NamingContext(Series Series, Chapter? Chapter = null);

public static class NamingTokenCategory
{
    public const string Series = "Series";
    public const string Chapter = "Chapter";
    public const string SeriesId = "Series ID";
}

/// <param name="Display">Canonical spelling shown in the UI, e.g. <c>{Series Title}</c>.</param>
/// <param name="Key">Normalized lookup key — separators dropped, lowercased.</param>
/// <param name="SupportsPadding">Whether <c>{Token:000}</c> means anything for this token.</param>
/// <param name="Resolve">
/// Renders the token. Second argument is the zero-padding pattern (<c>"000"</c>) or null.
/// A null or empty return is a legitimately absent value, not an error.
/// </param>
public sealed record NamingToken(
    string Display,
    string Key,
    string Category,
    string Description,
    bool SupportsPadding,
    Func<NamingContext, string?, string?> Resolve);

/// <summary>
/// The single catalogue behind the formatter, the save-time validator and the token-picker
/// endpoint. Adding a token here is all that's needed for it to work in all three.
/// </summary>
public static class NamingTokens
{
    /// <summary>Characters allowed to separate the words inside a token, e.g. <c>{Series.Title}</c>.</summary>
    public static readonly char[] Separators = [' ', '.', '_', '-'];

    public static string NormalizeKey(string name)
    {
        var chars = name.Where(c => !Separators.Contains(c)).ToArray();
        return new string(chars).ToLowerInvariant();
    }

    public static readonly IReadOnlyList<NamingToken> All =
    [
        // ---- Series -------------------------------------------------------------------------
        Token("{Series Title}", NamingTokenCategory.Series,
            "The series title as Maki has it",
            (c, _) => c.Series.Title),
        Token("{Series TitleYear}", NamingTokenCategory.Series,
            "Title with the release year in brackets; just the title when the year is unknown",
            (c, _) => c.Series.Year is int y ? $"{c.Series.Title} ({y})" : c.Series.Title),
        Token("{Series CleanTitle}", NamingTokenCategory.Series,
            "Title with punctuation removed: letters, digits and spaces only",
            (c, _) => CleanTitle(c.Series.Title)),
        Token("{Series SortTitle}", NamingTokenCategory.Series,
            "The sort title, e.g. leading article moved to the end",
            (c, _) => c.Series.SortTitle),
        Token("{Series OriginalTitle}", NamingTokenCategory.Series,
            "The native-language title; blank when the provider has none",
            (c, _) => c.Series.OriginalTitle),
        Token("{Series Year}", NamingTokenCategory.Series,
            "Release year; blank when unknown",
            (c, _) => c.Series.Year?.ToString(CultureInfo.InvariantCulture)),
        Token("{Series Type}", NamingTokenCategory.Series,
            "manga, manhwa, manhua and so on; blank when unknown",
            (c, _) => c.Series.Type),

        // ---- Chapter ------------------------------------------------------------------------
        Token("{Chapter VolChap}", NamingTokenCategory.Chapter,
            "Vol.3 Ch.24, or Ch.24 when the source has no volumes. Blank for a one-shot",
            (c, _) => VolChap(c.Chapter)),
        Token("{Chapter Number}", NamingTokenCategory.Chapter,
            "24, or 10.5 for a sub-chapter. Blank when the chapter has no number",
            (c, pad) => Number(c.Chapter?.Number, pad), padding: true),
        Token("{Chapter Volume}", NamingTokenCategory.Chapter,
            "3; blank when the source doesn't group into volumes",
            (c, pad) => c.Chapter?.Volume is int v
                ? v.ToString(pad ?? "0", CultureInfo.InvariantCulture)
                : null, padding: true),
        Token("{Chapter Title}", NamingTokenCategory.Chapter,
            "The chapter's own title; blank when it just repeats the series title",
            (c, _) => ChapterTitle(c)),
        Token("{Chapter OneShotSuffix}", NamingTokenCategory.Chapter,
            "\" - \" plus the chapter title, for a one-shot titled differently to its series. Blank otherwise",
            (c, _) => IsOneShot(c.Chapter) && ChapterTitle(c) is { Length: > 0 } t ? $" - {t}" : null),
        Token("{Chapter Language}", NamingTokenCategory.Chapter,
            "BCP-47 language tag, e.g. en",
            (c, _) => c.Chapter?.Language),

        // ---- Series ID ----------------------------------------------------------------------
        Token("{MangaBakaId}", NamingTokenCategory.SeriesId, "MangaBaka id; blank when unmatched",
            (c, _) => c.Series.MangaBakaId?.ToString(CultureInfo.InvariantCulture)),
        Token("{MalId}", NamingTokenCategory.SeriesId, "MyAnimeList id; blank when unmatched",
            (c, _) => c.Series.MalId?.ToString(CultureInfo.InvariantCulture)),
        Token("{AniListId}", NamingTokenCategory.SeriesId, "AniList id; blank when unmatched",
            (c, _) => c.Series.AniListId?.ToString(CultureInfo.InvariantCulture)),
        Token("{MangaDexId}", NamingTokenCategory.SeriesId, "MangaDex UUID; blank when unmatched",
            (c, _) => c.Series.MangaDexUuid),
        Token("{MangaUpdatesId}", NamingTokenCategory.SeriesId, "MangaUpdates id; blank when unmatched",
            (c, _) => c.Series.MangaUpdatesId),
        Token("{KitsuId}", NamingTokenCategory.SeriesId, "Kitsu id; blank when unmatched",
            (c, _) => c.Series.KitsuId?.ToString(CultureInfo.InvariantCulture))
    ];

    private static readonly Dictionary<string, NamingToken> ByKey =
        All.ToDictionary(t => t.Key, StringComparer.Ordinal);

    /// <summary>Resolves a token name as written in a format, ignoring separators and case.</summary>
    public static NamingToken? Find(string name) => ByKey.GetValueOrDefault(NormalizeKey(name));

    private static NamingToken Token(
        string display, string category, string description,
        Func<NamingContext, string?, string?> resolve, bool padding = false) =>
        new(display, NormalizeKey(display.Trim('{', '}')), category, description, padding, resolve);

    /// <summary>
    /// A chapter Maki names as a one-shot. A missing number counts as one too: there's nothing to
    /// build a "Ch.x" out of either way, and that's the rule the hardcoded builder always used.
    /// </summary>
    private static bool IsOneShot(Chapter? chapter) =>
        chapter is not null && (chapter.IsOneShot || chapter.Number is null);

    private static string? ChapterTitle(NamingContext c) =>
        !string.IsNullOrWhiteSpace(c.Chapter?.Title) && c.Chapter.Title != c.Series.Title
            ? c.Chapter.Title
            : null;

    private static string? Number(decimal? number, string? pad)
    {
        if (number is null)
        {
            return null;
        }

        // "0.###" is what the hardcoded builder always used: no trailing zeros, sub-chapters kept
        // intact. A padding pattern only widens the integer part.
        return number.Value.ToString($"{pad ?? "0"}.###", CultureInfo.InvariantCulture);
    }

    private static string VolChap(Chapter? chapter)
    {
        if (chapter is null || IsOneShot(chapter))
        {
            return string.Empty;
        }

        var number = Number(chapter.Number, null);
        return chapter.Volume is int volume ? $"Vol.{volume} Ch.{number}" : $"Ch.{number}";
    }

    /// <summary>
    /// Apostrophes and quotes are dropped outright while other punctuation becomes a space:
    /// "The Series Title's!" reads as "The Series Titles", not "The Series Title s".
    /// </summary>
    private static string CleanTitle(string title) =>
        new(title
            .Where(c => c is not ('\'' or '’' or '"' or '`'))
            .Select(c => char.IsLetterOrDigit(c) || c == ' ' ? c : ' ')
            .ToArray());
}
