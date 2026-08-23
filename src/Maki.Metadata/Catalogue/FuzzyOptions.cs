namespace Maki.Metadata.Catalogue;

/// <summary>
/// The knobs the typo-tolerant title pass runs with. Broken out as a record so
/// <c>distribution/eval-search.cs</c> can sweep them against the labelled query set, and so every
/// guard sits next to the measurement that justifies it.
///
/// <para>
/// Measured mostly outside <c>eval-search.cs</c>, and that is the point. That harness always runs
/// the full Discover fusion, where the dense channel already absorbs most misspellings, so this
/// pass barely registers there: the fused <c>typo</c> class goes from MRR 0.893 to 0.929, better on
/// one query of fourteen. It earns its keep on the title index alone, which is what the Add page's
/// Title mode, the command palette and <c>GET search/metadata</c> use, and there the same fourteen
/// queries go from MRR 0.143 to 0.764, and from 2 answered to 12. Over both runs the <c>alias</c>,
/// <c>title</c> and <c>premise</c> classes do not move by a thousandth, because a query that
/// already matches never reaches the expansion at all.
/// </para>
///
/// <para>
/// It lives in <c>Catalogue</c> rather than beside <c>SearchTuning</c> in <c>Embedding</c> on
/// purpose. <c>Embedding</c> already depends on <c>MangaBaka</c> (<c>SemanticSearcher</c> takes a
/// <c>MangaBakaLocalStore</c>), and the fuzzy pass runs inside that store, so putting these on
/// <c>SearchTuning</c> would have the store reach back into <c>Embedding</c> and close the loop.
/// <c>SearchTuning</c> holds one of these instead.
/// </para>
/// </summary>
public sealed record FuzzyOptions
{
    public static readonly FuzzyOptions Default = new();

    /// <summary>Off means the title search behaves exactly as it did before this existed.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Shortest token that may be corrected by one edit.
    ///
    /// <para>
    /// Three, which is lower than it looks and lower than this started at. The worry with a short
    /// floor is that one edit on a three-letter token reaches a large slice of a 684k-term
    /// vocabulary and "corrects" to a different series. Measured against the title index, that does
    /// not happen: over the ladders 5/8, 4/7 and 3/6 the <c>typo</c> class improves monotonically
    /// (MRR 0.571, 0.643, 0.764; 9, 10 and 12 of 14 answered) while <c>alias</c> and <c>title</c>
    /// stay at 0.807 and 0.965 to three decimal places in every one of them.
    /// </para>
    /// <para>
    /// The reason it is safe is <see cref="RescueBelow"/>, not the floor: a query whose exact pass
    /// already returned results never expands anything, so the only queries a loose floor can
    /// affect are the ones currently returning nothing. What it buys is the short-token typo, which
    /// is common and was otherwise unreachable: "tokyo ghol", "hajime no ipo", "vinland sga".
    /// </para>
    /// </summary>
    public int MinLengthForDistance1 { get; init; } = 3;

    /// <summary>
    /// Shortest token that may be corrected by two edits. Kept three above
    /// <see cref="MinLengthForDistance1"/>, the same spacing Elasticsearch's ladder uses against
    /// the same kind of term dictionary.
    /// </summary>
    public int MinLengthForDistance2 { get; init; } = 6;

    /// <summary>
    /// Alternatives admitted per token, best first by (distance, then document frequency). A cap
    /// rather than a similarity floor, because FTS5 gives every branch of an OR the same standing
    /// and a long tail of weak alternatives simply dilutes the query.
    /// </summary>
    public int MaxExpansionsPerToken { get; init; } = 6;

    /// <summary>
    /// Longest query, in tokens, that gets a fuzzy pass at all. Past four tokens the query is a
    /// sentence rather than a title, the dense channel is the one answering it, and expanding every
    /// word multiplies branches for nothing.
    /// </summary>
    public int MaxTokens { get; init; } = 4;

    /// <summary>
    /// Never expand *to* a term that already appears in more titles than this. Roughly 1% of the
    /// 2,180,575 rows in the shipped title index. The head of that dictionary is stopword-shaped
    /// ("no" in 286k titles, "dj" 148k, "the" 117k), and a single expansion into one of those turns
    /// a rescue into the whole catalogue in popularity order.
    /// </summary>
    public int MaxTermDocFrequency { get; init; } = 20_000;

    /// <summary>
    /// Run the fuzzy pass only when the exact pass returned fewer rows than this. A query that
    /// already works never pays for the second FTS round trip, and a correction can never displace
    /// a spelling that matched.
    ///
    /// <para>
    /// This is the guard that lets every other one here be generous, and it is why the length floor
    /// could come down to three without costing anything: the only queries the expansion can reach
    /// are the ones that found almost nothing, where a weak guess beats an empty page. A rescued
    /// query costs about 6 ms in total against the shipped index, 23 ms at the worst measured.
    /// </para>
    /// </summary>
    public int RescueBelow { get; init; } = 5;

    /// <summary>
    /// How much more common a spelling has to be than the token as typed before it counts as a
    /// correction rather than a different word.
    ///
    /// <para>
    /// Document frequency alone cannot tell a typo from a real word: measured against the shipped
    /// index, "kaisan" appears in 10 titles and is a misspelling of "kaisen" (1,639), while
    /// "vinland" appears in 12 and is spelled correctly. What separates them is the gap. Without
    /// this rule "vinland saga" widens to include "finland" (6 titles) and "inland" (5), which are
    /// not what anybody meant; with it, only a spelling that dominates the one typed gets in.
    /// </para>
    /// </summary>
    public int MinCorrectionDominance { get; init; } = 4;

    /// <summary>The edit budget for a token of this length, or zero for "exact only".</summary>
    public int BudgetFor(int tokenLength) =>
        tokenLength >= MinLengthForDistance2 ? 2
        : tokenLength >= MinLengthForDistance1 ? 1
        : 0;
}
