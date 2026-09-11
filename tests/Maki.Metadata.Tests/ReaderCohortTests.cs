using Maki.Metadata.ReaderCohorts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Maki.Metadata.Tests;

/// <summary>
/// The reader-cohort artifact: what groups of AniList readers finished and scored. The tests that
/// matter here are the ones pinning what happens when the file is absent, partly wrong, or written
/// by something that numbered its cohorts differently — because every one of those states loads
/// without complaint under a naive reader and silently answers with the wrong group.
/// </summary>
public class ReaderCohortTests : IDisposable
{
    private readonly string _dir;

    public ReaderCohortTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "maki-cohorts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public async Task NoFile_IsNotAnError_ItIsJustNoCohorts()
    {
        // The state every install starts in and most stay in: nothing published, so the hint does
        // not render, the rail returns nothing, and the taste page keeps its popularity proxy.
        var cache = new ReaderCohortCache(
            new ReaderCohortOptions(Path.Combine(_dir, "nothing-here.db"), _dir),
            NullLogger<ReaderCohortCache>.Instance);

        Assert.Null(await cache.GetAsync());
    }

    [Fact]
    public async Task AnUnreadableFile_DegradesToNoCohorts()
    {
        var path = Path.Combine(_dir, "corrupt.db");
        await File.WriteAllTextAsync(path, "this is not a database");

        Assert.Null(await Cache(path).GetAsync());
    }

    [Fact]
    public async Task RowsLoadAgainstTheCohortTheyName()
    {
        var index = await Load(
            cohorts: [(0, 100), (1, 300)],
            global: [(10L, 40, 20, 70.0)],
            cells: [(10L, 0, 30, 15, 80.0), (10L, 1, 10, 5, 60.0)]);

        Assert.Equal(2, index!.CohortCount);
        Assert.Equal(400, index.TotalReaders);
        Assert.True(index.TryGetSlot(10, out var slot));

        var byCohort = index.EntriesAt(slot).ToDictionary(e => e.Cohort);
        Assert.Equal(30, byCohort[0].Completions);
        Assert.Equal(80.0f, byCohort[0].Mean);
        Assert.Equal(60.0f, byCohort[1].Mean);
    }

    /// <summary>
    /// The global rate is what every lift divides by, and it is a share of ALL readers rather than
    /// of the cohort that happens to be looking. Getting this denominator wrong would make a series
    /// look rare to a small cohort and common to a large one.
    /// </summary>
    [Fact]
    public async Task GlobalRateDividesByEveryReader()
    {
        var index = await Load(
            cohorts: [(0, 100), (1, 300)],
            global: [(10L, 40, 20, 70.0)],
            cells: [(10L, 0, 30, 15, 80.0)]);

        Assert.True(index!.TryGetSlot(10, out var slot));
        Assert.Equal(40 / 400.0, index.GlobalRateAt(slot), 6);
    }

    /// <summary>
    /// A NULL mean is a legitimate state — finished often enough to count, rated too rarely to
    /// average — and must arrive as "no mean" rather than as a zero somebody would render as 0.0.
    /// </summary>
    [Fact]
    public async Task AMissingMeanIsNullRatherThanZero()
    {
        var index = await Load(
            cohorts: [(0, 100)],
            global: [(10L, 40, 0, null)],
            cells: [(10L, 0, 30, 0, null)]);

        Assert.True(index!.TryGetSlot(10, out var slot));
        Assert.Null(index.GlobalMeanAt(slot));
        Assert.Null(index.EntryAt(slot, 0)!.Value.Mean);
        Assert.Equal(30, index.EntryAt(slot, 0)!.Value.Completions);
    }

    /// <summary>
    /// The serving side indexes its per-cohort weight array by the row's own cohort column, so a
    /// row naming a cohort the table never listed would read past the end of that array. Dropping
    /// it is the only safe answer, and it must not take the rest of the file with it.
    /// </summary>
    [Fact]
    public async Task ARowNamingAnUnlistedCohortIsDropped_AndTheRestSurvives()
    {
        var index = await Load(
            cohorts: [(0, 100)],
            global: [(10L, 40, 20, 70.0), (11L, 5, 5, 65.0)],
            cells: [(10L, 0, 30, 15, 80.0), (11L, 7, 4, 4, 50.0)]);

        Assert.Equal(1, index!.EntryCount);
        Assert.True(index.TryGetSlot(11, out var orphan));
        Assert.Empty(index.EntriesAt(orphan));

        // Dropped from the cohort side, still present on the global side: the taste page's baseline
        // has its own floor and does not care which cohorts cleared theirs.
        Assert.Equal(5, index.GlobalCompletionsAt(orphan));
    }

    /// <summary>
    /// Cohort ids are positional: row `cohort = 2` means "the third row of the cohort table". A
    /// file whose ids are not 0..n-1 would silently attribute every row to the wrong group, which
    /// is worse than having no cohorts at all.
    /// </summary>
    [Fact]
    public async Task NonContiguousCohortIdsRejectTheWholeFile()
    {
        var index = await Load(
            cohorts: [(0, 100), (5, 300)],
            global: [(10L, 40, 20, 70.0)],
            cells: [(10L, 0, 30, 15, 80.0)]);

        Assert.Null(index);
    }

    /// <summary>
    /// The id space is the union of both tables, because neither is a subset of the other: the
    /// taste page reads global rows for series no cohort cleared its floor on, and the rail reads
    /// cohort rows for series the global floor happened to admit.
    /// </summary>
    [Fact]
    public void TheIdSpaceIsTheUnionOfBothTables()
    {
        var index = ReaderCohortIndexBuilder.Build(
            globalRows: [(10L, 40, 20, 70f)],
            cohortRows: [(11L, 0, 30, 15, 80f)],
            cohortReaders: [100],
            completionP99: 40,
            generatedAt: null);

        Assert.Equal(2, index.Count);
        Assert.True(index.TryGetSlot(10, out _));
        Assert.True(index.TryGetSlot(11, out var cohortOnly));

        // Present in one table and not the other, so it has cohort evidence and no global row. The
        // zero is a real answer here rather than a missing one.
        Assert.Equal(0, index.GlobalCompletionsAt(cohortOnly));
        Assert.Single(index.EntriesAt(cohortOnly));
    }

    /// <summary>
    /// The cohort id is packed into a byte, one per row over ~190,000 rows. A build wanting more
    /// groups than that needs the column widened; truncating one cohort into another's aggregate is
    /// not an acceptable way to find out.
    /// </summary>
    [Fact]
    public void MoreCohortsThanAByteCarriesIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReaderCohortIndexBuilder.Build(
            globalRows: [(10L, 40, 20, 70f)],
            cohortRows: [],
            cohortReaders: new int[256],
            completionP99: 40,
            generatedAt: null));
    }

    private ReaderCohortCache Cache(string path) =>
        new(new ReaderCohortOptions(path, _dir), NullLogger<ReaderCohortCache>.Instance);

    private async Task<ReaderCohortIndex?> Load(
        (int Cohort, int Readers)[] cohorts,
        (long Id, int Completions, int Raters, double? Mean)[] global,
        (long Id, int Cohort, int Completions, int Raters, double? Mean)[] cells)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db");
        await using (var conn = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            conn.Open();
            Execute(conn, "CREATE TABLE cohort (cohort INTEGER PRIMARY KEY, readers INTEGER NOT NULL, scale REAL NOT NULL, vec BLOB NOT NULL)");
            Execute(
                conn,
                "CREATE TABLE cohort_item (cohort INTEGER NOT NULL, id INTEGER NOT NULL, completions INTEGER NOT NULL, raters INTEGER NOT NULL, mean REAL, PRIMARY KEY (cohort, id))");
            Execute(
                conn,
                "CREATE TABLE item_global (id INTEGER PRIMARY KEY, completions INTEGER NOT NULL, raters INTEGER NOT NULL, mean REAL)");
            Execute(conn, "CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL)");
            Execute(conn, "INSERT INTO meta (key, value) VALUES ('completionP99', '40')");

            foreach (var (cohort, readers) in cohorts)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "INSERT INTO cohort (cohort, readers, scale, vec) VALUES ($c, $r, 1.0, zeroblob(4))";
                cmd.Parameters.AddWithValue("$c", cohort);
                cmd.Parameters.AddWithValue("$r", readers);
                cmd.ExecuteNonQuery();
            }

            foreach (var (id, completions, raters, mean) in global)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "INSERT INTO item_global (id, completions, raters, mean) VALUES ($i, $c, $r, $m)";
                cmd.Parameters.AddWithValue("$i", id);
                cmd.Parameters.AddWithValue("$c", completions);
                cmd.Parameters.AddWithValue("$r", raters);
                cmd.Parameters.AddWithValue("$m", (object?)mean ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }

            foreach (var (id, cohort, completions, raters, mean) in cells)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "INSERT INTO cohort_item (cohort, id, completions, raters, mean) VALUES ($k, $i, $c, $r, $m)";
                cmd.Parameters.AddWithValue("$k", cohort);
                cmd.Parameters.AddWithValue("$i", id);
                cmd.Parameters.AddWithValue("$c", completions);
                cmd.Parameters.AddWithValue("$r", raters);
                cmd.Parameters.AddWithValue("$m", (object?)mean ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        SqliteConnection.ClearAllPools();
        return await Cache(path).GetAsync();
    }

    private static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is harmless.
        }
    }
}
