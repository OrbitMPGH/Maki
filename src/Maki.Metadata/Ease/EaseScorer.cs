using Maki.Metadata.Embedding;

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

    /// <summary>
    /// The seeds that pushed each of <paramref name="candidates"/> up, strongest weighted
    /// contribution first. Only positive contributions count: a seed that weighs a pick down is not
    /// a reason for it. For the handful of picks a page returns, so a card can name a seed.
    /// </summary>
    public static Dictionary<long, List<long>> SeedsFor(
        EaseTable table,
        IReadOnlyCollection<long> seedIds,
        IReadOnlyDictionary<long, double>? seedWeights,
        IReadOnlySet<long> candidates,
        double minShare = 0)
    {
        var found = new Dictionary<long, List<(long Seed, double Contribution)>>();
        if (candidates.Count == 0)
        {
            return [];
        }

        foreach (var seedId in new HashSet<long>(seedIds))
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

            foreach (var candidate in candidates)
            {
                var weight = table.WeightOf(seed, candidate);
                if (weight <= 0 || candidate == seedId)
                {
                    continue;
                }

                if (!found.TryGetValue(candidate, out var list))
                {
                    found[candidate] = list = [];
                }

                list.Add((seedId, seedWeight * weight));
            }
        }

        return found.ToDictionary(kv => kv.Key, kv => ReasonShare.Keep(kv.Value, minShare));
    }
}
