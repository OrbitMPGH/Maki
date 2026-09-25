namespace Maki.Core.Recommendations;

/// <summary>
/// Wire names for <see cref="AnimeResumeBasis"/>. The API's enum converter writes PascalCase names, and
/// the client reads camelCase strings, so the DTOs carry the basis as a string.
/// </summary>
public static class AnimeResumeBasisNames
{
    public const string AllSeasons = "allSeasons";
    public const string SeasonCount = "seasonCount";

    public static string Of(AnimeResumeBasis basis) =>
        basis == AnimeResumeBasis.AllSeasons ? AllSeasons : SeasonCount;
}

/// <summary>The Discover modal's "start where the anime ended" line for a catalogue series.</summary>
/// <param name="ResumeAt">First chapter after <paramref name="CoveredTo"/>.</param>
/// <param name="InLibrarySeriesId">The caller's library copy of this series, when they have one.</param>
public record CatalogueAnimeResumeDto(
    string AnimeTitle,
    IReadOnlyList<string> Services,
    double? Score,
    string Basis,
    decimal CoveredTo,
    decimal ResumeAt,
    string? CoveredLabel,
    string? NextLabel,
    decimal? NextFrom,
    decimal? NextTo,
    int? InLibrarySeriesId);

/// <summary>The series page callout.</summary>
/// <param name="ReadTo">Highest chapter the reader genuinely read (watched ticks excluded).</param>
/// <param name="ResumeChapterId">The chapter to open after the anime, preferring a downloaded copy.</param>
/// <param name="UnmarkedCount">Distinct chapter numbers up to <paramref name="CoveredTo"/> not yet read or watched.</param>
public record SeriesAnimeResumeDto(
    string AnimeTitle,
    IReadOnlyList<string> Services,
    double? Score,
    string Basis,
    decimal CoveredTo,
    decimal ResumeAt,
    string? CoveredLabel,
    string? NextLabel,
    decimal? NextFrom,
    decimal? NextTo,
    decimal? ReadTo,
    int? ResumeChapterId,
    string? ResumeChapterLabel,
    bool ResumeDownloaded,
    int UnmarkedCount);

/// <summary>One card on Home's "Start where the anime ended" rail.</summary>
public record HomeAnimeResumeItem(
    int SeriesId,
    string SeriesTitle,
    string? CoverUrl,
    string AnimeTitle,
    double? Score,
    decimal CoveredTo,
    string? CoveredLabel,
    int? ResumeChapterId,
    string? ResumeChapterLabel);
