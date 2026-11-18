namespace Maki.Metadata.Embedding;

/// <summary>
/// Keeps a ranked list from being more famous than the shelf it was built from. The reader's own
/// series say how much of their reading sits inside each popularity band, and the list may hold at
/// most <see cref="RecommenderTuning.FameCapRatio"/> times that share at any point. A pick over the
/// limit is deferred to the first later slot it fits, never dropped, and every other pick keeps its
/// order.
/// </summary>
public static class ShelfFame
{
    /// <summary>
    /// Popularity ranks the limits are held at. A pick counts toward each one it is inside. Only the
    /// famous end is limited: holding ranks 3,000 and 10,000 as well more than doubled the cost on
    /// held-out readers and removed no more famous picks.
    /// </summary>
    public static readonly int[] Thresholds = [100, 300, 1_000];

    /// <summary>
    /// The largest share of a list that may sit at or inside each of <see cref="Thresholds"/>.
    /// </summary>
    /// <param name="shelf">Each seed's popularity rank (0 or less when unknown) and weight.</param>
    /// <param name="ratio">How many times the shelf's own share the list may hold.</param>
    /// <param name="pseudoSeeds">
    /// Seed weight added on the famous side of every threshold, so a shelf of a few series, whose
    /// shares are noise, limits almost nothing.
    /// </param>
    public static double[] Limits(IEnumerable<(int Rank, double Weight)> shelf, double ratio, double pseudoSeeds)
    {
        var inside = new double[Thresholds.Length];
        var total = 0.0;
        foreach (var (rank, weight) in shelf)
        {
            if (weight <= 0)
            {
                continue;
            }

            total += weight;
            for (var t = 0; t < Thresholds.Length; t++)
            {
                if (rank > 0 && rank <= Thresholds[t])
                {
                    inside[t] += weight;
                }
            }
        }

        var limits = new double[Thresholds.Length];
        for (var t = 0; t < limits.Length; t++)
        {
            limits[t] = total + pseudoSeeds <= 0 ? 1 : ratio * (inside[t] + pseudoSeeds) / (total + pseudoSeeds);
        }

        return limits;
    }

    /// <summary>
    /// The first <paramref name="take"/> of <paramref name="ranked"/> after deferring picks that
    /// would put the list over a limit. With fewer than <paramref name="take"/> picks every one is
    /// returned.
    /// </summary>
    public static List<T> Defer<T>(IReadOnlyList<T> ranked, Func<T, int> rankOf, double[] limits, int take)
    {
        var result = new List<T>(Math.Min(take, ranked.Count));
        var deferred = new List<T>();
        var counts = new int[Thresholds.Length];

        bool Fits(T pick)
        {
            var rank = rankOf(pick);
            if (rank <= 0)
            {
                return true;
            }

            var slot = result.Count + 1;
            for (var t = 0; t < Thresholds.Length; t++)
            {
                if (rank <= Thresholds[t] && counts[t] + 1 > Math.Ceiling(limits[t] * slot))
                {
                    return false;
                }
            }

            return true;
        }

        void Place(T pick)
        {
            var rank = rankOf(pick);
            for (var t = 0; t < Thresholds.Length; t++)
            {
                if (rank > 0 && rank <= Thresholds[t])
                {
                    counts[t]++;
                }
            }

            result.Add(pick);
        }

        void PlaceDeferred()
        {
            for (var i = 0; i < deferred.Count && result.Count < take;)
            {
                if (Fits(deferred[i]))
                {
                    Place(deferred[i]);
                    deferred.RemoveAt(i);
                    i = 0;
                }
                else
                {
                    i++;
                }
            }
        }

        foreach (var pick in ranked)
        {
            PlaceDeferred();
            if (result.Count >= take)
            {
                break;
            }

            if (Fits(pick))
            {
                Place(pick);
            }
            else
            {
                deferred.Add(pick);
            }
        }

        PlaceDeferred();
        foreach (var pick in deferred)
        {
            if (result.Count >= take)
            {
                break;
            }

            result.Add(pick);
        }

        return result;
    }
}
