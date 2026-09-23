using Maki.Core.Configuration;
using Maki.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Maki.Api.Tests;

/// <summary>
/// The <c>CustomRails</c> migration: a pinned Discover preset becomes a catalogue rail, the preset
/// itself is kept, and the dropped <c>Pinned</c> column stays gone.
/// </summary>
public class CustomRailsMigrationTests
{
    private const string PreMigration = "20260922234412_DiscoverSavedFilters";

    /// <summary>
    /// Raw ADO insert, not <c>Database.ExecuteSqlRaw</c>: that method treats the string as a
    /// composite format string, and a JSON spec's braces break it. Also keeps the insert on the
    /// old schema rather than whatever columns the current model has.
    /// </summary>
    private static void Exec(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void A_pinned_discover_preset_becomes_a_discover_rail_and_the_preset_survives()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<MakiDbContext>().UseSqlite(connection).Options;

        using (var before = new MakiDbContext(options))
        {
            before.Database.GetInfrastructure().GetRequiredService<IMigrator>().Migrate(PreMigration);
        }

        Exec(connection, """
            INSERT INTO SavedFilters (UserId, Name, Spec, SortOrder, Created, Scope, Pinned)
            VALUES (1, 'Pinned preset', '{"genres":["Action"]}', 0, '2026-01-01 00:00:00', 'discover', 1)
            """);
        Exec(connection, """
            INSERT INTO SavedFilters (UserId, Name, Spec, SortOrder, Created, Scope, Pinned)
            VALUES (1, 'Unpinned preset', '{}', 1, '2026-01-01 00:00:00', 'discover', 0)
            """);
        Exec(connection, """
            INSERT INTO SavedFilters (UserId, Name, Spec, SortOrder, Created, Scope, Pinned)
            VALUES (1, 'Library preset', '{}', 0, '2026-01-01 00:00:00', 'library', 0)
            """);

        using (var after = new MakiDbContext(options))
        {
            after.Database.Migrate();

            var rows = after.SavedFilters.AsNoTracking().ToList();
            // Two presets kept, plus the one new discoverrail row the pinned preset became.
            Assert.Equal(4, rows.Count);

            var presets = rows.Where(r => r.Scope == "discover").ToList();
            Assert.Equal(2, presets.Count);
            Assert.Contains(presets, r => r.Name == "Pinned preset");
            Assert.Contains(presets, r => r.Name == "Unpinned preset");
            Assert.Contains(rows, r => r.Scope == "library" && r.Name == "Library preset");

            var rail = Assert.Single(rows, r => r.Scope == "discoverrail");
            Assert.Equal("Pinned preset", rail.Name);
            var spec = CustomRailSpec.Parse(rail.Spec);
            Assert.Equal(CustomRailSources.Catalogue, spec.Source);
            Assert.Equal(CustomRailSorts.Popular, spec.Sort);
            Assert.False(spec.ExcludeOwned);
            Assert.Equal(["Action"], spec.Filters!.Genres);

            // The Pinned column is gone: selecting it must fail rather than read a phantom default.
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Pinned FROM SavedFilters";
            Assert.Throws<SqliteException>(() => cmd.ExecuteReader());
        }
    }

    [Fact]
    public void An_unpinned_discover_preset_produces_no_rail()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<MakiDbContext>().UseSqlite(connection).Options;

        using (var before = new MakiDbContext(options))
        {
            before.Database.GetInfrastructure().GetRequiredService<IMigrator>().Migrate(PreMigration);
        }

        Exec(connection, """
            INSERT INTO SavedFilters (UserId, Name, Spec, SortOrder, Created, Scope, Pinned)
            VALUES (1, 'Unpinned preset', '{}', 0, '2026-01-01 00:00:00', 'discover', 0)
            """);

        using (var after = new MakiDbContext(options))
        {
            after.Database.Migrate();

            Assert.Empty(after.SavedFilters.AsNoTracking().Where(r => r.Scope == "discoverrail" || r.Scope == "homerail"));
            Assert.Single(after.SavedFilters.AsNoTracking().Where(r => r.Scope == "discover"));
        }
    }
}
