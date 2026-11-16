using Maki.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Maki.Api.Tests;

/// <summary>
/// The Reading backfill over a populated pre-upgrade database: real reads and imported marks start as
/// Reading, while a mark that Anime resume left beside watched ticks or a pending marker does not.
/// </summary>
public class ReadingStatusMigrationTests : IDisposable
{
    private const string Previous = "20261112050435_EarlyAccessWaiting";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MakiDbContext> _options;

    public ReadingStatusMigrationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<MakiDbContext>().UseSqlite(_connection).Options;
    }

    public void Dispose() => _connection.Dispose();

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Inserts a row with the given values and a zero or empty value in every other required column.</summary>
    private void Insert(string table, Dictionary<string, object?> values)
    {
        var columns = new Dictionary<string, object?>(values);
        using (var info = _connection.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info(\"{table}\")";
            using var reader = info.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(1);
                var type = reader.GetString(2);
                var notNull = reader.GetInt64(3) == 1;
                if (notNull && reader.IsDBNull(4) && !columns.ContainsKey(name) && reader.GetInt64(5) == 0)
                {
                    columns[name] = type is "INTEGER" or "REAL" ? 0 : "";
                }
            }
        }

        using var cmd = _connection.CreateCommand();
        var names = columns.Keys.ToList();
        cmd.CommandText = $"INSERT INTO \"{table}\" ({string.Join(", ", names.Select(n => $"\"{n}\""))}) " +
                          $"VALUES ({string.Join(", ", names.Select((_, i) => $"$p{i}"))})";
        for (var i = 0; i < names.Count; i++)
        {
            cmd.Parameters.AddWithValue($"$p{i}", columns[names[i]] ?? DBNull.Value);
        }

        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Anime_resume_marks_do_not_start_a_series_as_reading()
    {
        using (var db = new MakiDbContext(_options))
        {
            db.Database.GetInfrastructure().GetRequiredService<IMigrator>().Migrate(Previous);
        }

        Exec("PRAGMA foreign_keys = OFF");
        for (var id = 1; id <= 4; id++)
        {
            Insert("Series", new() { ["Id"] = id, ["Title"] = $"Series {id}" });
            Insert("ReadingStates", new()
            {
                ["Id"] = id, ["UserId"] = 1, ["SeriesId"] = id, ["Title"] = $"Series {id}", ["MaxChapter"] = 10.0,
                ["MaxVolume"] = 0.0, ["LastProgressAt"] = "2026-01-01 00:00:00", ["UpdatedAt"] = "2026-01-01 00:00:00",
            });
        }

        // 1: an imported mark only. 2: watched ticks only. 3: a pending anime marker. 4: watched and read.
        Insert("ChapterProgress", new()
        {
            ["Id"] = 1, ["UserId"] = 1, ["SeriesId"] = 2, ["ChapterId"] = 1, ["Completed"] = 1, ["Watched"] = 1,
            ["StartedAt"] = "2026-01-01 00:00:00", ["UpdatedAt"] = "2026-01-01 00:00:00",
        });
        Insert("UserSeriesStates", new()
        {
            ["UserId"] = 1, ["SeriesId"] = 3, ["AnimeWatchPendingTo"] = 10.0, ["UpdatedAt"] = "2026-01-01 00:00:00",
        });
        Insert("ChapterProgress", new()
        {
            ["Id"] = 2, ["UserId"] = 1, ["SeriesId"] = 4, ["ChapterId"] = 2, ["Completed"] = 1, ["Watched"] = 1,
            ["StartedAt"] = "2026-01-01 00:00:00", ["UpdatedAt"] = "2026-01-01 00:00:00",
        });
        Insert("ChapterProgress", new()
        {
            ["Id"] = 3, ["UserId"] = 1, ["SeriesId"] = 4, ["ChapterId"] = 3, ["Completed"] = 1, ["Watched"] = 0,
            ["StartedAt"] = "2026-01-01 00:00:00", ["UpdatedAt"] = "2026-01-01 00:00:00",
        });

        using (var db = new MakiDbContext(_options))
        {
            db.Database.Migrate();
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT \"SeriesId\" FROM \"UserSeriesStates\" WHERE \"Status\" = 1 ORDER BY \"SeriesId\"";
        using var reader = cmd.ExecuteReader();
        var reading = new List<long>();
        while (reader.Read())
        {
            reading.Add(reader.GetInt64(0));
        }

        Assert.Equal([1L, 4L], reading);
    }
}
