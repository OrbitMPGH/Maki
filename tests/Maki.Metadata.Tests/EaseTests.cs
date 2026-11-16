using Maki.Metadata.Ease;
using Maki.Metadata.Taste;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Maki.Metadata.Tests;

public class EaseTests : IDisposable
{
    private readonly string _dir;

    public EaseTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "maki-ease-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void Score_SumsSignedWeightsAcrossSeeds_AndScalesTheBestToOne()
    {
        var table = EaseTable.Build([(1, 10, 0.4f), (2, 10, 0.4f), (1, 11, 0.2f), (2, 12, -0.2f)]);

        var scores = EaseScorer.Score(table, [1, 2], null, EaseTuning.Default);

        Assert.Equal(1.0, scores[10], 6);
        Assert.Equal(0.25, scores[11], 6);
        Assert.Equal(-0.25, scores[12], 6);
    }

    [Fact]
    public void Score_ReadsTheTableInTheSeedsDirectionOnly()
    {
        var table = EaseTable.Build([(1, 10, 0.4f), (10, 2, 0.9f)]);

        var scores = EaseScorer.Score(table, [1], null, EaseTuning.Default);

        Assert.Equal([10L], scores.Keys);
    }

    [Fact]
    public void Score_NeverReturnsASeed_AndAppliesSeedWeights()
    {
        var table = EaseTable.Build([(1, 2, 0.5f), (1, 10, 0.1f), (2, 11, 0.1f)]);

        var scores = EaseScorer.Score(table, [1, 2], new Dictionary<long, double> { [1] = 3.0 }, EaseTuning.Default);

        Assert.DoesNotContain(2L, scores.Keys);
        Assert.Equal(1.0, scores[10], 6);
        Assert.Equal(1.0 / 3, scores[11], 6);
    }

    [Fact]
    public void Score_FloorsAtMinusOne_AndCanDropNegatives()
    {
        var table = EaseTable.Build([(1, 10, 0.1f), (1, 11, -0.5f)]);

        Assert.Equal(-1.0, EaseScorer.Score(table, [1], null, EaseTuning.Default)[11]);
        Assert.DoesNotContain(11L, EaseScorer.Score(table, [1], null, EaseTuning.Default with { KeepNegative = false }).Keys);
    }

    [Fact]
    public void Score_IsEmpty_WhenNoSeedIsInTheTable_OrNothingSumsAboveZero()
    {
        var table = EaseTable.Build([(1, 10, -0.1f)]);

        Assert.Empty(EaseScorer.Score(table, [5], null, EaseTuning.Default));
        Assert.Empty(EaseScorer.Score(table, [1], null, EaseTuning.Default));
    }

    [Fact]
    public async Task NoFile_OrAnUnreadableOne_IsNoTable()
    {
        var corrupt = Path.Combine(_dir, "corrupt.db");
        await File.WriteAllTextAsync(corrupt, "not a database");

        Assert.Null(await new EaseCache(new EaseOptions(Path.Combine(_dir, "absent.db")), NullLogger<EaseCache>.Instance).GetAsync());
        Assert.Null(await new EaseCache(new EaseOptions(corrupt), NullLogger<EaseCache>.Instance).GetAsync());
    }

    [Fact]
    public void Build_SortsUnorderedRows_AndKeepsTheFirstOfADuplicatePair()
    {
        var table = EaseTable.Build([(2, 20, 0.1f), (1, 11, 0.3f), (1, 10, 0.2f), (1, 11, 0.9f), (3, 3, 1f), (2, 21, 0f)]);

        Assert.Equal(2, table.Count);
        Assert.Equal(3, table.WeightCount);
        Assert.True(table.TryGetSeed(1, out var seed));
        Assert.Equal([10L, 11L], table.TargetsAt(seed).ToArray());
        Assert.Equal(0.3f, table.WeightOf(seed, 11));
        Assert.Equal(0f, table.WeightOf(seed, 99));
    }

    [Fact]
    public void SeedsFor_NamesOnlySeedsThatPushAPickUp_StrongestWeightedFirst()
    {
        var table = EaseTable.Build([(1, 10, 0.2f), (2, 10, 0.3f), (3, 10, -0.5f), (1, 11, 0.1f)]);

        var plain = EaseScorer.SeedsFor(table, [1, 2, 3], null, new HashSet<long> { 10, 11 });
        var weighted = EaseScorer.SeedsFor(
            table, [1, 2, 3], new Dictionary<long, double> { [1] = 2.0 }, new HashSet<long> { 10 });

        Assert.Equal([2L, 1L], plain[10]);
        Assert.Equal([1L], plain[11]);
        Assert.Equal([1L, 2L], weighted[10]);
    }

    [Fact]
    public async Task TheCache_ReloadsFromItsSnapshot_AndUnloadsWhenIdle()
    {
        var path = Path.Combine(_dir, "ease-table.db");
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE weight (seed_id INTEGER, item_id INTEGER, weight REAL, PRIMARY KEY (seed_id, item_id)) WITHOUT ROWID;
                CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO weight VALUES (1, 10, 0.4), (1, 11, -0.2), (2, 10, 0.1);
                INSERT INTO meta VALUES ('generatedAt', '2026-11-15T20:39:26.958305+00:00');
                """;
            cmd.ExecuteNonQuery();
        }

        var first = new EaseCache(new EaseOptions(path), NullLogger<EaseCache>.Instance);
        var built = await first.GetAsync();
        Assert.NotNull(built);
        await first.SnapshotSavedForTest;
        Assert.True(File.Exists(first.SnapshotPath));
        Assert.True(first.IsLoaded);
        Assert.True(first.ReleaseIfIdle(TimeSpan.Zero));
        Assert.False(first.IsLoaded);

        var second = new EaseCache(new EaseOptions(path), NullLogger<EaseCache>.Instance);
        var reloaded = await second.GetAsync();
        Assert.NotNull(reloaded);
        Assert.Equal(built.WeightCount, reloaded.WeightCount);
        Assert.Equal(built.GeneratedAt, reloaded.GeneratedAt);
        Assert.Equal(
            EaseScorer.Score(built, [1, 2], null, EaseTuning.Default),
            EaseScorer.Score(reloaded, [1, 2], null, EaseTuning.Default));

        second.Invalidate();
        Assert.False(File.Exists(second.SnapshotPath));
        reloaded = null;
        built = null;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void TheShippedWeight_IsFour()
    {
        Assert.Equal(4, EaseTuning.Default.Weight);
    }

    [Fact]
    public void TheFoldInSolver_SolvesASymmetricPositiveDefiniteSystem()
    {
        double[] a = [4, 2, 0, 2, 5, 1, 0, 1, 3];
        double[] b = [2, 1, 1];

        Assert.True(TasteFoldIn.CholeskySolve(a, b, 3));

        double[] original = [4, 2, 0, 2, 5, 1, 0, 1, 3];
        for (var i = 0; i < 3; i++)
        {
            var row = (original[i * 3] * b[0]) + (original[(i * 3) + 1] * b[1]) + (original[(i * 3) + 2] * b[2]);
            Assert.Equal(new[] { 2.0, 1, 1 }[i], row, 9);
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
