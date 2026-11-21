namespace Maki.Metadata.Ease;

/// <summary>
/// The knobs behind the EASE channel, a signed item-item table fitted on AniList reading lists
/// (<see cref="EaseScorer"/>, artifact <c>ease-table.db</c>, kill switch
/// <c>recommendations.ease</c>). It runs on personal recommendations only: the series page's "More
/// like this" passes <c>ease: false</c>. <c>distribution/AGENTS.md</c> has the numbers.
/// </summary>
public sealed record EaseTuning
{
    public static readonly EaseTuning Default = new();

    /// <summary>
    /// The channel's coefficient in <c>EmbeddingMath.HybridScore</c>, against a score whose best
    /// candidate is 1. 0 switches the channel off entirely. Ships at 1 under the v8 tuning: 4 gains the
    /// most on held-out Completed lists but fills a romance-heavy page with long webtoons, and 1 keeps
    /// about half of that gain while matching 4 on install-like shelves (<c>distribution/AGENTS.md</c>,
    /// "v8: EASE at weight 1").
    /// </summary>
    public double Weight { get; init; } = 1;

    /// <summary>
    /// Seeds a request needs before the channel runs. From one seed it gains nothing on
    /// human-recommended pairs and costs 0.096 of judged feel@10 (1.633 to 1.537), so it stays out;
    /// from two to four seeds it is already +0.027 nDCG@40 on held-out readers.
    /// </summary>
    public int MinSeeds { get; init; } = 2;

    /// <summary>
    /// Whether negative weights count. On is what the research measured best: readers who finished
    /// a seed tend not to finish what it weighs negatively, and dropping that made picks more famous.
    /// </summary>
    public bool KeepNegative { get; init; } = true;

    /// <summary>Ceiling on rows the channel may push into the scoring pool per request.</summary>
    public int MaxInjected { get; init; } = 50;

    /// <summary>
    /// Normalized score a row needs before the channel may push it into the pool. Scoring is
    /// unaffected: a pooled row keeps its full term however small.
    /// </summary>
    public double MinInjectedScore { get; init; } = 0.6;
}
