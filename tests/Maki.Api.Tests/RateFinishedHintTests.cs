using Maki.Api.Controllers;
using Maki.Core.Entities;
using Maki.Data.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Tests;

/// <summary>
/// The "finished, rate it?" hint on the series page: when <see cref="SeriesController.RateHintAsync"/>
/// offers it, and that dismissing it writes the caller's own <see cref="UserSeriesState"/> row.
/// </summary>
public sealed class RateFinishedHintTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static SeriesController Controller(Maki.Data.MakiDbContext db) =>
        new(new TestLocalizer(), db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);

    private void SeedReader(int userId, int seriesId, bool finished)
    {
        using var db = _db.NewContext();
        db.ReadingStates.Add(new ReadingState
        {
            UserId = userId, SeriesId = seriesId, Title = "Berserk", Finished = finished, MaxChapter = 10,
        });
        db.SaveChanges();
    }

    private void SeedState(int userId, int seriesId, int? rating)
    {
        using var db = _db.NewContext();
        db.UserSeriesStates.Add(new UserSeriesState { UserId = userId, SeriesId = seriesId, Rating = rating });
        db.SaveChanges();
    }

    private async Task<bool> Offered(int userId, int seriesId)
    {
        using var db = _db.NewContext(userId);
        return await SeriesController.RateHintAsync(db, seriesId, default);
    }

    [Fact]
    public async Task Offered_once_the_caller_has_finished_an_unrated_series()
    {
        var alice = _db.SeedUser("alice");
        var seriesId = _db.SeedSeries("Berserk");
        SeedReader(alice, seriesId, finished: true);

        Assert.True(await Offered(alice, seriesId));
    }

    [Fact]
    public async Task Offered_when_the_callers_row_exists_without_a_rating()
    {
        var alice = _db.SeedUser("alice");
        var seriesId = _db.SeedSeries("Berserk");
        SeedReader(alice, seriesId, finished: true);
        SeedState(alice, seriesId, rating: null);

        Assert.True(await Offered(alice, seriesId));
    }

    [Fact]
    public async Task Not_offered_while_the_caller_is_still_reading()
    {
        var alice = _db.SeedUser("alice");
        var seriesId = _db.SeedSeries("Berserk");
        SeedReader(alice, seriesId, finished: false);

        Assert.False(await Offered(alice, seriesId));
    }

    [Fact]
    public async Task Not_offered_on_someone_elses_finish()
    {
        var alice = _db.SeedUser("alice");
        var bob = _db.SeedUser("bob");
        var seriesId = _db.SeedSeries("Berserk");
        SeedReader(bob, seriesId, finished: true);

        Assert.False(await Offered(alice, seriesId));
        Assert.True(await Offered(bob, seriesId));
    }

    [Fact]
    public async Task Not_offered_once_the_caller_has_rated_it_but_still_offered_to_others()
    {
        var alice = _db.SeedUser("alice");
        var bob = _db.SeedUser("bob");
        var seriesId = _db.SeedSeries("Berserk");
        SeedReader(alice, seriesId, finished: true);
        SeedReader(bob, seriesId, finished: true);
        SeedState(alice, seriesId, rating: 9);

        Assert.False(await Offered(alice, seriesId));
        Assert.True(await Offered(bob, seriesId));
    }

    [Fact]
    public async Task Dismissing_stamps_the_callers_row_and_hides_it_only_for_them()
    {
        var alice = _db.SeedUser("alice");
        var bob = _db.SeedUser("bob");
        var seriesId = _db.SeedSeries("Berserk");
        SeedReader(alice, seriesId, finished: true);
        SeedReader(bob, seriesId, finished: true);

        using (var db = _db.NewContext(alice))
        {
            Assert.IsType<NoContentResult>(await Controller(db).DismissRateHint(seriesId, default));
        }

        Assert.False(await Offered(alice, seriesId));
        Assert.True(await Offered(bob, seriesId));

        using var check = _db.NewContext();
        var state = Assert.Single(check.UserSeriesStates.IgnoreQueryFilters().ToList());
        Assert.Equal(alice, state.UserId);
        Assert.Null(state.Rating);
        Assert.NotNull(state.RateHintDismissedAt);
    }

    [Fact]
    public async Task Dismissing_an_unknown_series_is_not_found()
    {
        var alice = _db.SeedUser("alice");
        using var db = _db.NewContext(alice);
        Assert.IsType<NotFoundResult>(await Controller(db).DismissRateHint(404, default));
    }
}
