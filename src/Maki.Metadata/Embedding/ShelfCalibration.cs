namespace Maki.Metadata.Embedding;

/// <summary>
/// Orders a page so its mix of types and genres stays near the shelf's (Steck 2018, calibrated
/// recommendations). Every channel that scores a candidate against the whole shelf at once gives a
/// title fitting a fifth of the shelf about a fifth of the signal, so a reader's second taste falls
/// off the page even when it is in the pool. See distribution/CLAUDE.md, "v9: why a second taste
/// falls off the page".
/// </summary>
public static class ShelfCalibration
{
    private const double Smoothing = 0.01;

    /// <summary>
    /// A row's facet mix: half on its type, half spread evenly over its genres (all on the type
    /// when it has none). Facets are type ids first, then genre ids offset by <paramref name="typeCount"/>.
    /// </summary>
    public static double[] Facets(VectorIndex index, int row, int typeCount, int dims)
    {
        var v = new double[dims];
        var genres = index.GenresAt(row);
        var type = index.TypeAt(row);
        var typeShare = genres.Length == 0 ? 1.0 : 0.5;
        if (type < typeCount)
        {
            v[type] += typeShare;
        }

        foreach (var g in genres)
        {
            if (typeCount + g < dims)
            {
                v[typeCount + g] += 0.5 / genres.Length;
            }
        }

        return v;
    }

    /// <summary>
    /// Greedy order over the window: each step takes the candidate maximising
    /// (1 - <paramref name="lambda"/>) x the list's mean relevance - lambda x KL(shelf || list),
    /// so every prefix of the result is calibrated, not only the whole. With
    /// <paramref name="diversity"/> above 0 a candidate's relevance is first discounted by its
    /// nearest already-picked neighbour, as in <see cref="EmbeddingMath.SelectDiverse"/>.
    /// </summary>
    public static IReadOnlyList<int> Order(
        IReadOnlyList<double> relevance, IReadOnlyList<double[]> facets, double[] target, int take,
        double lambda, Func<int, int, double>? similarity = null, double diversity = 0)
    {
        take = Math.Min(take, relevance.Count);
        if (take <= 0)
        {
            return [];
        }

        var dims = target.Length;
        var support = Enumerable.Range(0, dims).Where(d => target[d] > 0).ToArray();
        var acc = new double[dims];
        var picked = new List<int>(take);
        var remaining = Enumerable.Range(0, relevance.Count).ToList();
        var mmr = similarity is not null && diversity > 0;
        var maxSimilarity = mmr ? new double[relevance.Count] : null;
        var total = 0.0;
        while (picked.Count < take && remaining.Count > 0)
        {
            var n = picked.Count + 1;
            var best = 0;
            var bestValue = double.NegativeInfinity;
            var bestRelevance = 0.0;
            for (var i = 0; i < remaining.Count; i++)
            {
                var c = remaining[i];
                var rel = mmr && picked.Count > 0
                    ? ((1 - diversity) * relevance[c]) - (diversity * maxSimilarity![c])
                    : relevance[c];
                var kl = 0.0;
                var f = facets[c];
                foreach (var d in support)
                {
                    var p = target[d];
                    var q = ((1 - Smoothing) * (acc[d] + f[d]) / n) + (Smoothing * p);
                    kl += p * Math.Log(p / q);
                }

                var value = ((1 - lambda) * (total + rel) / n) - (lambda * kl);
                if (value > bestValue)
                {
                    bestValue = value;
                    best = i;
                    bestRelevance = rel;
                }
            }

            var chosen = remaining[best];
            picked.Add(chosen);
            remaining.RemoveAt(best);
            total += bestRelevance;
            var chosenFacets = facets[chosen];
            for (var d = 0; d < dims; d++)
            {
                acc[d] += chosenFacets[d];
            }

            if (mmr)
            {
                foreach (var c in remaining)
                {
                    maxSimilarity![c] = Math.Max(maxSimilarity[c], similarity!(c, chosen));
                }
            }
        }

        return picked;
    }
}
