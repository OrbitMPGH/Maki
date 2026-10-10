using System.Text.Json;
using Maki.Api.Controllers;
using Maki.Api.Dtos;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Indexers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>Bulk retry and remove of Failed rows, and the release blocking that removing a torrent row records.</summary>
public class QueueBulkFailedTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly TestDb _db = new();
    private readonly DownloadQueueService _queue;
    private readonly DownloadBatchNotifier _batches;
    private readonly int _seriesId;
    private int _chapterNumber;

    public QueueBulkFailedTests()
    {
        _queue = new DownloadQueueService(_db.ScopeFactory(), TimeProvider.System, null!, NullLogger<DownloadQueueService>.Instance);
        _batches = new DownloadBatchNotifier(
            new RecordingNotifications(), new RecordingInbox(), new TestLocalizer(), new TestUserLocaleResolver(), TimeProvider.System,
            NullLogger<DownloadBatchNotifier>.Instance);
        _seriesId = _db.SeedSeries(mappings:
        [
            new SourceMapping { SourceName = "alpha", SourceSeriesId = "a", Url = "https://alpha.test", Priority = 1, Enabled = true },
            new SourceMapping { SourceName = "beta", SourceSeriesId = "b", Url = "https://beta.test", Priority = 2, Enabled = true },
        ]);
    }

    public void Dispose()
    {
        _batches.Dispose();
        _db.Dispose();
    }

    private QueueController Controller() => new(
        new TestLocalizer(), _db.NewContext(), _queue, _batches, null!, null!, null!, NullLogger<QueueController>.Instance);

    private int SeedScraper(string source, string? errorKey, QueueStatus status = QueueStatus.Failed, int retryCount = 0,
        DateTime? nextAttempt = null)
    {
        using var db = _db.NewContext();
        var chapter = new Chapter { SeriesId = _seriesId, Number = ++_chapterNumber, Language = "en" };
        db.Chapters.Add(chapter);
        db.SaveChanges();
        var mappingId = db.SourceMappings.Single(m => m.SeriesId == _seriesId && m.SourceName == source).Id;
        var item = new DownloadQueueItem
        {
            SeriesId = _seriesId, ChapterId = chapter.Id, SourceMappingId = mappingId, Protocol = AcquisitionProtocol.Scraper,
            Status = status, ErrorKey = errorKey, RetryCount = retryCount, NextAttempt = nextAttempt, QueuedAt = T0
        };
        db.DownloadQueue.Add(item);
        db.SaveChanges();
        return item.Id;
    }

    private int SeedTorrent(string guid, QueueStatus status = QueueStatus.Failed, string? errorKey = "error.download.torrentRemoved")
    {
        using var db = _db.NewContext();
        var item = new DownloadQueueItem
        {
            SeriesId = _seriesId, Protocol = AcquisitionProtocol.Torrent, Status = status, ErrorKey = errorKey, Title = guid,
            ReleaseInfoJson = JsonSerializer.Serialize(new ReleaseInfo(guid, "Series v01", "Nyaa", null)), QueuedAt = T0
        };
        db.DownloadQueue.Add(item);
        db.SaveChanges();
        return item.Id;
    }

    private QueueStatus? StatusOf(int id)
    {
        using var db = _db.NewContext();
        return db.DownloadQueue.Where(q => q.Id == id).Select(q => (QueueStatus?)q.Status).SingleOrDefault();
    }

    private List<string> Dismissed()
    {
        using var db = _db.NewContext();
        return db.TorrentProposals.Where(p => p.Status == TorrentProposalStatus.Dismissed).Select(p => p.ReleaseGuid).OrderBy(g => g).ToList();
    }

    private static int Affected(IActionResult result) =>
        Assert.IsType<QueueBulkResultDto>(Assert.IsType<OkObjectResult>(result).Value).Affected;

    [Fact]
    public async Task Retry_failed_ignores_the_attempt_cap_and_the_backoff()
    {
        var exhausted = SeedScraper("alpha", DownloadFailureReason.SourceError, retryCount: 9, nextAttempt: DateTime.UtcNow.AddHours(5));
        var running = SeedScraper("alpha", null, QueueStatus.Downloading);

        var result = await Controller().RetryFailed(null, CancellationToken.None);

        Assert.Equal(1, Affected(result));
        Assert.Equal(QueueStatus.Queued, StatusOf(exhausted));
        Assert.Equal(QueueStatus.Downloading, StatusOf(running));
    }

    [Fact]
    public async Task Retry_failed_can_be_narrowed_by_reason_and_by_source()
    {
        var a = SeedScraper("alpha", DownloadFailureReason.Network);
        var b = SeedScraper("alpha", DownloadFailureReason.SourceError);
        var c = SeedScraper("beta", DownloadFailureReason.Network);

        Assert.Equal(1, Affected(await Controller().RetryFailed(
            new QueueFailedActionDto(DownloadFailureReason.Network, "alpha"), CancellationToken.None)));

        Assert.Equal(QueueStatus.Queued, StatusOf(a));
        Assert.Equal(QueueStatus.Failed, StatusOf(b));
        Assert.Equal(QueueStatus.Failed, StatusOf(c));
    }

    [Fact]
    public async Task Retry_failed_leaves_torrents_and_rows_that_cannot_change()
    {
        var torrent = SeedTorrent("g1");
        var permanent = SeedScraper("alpha", DownloadQueueService.PermanentErrorKeys[0]);

        Assert.Equal(0, Affected(await Controller().RetryFailed(null, CancellationToken.None)));

        Assert.Equal(QueueStatus.Failed, StatusOf(torrent));
        Assert.Equal(QueueStatus.Failed, StatusOf(permanent));
    }

    [Fact]
    public async Task Remove_failed_deletes_only_failed_rows_matching_the_filter()
    {
        var failedAlpha = SeedScraper("alpha", DownloadFailureReason.Network);
        var failedBeta = SeedScraper("beta", DownloadFailureReason.Network);
        var queued = SeedScraper("alpha", null, QueueStatus.Queued);

        Assert.Equal(1, Affected(await Controller().RemoveFailed(
            new QueueFailedActionDto(Source: "alpha"), null, CancellationToken.None)));

        Assert.Null(StatusOf(failedAlpha));
        Assert.Equal(QueueStatus.Failed, StatusOf(failedBeta));
        Assert.Equal(QueueStatus.Queued, StatusOf(queued));

        Assert.Equal(1, Affected(await Controller().RemoveFailed(null, null, CancellationToken.None)));
        Assert.Null(StatusOf(failedBeta));
        Assert.Equal(QueueStatus.Queued, StatusOf(queued));
    }

    [Fact]
    public async Task Remove_failed_blocks_the_torrent_releases_it_removes()
    {
        SeedTorrent("g1");
        SeedTorrent("g2", errorKey: "error.download.torrentMissing");
        SeedScraper("alpha", DownloadFailureReason.Network);

        Assert.Equal(3, Affected(await Controller().RemoveFailed(null, null, CancellationToken.None)));
        Assert.Equal(["g1", "g2"], Dismissed());
    }

    [Fact]
    public async Task Remove_failed_can_leave_the_releases_unblocked()
    {
        SeedTorrent("g1");

        await Controller().RemoveFailed(new QueueFailedActionDto(BlockReleases: false), null, CancellationToken.None);

        Assert.Empty(Dismissed());
    }

    [Fact]
    public async Task Removing_a_torrent_row_blocks_its_release_by_default()
    {
        var id = SeedTorrent("g1", QueueStatus.Downloading, errorKey: null);

        Assert.IsType<NoContentResult>(await Controller().Remove(id, CancellationToken.None));

        Assert.Equal(QueueStatus.Cancelled, StatusOf(id));
        Assert.Equal(["g1"], Dismissed());
    }

    [Fact]
    public async Task Removing_a_torrent_row_can_skip_the_block()
    {
        var id = SeedTorrent("g1", QueueStatus.Queued, errorKey: null);

        await Controller().Remove(id, CancellationToken.None, blockRelease: false);

        Assert.Empty(Dismissed());
    }

    [Fact]
    public async Task Removing_a_scraper_row_blocks_nothing()
    {
        var id = SeedScraper("alpha", DownloadFailureReason.Network);

        await Controller().Remove(id, CancellationToken.None);

        Assert.Empty(Dismissed());
    }

    [Fact]
    public async Task The_failed_summary_groups_by_reason_and_source()
    {
        SeedScraper("alpha", DownloadFailureReason.Network);
        SeedScraper("alpha", DownloadFailureReason.Network);
        SeedScraper("beta", DownloadFailureReason.SourceError);
        SeedTorrent("g1");
        SeedScraper("alpha", null, QueueStatus.Queued);

        var groups = Assert.IsAssignableFrom<IReadOnlyList<QueueFailureGroupDto>>(
            Assert.IsType<OkObjectResult>(await Controller().Failed(CancellationToken.None)).Value);

        Assert.Equal(3, groups.Count);
        Assert.Equal(new QueueFailureGroupDto(DownloadFailureReason.Network, "alpha", 2), groups[0]);
        Assert.Contains(new QueueFailureGroupDto("error.download.torrentRemoved", "torrent", 1), groups);
    }
}
