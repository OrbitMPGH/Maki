namespace Maki.Metadata.MangaBaka;

/// <summary>
/// One ranked recommendation before its dump row is hydrated: everything computed while the
/// vector index was pinned, so per-page hydration only needs a primary-key lookup on
/// <c>series</c> and never touches the index or <c>embeddings.db</c>.
/// </summary>
public sealed record RankedRecommendationPick(
    long SeriesId,
    double FallbackRating,
    IReadOnlyList<string> MatchedGenres,
    IReadOnlyList<string> CandidateTags,
    bool AuthorMatch,
    bool CoRecommended,
    bool CoRead,
    bool TasteMatch,
    int? FranchiseId = null,
    int LaneFloor = 0,
    IReadOnlyList<long>? FeelSeedIds = null,
    IReadOnlyList<long>? CoReadSeedIds = null,
    long? BalancedFeelSeedId = null,
    long? BalancedCoReadSeedId = null)
{
    /// <summary>The strongest "Feels like" seed id before per-window reason balancing, if any.</summary>
    public long? PrimaryFeelSeedId => FeelSeedIds is { Count: > 0 } ? FeelSeedIds[0] : null;

    /// <summary>The strongest co-read / EASE seed id before per-window reason balancing, if any.</summary>
    public long? PrimaryCoReadSeedId => CoReadSeedIds is { Count: > 0 } ? CoReadSeedIds[0] : null;
}

/// <summary>
/// A compact ranked pool produced by <c>SemanticRecommender</c>: lightweight pick metadata plus
/// the small seed lookup table needed to render reason labels during per-page hydration.
/// </summary>
public sealed record RankedRecommendationPool(
    IReadOnlyList<RankedRecommendationPick> Picks,
    IReadOnlyDictionary<long, ReasonSeed> Seeds)
{
    public static readonly RankedRecommendationPool Empty = new([], new Dictionary<long, ReasonSeed>());
}
