using Maki.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Tests;

public sealed class MigrationLockTests : IDisposable
{
    private readonly SqliteConnection connection = new("DataSource=:memory:");

    public MigrationLockTests() => connection.Open();

    public void Dispose() => connection.Dispose();

    private MakiDbContext Context() => new(new DbContextOptionsBuilder<MakiDbContext>().UseSqlite(connection).Options);

    [Fact]
    public async Task A_lock_left_by_a_run_that_died_while_migrating_is_cleared_and_migrate_finishes()
    {
        using var db = Context();
        Assert.False(MigrationLock.ClearAbandoned(db.Database));
        await db.Database.MigrateAsync();
        // What a process killed inside Migrate() leaves behind.
        await db.Database.ExecuteSqlRawAsync("INSERT INTO \"__EFMigrationsLock\" (\"Id\", \"Timestamp\") VALUES (1, '2026-11-08 00:00:00')");

        Assert.True(MigrationLock.ClearAbandoned(db.Database));
        Assert.False(MigrationLock.ClearAbandoned(db.Database));
        await db.Database.MigrateAsync().WaitAsync(TimeSpan.FromSeconds(30));
    }
}
