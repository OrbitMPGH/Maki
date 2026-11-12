using Maki.Api.Controllers;
using Maki.Api.Services.Upscaling;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>The series' "Prepare ahead" setting, set by an editor for every reader of the series.</summary>
public sealed class SeriesPrepareAheadControllerTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static SeriesController Controller(Maki.Data.MakiDbContext db) =>
        new(new TestLocalizer(), db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);

    private static UpscalePrepareService Planner() =>
        new(null!, null!, null!, null!, null!, TimeProvider.System, NullLogger<UpscalePrepareService>.Instance);

    private int? Stored(int seriesId)
    {
        using var db = _db.NewContext();
        return db.Series.Where(s => s.Id == seriesId).Select(s => s.PrepareChapters).Single();
    }

    [Fact]
    public async Task Sets_and_clears_the_count_on_the_series()
    {
        var editor = _db.SeedUser("editor");
        var seriesId = _db.SeedSeries();
        using (var db = _db.NewContext(editor))
        {
            Assert.IsType<OkObjectResult>(await Controller(db).SetPrepareAhead(
                seriesId, new SeriesController.SetPrepareAheadRequest(12), Planner(), default));
        }

        Assert.Equal(12, Stored(seriesId));

        using (var db = _db.NewContext(editor))
        {
            await Controller(db).SetPrepareAhead(seriesId, new SeriesController.SetPrepareAheadRequest(null), Planner(), default);
        }

        Assert.Null(Stored(seriesId));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(PrepareOptions.MaxSeriesChapters + 1)]
    public async Task An_out_of_range_count_is_refused(int chapters)
    {
        var editor = _db.SeedUser("editor");
        var seriesId = _db.SeedSeries();
        using var db = _db.NewContext(editor);

        var result = await Controller(db).SetPrepareAhead(seriesId, new SeriesController.SetPrepareAheadRequest(chapters), Planner(), default);

        Assert.IsNotType<OkObjectResult>(result);
        Assert.Null(Stored(seriesId));
    }
}
