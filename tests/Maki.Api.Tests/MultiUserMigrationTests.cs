using Maki.Core.Security;
using Maki.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Maki.Api.Tests;

/// <summary>
/// Exercises the real migration pipeline against a database that already holds data.
/// <para>
/// <see cref="TestDb"/> uses <c>EnsureCreated()</c>, which builds the schema straight from the
/// current model and never runs a single migration — so every other test in this project would
/// pass even if a migration were incapable of applying. Meanwhile <c>Program.cs</c> calls
/// <c>db.Database.Migrate()</c> unattended at startup on real databases, migrations are
/// forward-only with no down path, and <c>MakiDbContext</c> already documents an index change that
/// <em>would throw</em> there. This file is the only thing standing between a bad migration and a
/// user's library failing to boot.
/// </para>
/// <para>
/// Everything pre-migration is seeded with raw SQL on purpose. Seeding through the EF model would
/// write whatever columns the <em>current</em> model has, which is exactly the set the migration
/// under test is adding — the insert would fail against the old schema, or worse, silently start
/// testing nothing.
/// </para>
/// </summary>
public class MultiUserMigrationTests : IDisposable
{
    /// <summary>The last migration before identity/multi-user work began.</summary>
    private const string PreMultiUser = "20260726190323_AddChapterFileDateAddedIndex";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MakiDbContext> _options;

    public MultiUserMigrationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<MakiDbContext>().UseSqlite(_connection).Options;
    }

    private MakiDbContext NewContext() => new(_options);

    private void MigrateTo(string target)
    {
        using var db = NewContext();
        db.Database.GetInfrastructure().GetRequiredService<IMigrator>().Migrate(target);
    }

    private void MigrateToHead()
    {
        using var db = NewContext();
        db.Database.Migrate();
    }

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? default! : (T)Convert.ChangeType(value, typeof(T))!;
    }

    /// <summary>
    /// Recreates what a single-user install looks like on the eve of the upgrade: a library, a read
    /// chapter, a content-rating preference, and — critically — two <c>ReadingStates</c> rows for
    /// the same series, which the schema comments call out as legal (two Kavita series can resolve
    /// to one local series). A migration that assumed one row per series would fail right here.
    /// </summary>
    private void SeedPreUpgradeLibrary()
    {
        Exec("""
            INSERT INTO "AppConfig" ("Key", "Value") VALUES ('discover.maxcontentrating', 'suggestive');
            INSERT INTO "AppConfig" ("Key", "Value") VALUES ('setup.completed', 'true');

            INSERT INTO "RootFolders" ("Id", "Path") VALUES (1, '/manga');

            INSERT INTO "Series" (
                "Id", "Title", "SortTitle", "Status", "Genres", "Tags", "MonitorNewItems",
                "RootFolderId", "FolderName", "HasAnime", "Added", "Rating")
            VALUES (1, 'Berserk', 'berserk', 0, '["Action"]', '["Dark Fantasy"]', 0,
                    1, 'Berserk', 0, '2026-01-01 00:00:00', 9);

            INSERT INTO "Chapters" ("Id", "SeriesId", "Number", "Language", "IsOneShot", "Monitored")
            VALUES (1, 1, 1.0, 'en', 0, 1);

            INSERT INTO "ChapterProgress" (
                "Id", "SeriesId", "ChapterId", "PageIndex", "PageCount",
                "Completed", "External", "StartedAt", "UpdatedAt")
            VALUES (1, 1, 1, 19, 20, 1, 0, '2026-02-01 00:00:00', '2026-02-01 00:00:00');

            INSERT INTO "ReaderBookmarks" ("Id", "SeriesId", "ChapterId", "PageIndex", "CreatedAt")
            VALUES (1, 1, 1, 4, '2026-02-01 00:00:00');

            -- Two rows for series 1: one adopted by Kavita, one native. Legal by design.
            INSERT INTO "ReadingStates" (
                "Id", "KavitaSeriesId", "SeriesId", "Title", "MaxChapter", "MaxVolume",
                "Finished", "LastProgressAt", "UpdatedAt")
            VALUES (1, 42, 1, 'Berserk', 5.0, 1.0, 0, '2026-02-01 00:00:00', '2026-02-01 00:00:00');
            INSERT INTO "ReadingStates" (
                "Id", "KavitaSeriesId", "SeriesId", "Title", "MaxChapter", "MaxVolume",
                "Finished", "LastProgressAt", "UpdatedAt")
            VALUES (2, NULL, 1, 'Berserk', 3.0, 1.0, 0, '2026-02-01 00:00:00', '2026-02-01 00:00:00');

            INSERT INTO "StatsEvents" ("Id", "Type", "Timestamp", "SeriesId", "SeriesTitle", "Value")
            VALUES (1, 3, '2026-02-01 00:00:00', 1, 'Berserk', 1);

            INSERT INTO "ScrobbleTokens" ("Service", "AccessToken", "Username")
            VALUES ('anilist', 'token-value', 'reader');
            """);
    }

    [Fact]
    public void MigratesAPopulatedSingleUserDatabaseToHead()
    {
        MigrateTo(PreMultiUser);
        SeedPreUpgradeLibrary();

        // The whole point: this is what runs unattended at startup.
        MigrateToHead();

        Assert.Empty(NewContext().Database.GetPendingMigrations());
    }

    [Fact]
    public void ReadingStateKeepsAllThreeInterlockingIndexesAfterTheUpgrade()
    {
        MigrateTo(PreMultiUser);
        SeedPreUpgradeLibrary();
        MigrateToHead();

        // The most dangerous edit in the per-user split: three indexes over overlapping columns, each
        // load-bearing for a different invariant, all rekeyed at once. See MakiDbContext for what each
        // one is for. Adding a column rebuilds the table in SQLite, so this is really asking whether the
        // rebuild put them all back.
        foreach (var name in new[]
                 {
                     "IX_ReadingStates_UserSeries",
                     "IX_ReadingStates_NativeSeries",
                     "IX_ReadingStates_UserId_KavitaSeriesId",
                 })
        {
            Assert.Equal(1, Scalar<long>(
                $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = '{name}';"));
        }

        // The partial filter is what makes duplicate rows per SeriesId legal while still allowing at
        // most one *native* row — lose it and Migrate() throws on any real database.
        Assert.Contains(
            "\"SeriesId\" IS NOT NULL AND \"KavitaSeriesId\" IS NULL",
            Scalar<string>(
                """SELECT "sql" FROM sqlite_master WHERE name = 'IX_ReadingStates_NativeSeries';"""));
    }

    [Fact]
    public void MigratesAnEmptyDatabaseToHead()
    {
        // A fresh install takes the same path and must also get the placeholder admin.
        MigrateToHead();

        Assert.Equal(1, Scalar<long>("""SELECT COUNT(*) FROM "AspNetUsers";"""));
    }

    [Fact]
    public void PlaceholderAdminIsUserOneAndUnclaimed()
    {
        MigrateTo(PreMultiUser);
        SeedPreUpgradeLibrary();
        MigrateToHead();

        using var db = NewContext();
        var admin = Assert.Single(db.Users.ToList());

        // Id 1 is load-bearing: every per-user column a later migration adds defaults to it.
        Assert.Equal(1, admin.Id);
        Assert.True(admin.PendingSetup);
        Assert.Null(admin.PasswordHash);
        Assert.False(admin.Disabled);
        Assert.True(admin.AllRootFolders);
        Assert.True(admin.Permissions.Grants(MakiPermission.Admin));

        // A stamp must exist or SignInManager has nothing to validate a session against.
        Assert.False(string.IsNullOrWhiteSpace(admin.SecurityStamp));
        Assert.False(string.IsNullOrWhiteSpace(admin.ConcurrencyStamp));

        // Lockout has to be enabled on the row itself, else failed-attempt counting never engages.
        Assert.True(admin.LockoutEnabled);
        Assert.Equal(0, admin.AccessFailedCount);
    }

    [Fact]
    public void PlaceholderAdminInheritsTheExistingContentRatingSetting()
    {
        MigrateTo(PreMultiUser);
        SeedPreUpgradeLibrary();
        MigrateToHead();

        // The install had discover.maxcontentrating = suggestive; the upgrade must not silently
        // widen it back to the default.
        Assert.Equal("suggestive", NewContext().Users.Single().MaxContentRating);
    }

    [Fact]
    public void PlaceholderAdminFallsBackToTheDefaultRatingWhenNoneWasSet()
    {
        MigrateTo(PreMultiUser);
        Exec("""INSERT INTO "RootFolders" ("Id", "Path") VALUES (1, '/manga');""");
        MigrateToHead();

        Assert.Equal("erotica", NewContext().Users.Single().MaxContentRating);
    }

    [Fact]
    public void ExistingLibraryDataSurvivesTheUpgrade()
    {
        MigrateTo(PreMultiUser);
        SeedPreUpgradeLibrary();
        MigrateToHead();

        using var db = NewContext();

        var series = Assert.Single(db.Series.ToList());
        Assert.Equal("Berserk", series.Title);
        Assert.Equal(["Action"], series.Genres);

        // The rating moved off Series onto the reader's own state row, and the migration carries it
        // across to user 1 — the account that owned everything before there was more than one.
        var state = Assert.Single(db.UserSeriesStates.ToList());
        Assert.Equal(1, state.UserId);
        Assert.Equal(9, state.Rating);

        var progress = Assert.Single(db.ChapterProgress.ToList());
        Assert.True(progress.Completed);
        Assert.Equal(19, progress.PageIndex);

        Assert.Single(db.ReaderBookmarks.ToList());
        Assert.Single(db.StatsEvents.ToList());
        Assert.Single(db.ScrobbleTokens.ToList());
    }

    [Fact]
    public void DuplicateReadingStateRowsForOneSeriesSurviveTheUpgrade()
    {
        MigrateTo(PreMultiUser);
        SeedPreUpgradeLibrary();
        MigrateToHead();

        // Two rows for series 1 — one Kavita-adopted, one native. Any migration that tightened
        // this into a plain unique index on SeriesId would have thrown above rather than reaching
        // this assertion, which is precisely the failure mode worth a test.
        var rows = NewContext().ReadingStates.Where(r => r.SeriesId == 1).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.KavitaSeriesId == 42);
        Assert.Contains(rows, r => r.KavitaSeriesId is null);
    }

    /// <summary>
    /// An upgraded install gets the same three reading profiles a new account is given, attached to
    /// the placeholder admin that owns the whole pre-upgrade library. Without this the upgrade would
    /// ship the feature switched off for exactly the people who already have a library.
    /// </summary>
    [Fact]
    public void ExistingUsersAreSeededWithTheBuiltInReadingProfiles()
    {
        MigrateTo(PreMultiUser);
        SeedPreUpgradeLibrary();
        MigrateToHead();

        var profiles = NewContext().ReadingProfiles.IgnoreQueryFilters().ToList();
        Assert.Equal(3, profiles.Count);
        Assert.All(profiles, p => Assert.Equal(1, p.UserId));

        var webtoon = Assert.Single(profiles, p => p.Name == "Webtoon");
        Assert.Equal("manhwa,manhua", webtoon.SeriesTypes);
        var prefs = Maki.Core.Reading.ReaderPrefsSpec.Parse(webtoon.PrefsJson);
        Assert.Equal(Maki.Core.Reading.ReaderPrefsSpec.ModeVertical, prefs.Mode);
        Assert.Equal(Maki.Core.Reading.ReaderPrefsSpec.DirectionLtr, prefs.Direction);
        Assert.Equal(Maki.Core.Reading.ReaderPrefsSpec.FitOriginal, prefs.Fit);

        // The library predates the Type column, so nothing auto-selects until a metadata refresh.
        Assert.Null(NewContext().Series.IgnoreQueryFilters().Single(s => s.Id == 1).Type);
    }

    [Fact]
    public void UserApiKeyHashIsUnique()
    {
        MigrateToHead();

        Exec("""
            INSERT INTO "UserApiKeys" ("UserId", "Name", "KeyHash", "Prefix", "Scope", "CreatedAt")
            VALUES (1, 'first', 'deadbeef', 'dead', 0, '2026-02-01 00:00:00');
            """);

        // The digest is the lookup key for every authenticated request; two rows sharing one would
        // make "which user is this" ambiguous.
        var ex = Assert.Throws<SqliteException>(() => Exec("""
            INSERT INTO "UserApiKeys" ("UserId", "Name", "KeyHash", "Prefix", "Scope", "CreatedAt")
            VALUES (1, 'second', 'deadbeef', 'dead', 0, '2026-02-01 00:00:00');
            """));
        Assert.Contains("UNIQUE", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _connection.Dispose();
}
