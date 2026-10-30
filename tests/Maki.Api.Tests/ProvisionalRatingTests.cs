using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Recommendations;
using Maki.Data.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// "So far" ratings: the endpoints that write them, the rule that a final rating wins, and how
/// <see cref="SeedWeightService"/> weighs them.
/// </summary>
public sealed class ProvisionalRatingTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    // QueueRatingPush returns before touching any dependency for an incognito series, which every
    // series here is, so a scrobbler built from nulls is enough for SetRating and FinalizeRating.
    private static ScrobbleService Scrobbler(TestDb db) =>
        new(db.ScopeFactory(), settings: null!, userSettings: null!, kavitaUser: null!, kavita: null!,
            volumeBoundaries: null!, anilist: null!, mal: null!, mangaBaka: null!, kitsu: null!,
            mangaUpdates: null!, logger: NullLogger<ScrobbleService>.Instance);

    private SeriesController Controller(Maki.Data.MakiDbContext db, int userId) =>
        new(new TestLocalizer(), db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            Scrobbler(_db), null!, null!, null!, null!, null!, null!, null!, new TestCurrentUser(userId), null!,
            null!, null!, null!);

    private int SeedSeries(int mangaBakaId = 9100) =>
        _db.SeedSeries("Vinland Saga", configure: s =>
        {
            s.MangaBakaId = mangaBakaId;
            s.Incognito = IncognitoMode.ScrobbleOnly;
        });

    private void SeedState(int userId, int seriesId, int? rating, int? provisional, DateTime? addedAt = null)
    {
        using var db = _db.NewContext();
        db.UserSeriesStates.Add(new UserSeriesState
        {
            UserId = userId, SeriesId = seriesId, Rating = rating, ProvisionalRating = provisional,
            AddedToLibraryAtUtc = addedAt,
        });
        db.SaveChanges();
    }

    private UserSeriesState State(int userId, int seriesId)
    {
        using var db = _db.NewContext();
        return db.UserSeriesStates.AsNoTracking().Single(s => s.UserId == userId && s.SeriesId == seriesId);
    }

    private async Task<SeedSnapshot> Snapshot(int userId)
    {
        using var db = _db.NewContext(userId);
        var tuning = TasteTuning.Default;
        return await new SeedWeightService(new BehavioralTasteService(tuning), tuning, new FakeAppSettings())
            .SnapshotAsync(db, new TestCurrentUser(userId));
    }

    [Fact]
    public async Task Setting_a_provisional_rating_stores_it_for_the_caller_only()
    {
        var alice = _db.SeedUser("alice");
        var bob = _db.SeedUser("bob");
        var seriesId = SeedSeries();

        using (var db = _db.NewContext(alice))
        {
            Assert.IsType<OkObjectResult>(
                await Controller(db, alice).SetProvisionalRating(seriesId, new SeriesController.SetRatingRequest(7), default));
        }

        var state = State(alice, seriesId);
        Assert.Equal(7, state.ProvisionalRating);
        Assert.Null(state.Rating);
        Assert.NotNull(state.ProvisionalRatedAt);
        using var check = _db.NewContext();
        Assert.False(await check.UserSeriesStates.AnyAsync(s => s.UserId == bob));
    }

    [Fact]
    public async Task A_provisional_rating_is_refused_quietly_while_a_final_one_exists()
    {
        var alice = _db.SeedUser("alice");
        var seriesId = SeedSeries();
        SeedState(alice, seriesId, rating: 9, provisional: null);

        using (var db = _db.NewContext(alice))
        {
            await Controller(db, alice).SetProvisionalRating(seriesId, new SeriesController.SetRatingRequest(3), default);
        }

        var state = State(alice, seriesId);
        Assert.Equal(9, state.Rating);
        Assert.Null(state.ProvisionalRating);
    }

    [Fact]
    public async Task An_out_of_range_provisional_rating_is_rejected()
    {
        var alice = _db.SeedUser("alice");
        var seriesId = SeedSeries();

        using var db = _db.NewContext(alice);
        Assert.IsType<BadRequestObjectResult>(
            await Controller(db, alice).SetProvisionalRating(seriesId, new SeriesController.SetRatingRequest(11), default));
    }

    [Fact]
    public async Task A_final_rating_clears_the_provisional_one()
    {
        var alice = _db.SeedUser("alice");
        var seriesId = SeedSeries();
        SeedState(alice, seriesId, rating: null, provisional: 6);

        using (var db = _db.NewContext(alice))
        {
            await Controller(db, alice).SetRating(seriesId, new SeriesController.SetRatingRequest(8), default);
        }

        var state = State(alice, seriesId);
        Assert.Equal(8, state.Rating);
        Assert.Null(state.ProvisionalRating);
        Assert.Null(state.ProvisionalRatedAt);
    }

    [Fact]
    public async Task Finalizing_makes_the_provisional_score_the_rating()
    {
        var alice = _db.SeedUser("alice");
        var seriesId = SeedSeries();
        SeedState(alice, seriesId, rating: null, provisional: 8);

        using (var db = _db.NewContext(alice))
        {
            Assert.IsType<OkObjectResult>(await Controller(db, alice).FinalizeRating(seriesId, default));
        }

        var state = State(alice, seriesId);
        Assert.Equal(8, state.Rating);
        Assert.Null(state.ProvisionalRating);
    }

    [Fact]
    public async Task Finalizing_with_nothing_to_keep_is_rejected()
    {
        var alice = _db.SeedUser("alice");
        var seriesId = SeedSeries();

        using var db = _db.NewContext(alice);
        Assert.IsType<BadRequestObjectResult>(await Controller(db, alice).FinalizeRating(seriesId, default));
    }

    private List<RatingSnapshot> History(int userId, int seriesId)
    {
        using var db = _db.NewContext();
        return db.RatingSnapshots.AsNoTracking()
            .Where(s => s.UserId == userId && s.SeriesId == seriesId)
            .OrderBy(s => s.Id).ToList();
    }

    [Fact]
    public async Task Every_score_lands_in_the_history_and_a_repeat_does_not()
    {
        var alice = _db.SeedUser("alice");
        var seriesId = SeedSeries();

        using (var db = _db.NewContext(alice))
        {
            var controller = Controller(db, alice);
            await controller.SetProvisionalRating(seriesId, new SeriesController.SetRatingRequest(7), default);
            await controller.SetProvisionalRating(seriesId, new SeriesController.SetRatingRequest(7), default);
            await controller.SetProvisionalRating(seriesId, new SeriesController.SetRatingRequest(8), default);
            await controller.FinalizeRating(seriesId, default);
        }

        var history = History(alice, seriesId);
        Assert.Equal([7, 8, 8], history.Select(s => s.Rating));
        Assert.Equal([false, false, true], history.Select(s => s.Final));
        Assert.Equal(RatingSnapshotOrigin.Finish, history[^1].Origin);
    }

    [Fact]
    public async Task Answering_a_prompt_is_recorded_as_a_checkpoint()
    {
        var alice = _db.SeedUser("alice");
        var seriesId = SeedSeries();
        using (var seed = _db.NewContext())
        {
            seed.UserSeriesStates.Add(new UserSeriesState
            {
                UserId = alice, SeriesId = seriesId, PendingRatingCheckpoint = RatingCheckpoints.CaughtUp,
            });
            seed.SaveChanges();
        }

        using (var db = _db.NewContext(alice))
        {
            await Controller(db, alice).SetProvisionalRating(seriesId, new SeriesController.SetRatingRequest(6), default);
        }

        Assert.Equal(RatingSnapshotOrigin.Checkpoint, Assert.Single(History(alice, seriesId)).Origin);
    }

    [Fact]
    public async Task A_provisional_rating_steers_less_than_the_same_rating_finalised()
    {
        var alice = _db.SeedUser("alice");
        var seriesId = SeedSeries(9101);
        SeedState(alice, seriesId, rating: null, provisional: 10);

        var snapshot = await Snapshot(alice);

        // Nothing read, so the confidence is at its floor of 0.35.
        Assert.Equal(1.35, snapshot.Effective.Weights[9101]);
        Assert.Contains(9101L, snapshot.Effective.EligibleIds);
        Assert.False(snapshot.Avoided.ContainsKey(9101));
    }

    [Fact]
    public async Task A_low_provisional_rating_avoids_at_reduced_strength_and_leaves_the_seeds()
    {
        var alice = _db.SeedUser("alice");
        var seriesId = SeedSeries(9102);
        SeedState(alice, seriesId, rating: null, provisional: 2);

        var snapshot = await Snapshot(alice);

        Assert.Equal(RecommendationFeedbackPolicy.AvoidStrength(2) * ProvisionalRatingPolicy.MinConfidence,
            snapshot.Avoided[9102], 6);
        Assert.DoesNotContain(9102L, snapshot.Effective.EligibleIds);
        Assert.Contains(9102L, snapshot.Observed.EligibleIds);
    }

    [Fact]
    public async Task A_provisional_rating_on_a_fresh_add_keeps_the_add_weight()
    {
        var alice = _db.SeedUser("alice");
        var seriesId = SeedSeries(9103);
        SeedState(alice, seriesId, rating: null, provisional: 8, addedAt: DateTime.UtcNow);

        var snapshot = await Snapshot(alice);

        Assert.Equal(RecommendationFeedbackPolicy.AddedWeight(DateTime.UtcNow, DateTime.UtcNow),
            snapshot.Effective.Weights[9103], 2);
    }

    [Fact]
    public async Task A_final_rating_outranks_a_stray_provisional_one()
    {
        var alice = _db.SeedUser("alice");
        var seriesId = SeedSeries(9104);
        SeedState(alice, seriesId, rating: 8, provisional: 2);

        var snapshot = await Snapshot(alice);

        Assert.Equal(8 / 5.0, snapshot.Effective.Weights[9104]);
        Assert.False(snapshot.Avoided.ContainsKey(9104));
    }
}
