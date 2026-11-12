using Maki.Api.Controllers;
using Maki.Api.Services.Upscaling;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>The per-series "Prepare ahead" choice, stored in the caller's own state row.</summary>
public sealed class SeriesPrepareAheadControllerTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static SeriesController Controller(Maki.Data.MakiDbContext db) =>
        new(new TestLocalizer(), db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);

    private static UpscalePrepareService Planner() =>
        new(null!, null!, null!, null!, null!, TimeProvider.System, NullLogger<UpscalePrepareService>.Instance);

    [Fact]
    public async Task Sets_and_clears_the_count_in_the_callers_row_and_keeps_the_rest()
    {
        var alice = _db.SeedUser("alice");
        var seriesId = _db.SeedSeries();
        using (var db = _db.NewContext(alice))
        {
            await Controller(db).SetNotificationMode(seriesId, new SeriesController.SetSeriesNotificationsRequest("Muted"), default);
            Assert.IsType<OkObjectResult>(await Controller(db).SetPrepareAhead(
                seriesId, new SeriesController.SetPrepareAheadRequest(12), Planner(), default));
        }

        using (var check = _db.NewContext())
        {
            var state = Assert.Single(check.UserSeriesStates.IgnoreQueryFilters().ToList());
            Assert.Equal((alice, 12), (state.UserId, state.PrepareChapters));
            Assert.Equal(Maki.Core.Entities.SeriesNotificationMode.Muted, state.NotificationMode);
        }

        using (var db = _db.NewContext(alice))
        {
            await Controller(db).SetPrepareAhead(seriesId, new SeriesController.SetPrepareAheadRequest(null), Planner(), default);
        }

        using var after = _db.NewContext();
        Assert.Null(Assert.Single(after.UserSeriesStates.IgnoreQueryFilters().ToList()).PrepareChapters);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(PrepareOptions.MaxSeriesChapters + 1)]
    public async Task An_out_of_range_count_is_refused(int chapters)
    {
        var alice = _db.SeedUser("alice");
        var seriesId = _db.SeedSeries();
        using var db = _db.NewContext(alice);

        var result = await Controller(db).SetPrepareAhead(seriesId, new SeriesController.SetPrepareAheadRequest(chapters), Planner(), default);

        Assert.IsNotType<OkObjectResult>(result);
        Assert.Empty(db.UserSeriesStates.IgnoreQueryFilters().ToList());
    }
}
