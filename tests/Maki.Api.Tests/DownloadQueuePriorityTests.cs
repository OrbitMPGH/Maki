using Microsoft.Extensions.Logging.Abstractions;
using Maki.Api.Services;
using Maki.Core.Entities;

namespace Maki.Api.Tests;

/// <summary>
/// Claim classes: new releases, Smart and a single clicked chapter go ahead of a "Download all"
/// backlog whatever their <see cref="DownloadQueueItem.SortOrder"/>, and a manual move still counts.
/// </summary>
public class DownloadQueuePriorityTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly DownloadQueueService _queue;

    public DownloadQueuePriorityTests()
    {
        _queue = new DownloadQueueService(
            _db.ScopeFactory(), TimeProvider.System, Sources.SingleChapterResolver(null, "a"),
            NullLogger<DownloadQueueService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    private int SeedItem(QueuePriority priority, int sortOrder, QueueStatus status = QueueStatus.Queued)
    {
        var seriesId = _db.SeedSeries(mappings:
            [new SourceMapping { SourceName = "a", SourceSeriesId = "s", Url = "https://a.test", Priority = 1 }]);
        using var db = _db.NewContext();
        var chapter = new Chapter { SeriesId = seriesId, Number = 1m, Language = "en" };
        db.Chapters.Add(chapter);
        db.SaveChanges();
        var item = new DownloadQueueItem
        {
            SeriesId = seriesId,
            ChapterId = chapter.Id,
            Protocol = AcquisitionProtocol.Scraper,
            Status = status,
            SourceMappingId = db.SourceMappings.Single(m => m.SeriesId == seriesId).Id,
            SortOrder = sortOrder,
            Priority = priority,
            QueuedAt = DateTime.UtcNow,
        };
        db.DownloadQueue.Add(item);
        db.SaveChanges();
        return item.Id;
    }

    private DownloadQueueItem Row(int id)
    {
        using var db = _db.NewContext();
        return db.DownloadQueue.Single(q => q.Id == id);
    }

    private (int SeriesId, List<int> ChapterIds) SeedChapters(int count)
    {
        var seriesId = _db.SeedSeries(mappings:
            [new SourceMapping { SourceName = "a", SourceSeriesId = "s", Url = "https://a.test", Priority = 1 }]);
        using var db = _db.NewContext();
        var chapters = Enumerable.Range(1, count)
            .Select(n => new Chapter { SeriesId = seriesId, Number = n, Language = "en" })
            .ToList();
        db.Chapters.AddRange(chapters);
        db.SaveChanges();
        return (seriesId, chapters.Select(c => c.Id).ToList());
    }

    [Theory]
    [InlineData(DownloadOrigin.MonitorRefresh, false, QueuePriority.High)]
    [InlineData(DownloadOrigin.SmartDownload, true, QueuePriority.High)]
    [InlineData(DownloadOrigin.Manual, false, QueuePriority.High)]
    [InlineData(DownloadOrigin.Manual, true, QueuePriority.Low)]
    [InlineData(DownloadOrigin.Upgrade, false, QueuePriority.Low)]
    [InlineData(DownloadOrigin.RequestApproval, true, QueuePriority.Normal)]
    [InlineData(DownloadOrigin.HealthRepair, false, QueuePriority.Normal)]
    [InlineData(DownloadOrigin.Unknown, false, QueuePriority.Normal)]
    public void Each_origin_takes_its_class(DownloadOrigin origin, bool bulk, QueuePriority expected) =>
        Assert.Equal(expected, QueuePriorities.For(origin, bulk));

    [Fact]
    public async Task A_new_release_is_claimed_before_an_older_backlog()
    {
        var backlog = SeedItem(QueuePriority.Low, sortOrder: 1);
        var normal = SeedItem(QueuePriority.Normal, sortOrder: 2);
        var release = SeedItem(QueuePriority.High, sortOrder: 500);

        Assert.Equal(release, await _queue.ClaimNextAsync());
        Assert.Equal(normal, await _queue.ClaimNextAsync());
        Assert.Equal(backlog, await _queue.ClaimNextAsync());
    }

    [Fact]
    public async Task A_bulk_manual_enqueue_is_backlog_and_a_single_one_is_not()
    {
        var (_, many) = SeedChapters(3);
        var (_, one) = SeedChapters(1);

        var bulk = await _queue.EnqueueChaptersAsync(many, DownloadOrigin.Manual, null);
        var single = await _queue.EnqueueChaptersAsync(one, DownloadOrigin.Manual, null);

        Assert.All(bulk.Queued, item => Assert.Equal(QueuePriority.Low, Row(item.Id).Priority));
        Assert.Equal(QueuePriority.High, Row(Assert.Single(single.Queued).Id).Priority);
    }

    private int QueueRow(int seriesId, int chapterId, QueuePriority priority)
    {
        using var db = _db.NewContext();
        var item = new DownloadQueueItem
        {
            SeriesId = seriesId,
            ChapterId = chapterId,
            Protocol = AcquisitionProtocol.Scraper,
            Status = QueueStatus.Queued,
            Priority = priority,
            QueuedAt = DateTime.UtcNow,
        };
        db.DownloadQueue.Add(item);
        db.SaveChanges();
        return item.Id;
    }

    [Fact]
    public async Task Clicking_a_chapter_already_in_the_backlog_raises_it()
    {
        var (seriesId, chapters) = SeedChapters(2);
        var other = QueueRow(seriesId, chapters[0], QueuePriority.Low);
        var target = QueueRow(seriesId, chapters[1], QueuePriority.Low);

        Assert.Null(await _queue.EnqueueChapterAsync(chapters[1], origin: DownloadOrigin.Manual));

        Assert.Equal(QueuePriority.High, Row(target).Priority);
        Assert.Equal(QueuePriority.Low, Row(other).Priority);
    }

    [Fact]
    public async Task A_backlog_enqueue_never_lowers_a_row_and_raises_none_above_its_class()
    {
        var (seriesId, chapters) = SeedChapters(3);
        var clicked = QueueRow(seriesId, chapters[0], QueuePriority.High);
        var upgrade = QueueRow(seriesId, chapters[1], QueuePriority.Low);

        await _queue.EnqueueChaptersAsync(chapters, DownloadOrigin.Manual, null);

        Assert.Equal(QueuePriority.High, Row(clicked).Priority);
        Assert.Equal(QueuePriority.Low, Row(upgrade).Priority);
    }

    [Fact]
    public async Task A_new_release_for_a_chapter_in_the_backlog_raises_it()
    {
        var (seriesId, chapters) = SeedChapters(2);
        var queued = QueueRow(seriesId, chapters[1], QueuePriority.Low);

        await _queue.EnqueueChaptersAsync(chapters, DownloadOrigin.MonitorRefresh, null);

        Assert.Equal(QueuePriority.High, Row(queued).Priority);
    }

    [Fact]
    public async Task Move_to_top_promotes_the_row_to_high()
    {
        var release = SeedItem(QueuePriority.High, sortOrder: 0);
        var backlog = SeedItem(QueuePriority.Low, sortOrder: 1);

        await _queue.ReorderAsync([backlog, release], movedId: backlog);

        Assert.Equal(QueuePriority.High, Row(backlog).Priority);
        Assert.Equal(backlog, await _queue.ClaimNextAsync());
    }

    [Fact]
    public async Task A_row_moved_across_a_class_takes_the_class_of_the_row_above_it()
    {
        var high = SeedItem(QueuePriority.High, sortOrder: 0);
        var normal = SeedItem(QueuePriority.Normal, sortOrder: 1);
        var low = SeedItem(QueuePriority.Low, sortOrder: 2);

        // "Move down" on the High row swaps it below the Normal one.
        await _queue.ReorderAsync([normal, high, low], movedId: high);
        Assert.Equal(QueuePriority.Normal, Row(high).Priority);

        // "Move up" on the Low row swaps it above the row now second.
        await _queue.ReorderAsync([normal, low, high], movedId: low);
        Assert.Equal(QueuePriority.Normal, Row(low).Priority);

        Assert.Equal(normal, await _queue.ClaimNextAsync());
        Assert.Equal(low, await _queue.ClaimNextAsync());
        Assert.Equal(high, await _queue.ClaimNextAsync());
    }

    [Fact]
    public async Task A_reorder_without_a_moved_row_keeps_every_class()
    {
        var high = SeedItem(QueuePriority.High, sortOrder: 0);
        var low = SeedItem(QueuePriority.Low, sortOrder: 1);

        await _queue.ReorderAsync([low, high]);

        Assert.Equal(QueuePriority.Low, Row(low).Priority);
        Assert.Equal(high, await _queue.ClaimNextAsync());
    }

    [Fact]
    public async Task A_claim_stamps_when_the_download_started()
    {
        var id = SeedItem(QueuePriority.Normal, sortOrder: 0);

        Assert.Equal(id, await _queue.ClaimNextAsync());

        Assert.NotNull(Row(id).StartedAt);
    }
}
