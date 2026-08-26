namespace Maki.Metadata.RecoGraph;

/// <summary>
/// Turns a reading history into a co-recommendation score per candidate series. Pure arithmetic
/// over <see cref="PairGraphIndex"/> — no I/O, no index, no dump — so the tuning can be swept and the
/// failure modes tested directly.
/// </summary>
public static class RecoGraphScorer
{
    /// <summary>
    /// Scores every series the seeds are paired with, keyed by MangaBaka id and normalized to
    /// (0, 1].
    ///
    /// <para>
    /// Per candidate <c>n</c>:
    /// <c>Σ_seeds seedWeight(s) · log1p(votes(s,n)) / degree(n)^DegreePenalty</c>, over edges
    /// clearing <see cref="RecoGraphTuning.MinVotes"/>.
    /// </para>
    ///
    /// <para>
    /// Each term earns its place against a measured failure. <b>log1p</b> because votes span 1 to
    /// 6008 and a linear sum is decided entirely by whichever mega-title is in range.
    /// <b>degree</b> because a title paired with 284 others says little about any one of them.
    /// <b>seedWeight</b> because a series rated 5 and read to the end is better evidence of taste
    /// than one abandoned at chapter 2, and the caller has already worked that out.
    /// </para>
    /// </summary>
    /// <param name="graph">The loaded graph.</param>
    /// <param name="seedIds">Series the user has read. Never scored as candidates themselves.</param>
    /// <param name="seedWeights">
    /// Per-seed evidence weight from <c>TasteWeights</c>. A seed absent from the map counts 1.0, so
    /// passing null degrades to unweighted rather than to nothing.
    /// </param>
    /// <param name="tuning">Knobs.</param>
    /// <returns>
    /// Candidate id → score. Empty when no seed is in the graph. Callers read a missing key as
    /// "no co-recommendation evidence", which is why the normalization below divides by the maximum
    /// rather than min-maxing: a min-max would floor the weakest real candidate at 0 and make it
    /// indistinguishable from a series nobody ever paired with anything.
    /// </returns>
    public static Dictionary<long, double> Score(
        PairGraphIndex graph,
        IReadOnlyCollection<long> seedIds,
        IReadOnlyDictionary<long, double>? seedWeights,
        RecoGraphTuning tuning)
    {
        var scores = new Dictionary<long, double>();
        if (seedIds.Count == 0)
        {
            return scores;
        }

        var seeds = new HashSet<long>(seedIds);

        // Degrees are shared across seeds, and a popular candidate is reached from many of them, so
        // the penalty is worth memoizing rather than recomputing Math.Pow per edge.
        var penaltyByNode = new Dictionary<int, double>();

        foreach (var seedId in seeds)
        {
            if (!graph.TryGetNode(seedId, out var seedNode))
            {
                continue;
            }

            var seedWeight = seedWeights is not null && seedWeights.TryGetValue(seedId, out var w)
                ? w
                : 1.0;

            if (seedWeight <= 0)
            {
                continue;
            }

            var neighbours = graph.NeighboursAt(seedNode);
            var votes = graph.WeightsAt(seedNode);

            for (var i = 0; i < neighbours.Length; i++)
            {
                if (votes[i] < tuning.MinVotes)
                {
                    continue;
                }

                var node = neighbours[i];
                var id = graph.IdAt(node);

                // A seed recommending another seed is not a recommendation the user can act on.
                if (seeds.Contains(id))
                {
                    continue;
                }

                if (!penaltyByNode.TryGetValue(node, out var penalty))
                {
                    penalty = tuning.DegreePenalty == 0
                        ? 1.0
                        : Math.Pow(
                            Math.Max(1, graph.DegreeAt(node)) + tuning.DegreeSmoothing,
                            tuning.DegreePenalty);
                    penaltyByNode[node] = penalty;
                }

                var contribution = seedWeight * Math.Log(1 + votes[i]) / penalty;
                scores[id] = scores.GetValueOrDefault(id) + contribution;
            }
        }

        if (scores.Count == 0)
        {
            return scores;
        }

        var max = 0.0;
        foreach (var value in scores.Values)
        {
            if (value > max)
            {
                max = value;
            }
        }

        if (max <= 0)
        {
            return [];
        }

        foreach (var id in scores.Keys.ToArray())
        {
            scores[id] /= max;
        }

        return scores;
    }
}
