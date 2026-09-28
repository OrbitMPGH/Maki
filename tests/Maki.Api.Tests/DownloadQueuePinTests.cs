using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Sources;

namespace Maki.Api.Tests;

/// <summary>
/// "Find better copy": a download pinned to one source mapping via
/// <see cref="DownloadQueueService.EnqueueChapterAsync"/>'s <c>preferMappingId</c>.
/// </summary>
public class DownloadQueuePinTests : IDisposable
{
    private readonly TestDb _db = new();
    public void Dispose() => _db.Dispose();

    private DownloadQueueService Queue(params string[] listing) => Queue(null, listing);

    /// <summary><paramref name="onList"/> runs before a source answers its listing, to hold one up.</summary>
    private DownloadQueueService Queue(Action<string>? onList, params string[] listing) => new(
        _db.ScopeFactory(), TimeProvider.System,
        Sources.Resolver(new SourceRegistry(new[] { "high", "low", "empty" }.Select(name => new FakeSource
        {
            Name = name,
            OnListChapters = _ =>
            {
                onList?.Invoke(name);
                return listing.Contains(name) ? [new(name, "s", "1", "1", 1m, null, null, "en", null)] : [];
            },
        }))),
        NullLogger<DownloadQueueService>.Instance);

    private ChapterController Controller(DownloadQueueService queue) => new(
        new TestLocalizer(), _db.NewContext(), queue, null!, null!, new SourceRegistry([]),
        new SourceChapterListCache(TimeProvider.System, NullLogger<SourceChapterListCache>.Instance),
        new DownloadBatchNotifier(
            new RecordingNotifications(), new RecordingInbox(), new TestLocalizer(),
            new TestUserLocaleResolver(), TimeProvider.System,
            NullLogger<DownloadBatchNotifier>.Instance),
        new TestCurrentUser(1), NullLogger<ChapterController>.Instance);

    private int SeedRow(int seriesId, int chapterId, QueueStatus status, int? mappingId = null, int? pin = null)
    {
        using var db = _db.NewContext();
        var row = new DownloadQueueItem
        {
            SeriesId = seriesId, ChapterId = chapterId, Protocol = AcquisitionProtocol.Scraper,
            Status = status, SourceMappingId = mappingId, PreferredMappingId = pin, QueuedAt = DateTime.UtcNow,
        };
        db.DownloadQueue.Add(row);
        db.SaveChanges();
        return row.Id;
    }

    private (int Series, int Chapter, int High, int Low, int Empty) Seed()
    {
        var seriesId = _db.SeedSeries(mappings:
        [
            new SourceMapping { SourceName = "high", SourceSeriesId = "s", Url = "u", Priority = 1, Enabled = true },
            new SourceMapping { SourceName = "low", SourceSeriesId = "s", Url = "u", Priority = 5, Enabled = true },
            new SourceMapping { SourceName = "empty", SourceSeriesId = "s", Url = "u", Priority = 9, Enabled = true },
        ]);
        using var db = _db.NewContext();
        var chapter = new Chapter { SeriesId = seriesId, Number = 1m, Language = "en" };
        db.Chapters.Add(chapter);
        db.SaveChanges();
        var m = db.SourceMappings.Where(x => x.SeriesId == seriesId).ToDictionary(x => x.SourceName, x => x.Id);
        return (seriesId, chapter.Id, m["high"], m["low"], m["empty"]);
    }

    private async Task<DownloadQueueItem> SettledRow(int id)
    {
        for (var i = 0; i < 100; i++)
        {
            using var db = _db.NewContext();
            var row = db.DownloadQueue.Single(q => q.Id == id);
            if (row.Status != QueueStatus.Resolving) return row;
            await Task.Delay(20);
        }
        throw new TimeoutException();
    }

    [Fact]
    public async Task New_pinned_item_resolves_to_the_pinned_lower_priority_mapping()
    {
        var s = Seed();
        var queue = Queue("high", "low");

        var item = await queue.EnqueueChapterAsync(s.Chapter, preferMappingId: s.Low);
        var row = await SettledRow(item!.Id);

        Assert.Equal(QueueStatus.Queued, row.Status);
        Assert.Equal(s.Low, row.SourceMappingId);
        Assert.Equal(s.Low, row.PreferredMappingId);
    }

    [Fact]
    public async Task Pin_overrides_a_queued_row_in_place()
    {
        var s = Seed();
        var queue = Queue("high", "low");
        var first = await queue.EnqueueChapterAsync(s.Chapter);
        var settled = await SettledRow(first!.Id);
        Assert.Equal(s.High, settled.SourceMappingId);

        var second = await queue.EnqueueChapterAsync(s.Chapter, preferMappingId: s.Low);
        var row = await SettledRow(second!.Id);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(QueueStatus.Queued, row.Status);
        Assert.Equal(s.Low, row.SourceMappingId);
        using var db = _db.NewContext();
        Assert.Equal(1, db.DownloadQueue.Count(q => q.ChapterId == s.Chapter));
    }

    [Fact]
    public async Task Pin_on_an_in_flight_row_returns_it_unchanged()
    {
        var s = Seed();
        var queue = Queue("high", "low");
        var id = SeedRow(s.Series, s.Chapter, QueueStatus.Downloading, s.High);

        var result = await queue.EnqueueChapterAsync(s.Chapter, preferMappingId: s.Low);

        Assert.Equal(id, result!.Id);
        using var check = _db.NewContext();
        var after = check.DownloadQueue.Single(q => q.Id == id);
        Assert.Equal(QueueStatus.Downloading, after.Status);
        Assert.Equal(s.High, after.SourceMappingId);
        Assert.Null(after.PreferredMappingId);
    }

    [Fact]
    public async Task Pinned_mapping_that_lacks_the_chapter_fails_instead_of_falling_back()
    {
        var s = Seed();
        var queue = Queue("high", "low");

        var item = await queue.EnqueueChapterAsync(s.Chapter, preferMappingId: s.Empty);
        var row = await SettledRow(item!.Id);

        Assert.Equal(QueueStatus.Failed, row.Status);
        Assert.Null(row.SourceMappingId);
        Assert.Equal("error.download.pickedSourceUnavailable", row.ErrorKey);
    }

    [Fact]
    public async Task A_pick_landing_mid_resolve_is_picked_up_by_that_resolve()
    {
        var s = Seed();
        using var listingHigh = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var queue = Queue(name =>
        {
            if (name == "high")
            {
                listingHigh.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        }, "high", "low");
        var id = SeedRow(s.Series, s.Chapter, QueueStatus.Resolving, pin: s.High);

        var inFlight = Task.Run(() => queue.ResolveAndActivateAsync(id, s.Chapter, CancellationToken.None));
        Assert.True(listingHigh.Wait(TimeSpan.FromSeconds(10)));

        await queue.EnqueueChapterAsync(s.Chapter, preferMappingId: s.Low);
        release.Set();
        await inFlight;
        var row = await SettledRow(id);

        Assert.Equal(QueueStatus.Queued, row.Status);
        Assert.Equal(s.Low, row.SourceMappingId);
        Assert.Equal(s.Low, row.PreferredMappingId);
    }

    [Fact]
    public async Task Download_from_reports_a_conflict_when_the_pick_could_not_be_applied()
    {
        var s = Seed();
        var queue = Queue("high", "low");
        SeedRow(s.Series, s.Chapter, QueueStatus.Downloading, s.High);

        var result = await Controller(queue).DownloadFrom(
            s.Chapter, new DownloadChapterFromRequest(s.Low), CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public async Task Download_from_accepts_a_pick_on_a_queued_row()
    {
        var s = Seed();
        var queue = Queue("high", "low");
        SeedRow(s.Series, s.Chapter, QueueStatus.Queued, s.High);

        var result = await Controller(queue).DownloadFrom(
            s.Chapter, new DownloadChapterFromRequest(s.Low), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
    }
}
