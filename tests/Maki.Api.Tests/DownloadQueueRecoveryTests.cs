using Microsoft.Extensions.Logging.Abstractions;
using Maki.Api.Services;
using Maki.Core.Entities;

namespace Maki.Api.Tests;

/// <summary>
/// Dispatch paths that used to strand the whole queue: a claimable row with no mapping, and a row
/// left in an in-flight status by an owner that died.
/// </summary>
public class DownloadQueueRecoveryTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly TestDb _db = new();
    private readonly DownloadQueueService _queue;

    public DownloadQueueRecoveryTests() => _queue = new DownloadQueueService(
        _db.ScopeFactory(), new StoppedClock(T0), Sources.SingleChapterResolver(null, "fake"),
        NullLogger<DownloadQueueService>.Instance);

    public void Dispose() => _db.Dispose();

    /// <summary>Seeds one queue row in the given state; returns its id.</summary>
    private int SeedItem(QueueStatus status, bool withMapping)
    {
        var seriesId = _db.SeedSeries(mappings: withMapping
            ? [new SourceMapping { SourceName = "fake", SourceSeriesId = "s", Url = "https://fake.test", Priority = 1 }]
            : []);

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
            SourceMappingId = withMapping ? db.SourceMappings.Single(m => m.SeriesId == seriesId).Id : null,
            QueuedAt = T0.UtcDateTime
        };
        db.DownloadQueue.Add(item);
        db.SaveChanges();
        return item.Id;
    }

    /// <summary>
    /// A failed resolve leaves the row Queued with no mapping (and deleting a mapping nulls the
    /// column outright). Looking its source up in the cooldown dictionary threw ArgumentNullException
    /// straight out of the worker loop, silently killing every worker until the app was restarted.
    /// </summary>
    [Fact]
    public async Task A_queued_item_with_no_mapping_is_claimable_rather_than_fatal()
    {
        var id = SeedItem(QueueStatus.Queued, withMapping: false);

        var claimed = await _queue.ClaimNextAsync();

        Assert.Equal(id, claimed);
        using var db = _db.NewContext();
        Assert.Equal(QueueStatus.FetchingPages, db.DownloadQueue.Single(q => q.Id == id).Status);
    }

    [Fact]
    public async Task A_mappingless_item_does_not_stop_later_items_being_claimed()
    {
        SeedItem(QueueStatus.Queued, withMapping: false);
        var withMapping = SeedItem(QueueStatus.Queued, withMapping: true);

        var first = await _queue.ClaimNextAsync();
        var second = await _queue.ClaimNextAsync();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Contains(withMapping, new[] { first!.Value, second!.Value });
    }

    [Theory]
    [InlineData(QueueStatus.FetchingPages)]
    [InlineData(QueueStatus.Downloading)]
    [InlineData(QueueStatus.Validating)]
    [InlineData(QueueStatus.Packaging)]
    [InlineData(QueueStatus.Importing)]
    public async Task Sweep_requeues_an_in_flight_row_that_no_worker_owns(QueueStatus stranded)
    {
        var id = SeedItem(stranded, withMapping: true);

        var swept = await _queue.SweepOrphanedAsync();

        Assert.Equal(1, swept);
        using var db = _db.NewContext();
        Assert.Equal(QueueStatus.Queued, db.DownloadQueue.Single(q => q.Id == id).Status);
        Assert.True(_queue.Reader.TryRead(out var signalled));
        Assert.Equal(id, signalled);
    }

    [Fact]
    public async Task Sweep_leaves_a_row_a_worker_still_owns_alone()
    {
        SeedItem(QueueStatus.Queued, withMapping: true);
        var claimed = await _queue.ClaimNextAsync();

        var swept = await _queue.SweepOrphanedAsync();

        Assert.Equal(0, swept);
        using var db = _db.NewContext();
        Assert.Equal(QueueStatus.FetchingPages, db.DownloadQueue.Single(q => q.Id == claimed).Status);
    }

    [Fact]
    public async Task Releasing_a_claim_makes_the_row_sweepable_again()
    {
        SeedItem(QueueStatus.Queued, withMapping: true);
        var claimed = await _queue.ClaimNextAsync();
        _queue.ReleaseClaim(claimed!.Value);

        Assert.Equal(1, await _queue.SweepOrphanedAsync());
    }

    /// <summary>Torrent items have no worker here at all, so their statuses must be left untouched.</summary>
    [Fact]
    public async Task Sweep_ignores_torrent_items()
    {
        var seriesId = _db.SeedSeries();
        int id;
        using (var db = _db.NewContext())
        {
            var item = new DownloadQueueItem
            {
                SeriesId = seriesId,
                Protocol = AcquisitionProtocol.Torrent,
                Status = QueueStatus.Downloading,
                QueuedAt = T0.UtcDateTime
            };
            db.DownloadQueue.Add(item);
            db.SaveChanges();
            id = item.Id;
        }

        Assert.Equal(0, await _queue.SweepOrphanedAsync());
        using var check = _db.NewContext();
        Assert.Equal(QueueStatus.Downloading, check.DownloadQueue.Single(q => q.Id == id).Status);
    }

    /// <summary>A Resolving row with no chapter can never resolve, so it must settle rather than be swept forever.</summary>
    [Fact]
    public async Task Sweep_fails_a_resolving_row_with_nothing_to_resolve()
    {
        var seriesId = _db.SeedSeries();
        int id;
        using (var db = _db.NewContext())
        {
            var item = new DownloadQueueItem
            {
                SeriesId = seriesId,
                ChapterId = null,
                Protocol = AcquisitionProtocol.Scraper,
                Status = QueueStatus.Resolving,
                QueuedAt = T0.UtcDateTime
            };
            db.DownloadQueue.Add(item);
            db.SaveChanges();
            id = item.Id;
        }

        await _queue.SweepOrphanedAsync();

        using var check = _db.NewContext();
        Assert.Equal(QueueStatus.Failed, check.DownloadQueue.Single(q => q.Id == id).Status);
        Assert.Equal(0, await _queue.SweepOrphanedAsync());
    }
}
