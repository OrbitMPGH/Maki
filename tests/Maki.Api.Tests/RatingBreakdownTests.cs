using System.Text.Json;
using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Ratings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// One-decimal ratings and the per-category breakdown: rounding and range checks on the rating
/// endpoints, the breakdown endpoint, the history copy, and the per-user ratings settings.
/// </summary>
public sealed class RatingBreakdownTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    // Every series here is incognito, so QueueRatingPush returns before touching a dependency.
    private ScrobbleService Scrobbler() =>
        new(_db.ScopeFactory(), settings: null!, userSettings: null!, kavitaUser: null!, kavita: null!,
            volumeBoundaries: null!, anilist: null!, mal: null!, mangaBaka: null!, kitsu: null!,
            mangaUpdates: null!, logger: NullLogger<ScrobbleService>.Instance);

    private SeriesController Series(Maki.Data.MakiDbContext db, int userId) =>
        new(new TestLocalizer(), db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            Scrobbler(), null!, null!, null!, null!, null!, null!, null!, new TestCurrentUser(userId), null!,
            null!, null!, null!);

    private SettingsController Settings(int userId)
    {
        var db = _db.NewContext(userId);
        return new SettingsController(
            localizer: new TestLocalizer(), userLocales: new TestUserLocaleResolver(),
            settings: null!, naming: null!, flareSolverr: null!, indexers: null!, torrentClients: null!,
            kavita: null!, sourceRegistry: null!, sourceAvailability: null!,
            mangaBakaDump: null!, embeddingModel: null!, embeddingStore: null!, embeddingStatus: null!,
            embeddingIndexer: null!, prebuiltIndex: null!, recoGraph: null!,
            recoGraphCache: null!, coReadInstaller: null!, coReadCache: null!, readerCohortInstaller: null!,
            readerCohortCache: null!, tasteVectorInstaller: null!, vectorIndexCache: null!,
            modelSwitcher: null!, db: db, updateCheck: null!, currentUser: new TestCurrentUser(userId),
            userSettings: new UserSettingsService(db, new TestCurrentUser(userId)),
            kavitaUser: null!, kavitaLive: null!, schedulerFactory: null!, scopeFactory: _db.ScopeFactory(),
            logger: NullLogger<SettingsController>.Instance);
    }

    private int SeedSeries() =>
        _db.SeedSeries("Vinland Saga", configure: s => s.Incognito = IncognitoMode.ScrobbleOnly);

    private static SeriesController.SetRatingBreakdownRequest Scores(string json) =>
        new(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));

    private async Task<IActionResult> Rate(int userId, int seriesId, double? rating)
    {
        using var db = _db.NewContext(userId);
        return await Series(db, userId).SetRating(seriesId, new SeriesController.SetRatingRequest(rating), default);
    }

    private async Task<IActionResult> Breakdown(int userId, int seriesId, string json)
    {
        using var db = _db.NewContext(userId);
        return await Series(db, userId).SetRatingBreakdown(seriesId, Scores(json), default);
    }

    private Maki.Data.Identity.UserSeriesState State(int userId, int seriesId)
    {
        using var db = _db.NewContext();
        return db.UserSeriesStates.AsNoTracking().Single(s => s.UserId == userId && s.SeriesId == seriesId);
    }

    [Theory]
    [InlineData(7.84, 7.8)]
    [InlineData(7.86, 7.9)]
    [InlineData(10.0, 10.0)]
    [InlineData(1.0, 1.0)]
    [InlineData(7, 7.0)]
    public async Task A_rating_is_stored_to_one_decimal(double sent, double stored)
    {
        var alice = _db.SeedUser("alice");
        var seriesId = SeedSeries();

        Assert.IsType<OkObjectResult>(await Rate(alice, seriesId, sent));

        Assert.Equal(stored, State(alice, seriesId).Rating);
        using var db = _db.NewContext();
        Assert.Equal(stored, db.RatingSnapshots.Single().Rating);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.99)]
    [InlineData(10.01)]
    [InlineData(11)]
    public async Task A_rating_outside_one_to_ten_is_refused(double sent)
    {
        var alice = _db.SeedUser("alice");
        var seriesId = SeedSeries();

        Assert.IsType<BadRequestObjectResult>(await Rate(alice, seriesId, sent));

        using var db = _db.NewContext();
        Assert.False(db.UserSeriesStates.Any());
    }

    [Fact]
    public async Task A_provisional_rating_is_stored_to_one_decimal_and_range_checked()
    {
        var alice = _db.SeedUser("alice");
        var seriesId = SeedSeries();

        using (var db = _db.NewContext(alice))
        {
            Assert.IsType<BadRequestObjectResult>(await Series(db, alice)
                .SetProvisionalRating(seriesId, new SeriesController.SetRatingRequest(0.5), default));
            Assert.IsType<OkObjectResult>(await Series(db, alice)
                .SetProvisionalRating(seriesId, new SeriesController.SetRatingRequest(6.25), default));
        }

        Assert.Equal(6.3, State(alice, seriesId).ProvisionalRating);
    }

    [Fact]
    public async Task Setting_a_breakdown_stores_it_and_leaves_the_rating_alone()
    {
        var alice = _db.SeedUser("alice");
        var seriesId = SeedSeries();
        await Rate(alice, seriesId, 7.5);

        var result = await Breakdown(alice, seriesId, """{"story":8,"characters":9,"c-k3j9a2":7}""");

        Assert.IsType<OkObjectResult>(result);
        var state = State(alice, seriesId);
        Assert.Equal(7.5, state.Rating);
        Assert.Equal(new Dictionary<string, int> { ["story"] = 8, ["characters"] = 9, ["c-k3j9a2"] = 7 },
            RatingBreakdown.Parse(state.RatingBreakdownJson));
        using var db = _db.NewContext();
        Assert.Equal(1, db.RatingSnapshots.Count());
    }

    [Fact]
    public async Task An_empty_breakdown_clears_it()
    {
        var alice = _db.SeedUser("alice");
        var seriesId = SeedSeries();
        await Breakdown(alice, seriesId, """{"story":8}""");

        Assert.IsType<OkObjectResult>(await Breakdown(alice, seriesId, "{}"));

        Assert.Null(State(alice, seriesId).RatingBreakdownJson);
    }

    [Theory]
    [InlineData("""{"story":0}""")]
    [InlineData("""{"story":11}""")]
    [InlineData("""{"story":7.5}""")]
    [InlineData("""{"story":"8"}""")]
    [InlineData("""{"Story":8}""")]
    [InlineData("""{"story arc":8}""")]
    [InlineData("""{"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa":8}""")]
    [InlineData("""{"a":1,"b":1,"c":1,"d":1,"e":1,"f":1,"g":1,"h":1,"i":1,"j":1,"k":1,"l":1,"m":1,"n":1,"o":1,"p":1,"q":1}""")]
    public async Task An_invalid_breakdown_is_refused(string json)
    {
        var alice = _db.SeedUser("alice");
        var seriesId = SeedSeries();

        Assert.IsType<BadRequestObjectResult>(await Breakdown(alice, seriesId, json));

        using var db = _db.NewContext();
        Assert.False(db.UserSeriesStates.Any());
    }

    [Fact]
    public async Task Sixteen_entries_are_accepted()
    {
        var alice = _db.SeedUser("alice");
        var seriesId = SeedSeries();
        var json = JsonSerializer.Serialize(Enumerable.Range(0, 16).ToDictionary(i => $"k{i}", _ => 5));

        Assert.IsType<OkObjectResult>(await Breakdown(alice, seriesId, json));
    }

    [Fact]
    public async Task A_rating_snapshot_copies_the_breakdown_including_a_correction()
    {
        var alice = _db.SeedUser("alice");
        var seriesId = SeedSeries();
        await Breakdown(alice, seriesId, """{"story":8}""");
        await Rate(alice, seriesId, 8);

        using (var db = _db.NewContext())
        {
            Assert.Equal("""{"story":8}""", db.RatingSnapshots.Single().BreakdownJson);
        }

        // A change within the correction window replaces the entry, breakdown and all.
        await Breakdown(alice, seriesId, """{"story":6}""");
        await Rate(alice, seriesId, 7.2);

        using var check = _db.NewContext();
        var snapshot = check.RatingSnapshots.Single();
        Assert.Equal(7.2, snapshot.Rating);
        Assert.Equal("""{"story":6}""", snapshot.BreakdownJson);
    }

    [Fact]
    public async Task Ratings_settings_default_to_whole_and_the_built_in_categories()
    {
        var alice = _db.SeedUser("alice");

        var body = Assert.IsType<SettingsController.RatingsSettings>(
            Assert.IsType<OkObjectResult>(await Settings(alice).GetRatings(default)).Value);

        Assert.Equal("whole", body.Precision);
        Assert.Equal(RatingCategoryDefaults.Defaults, body.Categories);
    }

    [Fact]
    public async Task Ratings_settings_round_trip_and_stay_per_user()
    {
        var alice = _db.SeedUser("alice");
        var bob = _db.SeedUser("bob");
        var request = new SettingsController.RatingsSettingsRequest("decimal",
        [
            new("art", null, true, 2),
            new("c-music", "  Music  ", true, 0.5),
            new("story", "ignored", true, 1),
        ]);

        Assert.IsType<OkObjectResult>(await Settings(alice).SetRatings(request, default));

        var body = Assert.IsType<SettingsController.RatingsSettings>(
            Assert.IsType<OkObjectResult>(await Settings(alice).GetRatings(default)).Value);
        Assert.Equal("decimal", body.Precision);
        Assert.Equal(["art", "c-music", "story", "characters", "enjoyment", "pacing", "world", "originality", "ending"],
            body.Categories.Select(c => c.Key));
        Assert.Equal("Music", body.Categories[1].Name);
        Assert.Null(body.Categories[2].Name);
        Assert.Equal(2, body.Categories[0].Weight);

        var other = Assert.IsType<SettingsController.RatingsSettings>(
            Assert.IsType<OkObjectResult>(await Settings(bob).GetRatings(default)).Value);
        Assert.Equal("whole", other.Precision);
    }

    [Fact]
    public async Task Ratings_settings_refuse_a_bad_precision_or_category()
    {
        var alice = _db.SeedUser("alice");

        Assert.IsType<BadRequestObjectResult>(await Settings(alice).SetRatings(
            new SettingsController.RatingsSettingsRequest("tenths", []), default));
        Assert.IsType<BadRequestObjectResult>(await Settings(alice).SetRatings(
            new SettingsController.RatingsSettingsRequest("whole", [new("plot", "Plot", true, 1)]), default));
        Assert.IsType<BadRequestObjectResult>(await Settings(alice).SetRatings(
            new SettingsController.RatingsSettingsRequest("whole", [new(null, "x", true, 1)]), default));
        Assert.IsType<BadRequestObjectResult>(await Settings(alice).SetRatings(
            new SettingsController.RatingsSettingsRequest("whole", null), default));
    }
}
