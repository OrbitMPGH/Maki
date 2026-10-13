using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Sources;

namespace Maki.Api.Tests;

/// <summary>
/// A row with no resolved source must not be resolved onto a source that is cooling down or paused.
/// When only such a source lists the chapter, the row waits on it instead of downloading from it.
/// </summary>
[Collection(ConfigDirCollection.Name)]
public class ChapterDownloadProcessorHeldSourceTests : IDisposable
{
    private readonly UpgradeWorld _world = new();
    private readonly int _chapterId;
    private int _officialPageCalls;

    public ChapterDownloadProcessorHeldSourceTests()
    {
        _world.Seed();
        Directory.CreateDirectory(_world.Library);
        (_chapterId, _) = _world.Chapter(1, withFile: false);
        _world.OfficialListing = id => [new SourceChapter(UpgradeWorld.Official, id, "o1", "1", 1m, null, null, "en", null)];
        var pages = UpgradeWorld.UrlPages(2);
        _world.OfficialPages = c =>
        {
            Interlocked.Increment(ref _officialPageCalls);
            return pages(c);
        };
    }

    public void Dispose() => _world.Dispose();

    private int QueueUnresolved()
    {
        using var db = _world.Db.NewContext();
        var item = new DownloadQueueItem
        {
            SeriesId = _world.SeriesId, ChapterId = _chapterId, Protocol = AcquisitionProtocol.Scraper,
            Status = QueueStatus.Queued, QueuedAt = DateTime.UtcNow
        };
        db.DownloadQueue.Add(item);
        db.SaveChanges();
        return item.Id;
    }

    [Fact]
    public async Task A_row_only_a_cooling_down_source_lists_waits_on_that_source()
    {
        var until = _world.Queue.EnterRateLimitCooldown(UpgradeWorld.Official, TimeSpan.FromMinutes(10));
        var id = QueueUnresolved();

        Assert.Equal(DownloadOutcome.RateLimited, await _world.ProcessAsync(id));

        using var db = _world.Db.NewContext();
        var row = db.DownloadQueue.Single(q => q.Id == id);
        Assert.Equal(QueueStatus.RateLimited, row.Status);
        Assert.Equal(_world.OfficialMappingId, row.SourceMappingId);
        Assert.Null(row.SourceChapterId);
        Assert.Equal(until, row.NextAttempt);
        Assert.Equal(0, _officialPageCalls);
    }
}
