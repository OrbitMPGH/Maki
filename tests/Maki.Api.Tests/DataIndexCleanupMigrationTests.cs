using Maki.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Maki.Api.Tests;

public class DataIndexCleanupMigrationTests : IDisposable
{
    private const string Previous = "20261009135021_ChapterProgressCountedAt";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MakiDbContext> _options;

    public DataIndexCleanupMigrationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<MakiDbContext>().UseSqlite(_connection).Options;
    }

    public void Dispose() => _connection.Dispose();

    private List<string> Indexes(string table)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = '{table}'";
        using var reader = cmd.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    [Fact]
    public void Upgrade_keeps_the_opds_index_and_drops_the_unused_auth_event_indexes()
    {
        using (var db = new MakiDbContext(_options))
        {
            db.Database.GetInfrastructure().GetRequiredService<IMigrator>().Migrate(Previous);
        }

        Assert.Contains("IX_AuthEvents_Timestamp", Indexes("AuthEvents"));
        Assert.Contains("IX_UserApiKeys_Opds_Live_UserId", Indexes("UserApiKeys"));

        using (var db = new MakiDbContext(_options))
        {
            db.Database.Migrate();
        }

        var authIndexes = Indexes("AuthEvents");
        Assert.DoesNotContain("IX_AuthEvents_Timestamp", authIndexes);
        Assert.DoesNotContain("IX_AuthEvents_UserId", authIndexes);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE name = 'IX_UserApiKeys_Opds_Live_UserId'";
        var sql = (string)cmd.ExecuteScalar()!;
        Assert.Contains("UNIQUE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Scope = 1", sql);
    }

    [Fact]
    public void A_fresh_schema_built_from_the_model_has_the_opds_index()
    {
        using var db = new MakiDbContext(_options);
        db.Database.EnsureCreated();

        Assert.Contains("IX_UserApiKeys_Opds_Live_UserId", Indexes("UserApiKeys"));
    }
}
