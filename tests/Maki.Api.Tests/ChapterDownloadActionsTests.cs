using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Sources;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// The two actions that replace unticking chapters as the way to download a series a bit at a time:
/// <c>SeriesController.DownloadNext</c> (the toolbar's "next N") and
/// <c>ChapterController.DownloadBulk</c> (the chapter table's select-mode Download).
/// </summary>
public class ChapterDownloadActionsTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly DownloadBatchNotifier _batches = new(
        new RecordingNotifications(), new RecordingInbox(), TimeProvider.System,
        NullLogger<DownloadBatchNotifier>.Instance);

    public void Dispose()
    {
        _batches.Dispose();
        _db.Dispose();
    }

    private DownloadQueueService Queue()
    {
        var registry = new SourceRegistry([new FakeSource
        {
            Name = "fake",
            OnListChapters = _ => [.. Enumerable.Range(1, 40).Select(n =>
                new FakeSource { Name = "fake" }.Chapter(n))],
        }]);
        return new DownloadQueueService(
            _db.ScopeFactory(), TimeProvider.System, Sources.Resolver(registry),
            NullLogger<DownloadQueueService>.Instance);
    }

    private int SeedSeries() => _db.SeedSeries(
        mappings: new SourceMapping { SourceName = "fake", SourceSeriesId = "series", Url = "u", Enabled = true });

    /// <summary>Adds chapters, optionally marking some unwanted or already on disk.</summary>
    private void SeedChapters(int seriesId, decimal[] numbers, decimal[]? unwanted = null, decimal[]? onDisk = null)
    {
        using var db = _db.NewContext();
        foreach (var n in numbers)
        {
            int? fileId = null;
            if (onDisk?.Contains(n) == true)
            {
                var file = new ChapterFile { SeriesId = seriesId, RelativePath = $"ch-{n}.cbz", DateAdded = DateTime.UtcNow };
                db.ChapterFiles.Add(file);
                db.SaveChanges();
                fileId = file.Id;
            }

            db.Chapters.Add(new Chapter
            {
                SeriesId = seriesId,
                Number = n,
                Language = "en",
                Wanted = unwanted?.Contains(n) != true,
                ChapterFileId = fileId,
            });
        }
        db.SaveChanges();
    }

    private List<decimal?> QueuedNumbers(int seriesId)
    {
        using var db = _db.NewContext();
        return
        [
            .. db.DownloadQueue
                .Where(q => q.SeriesId == seriesId)
                .Join(db.Chapters, q => q.ChapterId, c => c.Id, (q, c) => new { q.Id, c.Number })
                .OrderBy(x => x.Id)
                .Select(x => x.Number)
        ];
    }

    /// <summary>Only the queue, the batch notifier and the current user are reached from here.</summary>
    private SeriesController SeriesController(DownloadQueueService queue) => new(
        db: _db.NewContext(),
        coverService: null!,
        chapterSyncService: null!,
        cbzLinkService: null!,
        seriesCreation: null!,
        seriesRename: null!,
        metadataRefresh: null!,
        downloadQueue: queue,
        downloadBatches: _batches,
        appSettings: null!,
        kavitaScans: null!,
        scrobbler: null!,
        stats: null!,
        mangaBakaStore: null!,
        similarSeries: null!,
        archives: null!,
        sourceAvailability: null!,
        currentUser: new TestCurrentUser(1),
        logger: NullLogger<SeriesController>.Instance);

    private ChapterController ChapterController(DownloadQueueService queue) => new(
        _db.NewContext(), queue, null!, null!, new SourceRegistry([]),
        new SourceChapterListCache(TimeProvider.System, NullLogger<SourceChapterListCache>.Instance),
        _batches, new TestCurrentUser(1), NullLogger<ChapterController>.Instance);

    [Fact]
    public async Task Download_next_takes_the_lowest_numbered_wanted_chapters()
    {
        var seriesId = SeedSeries();
        // Deliberately out of order on disk: the old Smart selector took DB order, so a source
        // listing newest-first handed back the wrong "next" chapters.
        SeedChapters(seriesId, [5m, 1m, 3m, 2m, 4m]);

        var result = await SeriesController(Queue())
            .DownloadNext(seriesId, new Controllers.SeriesController.DownloadNextRequest(3), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal([1m, 2m, 3m], QueuedNumbers(seriesId));
    }

    [Fact]
    public async Task Download_next_skips_unwanted_and_already_downloaded_chapters()
    {
        var seriesId = SeedSeries();
        SeedChapters(seriesId, [1m, 2m, 3m, 4m], unwanted: [2m], onDisk: [1m]);

        await SeriesController(Queue())
            .DownloadNext(seriesId, new Controllers.SeriesController.DownloadNextRequest(10), CancellationToken.None);

        Assert.Equal([3m, 4m], QueuedNumbers(seriesId));
    }

    [Fact]
    public async Task Download_next_rejects_a_count_below_one()
    {
        var seriesId = SeedSeries();
        SeedChapters(seriesId, [1m]);

        var result = await SeriesController(Queue())
            .DownloadNext(seriesId, new Controllers.SeriesController.DownloadNextRequest(0), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(QueuedNumbers(seriesId));
    }

    [Fact]
    public async Task Download_next_on_a_missing_series_is_a_404()
    {
        var result = await SeriesController(Queue())
            .DownloadNext(9999, new Controllers.SeriesController.DownloadNextRequest(5), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    /// <summary>
    /// Picking rows by hand is a more explicit statement than the switch is, so the bulk action
    /// queues an unwanted chapter — the same way the per-row download button always has.
    /// </summary>
    [Fact]
    public async Task Bulk_download_queues_the_selection_including_unwanted_chapters()
    {
        var seriesId = SeedSeries();
        SeedChapters(seriesId, [1m, 2m, 3m], unwanted: [2m]);

        int[] ids;
        using (var db = _db.NewContext())
        {
            ids = [.. db.Chapters.Where(c => c.SeriesId == seriesId).OrderBy(c => c.Number).Select(c => c.Id)];
        }

        var result = await ChapterController(Queue())
            .DownloadBulk(new DownloadChaptersRequest(ids), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal([1m, 2m, 3m], QueuedNumbers(seriesId));
    }

    [Fact]
    public async Task Bulk_download_ignores_chapters_that_are_already_on_disk()
    {
        var seriesId = SeedSeries();
        SeedChapters(seriesId, [1m, 2m], onDisk: [1m]);

        int[] ids;
        using (var db = _db.NewContext())
        {
            ids = [.. db.Chapters.Where(c => c.SeriesId == seriesId).Select(c => c.Id)];
        }

        await ChapterController(Queue()).DownloadBulk(new DownloadChaptersRequest(ids), CancellationToken.None);

        Assert.Equal([2m], QueuedNumbers(seriesId));
    }

    /// <summary>
    /// "All wanted" shares the selector with "next N", so it queues in chapter-number order too.
    /// Queue position follows enqueue order, so without this a bulk grab on a newest-first source
    /// downloads the series backwards and the reader waits for chapter 1 last.
    /// </summary>
    [Fact]
    public async Task Search_missing_queues_every_wanted_chapter_in_number_order()
    {
        var seriesId = SeedSeries();
        SeedChapters(seriesId, [3m, 1m, 4m, 2m], unwanted: [4m], onDisk: [1m]);

        var result = await SeriesController(Queue()).SearchMissing(seriesId, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal([2m, 3m], QueuedNumbers(seriesId));
    }

    [Fact]
    public async Task Bulk_download_rejects_an_empty_selection()
    {
        var result = await ChapterController(Queue())
            .DownloadBulk(new DownloadChaptersRequest([]), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }
}
