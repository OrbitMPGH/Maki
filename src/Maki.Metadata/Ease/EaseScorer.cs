namespace Maki.Metadata.Ease;

/// <summary>
/// Turns a seed set into an EASE score per candidate. Pure arithmetic over <see cref="EaseTable"/>.
/// </summary>
public static class EaseScorer
{
    /// <summary>
    /// Per candidate <c>n</c>: <c>Σ_seeds seedWeight(s) · B[s, n]</c>, divided by the best
    /// candidate's sum so the top is 1. Negative sums stay negative, floored at -1, so the channel
    /// can push a row down as well as up.
    /// </summary>
    /// <param name="seedWeights">Per-seed weight; a seed absent from the map counts 1.0.</param>
    /// <returns>
    /// Candidate id to score. Empty when no seed is in the table or no candidate sums above zero.
    /// Seeds are never candidates.
    /// </returns>
    public static Dictionary<long, double> Score(
        EaseTable table,
        IReadOnlyCollection<long> seedIds,
        IReadOnlyDictionary<long, double>? seedWeights,
        EaseTuning tuning)
    {
        var scores = new Dictionary<long, double>();
        var seeds = new HashSet<long>(seedIds);
        foreach (var seedId in seeds)
        {
            if (!table.TryGetSeed(seedId, out var seed))
            {
                continue;
            }

            var seedWeight = seedWeights is not null && seedWeights.TryGetValue(seedId, out var w) ? w : 1.0;
            if (seedWeight <= 0)
            {
                continue;
            }

            var targets = table.TargetsAt(seed);
            var weights = table.WeightsAt(seed);
            for (var i = 0; i < targets.Length; i++)
            {
                if (weights[i] < 0 && !tuning.KeepNegative)
                {
                    continue;
                }

                if (seeds.Contains(targets[i]))
                {
                    continue;
                }

                scores[targets[i]] = scores.GetValueOrDefault(targets[i]) + (seedWeight * weights[i]);
            }
        }

        var max = 0.0;
        foreach (var value in scores.Values)
        {
            max = Math.Max(max, value);
        }

        if (max <= 0)
        {
            return [];
        }

        foreach (var id in scores.Keys.ToArray())
        {
            scores[id] = Math.Max(-1, scores[id] / max);
        }

        return scores;
    }
}
