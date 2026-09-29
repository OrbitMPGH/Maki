using Maki.Metadata.MangaBaka;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Maki.Metadata.Tests;

public class RecommendationFiltersTests
{
    private static (string Clause, SqliteParameterCollection Params) Build(RecommendationFilters f)
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        var cmd = conn.CreateCommand();
        var clause = f.BuildClause(cmd, "d");
        return (clause, cmd.Parameters);
    }

    [Fact]
    public void None_ProducesEmptyClause()
    {
        var (clause, ps) = Build(RecommendationFilters.None);
        Assert.Equal(string.Empty, clause);
        Assert.Empty(ps);
    }

    [Fact]
    public void YearAndRating_ProduceComparisons()
    {
        var (clause, ps) = Build(new RecommendationFilters(YearMin: 2010, YearMax: 2020, MinRating: 70));
        Assert.Contains("d.year >= $f_ymin", clause);
        Assert.Contains("d.year <= $f_ymax", clause);
        Assert.Contains("d.rating >= $f_mr", clause);
        Assert.Equal(2010, ps["$f_ymin"].Value);
        Assert.Equal(2020, ps["$f_ymax"].Value);
        Assert.Equal(70d, ps["$f_mr"].Value);
    }

    [Fact]
    public void Types_ProduceParameterizedInClause()
    {
        var (clause, ps) = Build(new RecommendationFilters(Types: ["manga", "manhwa"]));
        Assert.Contains("d.type IN ($f_t0,$f_t1)", clause);
        Assert.Equal("manga", ps["$f_t0"].Value);
        Assert.Equal("manhwa", ps["$f_t1"].Value);
    }

    [Fact]
    public void Statuses_ProduceParameterizedInClause()
    {
        var (clause, ps) = Build(new RecommendationFilters(Statuses: ["releasing"]));
        Assert.Contains("d.status IN ($f_s0)", clause);
        Assert.Equal("releasing", ps["$f_s0"].Value);
    }

    [Fact]
    public void EmptyLists_AreIgnored()
    {
        var (clause, _) = Build(new RecommendationFilters(Types: [], Statuses: []));
        Assert.Equal(string.Empty, clause);
    }

    [Fact]
    public void Clause_StartsWithAndSoItSplicesOntoWhere()
    {
        var (clause, _) = Build(new RecommendationFilters(MinRating: 50));
        Assert.StartsWith(" AND ", clause);
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

    /// <summary>Executes the clause over a four-row table, so the SQL itself is under test.</summary>
    private static List<long> Run(RecommendationFilters f)
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using (var setup = conn.CreateCommand())
        {
            setup.CommandText = "CREATE TABLE d (id INTEGER); INSERT INTO d VALUES (1), (2), (3), (4);";
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
