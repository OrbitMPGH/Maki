using Mangarr.Core.Entities;

namespace Mangarr.Metadata.MangaBaka;

/// <summary>
/// A recommendation candidate from the local MangaBaka dump. Relation fields are set
/// for direct relations of library series (sequel/spin-off/...); the matched lists
/// are set for genre/tag similarity hits. ProviderId is the MangaBaka id and can be
/// fed straight into the existing add-series flow.
/// </summary>
public record MangaBakaRecommendation(
    string ProviderId,
    string Title,
    string? CoverUrl,
    int? Year,
    string? Description,
    SeriesStatus Status,
    double? Rating,
    int? TotalChapters,
    IReadOnlyList<string> MatchedGenres,
    IReadOnlyList<string> MatchedTags,
    bool AuthorMatch,
    string? RelationKind,
    string? RelatedToTitle);
