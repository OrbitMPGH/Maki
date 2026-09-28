using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Recommendations;
using Maki.Metadata.MangaBaka;

namespace Maki.Api.Tests;

/// <summary>
/// <see cref="LibraryRailFilter"/>: a catalogue filter evaluated against a library series' own
/// metadata rather than the vector index.
/// </summary>
public class LibraryRailFilterTests
{
    private static LibraryRailRow Row(
        int id = 1, string title = "Series", string[]? genres = null, string[]? tags = null,
        string? contentRating = "safe", int? year = 2020, SeriesStatus status = SeriesStatus.Ongoing,
        string? type = "manga", int? totalChapters = 50, DateTime? added = null, long? mangaBakaId = null) =>
        new(id, mangaBakaId, title, title.ToLowerInvariant(), genres ?? [], tags ?? [], contentRating,
            year, status, type, totalChapters, added ?? DateTime.UtcNow);

    [Fact]
    public void A_null_content_rating_fails_a_filter_that_constrains_it()
    {
        var row = Row(contentRating: null);

        Assert.False(LibraryRailFilter.MatchesLocal(row, new RecommendationFilters(ContentRatings: ["safe"])));
    }

    [Fact]
    public void An_unconstrained_filter_matches_a_null_content_rating()
    {
        Assert.True(LibraryRailFilter.MatchesLocal(Row(contentRating: null), RecommendationFilters.None));
    }

    [Fact]
    public void Legacy_genres_and_tags_apply_as_an_implicit_all_rule()
    {
        var row = Row(genres: ["Action", "Drama"], tags: ["Isekai"]);

        Assert.True(LibraryRailFilter.MatchesLocal(row, new RecommendationFilters(Genres: ["Action", "Drama"])));
        Assert.False(LibraryRailFilter.MatchesLocal(row, new RecommendationFilters(Genres: ["Action", "Comedy"])));
        Assert.True(LibraryRailFilter.MatchesLocal(row, new RecommendationFilters(Tags: ["Isekai"])));
        Assert.False(LibraryRailFilter.MatchesLocal(row, new RecommendationFilters(Tags: ["Isekai", "Reverse Isekai"])));
    }

    [Fact]
    public void An_any_rule_matches_when_a_single_term_is_held()
    {
        var row = Row(genres: ["Romance"]);
        var filters = new RecommendationFilters(Rules:
        [
            new CatalogueRule(CatalogueRules.Any,
                [new CatalogueTerm(CatalogueRules.Genre, "Romance"), new CatalogueTerm(CatalogueRules.Genre, "Comedy")]),
        ]);

        Assert.True(LibraryRailFilter.MatchesLocal(row, filters));
    }

    [Fact]
    public void A_none_rule_excludes_a_row_carrying_any_of_its_terms()
    {
        var filters = new RecommendationFilters(Rules:
        [
            new CatalogueRule(CatalogueRules.None, [new CatalogueTerm(CatalogueRules.Genre, "Romance")]),
        ]);

        Assert.False(LibraryRailFilter.MatchesLocal(Row(genres: ["Romance"]), filters));
        Assert.True(LibraryRailFilter.MatchesLocal(Row(genres: ["Action"]), filters));
    }

    [Fact]
    public void Statuses_are_mapped_through_the_provider_before_comparing()
    {
        var row = Row(status: SeriesStatus.Ongoing);

        Assert.True(LibraryRailFilter.MatchesLocal(row, new RecommendationFilters(Statuses: ["releasing"])));
        Assert.False(LibraryRailFilter.MatchesLocal(row, new RecommendationFilters(Statuses: ["completed"])));
    }

    [Fact]
    public void Order_by_title_uses_sort_title_case_insensitively()
    {
        var rows = new[] { Row(id: 1, title: "Zeta"), Row(id: 2, title: "alpha") };

        var ordered = LibraryRailFilter.Order(rows, CustomRailSorts.Title, new Dictionary<int, DateTime>(), _ => null);

        Assert.Equal([2, 1], ordered);
    }

    [Fact]
    public void Order_by_read_prefers_the_most_recently_read_then_falls_back_to_added()
    {
        var now = DateTime.UtcNow;
        var rows = new[]
        {
            Row(id: 1, added: now.AddDays(-1)),
            Row(id: 2, added: now.AddDays(-2)),
            Row(id: 3, added: now.AddDays(-3)),
        };
        var lastRead = new Dictionary<int, DateTime> { [2] = now };

        var ordered = LibraryRailFilter.Order(rows, CustomRailSorts.Read, lastRead, _ => null);

        Assert.Equal([2, 1, 3], ordered);
    }

    [Fact]
    public void Order_by_popular_ranks_ascending_by_popularity_then_by_title_on_a_tie()
    {
        var rows = new[] { Row(id: 1, title: "B"), Row(id: 2, title: "A"), Row(id: 3, title: "C") };
        var popularity = new Dictionary<int, int?> { [1] = 5, [2] = 5, [3] = 1 };

        var ordered = LibraryRailFilter.Order(
            rows, CustomRailSorts.Popular, new Dictionary<int, DateTime>(), r => popularity[r.Id]);

        Assert.Equal([3, 2, 1], ordered);
    }

    [Fact]
    public void Order_by_added_puts_the_newest_first()
    {
        var now = DateTime.UtcNow;
        var rows = new[] { Row(id: 1, added: now.AddDays(-2)), Row(id: 2, added: now) };

        var ordered = LibraryRailFilter.Order(rows, CustomRailSorts.Added, new Dictionary<int, DateTime>(), _ => null);

        Assert.Equal([2, 1], ordered);
    }

    [Fact]
    public void Order_defaults_to_added_descending_for_a_null_or_unknown_sort()
    {
        var now = DateTime.UtcNow;
        var rows = new[] { Row(id: 1, added: now.AddDays(-2)), Row(id: 2, added: now) };

        Assert.Equal([2, 1], LibraryRailFilter.Order(rows, null, new Dictionary<int, DateTime>(), _ => null));
        Assert.Equal([2, 1], LibraryRailFilter.Order(rows, "not-a-sort", new Dictionary<int, DateTime>(), _ => null));
    }
}
