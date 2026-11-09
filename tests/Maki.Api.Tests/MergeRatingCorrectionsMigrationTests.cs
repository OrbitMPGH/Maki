using Maki.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Maki.Api.Tests;

public class MergeRatingCorrectionsMigrationTests : IDisposable
{
    private const string Before = "20261103221138_ChapterNotes";
    private const string Merge = "20261104230631_MergeRatingCorrections";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MakiDbContext> _options;

    public MergeRatingCorrectionsMigrationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<MakiDbContext>().UseSqlite(_connection).Options;
    }

    public void Dispose() => _connection.Dispose();

    private void MigrateTo(string target)
    {
        using var db = new MakiDbContext(_options);
        db.Database.GetInfrastructure().GetRequiredService<IMigrator>().Migrate(target);
    }

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private List<(long Id, int Rating, int Origin)> Rows()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT \"Id\", \"Rating\", \"Origin\" FROM \"RatingSnapshots\" ORDER BY \"Id\"";
        using var r = cmd.ExecuteReader();
        var rows = new List<(long, int, int)>();
        while (r.Read())
            rows.Add((r.GetInt64(0), r.GetInt32(1), r.GetInt32(2)));
        return rows;
    }

    [Fact]
    public void Merges_only_close_same_chapter_pairs_and_keeps_the_earlier_origin()
    {
        MigrateTo(Before);
        Exec("PRAGMA foreign_keys = OFF;");
        Exec("""
            INSERT INTO "RatingSnapshots" ("Id", "UserId", "SeriesId", "Rating", "Final", "AtChapter", "Origin", "CreatedAt") VALUES
                (1, 1, 1, 3, 0, 12.0, 1, '2026-11-01 10:00:00'),
                (2, 1, 1, 8, 0, 12.0, 0, '2026-11-01 10:05:00'),
                (3, 1, 1, 9, 0, 12.0, 0, '2026-11-03 10:00:00'),
                (4, 1, 1, 9, 0, 30.0, 0, '2026-11-03 10:02:00');
            """);

        MigrateTo(Merge);

        Assert.Equal([(2L, 8, 1), (3L, 9, 0), (4L, 9, 0)], Rows());
    }
}
