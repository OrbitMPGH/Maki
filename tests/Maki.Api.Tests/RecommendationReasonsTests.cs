using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Metadata.MangaBaka;

namespace Maki.Api.Tests;

/// <summary>
/// <c>RecommendationReasons.Balance</c>: which seed a card names as its reason, capped per run of
/// cards. It may only change the named seed, never which picks a rail holds or their order.
/// </summary>
public class RecommendationReasonsTests
{
    private static readonly ReasonSeed A = new("1", "Akira Failing in Love", "a.jpg");
    private static readonly ReasonSeed B = new("2", "Gals Can't Be Kind", "b.jpg");
    private static readonly ReasonSeed C = new("3", "Third Seed", "c.jpg");

    private static MangaBakaRecommendation Feel(int id, params ReasonSeed[] seeds) =>
        new($"{id}", $"Pick {id}", null, null, null, SeriesStatus.Completed, 80, null,
            [], [], false, null, null,
            BecauseOfTitle: seeds[0].Title, BecauseOfId: seeds[0].Id, BecauseOfThumbUrl: seeds[0].ThumbUrl,
            FeelSeeds: seeds);

    private static MangaBakaRecommendation CoRead(int id, params ReasonSeed[] seeds) =>
        new($"{id}", $"Pick {id}", null, null, null, SeriesStatus.Completed, 80, null,
            [], [], false, null, null, CoRead: true,
            CoReadWithTitle: seeds[0].Title, CoReadWithId: seeds[0].Id, CoReadWithThumbUrl: seeds[0].ThumbUrl,
            CoReadSeeds: seeds);

    private static string? Named(MangaBakaRecommendation pick) => pick.BecauseOfId ?? pick.CoReadWithId;

    [Fact]
    public void One_seed_is_named_at_most_twice_and_later_cards_name_their_next_seed()
    {
        var ranked = Enumerable.Range(1, 6).Select(i => CoRead(i, A, B, C)).ToList();

        var balanced = RecommendationReasons.Balance(ranked);

        Assert.Equal(["1", "1", "2", "2", "3", "3"], balanced.Select(Named));
        Assert.Equal("Gals Can't Be Kind", balanced[2].CoReadWithTitle);
        Assert.Equal("b.jpg", balanced[2].CoReadWithThumbUrl);
    }

    [Fact]
    public void Feel_and_co_read_reasons_share_one_count_per_seed()
    {
        List<MangaBakaRecommendation> ranked = [Feel(1, A, B), CoRead(2, A, B), Feel(3, A, B)];

        var balanced = RecommendationReasons.Balance(ranked);

        Assert.Equal(["1", "1", "2"], balanced.Select(Named));
        Assert.Equal("Gals Can't Be Kind", balanced[2].BecauseOfTitle);
    }

    [Fact]
    public void The_picks_and_their_order_are_unchanged()
    {
        var ranked = Enumerable.Range(1, 30).Select(i => i % 3 == 0 ? CoRead(i, A, C) : Feel(i, A, B)).ToList();

        var balanced = RecommendationReasons.Balance(ranked);

        Assert.Equal(ranked.Select(p => p.ProviderId), balanced.Select(p => p.ProviderId));
        Assert.All(ranked.Zip(balanced), pair => Assert.Equal(
            pair.First with { BecauseOfTitle = null, BecauseOfId = null, BecauseOfThumbUrl = null,
                CoReadWithTitle = null, CoReadWithId = null, CoReadWithThumbUrl = null },
            pair.Second with { BecauseOfTitle = null, BecauseOfId = null, BecauseOfThumbUrl = null,
                CoReadWithTitle = null, CoReadWithId = null, CoReadWithThumbUrl = null }));
    }

    [Fact]
    public void A_card_with_no_other_supporting_seed_keeps_its_top_seed()
    {
        List<MangaBakaRecommendation> ranked = [Feel(1, A, B), Feel(2, A, B), Feel(3, A), Feel(4, A, B)];

        var balanced = RecommendationReasons.Balance(ranked);

        Assert.Equal(["1", "1", "1", "2"], balanced.Select(Named));
        Assert.Equal("Akira Failing in Love", balanced[2].BecauseOfTitle);
    }

    [Fact]
    public void The_cap_counts_over_a_window_so_a_seed_comes_back_further_down()
    {
        var seeds = Enumerable.Range(1, 10).Select(i => new ReasonSeed($"{i}", $"Seed {i}", null)).ToArray();
        var ranked = Enumerable.Range(1, 40).Select(i => Feel(i, seeds)).ToList();

        var balanced = RecommendationReasons.Balance(ranked);

        for (var start = 0; start + RecommendationReasons.Window <= balanced.Count; start++)
        {
            Assert.All(
                balanced.Skip(start).Take(RecommendationReasons.Window).GroupBy(p => p.BecauseOfId),
                seed => Assert.True(seed.Count() <= RecommendationReasons.MaxPerWindow));
        }

        Assert.Equal("1", balanced[RecommendationReasons.Window].BecauseOfId);
    }

    [Fact]
    public void A_rail_where_every_card_has_only_one_seed_keeps_its_shared_reason()
    {
        var ranked = Enumerable.Range(1, 8).Select(i => CoRead(i, A)).ToList();

        var balanced = RecommendationReasons.Balance(ranked);

        Assert.All(balanced, p => Assert.Equal("1", p.CoReadWithId));
    }

    [Fact]
    public void A_card_showing_an_author_match_does_not_spend_its_co_read_seed()
    {
        var author = CoRead(1, A, B) with { AuthorMatch = true };
        List<MangaBakaRecommendation> ranked = [author, CoRead(2, A, B), CoRead(3, A, B)];

        var balanced = RecommendationReasons.Balance(ranked);

        Assert.Equal(["1", "1", "1"], balanced.Select(p => p.CoReadWithId));
    }

    [Fact]
    public void BalanceSlice_matches_full_balance_across_pages_and_preserves_original_seeds()
    {
        var seeds = Enumerable.Range(1, 5).Select(i => new ReasonSeed($"{i}", $"Seed {i}", $"{i}.jpg")).ToArray();
        var hydrated = Enumerable.Range(1, 50)
            .Select(i => i % 5 == 0
                ? CoRead(i, seeds[0], seeds[1], seeds[2]) with { AuthorMatch = true }
                : i % 3 == 0
                    ? CoRead(i, seeds[0], seeds[1], seeds[2])
                    : Feel(i, seeds[0], seeds[1], seeds[2]))
            .ToList();
        var compact = Enumerable.Range(1, 50)
            .Select(i => new RankedRecommendationPick(
                SeriesId: i,
                FallbackRating: 80,
                MatchedGenres: [],
                CandidateTags: [],
                AuthorMatch: i % 5 == 0,
                CoRecommended: false,
                CoRead: i % 5 == 0 || i % 3 == 0,
                TasteMatch: false,
                FeelSeedIds: i % 5 == 0 || i % 3 == 0 ? null : [1L, 2L, 3L],
                CoReadSeedIds: i % 5 == 0 || i % 3 == 0 ? [1L, 2L, 3L] : null))
            .ToList();

        var full = RecommendationReasons.Balance(hydrated);
        var page0 = RecommendationReasons.BalanceSlice(compact, skip: 0, take: 20);
        var page1 = RecommendationReasons.BalanceSlice(compact, skip: 20, take: 20);
        var page2 = RecommendationReasons.BalanceSlice(compact, skip: 40, take: 20);
        var combined = page0.Concat(page1).Concat(page2).ToList();

        Assert.Equal(full.Count, combined.Count);
        for (var i = 0; i < full.Count; i++)
        {
            var expected = full[i].AuthorMatch ? null : Named(full[i]);
            var actual = combined[i].AuthorMatch
                ? null
                : (combined[i].BalancedFeelSeedId ?? combined[i].BalancedCoReadSeedId)?.ToString();
            Assert.Equal(expected, actual);
            // Original seed lists remain untouched so callers like SideInterestRailService can re-balance.
            if (combined[i].FeelSeedIds is { } feel)
            {
                Assert.Equal([1L, 2L, 3L], feel);
            }
        }
    }

    [Fact]
    public void BalanceSlice_matches_Balance_after_suppressing_earlier_page_items()
    {
        var seeds = Enumerable.Range(1, 4).Select(i => new ReasonSeed($"{i}", $"Seed {i}", null)).ToArray();
        var suppressed = new HashSet<long> { 1, 2, 5, 8, 12 };
        var hydrated = Enumerable.Range(1, 50)
            .Where(i => !suppressed.Contains(i))
            .Select(i => Feel(i, seeds[0], seeds[1], seeds[2]))
            .ToList();
        var compact = Enumerable.Range(1, 50)
            .Where(i => !suppressed.Contains(i))
            .Select(i => new RankedRecommendationPick(
                SeriesId: i,
                FallbackRating: 80,
                MatchedGenres: [],
                CandidateTags: [],
                AuthorMatch: false,
                CoRecommended: false,
                CoRead: false,
                TasteMatch: false,
                FeelSeedIds: [1L, 2L, 3L]))
            .ToList();

        var full = RecommendationReasons.Balance(hydrated);
        var page1 = RecommendationReasons.BalanceSlice(compact, skip: 18, take: 20);

        Assert.Equal(
            full.Skip(18).Take(20).Select(p => p.BecauseOfId),
            page1.Select(p => p.BalancedFeelSeedId?.ToString()));
    }
}
