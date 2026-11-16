using System.Net;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Sources;

namespace Maki.Api.Tests;

/// <summary>
/// An early-access chapter is taken from another linked source that has it free before the row parks,
/// and a lock never counts against the source that said it.
/// </summary>
[Collection(ConfigDirCollection.Name)]
public class ChapterDownloadProcessorEarlyAccessFailoverTests : IDisposable
{
    private readonly UpgradeWorld _world;
    private readonly int _chapterId;
    private readonly DateTimeOffset _unlockAt = DateTimeOffset.UtcNow.AddDays(2);
    private int _officialPageCalls;

    public ChapterDownloadProcessorEarlyAccessFailoverTests() : this(SourceCapabilities.None)
    {
    }

    private ChapterDownloadProcessorEarlyAccessFailoverTests(SourceCapabilities officialCapabilities)
    {
        _world = new UpgradeWorld(officialCapabilities);
        _world.Seed();
        Directory.CreateDirectory(_world.Library);
        using (var db = _world.Db.NewContext())
        {
            db.SourceMappings.Single(m => m.Id == _world.OfficialMappingId).Priority = 1;
            db.SourceMappings.Single(m => m.Id == _world.AggMappingId).Priority = 2;
            db.SaveChanges();
        }

        (_chapterId, _) = _world.Chapter(1, withFile: false);
        _world.OfficialListing = id => [new SourceChapter(UpgradeWorld.Official, id, "o1", "1", 1m, null, null, "en", null)];
        _world.OfficialPages = _ =>
        {
            Interlocked.Increment(ref _officialPageCalls);
            throw new ChapterLockedException("Early access", _unlockAt);
        };
        _world.AggListing = id => [new SourceChapter(UpgradeWorld.Agg, id, "a1", "1", 1m, null, null, "en", null)];
        _world.AggPages = UpgradeWorld.InlinePages(4, 900);
    }

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task A_locked_chapter_is_downloaded_from_another_source_that_has_it()
    {
        var id = Enqueue(_world.OfficialMappingId, "o1");

        await _world.ProcessAsync(id);

        using var check = _world.Db.NewContext();
        var row = check.DownloadQueue.Single(q => q.Id == id);
        Assert.Equal(QueueStatus.Completed, row.Status);
        Assert.Equal(_world.AggMappingId, row.SourceMappingId);
        Assert.Equal(0, row.RetryCount);
        Assert.Equal(UpgradeWorld.Agg, check.ChapterFiles.Single().SourceName);
        Assert.Empty(check.SourceFailures);
    }

    [Fact]
    public async Task With_no_other_source_listing_it_the_row_waits_on_the_locked_source()
    {
        _world.AggListing = _ => [];
        var id = Enqueue(_world.OfficialMappingId, "o1");

        await _world.ProcessAsync(id);

        using var check = _world.Db.NewContext();
        var row = check.DownloadQueue.Single(q => q.Id == id);
        Assert.Equal(QueueStatus.Waiting, row.Status);
        Assert.Equal(ChapterDownloadProcessor.EarlyAccessKey, row.ErrorKey);
        Assert.Equal(_world.OfficialMappingId, row.SourceMappingId);
        Assert.Equal("o1", row.SourceChapterId);
        Assert.Equal(0, row.RetryCount);
        Assert.True(row.NextAttempt > _unlockAt.UtcDateTime);
        Assert.Empty(check.SourceFailures);
    }

    [Fact]
    public async Task When_the_other_source_fails_too_the_row_waits_for_the_unlock_instead_of_failing()
    {
        _world.AggPages = _ => throw new HttpRequestException("gone", null, HttpStatusCode.NotFound);
        var id = Enqueue(_world.OfficialMappingId, "o1");

        await _world.ProcessAsync(id);

        using var check = _world.Db.NewContext();
        var row = check.DownloadQueue.Single(q => q.Id == id);
        Assert.Equal(QueueStatus.Waiting, row.Status);
        Assert.Equal(_world.OfficialMappingId, row.SourceMappingId);
        Assert.Equal("o1", row.SourceChapterId);
        Assert.DoesNotContain(check.SourceFailures, f => f.SourceMappingId == _world.OfficialMappingId);
    }

    [Fact]
    public async Task A_server_error_on_the_other_source_parks_the_row_on_the_locked_source()
    {
        _world.AggPages = _ => throw new HttpRequestException("boom", null, HttpStatusCode.InternalServerError);
        var id = Enqueue(_world.OfficialMappingId, "o1");

        await _world.ProcessAsync(id);

        using var check = _world.Db.NewContext();
        var row = check.DownloadQueue.Single(q => q.Id == id);
        Assert.Equal(QueueStatus.Waiting, row.Status);
        Assert.Equal(ChapterDownloadProcessor.EarlyAccessKey, row.ErrorKey);
        Assert.Equal(_world.OfficialMappingId, row.SourceMappingId);
        Assert.Equal("o1", row.SourceChapterId);
        Assert.True(row.NextAttempt > _unlockAt.UtcDateTime);
    }

    [Fact]
    public async Task A_row_pinned_to_the_locked_source_stays_on_it()
    {
        var id = Enqueue(_world.OfficialMappingId, "o1", pinned: true);

        await _world.ProcessAsync(id);

        using var check = _world.Db.NewContext();
        var row = check.DownloadQueue.Single(q => q.Id == id);
        Assert.Equal(QueueStatus.Waiting, row.Status);
        Assert.Equal(_world.OfficialMappingId, row.SourceMappingId);
        Assert.Empty(check.ChapterFiles);
    }

    [Fact]
    public async Task A_chapter_the_listing_marks_locked_is_not_fetched_and_moves_to_a_free_copy()
    {
        using var test = new ChapterDownloadProcessorEarlyAccessFailoverTests(SourceCapabilities.ListsLockedChapters);
        test._world.OfficialListing = id =>
            [new SourceChapter(UpgradeWorld.Official, id, "o1", "1", 1m, null, null, "en", null, Locked: true)];
        var id = test.Enqueue(test._world.OfficialMappingId, "o1");

        await test._world.ProcessAsync(id);

        using var check = test._world.Db.NewContext();
        var row = check.DownloadQueue.Single(q => q.Id == id);
        Assert.Equal(QueueStatus.Completed, row.Status);
        Assert.Equal(test._world.AggMappingId, row.SourceMappingId);
        Assert.Equal(0, test._officialPageCalls);
    }

    [Fact]
    public async Task A_locked_listing_with_no_free_copy_parks_without_fetching_pages()
    {
        using var test = new ChapterDownloadProcessorEarlyAccessFailoverTests(SourceCapabilities.ListsLockedChapters);
        test._world.OfficialListing = id =>
            [new SourceChapter(UpgradeWorld.Official, id, "o1", "1", 1m, null, null, "en", null, Locked: true)];
        test._world.AggListing = _ => [];
        var id = test.Enqueue(test._world.OfficialMappingId, "o1");

        await test._world.ProcessAsync(id);

        using var check = test._world.Db.NewContext();
        var row = check.DownloadQueue.Single(q => q.Id == id);
        Assert.Equal(QueueStatus.Waiting, row.Status);
        Assert.Equal(ChapterDownloadProcessor.EarlyAccessKey, row.ErrorKey);
        Assert.Equal(0, test._officialPageCalls);
        Assert.Empty(check.SourceFailures);
    }

    [Fact]
    public async Task Resolving_prefers_a_source_that_lists_the_chapter_unlocked()
    {
        using var test = new ChapterDownloadProcessorEarlyAccessFailoverTests(SourceCapabilities.ListsLockedChapters);
        test._world.OfficialListing = id =>
            [new SourceChapter(UpgradeWorld.Official, id, "o1", "1", 1m, null, null, "en", null, Locked: true)];
        var id = test.Enqueue(null, null);

        await test._world.ProcessAsync(id);

        using var check = test._world.Db.NewContext();
        var row = check.DownloadQueue.Single(q => q.Id == id);
        Assert.Equal(QueueStatus.Completed, row.Status);
        Assert.Equal(test._world.AggMappingId, row.SourceMappingId);
        Assert.Equal(0, test._officialPageCalls);
    }

    private int Enqueue(int? mappingId, string? sourceChapterId, bool pinned = false)
    {
        using var db = _world.Db.NewContext();
        var item = new DownloadQueueItem
        {
            SeriesId = _world.SeriesId, ChapterId = _chapterId, Protocol = AcquisitionProtocol.Scraper,
            Status = QueueStatus.Queued, QueuedAt = DateTime.UtcNow, Origin = DownloadOrigin.MonitorRefresh,
            SourceMappingId = mappingId, SourceChapterId = sourceChapterId,
            PreferredMappingId = pinned ? mappingId : null
        };
        db.DownloadQueue.Add(item);
        db.SaveChanges();
        return item.Id;
    }
}
