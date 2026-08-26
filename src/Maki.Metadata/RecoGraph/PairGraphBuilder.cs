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
    /// Materializes both directions of every pair into CSR form. Two counting passes rather than a
    /// dictionary of lists: the node count is six figures and the per-node degree is a handful, so
    /// the list-per-node form would allocate more headers than data.
    /// </summary>
    public static PairGraphIndex Build(
        List<(long A, long B, float Weight)> pairs, DateTime? generatedAt)
    {
        var idSet = new HashSet<long>(pairs.Count);
        foreach (var (a, b, _) in pairs)
        {
            idSet.Add(a);
            idSet.Add(b);
        }

        var ids = idSet.ToArray();
        Array.Sort(ids);

        var nodeById = new Dictionary<long, int>(ids.Length);
        for (var i = 0; i < ids.Length; i++)
        {
            nodeById[ids[i]] = i;
        }

        // Pass 1: degrees, accumulated into the offset array shifted by one so the prefix sum
        // below turns it into the offsets directly.
        var offsets = new int[ids.Length + 1];
        foreach (var (a, b, _) in pairs)
        {
            offsets[nodeById[a] + 1]++;
            offsets[nodeById[b] + 1]++;
        }

        for (var i = 0; i < ids.Length; i++)
        {
            offsets[i + 1] += offsets[i];
        }

        // Pass 2: fill. `cursor` walks each node's slice as entries land in it.
        var neighbours = new int[pairs.Count * 2];
        var weights = new float[pairs.Count * 2];
        var cursor = new int[ids.Length];

        foreach (var (a, b, weight) in pairs)
        {
            var na = nodeById[a];
            var nb = nodeById[b];

            var slotA = offsets[na] + cursor[na]++;
            neighbours[slotA] = nb;
            weights[slotA] = weight;

            var slotB = offsets[nb] + cursor[nb]++;
            neighbours[slotB] = na;
            weights[slotB] = weight;
        }

        return new PairGraphIndex(ids, offsets, neighbours, weights, generatedAt);
    }
}
