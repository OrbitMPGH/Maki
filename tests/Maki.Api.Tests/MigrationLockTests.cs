using Maki.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Maki.Api.Tests;

public sealed class MigrationLockTests : IDisposable
{
    private readonly SqliteConnection connection = new("DataSource=:memory:");

    public MigrationLockTests() => connection.Open();

    public void Dispose() => connection.Dispose();

    private MakiDbContext Context() => new(new DbContextOptionsBuilder<MakiDbContext>().UseSqlite(connection).Options);

    /// <summary>The row a process killed inside Migrate() leaves, written the way EF writes it.</summary>
    private static Task Lock(MakiDbContext db, DateTimeOffset at)
    {
        var literal = db.GetService<IRelationalTypeMappingSource>().FindMapping(typeof(DateTimeOffset))!.GenerateSqlLiteral(at);
        return db.Database.ExecuteSqlRawAsync("INSERT INTO \"__EFMigrationsLock\" (\"Id\", \"Timestamp\") VALUES (1, " + literal + ")");
    }

    [Fact]
    public async Task A_lock_left_by_a_run_that_died_while_migrating_is_cleared_and_migrate_finishes()
    {
        using var db = Context();
        var started = DateTimeOffset.UtcNow;
        Assert.Equal(MigrationLockState.None, MigrationLock.ClearAbandoned(db.Database, started));
        await db.Database.MigrateAsync();
        await Lock(db, started.AddMinutes(-5));

        Assert.Equal(MigrationLockState.Cleared, MigrationLock.ClearAbandoned(db.Database, started));
        Assert.Equal(MigrationLockState.None, MigrationLock.ClearAbandoned(db.Database, started));
        await db.Database.MigrateAsync().WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task A_lock_taken_after_this_process_started_is_left_alone()
    {
        using var db = Context();
        var started = DateTimeOffset.UtcNow;
        await db.Database.MigrateAsync();
        await Lock(db, started.AddSeconds(2));

        Assert.Equal(MigrationLockState.KeptNewer, MigrationLock.ClearAbandoned(db.Database, started));
        Assert.Equal(1, db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS \"Value\" FROM \"__EFMigrationsLock\"").AsEnumerable().Single());
    }
}
