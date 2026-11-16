using Maki.Api.Controllers;
using Maki.Api.Dtos;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Scrobbling;
using Maki.Data.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary><c>GET series/{id}/scrobble</c> shows what the built-in reader pushed, not only Kavita's rows.</summary>
public sealed class SeriesScrobbleSectionTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private SeriesController Controller(Maki.Data.MakiDbContext db, int userId)
    {
        var settings = new FakeAppSettings();
        return new SeriesController(new TestLocalizer(), db, null!, null!, null!, null!, null!, null!, null!, null!,
            null!, settings, null!, Scrobbler(), null!, null!, null!, null!, null!, null!,
            null!, new TestCurrentUser(userId), null!, null!, null!, NullLogger<SeriesController>.Instance);
    }

    private ScrobbleService Scrobbler()
    {
        var http = new NoHttpClientFactory();
        var appSettings = new FakeAppSettings();
        var tokens = new ScrobbleTokenStore(_db.ScopeFactory());
        var userSettings = new UserSettingsStoreService(_db.ScopeFactory());
        var options = new ScrobbleTrackerOptions();
        return new ScrobbleService(
            _db.ScopeFactory(), settings: null!, userSettings, kavitaUser: null!, kavita: null!,
            volumeBoundaries: null!,
            anilist: new AniListTracker(http, appSettings, tokens, options, NullLogger<AniListTracker>.Instance),
            mal: new MalTracker(http, appSettings, tokens, options, NullLogger<MalTracker>.Instance),
            mangaBaka: new MangaBakaTracker(
                http, userSettings, tokens, options, NullLogger<MangaBakaTracker>.Instance),
            kitsu: new KitsuTracker(
                http, appSettings, userSettings, tokens, options, NullLogger<KitsuTracker>.Instance),
            mangaUpdates: new MangaUpdatesTracker(
                http, userSettings, tokens, options, NullLogger<MangaUpdatesTracker>.Instance),
            logger: NullLogger<ScrobbleService>.Instance);
    }

    private sealed class NoHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new NotSupportedException();
    }

    private void ConnectMangaBaka(int userId)
    {
        using var db = _db.NewContext();
        db.UserSettings.Add(new UserSetting { UserId = userId, Key = SettingKeys.ScrobbleMangaBakaToken, Value = "pat" });
        db.SaveChanges();
    }

    private static SeriesScrobbleDto Body(IActionResult result) => (SeriesScrobbleDto)((OkObjectResult)result).Value!;

    [Fact]
    public async Task A_reader_push_shows_without_kavita()
    {
        var alice = _db.SeedUser("alice");
        ConnectMangaBaka(alice);
        var series = _db.SeedSeries("Native Only", configure: s => s.MangaBakaId = 42);
        using (var db = _db.NewContext())
        {
            db.SeriesScrobbleStates.Add(new SeriesScrobbleState
            {
                UserId = alice, SeriesId = series, Service = "mangabaka", Chapter = 12, Volume = 2,
                Status = "reading", SyncedAt = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            db.SaveChanges();
        }

        using var ctx = _db.NewContext(alice);
        var dto = Body(await Controller(ctx, alice).Scrobble(series, default));

        Assert.True(dto.Configured);
        Assert.True(dto.Matched);
        Assert.Null(dto.KavitaSeriesId);
        var row = Assert.Single(dto.Services);
        Assert.Equal("mangabaka", row.Service);
        Assert.True(row.Connected);
        Assert.Equal("42", row.RemoteId);
        Assert.Equal(12, row.Chapter);
        Assert.Equal(2, row.Volume);
        Assert.Equal("reading", row.Status);
    }

    [Fact]
    public async Task The_newer_of_the_kavita_and_reader_rows_wins()
    {
        var alice = _db.SeedUser("alice");
        ConnectMangaBaka(alice);
        var series = _db.SeedSeries("Both Paths", configure: s => s.MangaBakaId = 7);
        using (var db = _db.NewContext())
        {
            db.ScrobbleSyncStates.Add(new ScrobbleSyncState
            {
                UserId = alice, KavitaSeriesId = 900, Service = "mangabaka", Title = "Both Paths",
                Chapter = 5, Status = "reading", SyncedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            db.SeriesScrobbleStates.Add(new SeriesScrobbleState
            {
                UserId = alice, SeriesId = series, Service = "mangabaka", Chapter = 9,
                Status = "reading", SyncedAt = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            db.SaveChanges();
        }

        using var ctx = _db.NewContext(alice);
        var dto = Body(await Controller(ctx, alice).Scrobble(series, default));

        Assert.Equal(900, dto.KavitaSeriesId);
        Assert.Equal(9, Assert.Single(dto.Services).Chapter);
    }

    [Fact]
    public async Task Another_users_reader_push_is_not_shown()
    {
        var alice = _db.SeedUser("alice");
        var bob = _db.SeedUser("bob");
        var series = _db.SeedSeries("Shared");
        using (var db = _db.NewContext())
        {
            db.SeriesScrobbleStates.Add(new SeriesScrobbleState
            {
                UserId = bob, SeriesId = series, Service = "mangabaka", Chapter = 3,
                SyncedAt = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            db.SaveChanges();
        }

        using var ctx = _db.NewContext(alice);
        var dto = Body(await Controller(ctx, alice).Scrobble(series, default));

        Assert.False(dto.Matched);
        Assert.Empty(dto.Services);
    }
}
