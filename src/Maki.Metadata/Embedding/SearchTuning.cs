namespace Maki.Metadata.Embedding;

/// <summary>
/// The knobs <see cref="SemanticSearcher"/> fuses its three channels with. Broken out of the
/// searcher as a record so <c>distribution/eval-search.cs</c> can sweep them against the labelled
/// query set instead of a rebuild per value, and so the defaults sit in one place with the
/// measurement that justifies them.
///
/// Registered as a singleton holding <see cref="Default"/>; nothing in the app changes it at
/// runtime.
/// </summary>
public sealed record SearchTuning
{
    public static readonly SearchTuning Default = new();

    /// <summary>
    /// Standard RRF damping for the dense and lexical channels. Larger = flatter, less dominated
    /// by whichever list ranked something first.
    /// </summary>
    public double RrfK { get; init; } = 60;

    /// <summary>
    /// The tag channel's own damping. Equal to <see cref="RrfK"/>, and kept as a separate knob
    /// only because the obvious argument for lowering it is wrong and worth recording.
    ///
    /// That argument: at a shared 60 the channel cannot surface anything alone, since a tag hit at
    /// rank 1 contributes <c>0.35/61</c>, which a dense-only row beats until about dense rank 113,
    /// so a series found *only* by its tags cannot reach a 60-result page. True, and it does not
    /// matter — measured over the premise class of eval-queries.tsv, lowering it makes things
    /// steadily worse (MRR 0.373 at 60, 0.353 at 30, 0.305 at 20, 0.128 at 10). The channel earns
    /// its keep by re-ordering candidates the dense pass already found, not by introducing new
    /// ones, and giving its head more mass mostly promotes series that merely share a common tag.
    /// </summary>
    public double TagRrfK { get; init; } = 60;

    /// <summary>
    /// The tag channel's share of the fused score. It must stay a fraction: at parity with the
    /// dense channel any candidate carrying a matched tag outranks a better one that simply isn't
    /// tagged for it, which measured worse than having no tag channel at all (MRR 0.341 with no
    /// channel, 0.244 at parity, over a 12-query set).
    /// </summary>
    public double TagChannelWeight { get; init; } = 0.35;

    /// <summary>
    /// Hard floor on a tag's cosine, on top of the relative tests below. Zero by default: an
    /// absolute floor cannot work here, and the 0.55 this code used to carry is why the tag
    /// channel never fired at all. Query and tag name are embedded in different regimes — the
    /// query takes bge's instruction prefix and is a sentence, a tag name is two bare words — so
    /// the *scale* of the cosines depends on the query shape, not on how good the match is.
    /// Measured against the shipped bge-base index over all 2,476 tag names: an
    /// instruction-prefixed query tops out around 0.42 with a median of 0.19 (nothing ever clears
    /// 0.55), while the same query embedded bare tops out at 0.97 with a median of 0.81 (every
    /// single tag clears it). The ordering is good in both regimes; only the scale moves. Kept as
    /// a knob so the eval can reproduce the old behaviour.
    /// </summary>
    public double TagFloorAbsolute { get; init; } = 0;

    /// <summary>
    /// Keep tags scoring at least this fraction of the best tag's cosine. Relative to the query's
    /// own scale, so it survives the regime shift the absolute floor cannot.
    /// </summary>
    public double TagFloorRelative { get; init; } = 0.80;

    /// <summary>
    /// And at least this far above the median tag cosine. Without it a query with no tag meaning
    /// at all still admits its eight nearest tags, because the relative test alone is satisfied by
    /// a flat distribution. The median over ~2.5k tags is a cheap stand-in for "what this query
    /// scores against an unrelated tag".
    /// </summary>
    public double TagFloorMedianGap { get; init; } = 0.05;

    /// <summary>Cap on the query's tag profile, so one query can't drag in a whole cluster.</summary>
    public int MaxQueryTags { get; init; } = 8;

    /// <summary>
    /// Weight of the popularity prior added to the fused score, in the same units as an RRF
    /// contribution (a first-place channel hit is worth <c>1/61 ≈ 0.0164</c>, so this is a nudge of
    /// roughly one and a half of those at most).
    ///
    /// It exists because pure similarity has no reason to prefer a series anyone has heard of, and
    /// 95% of the ~95.8k indexed series sit outside global popularity rank 5,000. On a query that
    /// names a common trope, thousands of rows are equally similar, and the ones that win are the
    /// obscure titles that state the trope literally in their name — "childhood friends turned
    /// lovers" used to return a page of rank-50,000-to-130,000 one-shots and no Tomo-chan is a
    /// Girl! (rank 147) anywhere in the top 200.
    ///
    /// 0.025 is the middle of a measured plateau over the premise class of eval-queries.tsv, and
    /// the curve is a clear inverted U rather than a monotone one, which is what says this is a
    /// real optimum and not the labelled set rewarding famous answers: MRR 0.373 at 0, 0.488 at
    /// 0.006, 0.565 at 0.012, 0.619 at 0.025, 0.621 at 0.030, 0.539 at 0.05, 0.426 at 0.10, 0.308
    /// at 0.30. Read the top end as the warning it is — past the plateau this stops being a search
    /// and becomes a popularity chart.
    /// </summary>
    public double PopularityWeight { get; init; } = 0.025;

    /// <summary>
    /// Popularity rank treated as "the bottom" when turning a rank into a [0,1] prior. Ranks are
    /// log-scaled, so this only sets where the curve flattens; rows with an unknown rank are
    /// treated as being here.
    /// </summary>
    public int PopularityFloorRank { get; init; } = 200_000;

    /// <summary>How deep each channel ranks, as a multiple of the caller's page size.</summary>
    public int PoolMultiplier { get; init; } = 8;

    /// <summary>
    /// Floor on that depth. Worth knowing that the pool is not only a performance dial: a series
    /// outside every channel's pool is not ranked low, it is not considered at all, and no
    /// weight or prior can promote what was never scored. Widening it is also nearly free, since
    /// both scans already visit all ~95.8k rows and the depth only decides how much of the sorted
    /// result is kept.
    ///
    /// It is still 200, because widening measured *worse*. Over the premise class, MRR 0.619 at
    /// this floor, 0.614 at 2,000, 0.612 at 4,000, 0.607 at 8,000, and paired against the default
    /// a 2,000 floor is better on 5 queries and worse on 16. Deeper pools buy a little recall at
    /// rank 50 and pay for it with noise nearer the top, which is the wrong trade for a page
    /// somebody actually looks at. So: a specific series you expected but cannot find is probably
    /// not a pool problem, and widening the pool to catch it will cost more than it wins.
    /// </summary>
    public int PoolMin { get; init; } = 200;

    /// <summary>Ceiling on that depth. Hydration is one <c>IN (…)</c> query over the winners only,
    /// so this bounds the fusion's memory rather than the SQL.</summary>
    public int PoolMax { get; init; } = 2000;
}
