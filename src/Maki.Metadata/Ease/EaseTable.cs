namespace Maki.Metadata.Ease;

/// <summary>
/// A directed, signed item-item weight table over MangaBaka ids: for each seed, the weights an
/// EASE^R fit gave its strongest neighbours (<c>distribution/build-ease-table.py</c>).
///
/// <para>
/// Not a <see cref="RecoGraph.PairGraphIndex"/>. That shape is undirected with one positive weight
/// per pair, while an EASE weight is directed (<c>B[a, b]</c> is not <c>B[b, a]</c>) and negative
/// on roughly half its rows, and the negatives carry a third of what the table is worth.
/// </para>
///
/// <para>
/// CSR over sorted seed ids. Immutable once built, so readers need no lock.
/// </para>
/// </summary>
public sealed class EaseTable
{
    private readonly long[] _seeds;
    private readonly int[] _offsets;
    private readonly long[] _targets;
    private readonly float[] _weights;

    private EaseTable(long[] seeds, int[] offsets, long[] targets, float[] weights)
    {
        _seeds = seeds;
        _offsets = offsets;
        _targets = targets;
        _weights = weights;
    }

    /// <summary>Seeds that carry at least one weight.</summary>
    public int Count => _seeds.Length;

    /// <summary>Stored weights across every seed.</summary>
    public int WeightCount => _weights.Length;

    public bool TryGetSeed(long id, out int seed)
    {
        seed = Array.BinarySearch(_seeds, id);
        return seed >= 0;
    }

    public ReadOnlySpan<long> TargetsAt(int seed) => _targets.AsSpan(_offsets[seed], _offsets[seed + 1] - _offsets[seed]);

    public ReadOnlySpan<float> WeightsAt(int seed) => _weights.AsSpan(_offsets[seed], _offsets[seed + 1] - _offsets[seed]);

    /// <summary>
    /// Builds the table from (seed, target, weight) rows in any order. Self-weights and non-finite or
    /// zero weights are dropped.
    /// </summary>
    public static EaseTable Build(IEnumerable<(long Seed, long Target, float Weight)> rows)
    {
        var kept = rows
            .Where(r => r.Seed != r.Target && float.IsFinite(r.Weight) && r.Weight != 0)
            .OrderBy(r => r.Seed)
            .ThenBy(r => r.Target)
            .ToList();

        var seeds = new List<long>();
        var offsets = new List<int> { 0 };
        var targets = new long[kept.Count];
        var weights = new float[kept.Count];
        for (var i = 0; i < kept.Count; i++)
        {
            if (seeds.Count == 0 || seeds[^1] != kept[i].Seed)
            {
                if (seeds.Count > 0)
                {
                    offsets.Add(i);
                }

                seeds.Add(kept[i].Seed);
            }

            targets[i] = kept[i].Target;
            weights[i] = kept[i].Weight;
        }

        if (seeds.Count > 0)
        {
            offsets.Add(kept.Count);
        }

        return new EaseTable([.. seeds], [.. offsets], targets, weights);
    }
}
