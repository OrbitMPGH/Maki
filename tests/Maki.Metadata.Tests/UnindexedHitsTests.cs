using Maki.Core.Recommendations;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Xunit;

namespace Maki.Metadata.Tests;

/// <summary>
/// Series the dump has and the vector index does not. The index is <see cref="CatalogueRuleTests"/>'s,
/// whose tag vocabulary has School with College and High School under it.
/// </summary>
public class UnindexedHitsTests
{
    private static readonly VectorIndex Index = CatalogueRuleTests.BuildIndex();

    private static readonly string[] UpToErotica = ["safe", "suggestive", "erotica"];

    private static DumpDbBuilder Dump() => new DumpDbBuilder()
        .AddSeries(1, "Safe Series", contentRating: "safe", popularity: 50)
        .AddSeries(2, "Erotica Series", contentRating: "erotica", popularity: 10)
        .AddSeries(3, "Pornographic Series", contentRating: "pornographic", popularity: 5)
        .AddSeries(4, "A Light Novel", type: "novel", contentRating: "safe", popularity: 1)
        .AddSeries(5, "Merged Series", state: "merged", contentRating: "safe", popularity: 2)
        .AddSeries(6, "Unranked Series", contentRating: "safe")
        .AddSeries(7, "Campus Romance", contentRating: "safe", genresJson: """["Romance"]""",
            tagsV2Json: """[{"id":2,"name":"College","weight":"incidental"}]""")
        .AddSeries(8, "Office Story", contentRating: "safe", genresJson: """["Drama"]""",
            tagsV2Json: """[{"id":9001,"name":"Office Workers","weight":"core"}]""");

    private static async Task<List<long>> Visible(DumpDbBuilder dump, IReadOnlyList<long> ids, RecommendationFilters? filters) =>
        (await UnindexedHits.VisibleAsync(dump.Path, Index, ids, filters, CancellationToken.None))
        .Select(hit => hit.Id)
        .ToList();

    private static RecommendationFilters Hiding(params CatalogueTerm[] terms) =>
        new(ContentRatings: UpToErotica, Hidden: terms);

    [Fact]
    public async Task Visible_keeps_active_non_novel_series_at_an_allowed_rating_in_the_order_given()
    {
        using var dump = Dump();

        var hits = await UnindexedHits.VisibleAsync(
            dump.Path, Index, [6L, 3L, 2L, 4L, 5L, 1L, 99L], new RecommendationFilters(ContentRatings: UpToErotica),
            CancellationToken.None);

        // 3 is above the ceiling, 4 is a novel, 5 is not active, 99 does not exist.
        Assert.Equal([6L, 2L, 1L], hits.Select(h => h.Id));
        Assert.Equal([VectorIndex.Unknown, 10, 50], hits.Select(h => h.Popularity));
    }

    [Fact]
    public async Task Visible_takes_the_default_ceiling_without_a_rating_list_and_nothing_with_an_empty_one()
    {
        using var dump = Dump();

        Assert.Equal([1L, 2L], await Visible(dump, [1L, 2L, 3L], null));
        Assert.Empty(await Visible(dump, [1L, 2L, 3L], new RecommendationFilters(ContentRatings: [])));
    }

    [Fact]
    public async Task Visible_stays_inside_the_creator_scope()
    {
        using var dump = Dump();

        Assert.Equal(
            [2L],
            await Visible(dump, [1L, 2L], new RecommendationFilters(ContentRatings: UpToErotica, CreditIds: [2L])));
    }

    [Fact]
    public async Task Visible_honours_the_never_show_list()
    {
        using var dump = Dump();
        long[] ids = [7L, 8L, 1L];

        Assert.Equal([8L, 1L], await Visible(dump, ids, Hiding(new CatalogueTerm(CatalogueRules.Genre, "romance"))));
        Assert.Equal([7L, 8L, 1L], await Visible(dump, ids, Hiding(new CatalogueTerm(CatalogueRules.Tag, "School"))));
        Assert.Equal(
            [8L, 1L],
            await Visible(dump, ids, Hiding(new CatalogueTerm(CatalogueRules.Tag, "School", Subtags: true))));
        Assert.Equal(
            [7L, 8L, 1L],
            await Visible(dump, ids, Hiding(new CatalogueTerm(CatalogueRules.Tag, "College", Central: true))));
        // A tag the index has never seen still hides by name.
        Assert.Equal([7L, 1L], await Visible(dump, ids, Hiding(new CatalogueTerm(CatalogueRules.Tag, "Office Workers"))));
    }

    [Fact]
    public void Only_preference_filters_count_as_narrowing()
    {
        Assert.False(UnindexedHits.NarrowsByPreference(null));
        Assert.False(UnindexedHits.NarrowsByPreference(RecommendationFilters.None));
        Assert.False(UnindexedHits.NarrowsByPreference(new RecommendationFilters(
            ContentRatings: ["safe"], Hidden: [new CatalogueTerm(CatalogueRules.Genre, "Romance")], CreditIds: [1L])));
        Assert.True(UnindexedHits.NarrowsByPreference(new RecommendationFilters(YearMin: 2020)));
        Assert.True(UnindexedHits.NarrowsByPreference(new RecommendationFilters(Genres: ["Action"])));
        Assert.True(UnindexedHits.NarrowsByPreference(new RecommendationFilters(
            Rules: [new CatalogueRule(CatalogueRules.Any, [new CatalogueTerm(CatalogueRules.Tag, "School")])])));
    }

    [Fact]
    public void Merge_puts_unindexed_titles_in_their_title_tier_and_word_hits_by_score()
    {
        // Rows 0..2 are ids 100..102. 100 is an exact title, the other two only scored.
        var fused = new Dictionary<int, double> { [0] = 0.01, [1] = 0.05, [2] = 0.02 };
        var exact = new HashSet<long> { 100, 900 };
        var near = new Dictionary<long, int> { [901] = 1 };
        IReadOnlyList<SemanticSearcher.UnindexedCandidate> unindexed =
        [
            new(900, 0, 0.001),
            new(901, 1, 0.001),
            new(902, null, 0.03),
            new(903, null, 0.001),
        ];

        var merged = SemanticSearcher.MergeUnindexed(Index, [100L, 101L, 102L], fused, exact, near, unindexed, 10);

        Assert.Equal([100L, 900L, 901L, 101L, 902L, 102L, 903L], merged);
        Assert.Equal(
            [100L, 900L, 901L],
            SemanticSearcher.MergeUnindexed(Index, [100L, 101L, 102L], fused, exact, near, unindexed, 3));
    }

    [Fact]
    public void A_creators_unindexed_works_join_the_popularity_order()
    {
        // Every row of this index has popularity 1000.
        IReadOnlyList<UnindexedHits.Hit> unindexed =
            [new(900, 5), new(901, 2000), new(902, VectorIndex.Unknown)];

        var merged = SemanticSearcher.MergeByPopularity(Index, [100L, 101L], unindexed, 10);

        Assert.Equal([900L, 100L, 101L, 901L, 902L], merged);
    }
}
