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
/// with no search. These load whole rather than being <c>ATTACH</c>ed and queried per request the
/// way the dump is: the lookup sits on the recommendation hot path and runs once per seed.
/// </para>
///
/// <para>
/// Neighbours are <see cref="ushort"/> node indices while the graph has at most 65,536 nodes (both
/// shipped graphs are under 50k), and weights are <see cref="Half"/>. Co-read strengths sit between
/// 0.0004 and 0.17 and votes below 6,100, all well inside Half's normal range, so each weight moves
/// by at most 0.05%. At the co-read graph's two million pairs that is 16 MB instead of 32, and the
/// node lookup is a binary search over the sorted ids rather than a 50k-entry dictionary.
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
    private readonly ushort[]? _narrow;
    private readonly int[]? _wide;
    private readonly Half[] _weights;
    private readonly DateTime? _generatedAt;

    internal PairGraphIndex(
        long[] ids, int[] offsets, ushort[]? narrow, int[]? wide, Half[] weights, DateTime? generatedAt)
    {
        _ids = ids;
        _offsets = offsets;
        _narrow = narrow;
        _wide = wide;
        _weights = weights;
        _generatedAt = generatedAt;
    }

    /// <summary>Series that carry at least one edge.</summary>
    public int Count => _ids.Length;

    /// <summary>Directed entries, i.e. twice the number of unordered pairs.</summary>
    public int EdgeCount => _weights.Length;

    /// <summary>When the artifact was generated, if it says. Null for a file with no meta table.</summary>
    public DateTime? GeneratedAt => _generatedAt;

    public long IdAt(int node) => _ids[node];

    public bool TryGetNode(long id, out int node)
    {
        node = Array.BinarySearch(_ids, id);
        return node >= 0;
    }

    /// <summary>
    /// How many series this one is paired with. The denominator of the co-recommendation graph's
    /// hub penalty: a title recommended alongside everything says little about any one of them.
    /// The co-read graph does not use it — its weights are already divided by both endpoints'
    /// popularity, and its degree is capped at build time anyway.
    /// </summary>
    public int DegreeAt(int node) => _offsets[node + 1] - _offsets[node];

    /// <summary>
    /// The pairs this node is in: node index and weight of each neighbour. The weight is the evidence
    /// in whatever unit the artifact stores: raw votes for the co-recommendation graph,
    /// co-occurrence strength for the co-read one.
    ///
    /// <para>
    /// Kept <b>raw</b> rather than pre-compressed to <c>log1p</c>: the per-graph minimum
    /// (<c>RecoGraphTuning.MinVotes</c>, <c>CoReadTuning.MinStrength</c>) is a live tuning knob the
    /// eval harness sweeps, and a floor cannot be applied to a value that has already been
    /// log-compressed at load. The few hundred logarithms a request actually needs cost nothing
    /// next to baking the tuning into the index.
    /// </para>
    /// </summary>
    public Edges EdgesAt(int node)
    {
        var start = _offsets[node];
        var length = _offsets[node + 1] - start;
        return new Edges(
            _narrow is null ? default : _narrow.AsSpan(start, length),
            _wide is null ? default : _wide.AsSpan(start, length),
            _weights.AsSpan(start, length));
    }

    /// <summary>One node's slice of the adjacency, whichever width its neighbour indices are stored at.</summary>
    public readonly ref struct Edges(ReadOnlySpan<ushort> narrow, ReadOnlySpan<int> wide, ReadOnlySpan<Half> weights)
    {
        private readonly ReadOnlySpan<ushort> _narrow = narrow;
        private readonly ReadOnlySpan<int> _wide = wide;
        private readonly ReadOnlySpan<Half> _weights = weights;

        public int Length => _weights.Length;

        public int Neighbour(int i) => _wide.IsEmpty ? _narrow[i] : _wide[i];

        public float Weight(int i) => (float)_weights[i];
    }
}
