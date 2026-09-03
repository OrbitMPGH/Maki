using Maki.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Maki.Api.Tests;

/// <summary>
/// The <c>Monitored</c> → <c>Wanted</c> rename and its backfill, run as a real migration over a
/// populated pre-upgrade database. <see cref="TestDb"/> builds its schema with
/// <c>EnsureCreated()</c> and runs no migrations at all, so nothing else in this project would
/// notice if this one were incapable of applying.
/// <para>
/// What the backfill has to get right: on a Smart series the flags were the download job's
/// bookkeeping, not the user's choice, so they come back on — otherwise the new denominator reads
/// them as deliberate exclusions and the series' chapter total stays permanently wrong. Everywhere
/// else the flags really are the user's choice and must survive untouched.
/// </para>
/// </summary>
public class ChapterWantedMigrationTests : IDisposable
{
    /// <summary>The migration immediately before the rename.</summary>
    private const string PreRename = "20260902182132_AddChapterFileReleaseName";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MakiDbContext> _options;

    public ChapterWantedMigrationTests()
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

    private void MigrateToHead()
    {
        using var db = new MakiDbContext(_options);
        db.Database.Migrate();
    }

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private bool WantedOf(int chapterId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT \"Wanted\" FROM \"Chapters\" WHERE \"Id\" = {chapterId}";
        return Convert.ToInt64(cmd.ExecuteScalar()) == 1;
    }

    /// <summary>
    /// Two series on the old schema: one Smart (MonitorNewItems 3) carrying the job's leftover
    /// window, one MainOnly (2) whose unticked special is a real preference. Raw SQL on purpose —
    /// seeding through the EF model would write <c>Wanted</c>, the very column under test.
    /// </summary>
    private void SeedPreRename(bool unmonitorSpecials)
    {
        Exec($"""
            INSERT INTO "AppConfig" ("Key", "Value")
            VALUES ('monitoring.unmonitorspecials', '{(unmonitorSpecials ? "true" : "false")}');

            INSERT INTO "RootFolders" ("Id", "Path") VALUES (1, '/manga');

            INSERT INTO "Series" (
                "Id", "Title", "SortTitle", "Status", "Genres", "Tags", "MonitorNewItems",
                "RootFolderId", "FolderName", "HasAnime", "Added")
            VALUES (1, 'Smart One', 'smart one', 0, '[]', '[]', 3, 1, 'Smart One', 0, '2026-01-01 00:00:00'),
                   (2, 'Main Only', 'main only', 0, '[]', '[]', 2, 1, 'Main Only', 0, '2026-01-01 00:00:00');

            -- Smart series: only chapter 1 is inside the job's window, the rest it unticked itself.
            INSERT INTO "Chapters" ("Id", "SeriesId", "Number", "Language", "IsOneShot", "Monitored")
            VALUES (1, 1, 1.0,  'en', 0, 1),
                   (2, 1, 2.0,  'en', 0, 0),
                   (3, 1, 2.5,  'en', 0, 0),
                   (4, 1, NULL, 'en', 1, 0);

            -- MainOnly series: the unticked special is the user's own choice and must stay off.
            INSERT INTO "Chapters" ("Id", "SeriesId", "Number", "Language", "IsOneShot", "Monitored")
            VALUES (5, 2, 1.0, 'en', 0, 1),
                   (6, 2, 1.5, 'en', 0, 0);
            """);
    }

    [Fact]
    public void Smart_series_chapters_come_back_wanted()
    {
        MigrateTo(PreRename);
        SeedPreRename(unmonitorSpecials: false);

        MigrateToHead();

        Assert.True(WantedOf(1));
        Assert.True(WantedOf(2));
        Assert.True(WantedOf(3)); // the special too, since the setting is off
        Assert.True(WantedOf(4)); // one-shots have no number and are never specials
    }

    [Fact]
    public void Smart_series_specials_stay_unwanted_when_the_setting_is_on()
    {
        MigrateTo(PreRename);
        SeedPreRename(unmonitorSpecials: true);

        MigrateToHead();

        Assert.True(WantedOf(2));
        Assert.False(WantedOf(3)); // 2.5 is a special
        Assert.True(WantedOf(4));  // a one-shot is not
    }

    [Fact]
    public void Non_smart_series_keeps_every_flag_exactly_as_it_was()
    {
        MigrateTo(PreRename);
        SeedPreRename(unmonitorSpecials: false);

        MigrateToHead();

        Assert.True(WantedOf(5));
        Assert.False(WantedOf(6));
    }

    /// <summary>An install that never opened the setting has no AppConfig row at all.</summary>
    [Fact]
    public void Missing_specials_setting_reads_as_off()
    {
        MigrateTo(PreRename);
        Exec("""
            INSERT INTO "RootFolders" ("Id", "Path") VALUES (1, '/manga');
            INSERT INTO "Series" (
                "Id", "Title", "SortTitle", "Status", "Genres", "Tags", "MonitorNewItems",
                "RootFolderId", "FolderName", "HasAnime", "Added")
            VALUES (1, 'Smart One', 'smart one', 0, '[]', '[]', 3, 1, 'Smart One', 0, '2026-01-01 00:00:00');
            INSERT INTO "Chapters" ("Id", "SeriesId", "Number", "Language", "IsOneShot", "Monitored")
            VALUES (1, 1, 2.5, 'en', 0, 0);
            """);

        MigrateToHead();

        Assert.True(WantedOf(1));
    }
}
