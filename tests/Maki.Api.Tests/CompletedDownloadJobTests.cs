using System.Text.Json;
using Maki.Api.Hubs;
using Maki.Api.Jobs;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Download;
using Maki.Core.Entities;
using Maki.Core.Indexers;
using Maki.Core.Inbox;
using Maki.Core.Notifications;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>The poll that ties torrent queue rows to the download client: hash claiming, the missing-torrent failure and the park gate.</summary>
public class CompletedDownloadJobTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTorrentClient _client = new();
    private readonly RecordingNotifications _notifications = new();
    private readonly RecordingInbox _inbox = new();
    private int _seriesId;

    public CompletedDownloadJobTests()
    {
        _db.SetConfig((SettingKeys.DownloadClientUrl, "http://qbt.test"));
        _seriesId = _db.SeedSeries("Berserk");
        CompletedDownloadJob.ErrorLogged.Clear();
    }

    public void Dispose() => _db.Dispose();

    private static ClientTorrent Torrent(string hash, double progress, DateTime addedAt) => new(
        hash, "Berserk v01 (Digital)", progress, progress >= 1.0, "/dl/Berserk v01", new DateTimeOffset(addedAt), false, null);

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
            db, new ProwlarrClient(new StubHttpClientFactory("[]")), _client.Registry(), settings, NullLogger<ReleaseService>.Instance);
        var importer = new TorrentImportService(
            db, null!, null!, null!, null!, null!, settings, null!, new RecordingInbox(),
            NullLogger<TorrentImportService>.Instance);
        var job = new CompletedDownloadJob(
            db, releases, _client.Registry(), importer, new EventBroadcaster(new NoopHubContext(), _db.ScopeFactory()),
            null!, _notifications, _inbox, new TestLocalizer(), new TestUserLocaleResolver(),
            NullLogger<CompletedDownloadJob>.Instance);
        await job.Execute(new TestJobContext());
    }

    private string ReleaseGuidOf(int id)
    {
        using var db = _db.NewContext();
        return JsonSerializer.Deserialize<ReleaseInfo>(db.DownloadQueue.Single(q => q.Id == id).ReleaseInfoJson!)!.Guid;
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

        var sent = Assert.Single(_notifications.Sent);
        Assert.Equal(NotificationEventType.DownloadFailed, sent.Type);
        Assert.Contains("error.download.torrentMissing", sent.Message.Body);
        Assert.Empty(_inbox.RaisedForSeries);
    }

    [Fact]
    public async Task A_torrent_that_never_appears_blocks_its_release_for_the_volume_search()
    {
        var id = SeedItem(QueueStatus.Downloading, hash: null, DateTime.UtcNow.AddHours(-3));
        var guid = ReleaseGuidOf(id);

        await RunAsync();

        using var db = _db.NewContext();
        var proposal = Assert.Single(db.TorrentProposals);
        Assert.Equal(guid, proposal.ReleaseGuid);
        Assert.Equal(TorrentProposalStatus.Dismissed, proposal.Status);
        Assert.Equal(_seriesId, proposal.SeriesId);
    }

    [Fact]
    public async Task A_release_is_not_blocked_while_its_torrent_is_still_coming()
    {
        SeedItem(QueueStatus.Downloading, hash: null, DateTime.UtcNow.AddMinutes(-5));

        await RunAsync();

        using var db = _db.NewContext();
        Assert.Empty(db.TorrentProposals);
    }

    private static ClientTorrent ErroredTorrent(string hash) => new(
        hash, "Berserk v01 (Digital)", 1.0, false, "/dl/Berserk v01", DateTimeOffset.UtcNow, true, "Missing files");

    [Fact]
    public async Task A_torrent_in_the_clients_error_state_keeps_its_row_and_shows_the_clients_message()
    {
        var id = SeedItem(QueueStatus.Downloading, "abc123", DateTime.UtcNow.AddHours(-1));
        _client.Torrents.Add(ErroredTorrent("abc123"));

        await RunAsync();
        await RunAsync();

        var item = Reload(id);
        Assert.Equal(QueueStatus.Downloading, item.Status);
        Assert.Equal(CompletedDownloadJob.ClientErrorKey, item.ErrorKey);
        Assert.Equal("Missing files", item.ErrorMessage);
        Assert.Empty(_notifications.Sent);

        using var db = _db.NewContext();
        Assert.Empty(db.TorrentProposals);
    }

    [Fact]
    public async Task A_torrent_that_recovers_from_an_error_clears_the_reason_and_still_imports()
    {
        var id = SeedItem(QueueStatus.Downloading, "abc123", DateTime.UtcNow.AddHours(-1));
        _client.Torrents.Add(ErroredTorrent("abc123"));
        await RunAsync();

        _client.Torrents.Clear();
        _client.Torrents.Add(Torrent("abc123", 0.4, DateTime.UtcNow.AddHours(-1)));
        await RunAsync();

        var item = Reload(id);
        Assert.Equal(QueueStatus.Downloading, item.Status);
        Assert.Null(item.ErrorKey);
        Assert.Null(item.ErrorMessage);
        Assert.Equal(40, item.PagesDone);
        Assert.False(CompletedDownloadJob.ErrorLogged.ContainsKey(id));
    }

    [Theory]
    [InlineData("error.download.torrentClientError", false)]
    [InlineData("error.download.torrentMissing", true)]
    [InlineData("error.download.torrentRemoved", true)]
    [InlineData("error.torrentImport.noComicsEmpty", true)]
    [InlineData("error.torrentImport.noComicsOfType", true)]
    [InlineData(null, false)]
    [InlineData("error.download.unexpected", false)]
    [InlineData("error.upgrades.volumeGuard", false)]
    [InlineData("error.torrentImport.notInClient", false)]
    [InlineData("error.torrentImport.pathNotAccessible", false)]
    [InlineData("error.torrentImport.noRootFolder", false)]
    [InlineData("error.torrentImport.copyFailed", false)]
    public void Only_failures_that_are_the_releases_own_block_it(string? key, bool blocks) =>
        Assert.Equal(blocks, CompletedDownloadJob.BlamesRelease(key));

    [Fact]
    public async Task An_automatic_torrent_failure_also_reaches_the_inbox()
    {
        var id = SeedItem(QueueStatus.Downloading, hash: null, DateTime.UtcNow.AddHours(-3));
        using (var db = _db.NewContext())
        {
            db.DownloadQueue.Single(q => q.Id == id).Origin = DownloadOrigin.MonitorRefresh;
            db.SaveChanges();
        }

        await RunAsync();

        var raised = Assert.Single(_inbox.RaisedForSeries);
        Assert.Equal(InboxEventType.DownloadFailed, raised.Type);
        Assert.Equal("inbox.download.failed", raised.Message.Key);
        Assert.Equal("error.download.torrentMissing", raised.Message.Params!["error"]);
    }

    [Fact]
    public async Task A_failure_already_announced_is_not_announced_again_on_the_next_poll()
    {
        SeedItem(QueueStatus.Downloading, hash: null, DateTime.UtcNow.AddHours(-3));

        await RunAsync();
        await RunAsync();

        Assert.Single(_notifications.Sent);
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
        _client.Torrents.Add(Torrent("abc123", 0.5, queuedAt.AddMinutes(1)));

        await RunAsync();

        var item = Reload(id);
        Assert.Equal("abc123", JsonSerializer.Deserialize<ReleaseInfo>(item.ReleaseInfoJson!)!.TorrentHash);
        Assert.Equal(50, item.PagesDone);
        Assert.Equal(QueueStatus.Downloading, item.Status);
    }

    [Fact]
    public async Task A_torrent_a_finished_row_already_owns_is_not_claimed_by_a_new_grab()
    {
        var queuedAt = DateTime.UtcNow.AddMinutes(-10);
        SeedItem(QueueStatus.Completed, hash: "ABC123", queuedAt.AddDays(-1));
        var id = SeedItem(QueueStatus.Downloading, hash: null, queuedAt);
        _client.Torrents.Add(Torrent("abc123", 1.0, queuedAt.AddMinutes(1)));

        await RunAsync();

        var item = Reload(id);
        Assert.Null(JsonSerializer.Deserialize<ReleaseInfo>(item.ReleaseInfoJson!)!.TorrentHash);
        Assert.Equal(QueueStatus.Downloading, item.Status);
    }

    [Fact]
    public async Task A_finished_torrent_waiting_for_an_import_decision_is_not_advanced()
    {
        var id = SeedItem(QueueStatus.AwaitingImport, hash: "def456", DateTime.UtcNow.AddHours(-1));
        _client.Torrents.Add(Torrent("def456", 1.0, DateTime.UtcNow.AddHours(-1)));

        await RunAsync();

        var item = Reload(id);
        Assert.Equal(QueueStatus.AwaitingImport, item.Status);
        Assert.Null(item.ErrorKey);
    }
}
