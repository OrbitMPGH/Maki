using Maki.Metadata.RecoGraph;

namespace Maki.Metadata.CoRead;

/// <summary>
/// Turns a reading history into a co-read score per candidate series. Pure arithmetic over
/// <see cref="PairGraphIndex"/> — no I/O, no index, no dump — so the tuning can be swept and the
/// failure modes tested directly.
/// </summary>
public static class CoReadScorer
{
    /// <summary>
    /// Scores every series the seeds were finished alongside, keyed by MangaBaka id and normalized
    /// to (0, 1].
    ///
    /// <para>
    /// Per candidate <c>n</c>: <c>Σ_seeds seedWeight(s) · strength(s, n)</c>, over edges clearing
    /// <see cref="CoReadTuning.MinStrength"/>. That is the whole of it, and the two terms the vote
    /// graph's scorer has and this one does not are the interesting part.
    /// </para>
    ///
    /// <para>
    /// <b>No <c>log1p</c>.</b> The vote graph needs it because its weights span 1 to 6008 and a
    /// linear sum is decided entirely by whichever mega-title is in range. A strength is a bounded
    /// ratio, and <c>log1p</c> of a number that small is very nearly the number itself.
    /// </para>
    ///
    /// <para>
    /// <b>No degree penalty</b>, though not for the reason it first appears. Each individual
    /// strength is already hub-corrected: the build divides every co-occurrence by
    /// <c>sqrt((users(a)+k) · (users(b)+k))</c>, which is the same idea the vote graph's degree
    /// exponent approximates, with real denominators instead. Dividing again would be the double
    /// normalization measured on that graph at <c>DegreePenalty = 0.5</c>, where the top of the
    /// channel inverted into single-edge obscurities.
    /// </para>
    ///
    /// <para>
    /// <b>That argument is nonetheless incomplete, and the gap is worth knowing about.</b> The sum
    /// above re-introduces popularity by a route the per-edge normalization cannot touch: a famous
    /// title is simply reached from more of the seeds. Measured on a real library, candidates in the
    /// catalogue's top 200 were reached from 2.00 seeds on average against 1.02 for those past rank
    /// 20,000, so the sum favours them however well each term is normalized. A degree penalty does
    /// not fix it either — applied here it moved the top-40 median popularity rank from 117 to 173
    /// at 0.25 and 268 at 0.5, still deep inside the catalogue's top 0.2%, while adding a knob and a
    /// second normalization. The lever that does work is <see cref="CoReadTuning.Weight"/>, which is
    /// why it ships low.
    /// </para>
    /// </summary>
    /// <param name="graph">The loaded co-read graph.</param>
    /// <param name="seedIds">Series the user has read. Never scored as candidates themselves.</param>
    /// <param name="seedWeights">
    /// Per-seed evidence weight from <c>TasteWeights</c>. A seed absent from the map counts 1.0, so
    /// passing null degrades to unweighted rather than to nothing.
    /// </param>
    /// <param name="tuning">Knobs.</param>
    /// <returns>
    /// Candidate id → score. Empty when no seed is in the graph. Callers read a missing key as "no
    /// co-read evidence", which is why the normalization below divides by the maximum rather than
    /// min-maxing: a min-max would floor the weakest real candidate at 0 and make it
    /// indistinguishable from a series nobody ever finished alongside anything.
    /// </returns>
    public static Dictionary<long, double> Score(
        PairGraphIndex graph,
        IReadOnlyCollection<long> seedIds,
        IReadOnlyDictionary<long, double>? seedWeights,
        CoReadTuning tuning)
    {
        var scores = new Dictionary<long, double>();
        if (seedIds.Count == 0)
        {
            return scores;
        }

        var seeds = new HashSet<long>(seedIds);

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
            var strengths = graph.WeightsAt(seedNode);

            for (var i = 0; i < neighbours.Length; i++)
            {
                if (strengths[i] < tuning.MinStrength)
                {
                    continue;
                }

                var id = graph.IdAt(neighbours[i]);

                // A seed being read alongside another seed is not something the user can act on.
                if (seeds.Contains(id))
                {
                    continue;
                }

                scores[id] = scores.GetValueOrDefault(id) + (seedWeight * strengths[i]);
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
