using Maki.Api.Auth;
using Maki.Api.Controllers;
using Maki.Core.Security;
using Maki.Api.Services;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// The content ceiling a reader sets on the Settings page is read back from the per-user snapshot,
/// so saving it has to drop that snapshot or the card and every catalogue path keep the old value.
/// </summary>
public sealed class SettingsDiscoverCeilingTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly StoppedClock _clock = new(new DateTimeOffset(2026, 7, 30, 0, 0, 0, TimeSpan.Zero));

    public void Dispose() => _db.Dispose();

    private SettingsController Controller(int userId, MakiDbContext db) => new(
        localizer: new TestLocalizer(), userLocales: new TestUserLocaleResolver(),
        settings: null!, naming: null!, flareSolverr: null!, prowlarr: null!, qbittorrent: null!,
        kavita: null!, sourceRegistry: null!, sourceAvailability: null!,
        mangaBakaDump: null!, embeddingModel: null!, embeddingStore: null!, embeddingStatus: null!,
        embeddingIndexer: null!, prebuiltIndex: null!, recoGraph: null!,
        recoGraphCache: null!, coReadInstaller: null!, coReadCache: null!, readerCohortInstaller: null!,
        readerCohortCache: null!, tasteVectorInstaller: null!, vectorIndexCache: null!,
        modelSwitcher: null!, db: db, updateCheck: null!, currentUser: new TestCurrentUser(userId, "reader"),
        userSettings: new UserSettingsService(db, new TestCurrentUser(userId)),
        kavitaUser: null!, kavitaLive: null!, schedulerFactory: null!, scopeFactory: null!,
        logger: NullLogger<SettingsController>.Instance)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
    };

    [Fact]
    public async Task Saving_the_ceiling_evicts_the_cached_snapshot_and_writes_an_audit_row()
    {
        var userId = _db.SeedUser("reader", MakiPermission.ChangeContentRating);
        var snapshots = new UserSnapshotCache(new MemoryCache(new MemoryCacheOptions()));
        snapshots.Set(new UserSnapshot(userId, "reader", MakiPermission.ChangeContentRating, true,
            new HashSet<int>(), "safe"), snapshots.Generation);
        using var db = _db.NewContext(userId);

        var result = await Controller(userId, db).SetDiscover(
            new SettingsController.DiscoverSettings("pornographic"), snapshots, new AuthEventLogger(db, _clock), default);

        Assert.IsType<OkObjectResult>(result);
        Assert.Null(snapshots.Get(userId));
        using var check = _db.NewContext();
        Assert.Equal("pornographic", check.Users.Single(u => u.Id == userId).MaxContentRating);
        Assert.Contains(check.AuthEvents, e => e.Type == AuthEventType.UserUpdated && e.UserId == userId);
    }
}
