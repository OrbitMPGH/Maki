using Maki.Core.Entities;

namespace Maki.Metadata.MangaBaka;

/// <summary>
/// A single categorized tag from the dump's <c>tags_v2</c> column. <see cref="Weight"/> is
/// MangaBaka's own relevance bucket — one of <c>core</c>/<c>defining</c>/<c>recurrent</c>/
/// <c>incidental</c> (or <c>unweighted</c>) — mirrored from the MangaBaka site's tag sections.
/// </summary>
public record MangaBakaTag(string Name, string Weight, string? Description, bool IsSpoiler);

/// <summary>A per-source normalized rating (0–100) from one of the aggregated trackers.</summary>
public record MangaBakaSourceRating(string Source, double Rating);

/// <summary>
/// What readers with the caller's habits scored a series, when that differs from what the same
/// crowd scored it overall by enough to be worth saying.
/// <para>
/// <paramref name="Baseline"/> is the all-readers mean from the same population, deliberately not
/// the catalogue rating shown beside it: MangaBaka's aggregate averages several metadata providers,
/// and a gap against that would be a fact about the two sources rather than about this reader.
/// </para>
/// <para>
/// Both are on the same 0–100 scale as <see cref="MangaBakaDetail.Rating"/>.
/// <paramref name="Readers"/> is how many of the matched cohorts' readers actually scored it, which
/// is the honest caveat on the other two.
/// </para>
/// </summary>
public record ReaderCohortHint(double Score, double Baseline, int Readers);

/// <summary>
/// Rich detail for one MangaBaka series, used by the Discover detail card. Everything here
/// comes from the local dump; MAL reviews are fetched separately (lazily) by scraping MAL.
/// </summary>
public record MangaBakaDetail(
    string ProviderId,
    string Title,
    string? NativeTitle,
    string? RomanizedTitle,
    IReadOnlyList<string> AltTitles,
    string? Description,
    string? CoverUrl,
    int? Year,
    string? Type,
    SeriesStatus Status,
    string? ContentRating,
    double? Rating,
    IReadOnlyList<MangaBakaSourceRating> SourceRatings,
    int? TotalChapters,
    int? FinalVolume,
    IReadOnlyList<string> Authors,
    IReadOnlyList<string> Artists,
    IReadOnlyList<string> Publishers,
    IReadOnlyList<string> Genres,
    IReadOnlyList<MangaBakaTag> Tags,
    IReadOnlyList<MetadataLink> Links,
    int? MalId,
    bool HasAnime,
    string? AnimeStart,
    string? AnimeEnd,
    /// <summary>
    /// Filled by <c>RecommendationController.Detail</c> from the caller's own reading, never by
    /// <see cref="MangaBakaLocalStore"/> — this record is otherwise a pure dump projection with no
    /// user in scope. Null is by far the common case: the artifact may be absent, the reader may
    /// not be placeable, and even when both are fine only about one series in nine has cohorts that
    /// disagree with the crowd by enough to be worth a line.
    /// </summary>
    ReaderCohortHint? ReaderHint = null);

/// <summary>
/// One dump row reduced to what a taste profile aggregates over. Tags carry their bucket and
/// spoiler flag so the caller can weight and filter them without re-reading <c>tags_v2</c>.
/// </summary>
public sealed record MangaBakaProfileRow(
    long Id,
    string? Title,
    IReadOnlyList<string> Genres,
    IReadOnlyList<MangaBakaTag> Tags,
    IReadOnlyList<string> Authors,
    IReadOnlyList<string> Artists,
    string? Type,
    int? Year);
