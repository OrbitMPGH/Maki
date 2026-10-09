using Maki.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Maki.Api.Tests;

/// <summary>
/// The <c>CountedAt</c> backfill over a populated pre-upgrade database: only one-shots that were
/// genuinely read (not watched, external or bulk-ticked) are marked as already counted.
/// </summary>
public class ChapterProgressCountedAtMigrationTests : IDisposable
{
    private const string Previous = "20261008231827_ChapterProgressBulkMarked";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MakiDbContext> _options;

    public ChapterProgressCountedAtMigrationTests()
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

    [Fact]
    public void Only_genuinely_read_one_shots_are_marked_as_counted()
    {
        using (var db = new MakiDbContext(_options))
        {
            db.Database.GetInfrastructure().GetRequiredService<IMigrator>().Migrate(Previous);
        }

        Exec("PRAGMA foreign_keys = OFF");
        Exec("""
            INSERT INTO "Chapters" ("Id", "SeriesId", "Number", "Language", "IsOneShot", "Wanted")
            VALUES (1, 1, NULL, 'en', 1, 1), (2, 1, NULL, 'en', 1, 1), (3, 1, NULL, 'en', 1, 1),
                   (4, 1, NULL, 'en', 1, 1), (5, 1, NULL, 'en', 1, 1), (6, 1, 7.0, 'en', 0, 1);

            INSERT INTO "ChapterProgress" (
                "Id", "UserId", "SeriesId", "ChapterId", "PageIndex", "PageCount", "Completed", "External",
                "Watched", "BulkMarked", "ReadSeconds", "ReportedSeconds", "StartedAt", "UpdatedAt", "CompletedAt")
            VALUES
                (1, 1, 1, 1, 0, 10, 1, 0, 0, 0, 0, 0, '2026-01-01 00:00:00', '2026-01-02 00:00:00', '2026-01-02 00:00:00'),
                (2, 1, 1, 2, 0, 10, 1, 0, 0, 1, 0, 0, '2026-01-01 00:00:00', '2026-01-02 00:00:00', '2026-01-02 00:00:00'),
                (3, 1, 1, 3, 0, 0, 1, 1, 0, 0, 0, 0, '2026-01-01 00:00:00', '2026-01-02 00:00:00', '2026-01-02 00:00:00'),
                (4, 1, 1, 4, 0, 10, 1, 0, 1, 0, 0, 0, '2026-01-01 00:00:00', '2026-01-02 00:00:00', '2026-01-02 00:00:00'),
                (5, 1, 1, 5, 3, 10, 0, 0, 0, 0, 0, 0, '2026-01-01 00:00:00', '2026-01-02 00:00:00', NULL),
                (6, 1, 1, 6, 0, 10, 1, 0, 0, 0, 0, 0, '2026-01-01 00:00:00', '2026-01-02 00:00:00', '2026-01-02 00:00:00');
            """);

        using (var db = new MakiDbContext(_options))
        {
            db.Database.Migrate();
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT \"Id\" FROM \"ChapterProgress\" WHERE \"CountedAt\" IS NOT NULL ORDER BY \"Id\"";
        using var reader = cmd.ExecuteReader();
        var counted = new List<long>();
        while (reader.Read())
        {
            counted.Add(reader.GetInt64(0));
        }

        Assert.Equal([1L], counted);
    }
}
