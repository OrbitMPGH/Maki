using Maki.Metadata.MangaBaka;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Maki.Metadata.Tests;

/// <summary>
/// The search index is built in rowid ranges so the indexing bar can move. These pin that the
/// ranges cover every row, including ids far apart, and that the reported share only climbs to 1.
/// </summary>
public class MangaBakaSearchIndexProgressTests : IDisposable
{
    private readonly DumpDbBuilder _db = new();

    public void Dispose() => _db.Dispose();

    private List<long> Search(string term)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT series_id FROM {MangaBakaDumpService.SearchTableName} WHERE title MATCH $q ORDER BY series_id";
        cmd.Parameters.AddWithValue("$q", term);
        using var reader = cmd.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    [Fact]
    public void Every_rangeIsIndexed_whenIdsSpanSeveralChunks()
    {
        _db.AddSeries(1, "Berserk")
            .AddSeries(24_999, "Berserk Gaiden")
            .AddSeries(25_000, "Berserk Origins", nativeTitle: "ベルセルク")
            .AddSeries(25_001, "Berserk Rage")
            .AddSeries(130_000, "Berserk Finale", titlesJson: """[{"title": "Guts Story", "language": "en"}]""")
            .BuildSearchIndex();

        Assert.Equal([1L, 24_999, 25_000, 25_001, 130_000], Search("berserk"));
        Assert.Equal([25_000L], Search("ベルセルク"));
        Assert.Equal([130_000L], Search("guts"));
    }

    [Fact]
    public void Progress_climbsToOne()
    {
        _db.AddSeries(1, "Berserk").AddSeries(60_000, "Vagabond").AddSeries(120_000, "Monster");

        var reports = new List<double>();
        using (var conn = _db.Open())
        {
            MangaBakaDumpService.BuildSearchIndex(conn, reports.Add);
        }

        Assert.True(reports.Count > 4, $"only {reports.Count} reports");
        Assert.All(reports.Zip(reports.Skip(1)), pair => Assert.True(pair.Second >= pair.First));
        Assert.Equal(1, reports[^1]);
    }

    [Fact]
    public void Scanned_pagesAreDropped_everyFewRanges()
    {
        _db.AddSeries(1, "Berserk").AddSeries(60_000, "Vagabond").AddSeries(120_000, "Monster");

        var drops = 0;
        using (var conn = _db.Open())
        {
            MangaBakaDumpService.BuildSearchIndex(conn, dropScanned: () => drops++);
        }

        Assert.Equal(12 / MangaBakaDumpService.DropEveryChunks, drops);
        Assert.Equal([120_000L], Search("monster"));
    }

    [Fact]
    public void An_emptyTable_stillFinishes()
    {
        var reports = new List<double>();
        using (var conn = _db.Open())
        {
            MangaBakaDumpService.BuildSearchIndex(conn, reports.Add);
        }

        Assert.Equal([1.0], reports);
        Assert.Empty(Search("berserk"));
    }
}
