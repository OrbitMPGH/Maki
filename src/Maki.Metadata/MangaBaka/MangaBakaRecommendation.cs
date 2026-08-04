using Maki.Core.Entities;

namespace Maki.Metadata.MangaBaka;

/// <summary>
/// A recommendation candidate from the local MangaBaka dump. Relation fields are set
/// for direct relations of library series (sequel/spin-off/...); the matched lists
/// are set for genre/tag similarity hits. <see cref="BecauseOfTitle"/> is the specific
/// seed whose "feel" most drove a semantic pick (null for genre-only hits). ProviderId is
/// the MangaBaka id and can be fed straight into the existing add-series flow.
/// </summary>
/// <param name="CoverUrl">
/// The full-size cover. Only for surfaces that actually show one that big (the Discover detail
/// card) and for the add-series flow, which downloads it into MediaCover — a poster card must use
/// <see cref="ThumbUrl"/> instead, see there for why.
/// </param>
/// <param name="ThumbUrl">
/// A 167x250 cover from MangaBaka's image proxy (`cover_x250_x1` in the dump), with
/// <see cref="ThumbUrlHiDpi"/> its 334x500 twin for the 2x descriptor. Poster cards render into
/// ~150-260 CSS px, and the raw cover behind <see cref="CoverUrl"/> averages ~460x690: a Discover
/// page mounts 240 of them, which is ~590 MB of decoded RGBA against a browser image cache an
/// order of magnitude smaller, so the covers are evicted and re-decoded as you scroll and the page
/// visibly fails to keep up. At x250 the same page is ~44 MB and stays cached. Null only when the
/// dump has no cover at all, in which case the card falls back to <see cref="CoverUrl"/>.
/// </param>
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
    string? RelatedToTitle,
    string? BecauseOfTitle = null,
    string? ThumbUrl = null,
    string? ThumbUrlHiDpi = null);
