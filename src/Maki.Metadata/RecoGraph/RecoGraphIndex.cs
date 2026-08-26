namespace Maki.Metadata.RecoGraph;

/// <summary>
/// The co-recommendation graph, resident in memory: which series readers of a given series also
/// read, aggregated from AniList and MyAnimeList's user-submitted "if you liked X, try Y" pairs and
/// keyed by MangaBaka id.
///
/// <para>
/// This is the one signal in the recommender that is <em>not</em> derived from what a series says
/// about itself. Embeddings can only find Vagabond from Berserk if the two describe alike; this
/// finds it because thousands of readers said so.
/// </para>
///
/// <para>
/// Stored as CSR (compressed sparse row): sorted ids, an offset per node, and one flat neighbour
/// array. Both directions of every pair are materialized, so a lookup is a single contiguous slice
/// with no search. At ~100k pairs that is about 2.5 MB of arrays, which is why this loads whole
/// rather than being <c>ATTACH</c>ed and queried per request the way the dump is — the lookup sits
/// on the recommendation hot path and runs once per seed.
/// </para>
///
/// <para>
/// Immutable once built, so readers need no lock. Same contract as <see cref="Embedding.VectorIndex"/>.
/// </para>
/// </summary>
public sealed class RecoGraphIndex
{
    private readonly long[] _ids;
    private readonly int[] _offsets;
    private readonly int[] _neighbours;
    private readonly int[] _votes;
    private readonly Dictionary<long, int> _nodeById;

    internal RecoGraphIndex(long[] ids, int[] offsets, int[] neighbours, int[] votes, DateTime? generatedAt)
    {
        _ids = ids;
        _offsets = offsets;
        _neighbours = neighbours;
        _votes = votes;
        _generatedAt = generatedAt;

        _nodeById = new Dictionary<long, int>(ids.Length);
        for (var i = 0; i < ids.Length; i++)
        {
            _nodeById[ids[i]] = i;
        }
    }

    private readonly DateTime? _generatedAt;

    /// <summary>Series that carry at least one edge.</summary>
    public int Count => _ids.Length;

    /// <summary>Directed entries, i.e. twice the number of unordered pairs.</summary>
    public int EdgeCount => _neighbours.Length;

    /// <summary>When the artifact was generated, if it says. Null for a file with no meta table.</summary>
    public DateTime? GeneratedAt => _generatedAt;

    public long IdAt(int node) => _ids[node];

    public bool TryGetNode(long id, out int node) => _nodeById.TryGetValue(id, out node);

    /// <summary>
    /// How many series this one is paired with. The denominator of the hub penalty: a title
    /// recommended alongside everything says little about any one of them.
    /// </summary>
    public int DegreeAt(int node) => _offsets[node + 1] - _offsets[node];

    /// <summary>Node indices this one is paired with. Parallel to <see cref="VotesAt"/>.</summary>
    public ReadOnlySpan<int> NeighboursAt(int node) =>
        _neighbours.AsSpan(_offsets[node], _offsets[node + 1] - _offsets[node]);

    /// <summary>
    /// Votes backing each neighbour, summed across providers. Kept <b>raw</b> rather than
    /// pre-compressed to <c>log1p</c>: <c>RecoGraphTuning.MinVotes</c> is a live tuning knob the
    /// eval harness sweeps, and a floor cannot be applied to a value that has already been
    /// log-compressed at load. The few hundred logarithms a request actually needs cost nothing
    /// next to baking the tuning into the index.
    /// </summary>
    public ReadOnlySpan<int> VotesAt(int node) =>
        _votes.AsSpan(_offsets[node], _offsets[node + 1] - _offsets[node]);
}
