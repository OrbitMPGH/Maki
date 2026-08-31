using Maki.Metadata.MangaBaka;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Metadata.Tests;

/// <summary>
/// The MangaBaka dump ships with no indexes at all, so the Discover rails full-scan ~558k rows
/// across ~3.5 GB unless <see cref="MangaBakaDumpService.BuildBrowseIndexes"/> runs. These pin the
/// two properties that matter: the indexes exist after a build, and a dump missing a column
/// degrades to a slower rail rather than failing the refresh that installs it.
/// </summary>
public class MangaBakaBrowseIndexTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"maki-idx-{Guid.NewGuid():N}")).FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static readonly string[] AllIndexes =
    [
        "ix_browse_pop", "ix_browse_trend", "ix_browse_new", "ix_browse_rating", "ix_browse_type",
    ];

    private SqliteConnection OpenWith(string columns)
    {
        var conn = new SqliteConnection($"Data Source={Path.Combine(_dir, $"{Guid.NewGuid():N}.db")};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE TABLE series ({columns})";
        cmd.ExecuteNonQuery();
        return conn;
    }

    private const string FullColumns =
        "id INTEGER PRIMARY KEY, state TEXT, type TEXT, title TEXT, rating REAL, cover_raw_url TEXT, " +
        "popularity_global_current INTEGER, popularity_global_history_1mo INTEGER, " +
        "popularity_type_current INTEGER, published_start_date TEXT";

    private static HashSet<string> IndexesOn(SqliteConnection conn)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND name LIKE 'ix_browse_%'";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            found.Add(reader.GetString(0));
        }

        return found;
    }

    [Fact]
    public void Builds_every_index_when_the_dump_has_all_the_columns()
    {
        using var conn = OpenWith(FullColumns);
        MangaBakaDumpService.BuildBrowseIndexes(conn);
        Assert.Equal(AllIndexes.ToHashSet(StringComparer.OrdinalIgnoreCase), IndexesOn(conn));
    }

    /// <summary>
    /// The indexes are an optimization. A dump variant that drops a column must cost one rail its
    /// index, never throw — throwing here would abort <c>RefreshAsync</c> and stop metadata
    /// updating entirely, which is far worse than a slow rail. Found by an existing test failing
    /// against a fixture that had no <c>popularity_global_history_1mo</c>.
    /// </summary>
    [Fact]
    public void Skips_only_the_indexes_whose_columns_are_missing()
    {
        using var conn = OpenWith(
            "id INTEGER PRIMARY KEY, state TEXT, type TEXT, rating REAL, cover_raw_url TEXT, " +
            "popularity_global_current INTEGER");

        MangaBakaDumpService.BuildBrowseIndexes(conn, NullLogger.Instance);

        var built = IndexesOn(conn);
        Assert.Contains("ix_browse_pop", built);
        Assert.Contains("ix_browse_rating", built);
        Assert.DoesNotContain("ix_browse_trend", built);   // no popularity_global_history_1mo
        Assert.DoesNotContain("ix_browse_new", built);     // no published_start_date
        Assert.DoesNotContain("ix_browse_type", built);    // no popularity_type_current
    }

    [Fact]
    public void Rebuilding_replaces_rather_than_failing_on_an_existing_index()
    {
        using var conn = OpenWith(FullColumns);
        MangaBakaDumpService.BuildBrowseIndexes(conn);
        MangaBakaDumpService.BuildBrowseIndexes(conn);
        Assert.Equal(AllIndexes.ToHashSet(StringComparer.OrdinalIgnoreCase), IndexesOn(conn));
    }

    /// <summary>
    /// The rails' WHERE has to imply each partial index's, or SQLite silently ignores the index and
    /// falls back to the scan this whole change exists to remove. A plan naming the index is the
    /// only proof; asserting the index merely exists would pass while it went unused.
    /// </summary>
    [Theory]
    [InlineData("popularity_global_current IS NOT NULL", "popularity_global_current ASC", "ix_browse_pop")]
    [InlineData("published_start_date IS NOT NULL", "published_start_date DESC", "ix_browse_new")]
    [InlineData("type = 'manhwa' AND popularity_type_current IS NOT NULL", "popularity_type_current ASC", "ix_browse_type")]
    public void The_rail_queries_actually_use_the_partial_indexes(string extraWhere, string orderBy, string expected)
    {
        using var conn = OpenWith(FullColumns);
        MangaBakaDumpService.BuildBrowseIndexes(conn);

        using var cmd = conn.CreateCommand();
        // Mirrors MangaBakaLocalStore.GetBrowseAsync's gate, including the title predicate the
        // index deliberately omits.
        cmd.CommandText = $"""
            EXPLAIN QUERY PLAN
            SELECT id FROM series
            WHERE state = 'active' AND type != 'novel' AND rating IS NOT NULL
              AND cover_raw_url IS NOT NULL AND title NOT LIKE 'unknown title%'
              AND {extraWhere}
            ORDER BY {orderBy}
            LIMIT 200
            """;

        var plan = new List<string>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                plan.Add(reader.GetString(3));
            }
        }

        Assert.Contains(expected, string.Join(" | ", plan));
    }
}
