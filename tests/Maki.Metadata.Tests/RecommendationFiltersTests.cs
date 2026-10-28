using Maki.Core.Recommendations;
using Maki.Metadata.MangaBaka;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Maki.Metadata.Tests;

public class RecommendationFiltersTests
{
    [Fact]
    public void No_filters_keep_every_row()
    {
        Assert.Equal([1L, 2L, 3L, 4L], Run(RecommendationFilters.None));
    }

    [Fact]
    public void Year_and_rating_bounds_are_inclusive()
    {
        Assert.Equal([2L, 3L], Run(new RecommendationFilters(YearMin: 2010, YearMax: 2020, MinRating: 70)));
    }

    [Fact]
    public void Types_and_statuses_keep_any_listed_value()
    {
        Assert.Equal([1L, 2L, 3L], Run(new RecommendationFilters(Types: ["manga", "manhwa"])));
        Assert.Equal([2L, 3L], Run(new RecommendationFilters(Statuses: ["releasing"])));
    }

    [Fact]
    public void Empty_type_and_status_lists_filter_nothing()
    {
        Assert.Equal([1L, 2L, 3L, 4L], Run(new RecommendationFilters(Types: [], Statuses: [])));
    }

    [Fact]
    public void CreditIds_narrow_the_scan_to_those_series()
    {
        Assert.Equal([2L, 4L], Run(new RecommendationFilters(CreditIds: [2, 4, 99])));
    }

    [Fact]
    public void Empty_CreditIds_match_nothing()
    {
        Assert.Empty(Run(new RecommendationFilters(CreditIds: [])));
    }

    [Fact]
    public void FromSpec_carries_the_credits()
    {
        var filters = RecommendationFilters.FromSpec(
            new Maki.Core.Configuration.SearchDefaultsSpec(Credits: [new("Junji Ito", "author")]));

        Assert.Equal([new Maki.Core.Recommendations.CatalogueCredit("Junji Ito", "author")], filters.Credits!);
        Assert.Null(filters.CreditIds);
    }

    [Fact]
    public void MatchesNames_applies_all_any_and_none_rules_to_plain_name_lists()
    {
        static CatalogueTerm Genre(string name) => new(CatalogueRules.Genre, name);
        static CatalogueTerm Tag(string name) => new(CatalogueRules.Tag, name);
        var filters = new RecommendationFilters(Rules:
        [
            new CatalogueRule(CatalogueRules.All, [Genre("Action")]),
            new CatalogueRule(CatalogueRules.Any, [Tag("Isekai"), Tag("Revenge")]),
            new CatalogueRule(CatalogueRules.None, [Tag("Harem")]),
        ]);

        Assert.True(filters.MatchesNames(["action", "Comedy"], ["REVENGE"]));
        Assert.False(filters.MatchesNames(["Comedy"], ["Isekai"]));
        Assert.False(filters.MatchesNames(["Action"], ["Slice of Life"]));
        Assert.False(filters.MatchesNames(["Action"], ["Isekai", "Harem"]));
    }

    [Fact]
    public void MatchesNames_rejects_anything_on_the_hidden_list()
    {
        var filters = new RecommendationFilters(
            Hidden: [new CatalogueTerm(CatalogueRules.Genre, "Horror"), new CatalogueTerm(CatalogueRules.Tag, "Gore")]);

        Assert.True(filters.MatchesNames(["Action"], ["Isekai"]));
        Assert.False(filters.MatchesNames(["horror"], []));
        Assert.False(filters.MatchesNames([], ["gore"]));
    }

    /// <summary>Executes the clause over a four-row table, so the SQL itself is under test.</summary>
    private static List<long> Run(RecommendationFilters f)
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using (var setup = conn.CreateCommand())
        {
            setup.CommandText = """
                CREATE TABLE d (id INTEGER, year INTEGER, rating REAL, type TEXT, status TEXT);
                INSERT INTO d VALUES
                    (1, 2005, 60, 'manga', 'completed'),
                    (2, 2010, 70, 'manhwa', 'releasing'),
                    (3, 2020, 80, 'manga', 'releasing'),
                    (4, 2024, 90, 'manhua', 'hiatus');
                """;
            setup.ExecuteNonQuery();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM d WHERE 1 = 1" + f.BuildClause(cmd, "d") + " ORDER BY id";
        var ids = new List<long>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }
}
