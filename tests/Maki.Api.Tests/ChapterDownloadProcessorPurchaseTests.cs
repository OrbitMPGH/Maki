using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Sources;
using Maki.Data;

namespace Maki.Api.Tests;

/// <summary>A source that wants a purchase for a chapter is not retried: the download moves on, or stops.</summary>
[Collection(ConfigDirCollection.Name)]
public class ChapterDownloadProcessorPurchaseTests : IDisposable
{
    private readonly UpgradeWorld _world = new();
    private readonly int _chapterId;
    private int _pageCalls;

    public ChapterDownloadProcessorPurchaseTests()
    {
        _world.Seed();
        Directory.CreateDirectory(_world.Library);
        using (var db = _world.Db.NewContext())
        {
            // The source that wants a purchase is tried first.
            db.SourceMappings.Single(m => m.Id == _world.OfficialMappingId).Priority = 1;
            db.SourceMappings.Single(m => m.Id == _world.AggMappingId).Priority = 2;
            db.SaveChanges();
        }

        (_chapterId, _) = _world.Chapter(1, withFile: false);
        _world.OfficialListing = id => [new SourceChapter(UpgradeWorld.Official, id, "o1", "1", 1m, null, null, "en", null)];
        _world.OfficialPages = _ =>
        {
            Interlocked.Increment(ref _pageCalls);
            throw new ChapterRequiresPurchaseException("Log in via WebView and purchase this chapter to read.");
        };
    }

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task With_no_other_source_the_download_fails_at_once_and_is_not_retried()
    {
        using (var db = _world.Db.NewContext())
        {
            db.SourceMappings.Single(m => m.Id == _world.AggMappingId).Enabled = false;
            db.SaveChanges();
        }

        var id = Enqueue();

        await _world.ProcessAsync(id);

        using var check = _world.Db.NewContext();
        var row = check.DownloadQueue.Single(q => q.Id == id);
        Assert.Equal(QueueStatus.Failed, row.Status);
        Assert.Equal(DownloadFailureReason.NeedsPurchase, row.ErrorKey);
        Assert.Contains("purchase", row.ErrorMessage);
        Assert.Null(row.NextAttempt);
        Assert.Equal(1, _pageCalls);
    }

    [Fact]
    public async Task The_automatic_retry_leaves_a_purchase_failure_alone_but_a_manual_retry_picks_it_up()
    {
        using (var db = _world.Db.NewContext())
        {
            db.SourceMappings.Single(m => m.Id == _world.AggMappingId).Enabled = false;
            db.SaveChanges();
        }

        var id = Enqueue();
        await _world.ProcessAsync(id);

        // Its retry count never grows, so a sweep that took it would retry it every five minutes forever.
        Assert.Equal(0, await _world.Queue.RequeueEligibleFailuresAsync(5));
        Assert.Equal(1, await _world.Queue.RetryFailedAsync(null, null, new DataScope()));
        using var check = _world.Db.NewContext();
        Assert.Equal(QueueStatus.Queued, check.DownloadQueue.Single(q => q.Id == id).Status);
    }

    [Fact]
    public async Task A_purchase_failure_that_moves_on_is_still_held_against_the_source_that_asked()
    {
        ServeFromTheAggregator();
        var id = Enqueue();

        await _world.ProcessAsync(id);

        using var check = _world.Db.NewContext();
        var failure = Assert.Single(check.SourceFailures);
        Assert.Equal(_world.OfficialMappingId, failure.SourceMappingId);
        Assert.Equal(DownloadFailureReason.NeedsPurchase, failure.ErrorKey);
    }

    [Fact]
    public async Task A_source_error_is_held_against_the_source_but_a_disk_error_is_not()
    {
        _world.OfficialPages = _ => throw new SourceErrorException("Internal server error");
        var sourceError = Enqueue();
        await _world.ProcessAsync(sourceError);

        _world.OfficialPages = _ => throw new IOException("Disk full");
        var diskError = Enqueue();
        await _world.ProcessAsync(diskError);

        using var check = _world.Db.NewContext();
        var failure = Assert.Single(check.SourceFailures);
        Assert.Equal(_world.OfficialMappingId, failure.SourceMappingId);
        Assert.Equal(DownloadFailureReason.SourceError, failure.ErrorKey);
    }

    private int Enqueue(Action<DownloadQueueItem>? configure = null)
    {
        using var db = _world.Db.NewContext();
        var item = new DownloadQueueItem
        {
            SeriesId = _world.SeriesId, ChapterId = _chapterId, Protocol = AcquisitionProtocol.Scraper,
            Status = QueueStatus.Queued, QueuedAt = DateTime.UtcNow
        };
        configure?.Invoke(item);
        db.DownloadQueue.Add(item);
        db.SaveChanges();
        return item.Id;
    }

    private int SeedHealthRepair()
    {
        using var db = _world.Db.NewContext();
        var file = new HealthFile
        {
            RootFolderId = db.Series.Single(s => s.Id == _world.SeriesId).RootFolderId, SeriesId = _world.SeriesId,
            RelativePath = "repair.cbz"
        };
        db.HealthFiles.Add(file);
        db.SaveChanges();
        var operation = new HealthOperation { Kind = "repair", Status = "downloading", FileId = file.Id, Version = file.Version, UserId = 1 };
        db.HealthOperations.Add(operation);
        db.SaveChanges();
        return operation.Id;
    }

    private int _aggregatorCalls;

    private void ServeFromTheAggregator()
    {
        var pages = UpgradeWorld.InlinePages(12, 700);
        _world.AggListing = id => [new SourceChapter(UpgradeWorld.Agg, id, "a1", "1", 1m, null, null, "en", null)];
        _world.AggPages = chapter =>
        {
            Interlocked.Increment(ref _aggregatorCalls);
            return pages(chapter);
        };
    }

    [Fact]
    public async Task With_another_source_the_download_moves_to_it_and_completes()
    {
        ServeFromTheAggregator();
        var id = Enqueue();

        await _world.ProcessAsync(id);

        using var check = _world.Db.NewContext();
        var row = check.DownloadQueue.Single(q => q.Id == id);
        Assert.True(row.Status == QueueStatus.Completed, $"{row.Status} {row.ErrorKey} {row.ErrorMessage}");
        Assert.Equal(_world.AggMappingId, row.SourceMappingId);
        Assert.Equal(1, _pageCalls);
        Assert.Equal(1, _aggregatorCalls);
    }

    public static TheoryData<string> ProtectedDownloads => new() { "pinned", "health repair", "upgrade" };

    [Theory]
    [MemberData(nameof(ProtectedDownloads))]
    public async Task A_download_tied_to_its_source_is_not_moved_to_another_one(string kind)
    {
        ServeFromTheAggregator();
        var id = Enqueue(item =>
        {
            switch (kind)
            {
                case "pinned": item.PreferredMappingId = _world.OfficialMappingId; break;
                case "health repair":
                    // A repair is tied to the source and chapter the user approved.
                    item.HealthOperationId = SeedHealthRepair();
                    item.SourceMappingId = _world.OfficialMappingId;
                    item.SourceChapterId = "o1";
                    break;
                default: item.UpgradeInfoJson = "{}"; break;
            }
        });

        await _world.ProcessAsync(id);

        using var check = _world.Db.NewContext();
        var row = check.DownloadQueue.Single(q => q.Id == id);
        Assert.Equal(QueueStatus.Failed, row.Status);
        Assert.Equal(1, _pageCalls);
        Assert.Equal(0, _aggregatorCalls);
    }
}
