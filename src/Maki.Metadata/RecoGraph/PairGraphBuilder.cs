namespace Maki.Metadata.RecoGraph;

/// <summary>
/// Folds a list of unordered pairs into a <see cref="PairGraphIndex"/>. Shared by both graph
/// caches: the artifacts differ in what their weight means and in how it is read off the file, but
/// the adjacency they build is identical, and two copies of this would be two copies to keep in
/// step.
/// </summary>
internal static class PairGraphBuilder
{
    /// <summary>
    /// Materializes both directions of every pair into CSR form, in two passes over
    /// <paramref name="pairs"/>: one for the ids and their degrees, one to fill the slices. The
    /// source is enumerated twice so a caller can stream a table straight into the final arrays;
    /// collecting the co-read graph's two million pairs first cost a 48 MB list, doubled while it grew.
    /// </summary>
    public static PairGraphIndex Build(Func<IEnumerable<(long A, long B, float Weight)>> pairs, DateTime? generatedAt)
    {
        var degrees = new Dictionary<long, int>();
        var edges = 0;
        foreach (var (a, b, _) in pairs())
        {
            degrees[a] = degrees.GetValueOrDefault(a) + 1;
            degrees[b] = degrees.GetValueOrDefault(b) + 1;
            edges += 2;
        }

        var ids = degrees.Keys.ToArray();
        Array.Sort(ids);

        var nodeById = new Dictionary<long, int>(ids.Length);
        var offsets = new int[ids.Length + 1];
        for (var i = 0; i < ids.Length; i++)
        {
            nodeById[ids[i]] = i;
            offsets[i + 1] = offsets[i] + degrees[ids[i]];
        }

        var narrow = ids.Length <= ushort.MaxValue + 1 ? new ushort[edges] : null;
        var wide = narrow is null ? new int[edges] : null;
        var weights = new Half[edges];
        var cursor = new int[ids.Length];

        foreach (var (a, b, weight) in pairs())
        {
            var na = nodeById[a];
            var nb = nodeById[b];
            var stored = (Half)Math.Min(weight, (float)Half.MaxValue);

            var slotA = offsets[na] + cursor[na]++;
            var slotB = offsets[nb] + cursor[nb]++;
            if (narrow is not null)
            {
                narrow[slotA] = (ushort)nb;
                narrow[slotB] = (ushort)na;
            }
            else
            {
                wide![slotA] = nb;
                wide[slotB] = na;
            }

            weights[slotA] = stored;
            weights[slotB] = stored;
        }

        return new PairGraphIndex(ids, offsets, narrow, wide, weights, generatedAt);
    }
}
