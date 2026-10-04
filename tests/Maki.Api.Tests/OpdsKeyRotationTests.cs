using System.Data.Common;
using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// Two concurrent OPDS rotations used to both read "no live key yet" between the other's revoke and
/// insert, and both would then insert a live row: <see cref="SettingsController"/>'s
/// <c>MintOpdsKeyAsync</c> ran the revoke and the insert as two separate round trips. The real guard
/// is the <c>OpdsKeyOneLivePerScope</c> migration's filtered unique index, exercised here against a
/// genuinely migrated database, the same reason and the same pattern as
/// <see cref="SeriesRequestPendingUniqueTests"/>, since <see cref="TestDb"/>'s <c>EnsureCreated()</c>
/// never runs a single migration and would not create it.
/// </summary>
public class OpdsKeyRotationTests : IDisposable
{
    /// <summary>The last migration before this one.</summary>
    private const string BeforeUniqueIndex = "20260927221054_SeriesRequestPendingUnique";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MakiDbContext> _options;

    public OpdsKeyRotationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<MakiDbContext>().UseSqlite(_connection).Options;
    }

    public void Dispose() => _connection.Dispose();

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

    /// <summary>
    /// The schema for <c>UserApiKeys</c> is unchanged by this migration (it only adds an index), so
    /// seeding through the current entity model against the pre-migration schema is safe.
    /// </summary>
    private int SeedKey(
        int userId, UserApiKeyScope scope, DateTime? revokedAt, DateTime created, string name = "key")
    {
        using var db = NewContext();
        var key = new UserApiKey
        {
            UserId = userId,
            Name = name,
            KeyHash = Guid.NewGuid().ToString("N"),
            Prefix = "abcdefgh",
            Scope = scope,
            CreatedAt = created,
            RevokedAt = revokedAt,
        };
        db.UserApiKeys.Add(key);
        db.SaveChanges();
        return key.Id;
    }

    private List<int> LiveOpdsKeyIds(int userId)
    {
        using var db = NewContext();
        return db.UserApiKeys.IgnoreQueryFilters()
            .Where(k => k.UserId == userId && k.Scope == UserApiKeyScope.Opds && k.RevokedAt == null)
            .Select(k => k.Id)
            .ToList();
    }

    private T Scalar<T>(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? default! : (T)Convert.ChangeType(value, typeof(T))!;
    }

    // ---- migration: dedupe existing duplicates ----

    [Fact]
    public void Migrating_dedupes_existing_live_opds_keys_keeping_the_newest()
    {
        MigrateTo(BeforeUniqueIndex);
        var oldest = SeedKey(1, UserApiKeyScope.Opds, revokedAt: null,
            created: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var newest = SeedKey(1, UserApiKeyScope.Opds, revokedAt: null,
            created: new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

        MigrateToHead();

        Assert.Equal([newest], LiveOpdsKeyIds(1));
        using var db = NewContext();
        Assert.NotNull(db.UserApiKeys.IgnoreQueryFilters().Single(k => k.Id == oldest).RevokedAt);
    }

    [Fact]
    public void Migrating_leaves_an_already_revoked_opds_key_alone()
    {
        MigrateTo(BeforeUniqueIndex);
        var revoked = SeedKey(1, UserApiKeyScope.Opds,
            revokedAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            created: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var live = SeedKey(1, UserApiKeyScope.Opds, revokedAt: null,
            created: new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

        MigrateToHead();

        using var db = NewContext();
        Assert.Equal(revoked, db.UserApiKeys.IgnoreQueryFilters()
            .Single(k => k.RevokedAt == new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Id);
        Assert.Equal([live], LiveOpdsKeyIds(1));
    }

    [Fact]
    public void Migrating_does_not_touch_live_full_scope_keys()
    {
        MigrateTo(BeforeUniqueIndex);
        var first = SeedKey(1, UserApiKeyScope.Full, revokedAt: null,
            created: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), name: "script a");
        var second = SeedKey(1, UserApiKeyScope.Full, revokedAt: null,
            created: new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), name: "script b");

        MigrateToHead();

        using var db = NewContext();
        var live = db.UserApiKeys.IgnoreQueryFilters()
            .Where(k => k.UserId == 1 && k.Scope == UserApiKeyScope.Full && k.RevokedAt == null)
            .Select(k => k.Id).OrderBy(id => id).ToList();
        Assert.Equal([first, second], live);
    }

    // ---- the index itself ----

    [Fact]
    public void Index_exists_and_is_unique_and_partial()
    {
        MigrateToHead();

        var sql = Scalar<string>(
            """SELECT "sql" FROM sqlite_master WHERE name = 'IX_UserApiKeys_Opds_Live_UserId';""");
        Assert.Contains("UNIQUE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WHERE", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_index_refuses_a_second_live_opds_key_for_the_same_user()
    {
        MigrateToHead();
        SeedKey(1, UserApiKeyScope.Opds, revokedAt: null, created: DateTime.UtcNow);

        var ex = Assert.Throws<DbUpdateException>(
            () => SeedKey(1, UserApiKeyScope.Opds, revokedAt: null, created: DateTime.UtcNow));

        Assert.Equal(2067, Assert.IsType<SqliteException>(ex.InnerException).SqliteExtendedErrorCode);
    }

    [Fact]
    public void The_index_allows_two_live_full_scope_keys_for_the_same_user()
    {
        MigrateToHead();
        SeedKey(1, UserApiKeyScope.Full, revokedAt: null, created: DateTime.UtcNow, name: "script a");

        // No exception: unlike Opds, several named Full-scope keys are meant to coexist.
        SeedKey(1, UserApiKeyScope.Full, revokedAt: null, created: DateTime.UtcNow, name: "script b");

        using var db = NewContext();
        Assert.Equal(2, db.UserApiKeys.IgnoreQueryFilters()
            .Count(k => k.UserId == 1 && k.Scope == UserApiKeyScope.Full && k.RevokedAt == null));
    }

    [Fact]
    public void A_live_opds_key_and_a_live_full_scope_key_coexist_for_the_same_user()
    {
        MigrateToHead();
        SeedKey(1, UserApiKeyScope.Opds, revokedAt: null, created: DateTime.UtcNow);

        // Different scope, so it's not the same slot the partial index guards.
        SeedKey(1, UserApiKeyScope.Full, revokedAt: null, created: DateTime.UtcNow);

        using var db = NewContext();
        Assert.Equal(2, db.UserApiKeys.IgnoreQueryFilters().Count(k => k.UserId == 1 && k.RevokedAt == null));
    }

    // ---- the controller, end to end ----

    private static SettingsController Controller(int userId, MakiDbContext db) =>
        new(
            localizer: new TestLocalizer(), userLocales: new TestUserLocaleResolver(),
            settings: null!, naming: null!, flareSolverr: null!, prowlarr: null!, qbittorrent: null!,
            kavita: null!, sourceRegistry: null!, sourceAvailability: null!,
            mangaBakaDump: null!, embeddingModel: null!, embeddingStore: null!, embeddingStatus: null!,
            embeddingIndexer: null!, prebuiltIndex: null!, recoGraph: null!,
            recoGraphCache: null!, coReadInstaller: null!, coReadCache: null!, readerCohortInstaller: null!,
            readerCohortCache: null!, tasteVectorInstaller: null!, vectorIndexCache: null!,
            modelSwitcher: null!, db: db, updateCheck: null!, currentUser: new TestCurrentUser(userId),
            userSettings: new UserSettingsService(db, new TestCurrentUser(userId)),
            kavitaUser: null!, kavitaLive: null!, schedulerFactory: null!, scopeFactory: null!,
            logger: NullLogger<SettingsController>.Instance);

    private MakiDbContext ScopedContext(int userId, DbContextOptions<MakiDbContext>? options = null)
    {
        var scope = new DataScope();
        scope.SetUser(userId, allRootFolders: true);
        return new MakiDbContext(options ?? _options, scope);
    }

    [Fact]
    public async Task Rotating_the_opds_token_revokes_the_previous_one_and_leaves_exactly_one_live_key()
    {
        MigrateToHead();
        using var db = ScopedContext(1);
        var controller = Controller(1, db);

        Assert.IsType<OkObjectResult>(await controller.RotateOpdsToken(null, IdentityTestKit.UserManager(db), null!, CancellationToken.None));
        Assert.IsType<OkObjectResult>(await controller.RotateOpdsToken(null, IdentityTestKit.UserManager(db), null!, CancellationToken.None));

        Assert.Single(LiveOpdsKeyIds(1));
    }

    /// <summary>
    /// Simulates a second rotation landing mid-transaction: fires when the primary insert's own
    /// command executes and, from inside the same connection and transaction, inserts a second live
    /// Opds row first, the same technique <see cref="SeriesRequestPendingUniqueTests"/> uses for its
    /// own race. Whatever the primary call does with that, the invariant that broke before the fix is
    /// the one to check: never zero live keys for the user, never two.
    /// </summary>
    private sealed class CompetingLiveInsert(int userId) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (!Fired && command.CommandText.Contains("INSERT INTO \"UserApiKeys\""))
            {
                Fired = true;
                await using var other = command.Connection!.CreateCommand();
                other.Transaction = command.Transaction;
                other.CommandText =
                    $"""
                    INSERT INTO "UserApiKeys"
                        ("UserId", "Name", "KeyHash", "Prefix", "Scope", "CreatedAt")
                    VALUES ({userId}, 'competing', 'deadbeef', 'deadbeef', 1, '2026-01-01 00:00:00');
                    """;
                await other.ExecuteNonQueryAsync(cancellationToken);
            }

            return result;
        }
    }

    [Fact]
    public async Task A_rotation_racing_a_concurrent_insert_never_leaves_zero_or_two_live_keys()
    {
        MigrateToHead();
        SeedKey(1, UserApiKeyScope.Opds, revokedAt: null, created: DateTime.UtcNow);

        var interceptor = new CompetingLiveInsert(1);
        var options = new DbContextOptionsBuilder<MakiDbContext>(_options).AddInterceptors(interceptor).Options;
        using var db = ScopedContext(1, options);
        var controller = Controller(1, db);

        try
        {
            await controller.RotateOpdsToken(null, IdentityTestKit.UserManager(db), null!, CancellationToken.None);
        }
        catch (DbUpdateException)
        {
            // The competing insert claimed the (user, Opds) slot first, inside the same transaction;
            // the rotation's own insert then collides with it and the whole transaction rolls back,
            // including the revoke, leaving the original key exactly as it was.
        }

        Assert.True(interceptor.Fired);
        Assert.Single(LiveOpdsKeyIds(1));
    }
}
