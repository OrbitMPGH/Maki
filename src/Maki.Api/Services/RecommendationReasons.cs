using Maki.Metadata.MangaBaka;

namespace Maki.Api.Services;

/// <summary>
/// Spreads the seed a rail's cards name as their reason ("Feels like X", "Fans of X") so one or two
/// heavily read titles do not caption every card. Only the named seed changes: the picks, their order
/// and every other field stay exactly as the recommender returned them.
///
/// <para>
/// A seed may be named at most <see cref="MaxPerWindow"/> times in any <see cref="Window"/>
/// consecutive cards. Past that, a card names its next-best seed of the same kind
/// (<see cref="MangaBakaRecommendation.FeelSeeds"/> or <see cref="MangaBakaRecommendation.CoReadSeeds"/>,
/// which only hold seeds that cleared the recommender's own attribution bar). A card with no such
/// seed left keeps its strongest one rather than naming a weak reason. A window instead of a flat
/// count, because rails are prefixes or filtered runs of one list: Home shows 20, Discover's rails
/// and pages 40, and the window keeps the cap honest on each without knowing which it is.
/// </para>
///
/// <para>
/// Which reason a card shows mirrors <c>engineWhy</c> in <c>frontend/src/components/ui/DiscoverRail.tsx</c>:
/// a relation first, then "Feels like", then an author match, then co-read. Only the reason the
/// card actually shows is counted or rewritten.
/// </para>
/// </summary>
public static class RecommendationReasons
{
    /// <summary>
    /// The run of cards the cap is counted over: Home's rail length, and about two screens of a
    /// desktop rail.
    /// </summary>
    internal const int Window = 20;

    /// <summary>
    /// Twice per <see cref="Window"/>: a seed that really explains a lot of the rail still shows up
    /// more than once, and no title can caption a whole screen of it.
    /// </summary>
    internal const int MaxPerWindow = 2;

    /// <summary>
    /// Rewrites each card's named seed under the cap. Always derives from the seed lists, never from
    /// what a previous pass chose, so running it again over a filtered subset is safe.
    /// </summary>
    public static IReadOnlyList<MangaBakaRecommendation> Balance(IReadOnlyList<MangaBakaRecommendation> ranked)
    {
        if (ranked.Count < 2)
        {
            return ranked;
        }

        var named = new string?[ranked.Count];
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var balanced = new List<MangaBakaRecommendation>(ranked.Count);
        for (var i = 0; i < ranked.Count; i++)
        {
            if (i >= Window && named[i - Window] is { } leaving)
            {
                counts[leaving]--;
            }

            var pick = ranked[i];
            var (feel, seeds) = Candidates(pick);
            if (seeds is null)
            {
                balanced.Add(pick);
                continue;
            }

            var chosen = seeds.FirstOrDefault(s => counts.GetValueOrDefault(Key(s)) < MaxPerWindow) ?? seeds[0];
            var key = Key(chosen);
            named[i] = key;
            counts[key] = counts.GetValueOrDefault(key) + 1;
            balanced.Add(feel
                ? pick with { BecauseOfTitle = chosen.Title, BecauseOfId = chosen.Id, BecauseOfThumbUrl = chosen.ThumbUrl }
                : pick with { CoReadWithTitle = chosen.Title, CoReadWithId = chosen.Id, CoReadWithThumbUrl = chosen.ThumbUrl });
        }

        return balanced;
    }

    /// <summary>
    /// Balances a compact ranked pool up to the end of the requested slice and returns only that
    /// slice with its chosen seed ids populated. Strictly left-to-right causal over the
    /// <see cref="Window"/> lookback, so picks past <paramref name="skip"/> + <paramref name="take"/>
    /// never affect the slice and are not walked.
    /// </summary>
    public static IReadOnlyList<RankedRecommendationPick> BalanceSlice(
        IReadOnlyList<RankedRecommendationPick> ranked, int skip, int take)
    {
        if (take <= 0 || skip >= ranked.Count)
        {
            return [];
        }

        var start = Math.Max(0, skip);
        var end = Math.Min(ranked.Count, start + take);
        if (end <= 1)
        {
            return start < end ? [ranked[start]] : [];
        }

        var named = new long?[end];
        var counts = new Dictionary<long, int>();
        var slice = new List<RankedRecommendationPick>(end - start);
        for (var i = 0; i < end; i++)
        {
            if (i >= Window && named[i - Window] is { } leaving)
            {
                counts[leaving]--;
            }

            var pick = ranked[i];
            var (feel, seeds) = CompactCandidates(pick);
            if (seeds is null)
            {
                if (i >= start)
                {
                    slice.Add(pick);
                }

                continue;
            }

            var chosen = seeds[0];
            for (var s = 0; s < seeds.Count; s++)
            {
                if (counts.GetValueOrDefault(seeds[s]) < MaxPerWindow)
                {
                    chosen = seeds[s];
                    break;
                }
            }

            named[i] = chosen;
            counts[chosen] = counts.GetValueOrDefault(chosen) + 1;
            if (i >= start)
            {
                slice.Add(feel
                    ? pick with { BalancedFeelSeedId = chosen }
                    : pick with { BalancedCoReadSeedId = chosen });
            }
        }

        return slice;
    }

    private static (bool Feel, IReadOnlyList<long>? Seeds) CompactCandidates(RankedRecommendationPick pick)
    {
        if (pick.FeelSeedIds is { Count: > 0 } feel)
        {
            return (true, feel);
        }

        if (pick.AuthorMatch)
        {
            return (false, null);
        }

        return pick.CoRead && pick.CoReadSeedIds is { Count: > 0 } coRead
            ? (false, coRead)
            : (false, null);
    }

    private static (bool Feel, IReadOnlyList<ReasonSeed>? Seeds) Candidates(MangaBakaRecommendation pick)
    {
        if (pick.RelationKind is not null && pick.RelatedToTitle is not null)
        {
            return (false, null);
        }

        if (pick.BecauseOfTitle is not null)
        {
            return (true, pick.FeelSeeds is { Count: > 0 } feel ? feel : null);
        }

        if (pick.AuthorMatch)
        {
            return (false, null);
        }

        return pick.CoRead && pick.CoReadWithTitle is not null && pick.CoReadSeeds is { Count: > 0 } coRead
            ? (false, coRead)
            : (false, null);
    }

    private static string Key(ReasonSeed seed) => seed.Id ?? $"t:{seed.Title}";
}
