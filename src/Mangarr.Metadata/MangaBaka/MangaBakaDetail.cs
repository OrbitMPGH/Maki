using Mangarr.Core.Entities;

namespace Mangarr.Metadata.MangaBaka;

/// <summary>
/// A single categorized tag from the dump's <c>tags_v2</c> column. <see cref="Weight"/> is
/// MangaBaka's own relevance bucket — one of <c>core</c>/<c>defining</c>/<c>recurrent</c>/
/// <c>incidental</c> (or <c>unweighted</c>) — mirrored from the MangaBaka site's tag sections.
/// </summary>
public record MangaBakaTag(string Name, string Weight, string? Description, bool IsSpoiler);

/// <summary>A per-source normalized rating (0–100) from one of the aggregated trackers.</summary>
public record MangaBakaSourceRating(string Source, double Rating);

/// <summary>
/// Rich detail for one MangaBaka series, used by the Discover detail card. Everything here
/// comes from the local dump; MAL reviews are fetched separately (lazily) via Jikan.
/// </summary>
public record MangaBakaDetail(
    string ProviderId,
    string Title,
    string? NativeTitle,
    string? RomanizedTitle,
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
    int? MalId);
