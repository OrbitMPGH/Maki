using Maki.Metadata.MangaBaka;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Maki.Metadata.Tests;

/// <summary>
/// Which series are the same work. The definition matters more than usual because two things read
/// it: the ranker's collapse and the eval's franchise metric. If they disagreed, the number that
/// measures the problem could not see the fix.
/// </summary>
public class FranchiseGraphTests : IDisposable
{
    private readonly DumpDbBuilder _dump = new();

    public void Dispose()
    {
        _dump.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void JoinsSequelsIntoOneComponent()
    {
        _dump.AddSeries(1, "Volume One", relationshipsV2: Relation("sequel", 2));
        _dump.AddSeries(2, "Volume Two");
        _dump.AddSeries(3, "Something Else");

        var components = Build();

        Assert.Equal(components[1], components[2]);
        Assert.False(components.ContainsKey(3));
    }

    [Fact]
    public void JoinsTransitively_ThroughASeriesTheIndexNeverCarries()
    {
        // A trilogy linked 1 to 2 to 3 has to come back as one component even when the middle
        // volume is a novel, inactive, or otherwise never embedded. Unioning only over indexed rows
        // would split it and leak exactly the rows the collapse exists to catch.
        _dump.AddSeries(1, "First", relationshipsV2: Relation("sequel", 2));
        _dump.AddSeries(2, "Middle", relationshipsV2: Relation("sequel", 3));
        _dump.AddSeries(3, "Last");

        var components = Build();

        Assert.Equal(components[1], components[3]);
    }

    [Fact]
    public void ReadsTheLegacyFlatColumnsToo()
    {
        // relationships_v2 covers 15% of the dump; the flat columns cover rows it does not.
        _dump.AddSeries(1, "First", sequels: "[2]");
        _dump.AddSeries(2, "Second");

        var components = Build();

        Assert.Equal(components[1], components[2]);
    }

    [Theory]
    [InlineData("adaptation")]
    [InlineData("source")]
    [InlineData("parody")]
    [InlineData("alternative")]
    public void IgnoresRelationsThatAreADifferentReadingExperience(string relation)
    {
        // A manga adapted from the same novel is a different work to read, and suppressing it would
        // make the recommender worse rather than tidier.
        _dump.AddSeries(1, "The Novel", relationshipsV2: Relation(relation, 2));
        _dump.AddSeries(2, "The Manga");

        Assert.Empty(Build());
    }

    [Fact]
    public void ComponentIdsAreStableAcrossBuilds()
    {
        // RecommendationService caches a pool for twelve hours against a key that says nothing about
        // franchises, so two builds over the same dump answering differently would make that key a
        // lie. The union-find picks the smaller id as the representative for this reason.
        _dump.AddSeries(1, "A", relationshipsV2: Relation("sequel", 2));
        _dump.AddSeries(2, "B");
        _dump.AddSeries(3, "C", relationshipsV2: Relation("prequel", 4));
        _dump.AddSeries(4, "D");

        Assert.Equal(Build(), Build());
    }

    [Fact]
    public void SurvivesAMalformedBlob()
    {
        _dump.AddSeries(1, "Broken", relationshipsV2: "{not json");
        _dump.AddSeries(2, "Fine", relationshipsV2: Relation("sequel", 3));
        _dump.AddSeries(3, "Also Fine");

        var components = Build();

        // One bad row is a dump defect, not a reason to lose the rest of the graph.
        Assert.Equal(components[2], components[3]);
        Assert.False(components.ContainsKey(1));
    }

    private Dictionary<long, int> Build()
    {
        using var conn = new SqliteConnection($"Data Source={_dump.Path};Mode=ReadOnly;Pooling=False");
        conn.Open();
        return FranchiseGraph.Build(conn);
    }

    private static string Relation(string type, long to) =>
        $$"""[{"relation_type": "{{type}}", "to_series_id": {{to}}}]""";
}
