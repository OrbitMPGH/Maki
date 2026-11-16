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
/// CSR over sorted seed ids, targets sorted within each seed. Immutable once built, so readers need
/// no lock. The columns are either heap arrays or slices of a mapped snapshot
/// (<see cref="SnapshotFile"/>), the same as the co-read graph's.
/// </para>
/// </summary>
public sealed class EaseTable
{
    private readonly Column<long> _seeds;
    private readonly Column<int> _offsets;
    private readonly Column<long> _targets;
    private readonly Column<float> _weights;
    private readonly DateTime? _generatedAt;

    private EaseTable(
        Column<long> seeds, Column<int> offsets, Column<long> targets, Column<float> weights, DateTime? generatedAt)
    {
        _seeds = seeds;
        _offsets = offsets;
        _targets = targets;
        _weights = weights;
        _generatedAt = generatedAt;
    }

    /// <summary>Seeds that carry at least one weight.</summary>
    public int Count => _seeds.Length;

    /// <summary>Stored weights across every seed.</summary>
    public int WeightCount => _weights.Length;

    /// <summary>When the artifact was generated, if its meta table says.</summary>
    public DateTime? GeneratedAt => _generatedAt;

    internal bool IsMapped => _weights.IsMapped;

    public bool TryGetSeed(long id, out int seed)
    {
        seed = _seeds.Span.BinarySearch(id);
        return seed >= 0;
    }

    public ReadOnlySpan<long> TargetsAt(int seed) => _targets.AsSpan(_offsets[seed], _offsets[seed + 1] - _offsets[seed]);

    public ReadOnlySpan<float> WeightsAt(int seed) => _weights.AsSpan(_offsets[seed], _offsets[seed + 1] - _offsets[seed]);

    /// <summary>The weight from <paramref name="seed"/> to <paramref name="target"/>, or 0 when none is stored.</summary>
    public float WeightOf(int seed, long target)
    {
        var at = TargetsAt(seed).BinarySearch(target);
        return at >= 0 ? WeightsAt(seed)[at] : 0;
    }

    /// <summary>
    /// Builds the table from (seed, target, weight) rows in any order. Self-weights and non-finite or
    /// zero weights are dropped.
    /// </summary>
    public static EaseTable Build(IEnumerable<(long Seed, long Target, float Weight)> rows, DateTime? generatedAt = null)
    {
        var seedCol = new List<long>();
        var targetCol = new List<long>();
        var weightCol = new List<float>();
        var sorted = true;
        foreach (var (seed, target, weight) in rows)
        {
            if (seed == target || !float.IsFinite(weight) || weight == 0)
            {
                continue;
            }

            if (seedCol.Count > 0
                && (seed < seedCol[^1] || (seed == seedCol[^1] && target <= targetCol[^1])))
            {
                sorted = false;
            }

            seedCol.Add(seed);
            targetCol.Add(target);
            weightCol.Add(weight);
        }

        var count = seedCol.Count;
        var seedArr = seedCol.ToArray();
        var targets = targetCol.ToArray();
        var weights = weightCol.ToArray();
        seedCol = null;
        targetCol = null;
        weightCol = null;

        // The exporter writes a WITHOUT ROWID table keyed on (seed, target), so a plain scan already
        // comes back in order. Anything else is sorted here, and a duplicate pair keeps its first row.
        if (!sorted)
        {
            var order = Enumerable.Range(0, count).ToArray();
            Array.Sort(order, (a, b) => seedArr[a] != seedArr[b]
                ? seedArr[a].CompareTo(seedArr[b])
                : targets[a] != targets[b] ? targets[a].CompareTo(targets[b]) : a.CompareTo(b));
            var s = new long[count];
            var t = new long[count];
            var w = new float[count];
            var kept = 0;
            foreach (var i in order)
            {
                if (kept > 0 && s[kept - 1] == seedArr[i] && t[kept - 1] == targets[i])
                {
                    continue;
                }

                s[kept] = seedArr[i];
                t[kept] = targets[i];
                w[kept] = weights[i];
                kept++;
            }

            seedArr = s[..kept];
            targets = t[..kept];
            weights = w[..kept];
            count = kept;
        }

        var seeds = new List<long>();
        var offsets = new List<int> { 0 };
        for (var i = 0; i < count; i++)
        {
            if (seeds.Count == 0 || seeds[^1] != seedArr[i])
            {
                if (seeds.Count > 0)
                {
                    offsets.Add(i);
                }

                seeds.Add(seedArr[i]);
            }
        }

        if (seeds.Count > 0)
        {
            offsets.Add(count);
        }

        return new EaseTable([.. seeds], [.. offsets], targets, weights, generatedAt);
    }

    internal void WriteTo(BinaryWriter writer)
    {
        SnapshotFile.WriteArray(writer, _seeds.AsSpan());
        SnapshotFile.WriteArray(writer, _offsets.AsSpan());
        SnapshotFile.WriteArray(writer, _targets.AsSpan());
        SnapshotFile.WriteArray(writer, _weights.AsSpan());
        writer.Write(_generatedAt?.ToBinary() ?? 0L);
        writer.Write(_generatedAt.HasValue);
    }

    internal static EaseTable? ReadFrom(BinaryReader reader)
    {
        var seeds = SnapshotFile.ReadArray<long>(reader);
        var offsets = SnapshotFile.ReadArray<int>(reader);
        var targets = SnapshotFile.ReadArray<long>(reader);
        var weights = SnapshotFile.ReadArray<float>(reader);
        var generatedAt = reader.ReadInt64();
        var hasGeneratedAt = reader.ReadBoolean();
        var consistent = targets.Length == weights.Length
            && (seeds.Length == 0
                ? offsets.Length <= 1 && weights.Length == 0
                : offsets.Length == seeds.Length + 1 && offsets[^1] == weights.Length);
        return consistent
            ? new EaseTable(seeds, offsets, targets, weights, hasGeneratedAt ? DateTime.FromBinary(generatedAt) : null)
            : null;
    }
}
