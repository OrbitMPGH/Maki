using System.Data.Common;
using Maki.Api.Controllers;
using Maki.Api.Dtos;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Maki.Api.Tests;

/// <summary>
/// <see cref="CustomRailsControllerTests"/> shares one <see cref="SqliteConnection"/> across every
/// context, the same as <see cref="TestDb"/> does everywhere else, which is deliberate for that
/// fixture's own sake, but it means two "concurrent" <c>Create</c> calls there never actually
/// contend for anything: a single <see cref="SqliteConnection"/> instance cannot run two commands at
/// once, and both calls complete synchronously one after the other regardless of whether
/// <see cref="CustomRailsController.Create"/>'s own transaction does any work.
/// <para>
/// This fixture gives each racer a real connection of its own, against a shared temp-file database,
/// and forces the actual interleaving the cap check has to survive: a
/// <see cref="DbCommandInterceptor"/> pauses the first racer right after its cap-check count has come
/// back, but before it acts on that value, and only then lets the second racer run a real
/// <see cref="CustomRailsController.Create"/> call to completion on its own connection. With the fix
/// (<c>BEGIN IMMEDIATE</c> taken before the count is read) the second racer's own
/// <c>BeginTransactionAsync</c> blocks for the whole pause and only proceeds once the first commits,
/// so its count is never stale. Without it, exactly this interleaving is what lets both racers act on
/// the same pre-insert count and land 31 rails in a 30-rail cap.
/// </para>
/// </summary>
public sealed class CustomRailsConcurrencyTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"maki-customrails-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }
        catch (IOException)
        {
            // best-effort cleanup; a lingering handle on Windows is not worth failing the run over
        }
    }

    private DbContextOptions<MakiDbContext> Options(params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<MakiDbContext>().UseSqlite($"Data Source={_dbPath}");
        if (interceptors.Length > 0)
        {
            builder.AddInterceptors(interceptors);
        }

        return builder.Options;
    }

    private void MigrateToHead()
    {
        using var db = new MakiDbContext(Options());
        db.Database.Migrate();
    }

    private int SeedUser(string userName)
    {
        using var db = new MakiDbContext(Options());
        var user = new MakiUser
        {
            UserName = userName,
            NormalizedUserName = userName.ToUpperInvariant(),
            Permissions = Maki.Core.Security.MakiPermission.Admin,
            AllRootFolders = true,
            MaxContentRating = "erotica",
            SecurityStamp = Guid.NewGuid().ToString(),
            ConcurrencyStamp = Guid.NewGuid().ToString(),
            LockoutEnabled = true,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Users.Add(user);
        db.SaveChanges();
        return user.Id;
    }

    /// <summary>A fresh connection and a fresh <see cref="MakiDbContext"/> for this one call, the way a
    /// real request's scoped context is its own connection.</summary>
    private static CustomRailsController Controller(int userId, MakiDbContext db)
    {
        var service = new CustomRailService(db, new TestCurrentUser(userId), null!, null!, null!, null!, null!, null!);
        return new CustomRailsController(new TestLocalizer(), db, service);
    }

    private MakiDbContext NewContext(int userId, params IInterceptor[] interceptors)
    {
        var scope = new DataScope();
        scope.SetUser(userId, allRootFolders: true);
        return new MakiDbContext(Options(interceptors), scope);
    }

    private static SaveCustomRailRequest Request(string name) =>
        new(name, CustomRailPlacements.Home, new CustomRailSpec(Source: CustomRailSources.Catalogue));

    /// <summary>
    /// Pauses right after the very first <c>COUNT</c> this connection runs (<c>Create</c>'s cap
    /// check) comes back, but before the awaiting C# code acts on it. Whatever the count check sees
    /// (stale or not) is already decided by the time this fires; the pause is purely about giving the
    /// other racer's connection a window to run while this one holds whatever lock (if any) its own
    /// count query left it holding.
    /// </summary>
    private sealed class PauseAfterFirstCount(TimeSpan delay) : DbCommandInterceptor
    {
        private bool _fired;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (!_fired && command.CommandText.Contains("COUNT"))
            {
                _fired = true;
                await Task.Delay(delay, cancellationToken);
            }

            return result;
        }
    }

    [Fact]
    public async Task Concurrent_creates_never_exceed_the_cap()
    {
        MigrateToHead();
        var alice = SeedUser("alice");

        for (var i = 0; i < 29; i++)
        {
            using var seedDb = NewContext(alice);
            Assert.IsType<OkObjectResult>(
                await Controller(alice, seedDb).Create(Request($"Rail {i}"), CancellationToken.None));
        }

        // One slot left (29 of 30). The first racer's cap-check count is deliberately delayed after
        // it has already read the value, giving the second racer's own, fully independent connection
        // a real window to run a whole Create to completion before the first acts on what it read.
        var pause = new PauseAfterFirstCount(TimeSpan.FromMilliseconds(500));
        using var dbA = NewContext(alice, pause);
        using var dbB = NewContext(alice);

        var racerA = Task.Run(() => Controller(alice, dbA).Create(Request("Racer A"), CancellationToken.None));
        await Task.Delay(TimeSpan.FromMilliseconds(100)); // let A's count fire and start its pause first
        var racerB = Task.Run(() => Controller(alice, dbB).Create(Request("Racer B"), CancellationToken.None));

        var results = await Task.WhenAll(racerA, racerB);

        Assert.Single(results, r => r is OkObjectResult);

        using var check = new MakiDbContext(Options());
        Assert.True(check.SavedFilters.Count(f => f.UserId == alice) <= 30);
    }
}
