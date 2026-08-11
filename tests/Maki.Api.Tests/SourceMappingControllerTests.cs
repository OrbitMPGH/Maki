using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Sources;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Tests;

/// <summary>
/// Covers the re-runnable auto-match endpoint: what it flags, what it hands to the background
/// worker, and the guards that stop a second click queueing the same series twice.
/// </summary>
public class SourceMappingControllerTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly SourceMatchQueue _queue = new();

    public void Dispose() => _db.Dispose();

    private SourceMappingController BuildController() =>
        new(_db.NewContext(), new SourceRegistry([new FakeSource { Name = "fake" }]),
            new FakeAppSettings(), Sources.AllEnabled, _queue);

    /// <summary>Everything the worker was handed, in order.</summary>
    private List<int> Queued()
    {
        var ids = new List<int>();
        while (_queue.Reader.TryRead(out var id))
        {
            ids.Add(id);
        }
        return ids;
    }

    private bool PendingOf(int seriesId)
    {
        using var db = _db.NewContext();
        return db.Series.Single(s => s.Id == seriesId).SourceMatchPending;
    }

    private static int QueuedCount(IActionResult result) =>
        (int)Assert.IsType<OkObjectResult>(result).Value!.GetType()
            .GetProperty("queued")!.GetValue(Assert.IsType<OkObjectResult>(result).Value)!;

    [Fact]
    public async Task Flags_the_series_and_hands_it_to_the_worker()
    {
        var first = _db.SeedSeries("Hajime no Ippo");
        var second = _db.SeedSeries("Berserk");

        var result = await BuildController().AutoMatch(new([first, second]), default);

        Assert.Equal(2, QueuedCount(result));
        Assert.Equal([first, second], Queued());
        Assert.True(PendingOf(first));
        Assert.True(PendingOf(second));
    }

    [Fact]
    public async Task Series_already_matching_is_not_queued_again()
    {
        // A second click, or the same series caught by both a bulk run and a single one: the flag
        // is the record of what's owed, and the worker would drop the duplicate anyway.
        var seriesId = _db.SeedSeries("Hajime no Ippo", configure: s => s.SourceMatchPending = true);

        var result = await BuildController().AutoMatch(new([seriesId]), default);

        Assert.Equal(0, QueuedCount(result));
        Assert.Empty(Queued());
    }

    [Fact]
    public async Task Duplicate_ids_in_one_request_queue_once()
    {
        var seriesId = _db.SeedSeries("Hajime no Ippo");

        var result = await BuildController().AutoMatch(new([seriesId, seriesId]), default);

        Assert.Equal(1, QueuedCount(result));
        Assert.Equal([seriesId], Queued());
    }

    [Fact]
    public async Task Unknown_ids_are_ignored_rather_than_failing_the_batch()
    {
        var seriesId = _db.SeedSeries("Hajime no Ippo");

        var result = await BuildController().AutoMatch(new([seriesId, 9999]), default);

        Assert.Equal(1, QueuedCount(result));
        Assert.Equal([seriesId], Queued());
    }

    [Fact]
    public async Task Empty_request_is_rejected()
    {
        var result = await BuildController().AutoMatch(new([]), default);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(Queued());
    }

    [Fact]
    public async Task Existing_mappings_are_left_alone()
    {
        // The endpoint only ever flags the row; nothing already linked is touched, which is what
        // makes re-running safe on a series that's already partly matched.
        var seriesId = _db.SeedSeries("Hajime no Ippo", mappings: new SourceMapping
        {
            SourceName = "fake", SourceSeriesId = "existing", Url = "https://fake.test/s"
        });

        await BuildController().AutoMatch(new([seriesId]), default);

        using var db = _db.NewContext();
        Assert.Equal("existing", db.SourceMappings.Single(m => m.SeriesId == seriesId).SourceSeriesId);
    }
}
