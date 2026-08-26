namespace Maki.Metadata.RecoGraph;

/// <summary>
/// An undirected weighted graph over MangaBaka ids, resident in memory. Two artifacts load into
/// this same shape, and they are the two signals in the recommender that are <em>not</em> derived
/// from what a series says about itself:
///
/// <list type="bullet">
/// <item><b>Co-recommendation</b> (<see cref="RecoGraphCache"/>, <c>reco-edges.db</c>): what
/// AniList and MyAnimeList readers <em>said</em>, as submitted "if you liked X, try Y" pairs. The
/// weight is a vote count.</item>
/// <item><b>Co-read</b> (<see cref="CoRead.CoReadCache"/>, <c>coread-edges.db</c>): what AniList
/// readers actually <em>did</em>, as a co-occurrence strength over completed reading lists. The
/// weight is already hub-normalized at build time.</item>
/// </list>
///
/// <para>
/// The two are scored differently and must not be conflated — see <see cref="CoRead.CoReadScorer"/>
/// for why applying the vote-graph's log and degree penalty to a strength would normalize it twice.
/// Only the storage is shared, which is why the weight is a <see cref="float"/> rather than the
/// vote count it started as.
/// </para>
///
/// <para>
/// Stored as CSR (compressed sparse row): sorted ids, an offset per node, and one flat neighbour
/// array. Both directions of every pair are materialized, so a lookup is a single contiguous slice
/// with no search. At ~100k pairs that is about 2.5 MB of arrays and at the co-read graph's 1.18M
/// pairs about 19 MB, which is why these load whole rather than being <c>ATTACH</c>ed and queried
/// per request the way the dump is — the lookup sits on the recommendation hot path and runs once
/// per seed.
/// </para>
///
/// <para>
/// Immutable once built, so readers need no lock. Same contract as <see cref="Embedding.VectorIndex"/>.
/// </para>
///
/// <para>
/// Named <c>PairGraphIndex</c> and not <c>RecoGraph</c> because a type cannot share its namespace's
/// name: anything inside <c>Maki.Metadata.*</c> resolves a bare <c>RecoGraph</c> to the namespace.
/// </para>
/// </summary>
public sealed class PairGraphIndex
{
    private readonly long[] _ids;
    private readonly int[] _offsets;
    private readonly int[] _neighbours;
    private readonly float[] _weights;
    private readonly Dictionary<long, int> _nodeById;
    private readonly DateTime? _generatedAt;

    internal PairGraphIndex(
        long[] ids, int[] offsets, int[] neighbours, float[] weights, DateTime? generatedAt)
    {
        _ids = ids;
        _offsets = offsets;
        _neighbours = neighbours;
        _weights = weights;
        _generatedAt = generatedAt;

        _nodeById = new Dictionary<long, int>(ids.Length);
        for (var i = 0; i < ids.Length; i++)
        {
            _nodeById[ids[i]] = i;
        }
    }

    /// <summary>Series that carry at least one edge.</summary>
    public int Count => _ids.Length;

    /// <summary>Directed entries, i.e. twice the number of unordered pairs.</summary>
    public int EdgeCount => _neighbours.Length;

    /// <summary>When the artifact was generated, if it says. Null for a file with no meta table.</summary>
    public DateTime? GeneratedAt => _generatedAt;

    public long IdAt(int node) => _ids[node];

    public bool TryGetNode(long id, out int node) => _nodeById.TryGetValue(id, out node);

    /// <summary>
    /// How many series this one is paired with. The denominator of the co-recommendation graph's
    /// hub penalty: a title recommended alongside everything says little about any one of them.
    /// The co-read graph does not use it — its weights are already divided by both endpoints'
    /// popularity, and its degree is capped at build time anyway.
    /// </summary>
    public int DegreeAt(int node) => _offsets[node + 1] - _offsets[node];

    /// <summary>Node indices this one is paired with. Parallel to <see cref="WeightsAt"/>.</summary>
    public ReadOnlySpan<int> NeighboursAt(int node) =>
        _neighbours.AsSpan(_offsets[node], _offsets[node + 1] - _offsets[node]);

    /// <summary>
    /// Evidence backing each neighbour, in whatever unit the artifact stores: raw votes for the
    /// co-recommendation graph, co-occurrence strength for the co-read one.
    ///
    /// <para>
    /// Kept <b>raw</b> rather than pre-compressed to <c>log1p</c>: the per-graph minimum
    /// (<c>RecoGraphTuning.MinVotes</c>, <c>CoReadTuning.MinStrength</c>) is a live tuning knob the
    /// eval harness sweeps, and a floor cannot be applied to a value that has already been
    /// log-compressed at load. The few hundred logarithms a request actually needs cost nothing
    /// next to baking the tuning into the index.
    /// </para>
    /// </summary>
    public ReadOnlySpan<float> WeightsAt(int node) =>
        _weights.AsSpan(_offsets[node], _offsets[node + 1] - _offsets[node]);
}
