using System.Text.Json;
using Maki.Api.Hubs;
using Maki.Api.Jobs;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Download;
using Maki.Core.Entities;
using Maki.Core.Indexers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>The poll that ties torrent queue rows to qBittorrent: hash claiming, the missing-torrent failure and the park gate.</summary>
public class CompletedDownloadJobTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeQbt _qbt = new();
    private int _seriesId;

    public CompletedDownloadJobTests()
    {
        _db.SetConfig((SettingKeys.QBittorrentUrl, "http://qbt.test"));
        _seriesId = _db.SeedSeries("Berserk");
    }

    public void Dispose() => _db.Dispose();

    private sealed class FakeQbt : QBittorrentClient
    {
        public List<QbtTorrent> Torrents { get; } = [];

        public override Task<IReadOnlyList<QbtTorrent>> ListAsync(
            string baseUrl, string username, string password, string category, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<QbtTorrent>>(Torrents.ToList());
    }

    private static long Unix(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeSeconds();

    private int SeedItem(QueueStatus status, string? hash, DateTime queuedAt, string title = "Berserk v01")
    {
        using var db = _db.NewContext();
        var item = new DownloadQueueItem
        {
            SeriesId = _seriesId,
            Protocol = AcquisitionProtocol.Torrent,
            Status = status,
            Title = title,
            ReleaseInfoJson = JsonSerializer.Serialize(new ReleaseInfo("guid-" + Guid.NewGuid().ToString("N"), title, "Nyaa", hash)),
            QueuedAt = queuedAt,
            SortOrder = 1,
        };
        db.DownloadQueue.Add(item);
        db.SaveChanges();
        return item.Id;
    }

    private async Task RunAsync()
    {
        using var db = _db.NewContext();
        var settings = new SettingsService(_db.ScopeFactory());
        var releases = new ReleaseService(
            db, new ProwlarrClient(new StubHttpClientFactory("[]")), _qbt, settings, NullLogger<ReleaseService>.Instance);
        var importer = new TorrentImportService(
            db, null!, null!, null!, null!, null!, settings, null!, new RecordingInbox(),
            NullLogger<TorrentImportService>.Instance);
        var job = new CompletedDownloadJob(
            db, releases, _qbt, importer, new EventBroadcaster(new NoopHubContext(), _db.ScopeFactory()),
            null!, NullLogger<CompletedDownloadJob>.Instance);
        await job.Execute(new TestJobContext());
    }

    private DownloadQueueItem Reload(int id)
    {
        using var db = _db.NewContext();
        return db.DownloadQueue.Single(q => q.Id == id);
    }

    [Fact]
    public async Task A_hashless_grab_that_never_appears_fails_after_two_hours()
    {
        var id = SeedItem(QueueStatus.Downloading, hash: null, DateTime.UtcNow.AddHours(-3));

        await RunAsync();

        var item = Reload(id);
        Assert.Equal(QueueStatus.Failed, item.Status);
        Assert.Equal("error.download.torrentMissing", item.ErrorKey);
    }

    [Fact]
    public async Task A_recent_hashless_grab_is_left_waiting()
    {
        var id = SeedItem(QueueStatus.Downloading, hash: null, DateTime.UtcNow.AddMinutes(-5));

        await RunAsync();

        Assert.Equal(QueueStatus.Downloading, Reload(id).Status);
    }

    [Fact]
    public async Task A_hashless_grab_claims_the_torrent_added_after_it_and_follows_its_progress()
    {
        var queuedAt = DateTime.UtcNow.AddMinutes(-10);
        var id = SeedItem(QueueStatus.Downloading, hash: null, queuedAt);
        _qbt.Torrents.Add(new QBittorrentClient.QbtTorrent(
            "ABC123", "Berserk v01 (Digital)", "downloading", 0.5, "/dl/Berserk v01", "/dl", Unix(queuedAt.AddMinutes(1))));

        await RunAsync();

        var item = Reload(id);
        Assert.Equal("ABC123", JsonSerializer.Deserialize<ReleaseInfo>(item.ReleaseInfoJson!)!.TorrentHash);
        Assert.Equal(50, item.PagesDone);
        Assert.Equal(QueueStatus.Downloading, item.Status);
    }

    [Fact]
    public async Task A_torrent_a_finished_row_already_owns_is_not_claimed_by_a_new_grab()
    {
        var queuedAt = DateTime.UtcNow.AddMinutes(-10);
        SeedItem(QueueStatus.Completed, hash: "ABC123", queuedAt.AddDays(-1));
        var id = SeedItem(QueueStatus.Downloading, hash: null, queuedAt);
        _qbt.Torrents.Add(new QBittorrentClient.QbtTorrent(
            "abc123", "Berserk v01 (Digital)", "stalledUP", 1.0, "/dl/Berserk v01", "/dl", Unix(queuedAt.AddMinutes(1))));

        await RunAsync();

        var item = Reload(id);
        Assert.Null(JsonSerializer.Deserialize<ReleaseInfo>(item.ReleaseInfoJson!)!.TorrentHash);
        Assert.Equal(QueueStatus.Downloading, item.Status);
    }

    [Fact]
    public async Task A_finished_torrent_waiting_for_an_import_decision_is_not_advanced()
    {
        var id = SeedItem(QueueStatus.AwaitingImport, hash: "def456", DateTime.UtcNow.AddHours(-1));
        _qbt.Torrents.Add(new QBittorrentClient.QbtTorrent(
            "DEF456", "Berserk v01 (Digital)", "stalledUP", 1.0, "/dl/Berserk v01", "/dl", Unix(DateTime.UtcNow.AddHours(-1))));

        await RunAsync();

        var item = Reload(id);
        Assert.Equal(QueueStatus.AwaitingImport, item.Status);
        Assert.Null(item.ErrorKey);
    }
}
