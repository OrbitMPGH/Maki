using System.IO.Compression;
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

    private static ClientTorrent Torrent(string hash, double progress, DateTime addedAt, string name = "Berserk v01 (Digital)") => new(
        hash, name, progress, progress >= 1.0, "/dl/" + name, new DateTimeOffset(addedAt), false, null);

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
            db, TestIndexers.Registry(new StubHttpClientFactory("[]")), _client.Registry(), settings, NullLogger<ReleaseService>.Instance);
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

    private const string PackName = "Berserk v01-05 (2024-2026) (Digital) (1r0n)";

    private string? HashOf(int id) =>
        JsonSerializer.Deserialize<ReleaseInfo>(Reload(id).ReleaseInfoJson!)!.TorrentHash;

    [Fact]
    public async Task A_hashless_grab_claims_a_torrent_named_like_its_release_that_the_client_already_had()
    {
        var id = SeedItem(QueueStatus.Downloading, hash: null, DateTime.UtcNow.AddHours(-3), PackName);
        _client.Torrents.Add(Torrent("abc123", 0.5, DateTime.UtcNow.AddDays(-2), PackName));

        await RunAsync();

        Assert.Equal("abc123", HashOf(id));
        Assert.Equal(QueueStatus.Downloading, Reload(id).Status);
    }

    [Fact]
    public async Task A_torrent_left_behind_by_a_failed_row_of_the_same_series_is_claimed_by_name()
    {
        SeedItem(QueueStatus.Failed, hash: "abc123", DateTime.UtcNow.AddDays(-1), PackName);
        var id = SeedItem(QueueStatus.Downloading, hash: null, DateTime.UtcNow.AddHours(-3), PackName);
        _client.Torrents.Add(Torrent("abc123", 0.5, DateTime.UtcNow.AddDays(-1), PackName));

        await RunAsync();

        Assert.Equal("abc123", HashOf(id));
    }

    [Theory]
    [InlineData(QueueStatus.Completed)]
    [InlineData(QueueStatus.Cancelled)]
    public async Task A_torrent_a_finished_row_of_the_same_series_holds_is_claimed_by_name(QueueStatus ownerStatus)
    {
        SeedItem(ownerStatus, hash: "abc123", DateTime.UtcNow.AddDays(-1), PackName);
        var id = SeedItem(QueueStatus.Queued, hash: null, DateTime.UtcNow.AddHours(-3), PackName);
        _client.Torrents.Add(Torrent("abc123", 0.5, DateTime.UtcNow.AddDays(-1), PackName));

        await RunAsync();

        Assert.Equal("abc123", HashOf(id));
        Assert.Equal(QueueStatus.Queued, Reload(id).Status);
    }

    [Theory]
    [InlineData(QueueStatus.Failed, true, "Vagabond")]
    [InlineData(QueueStatus.Completed, true, "Vagabond")]
    [InlineData(QueueStatus.Downloading, false, "Berserk")]
    public async Task A_torrent_another_series_or_a_live_row_holds_is_not_claimed_and_the_failure_names_it(
        QueueStatus ownerStatus, bool otherSeries, string ownerSeries)
    {
        var owner = SeedItem(ownerStatus, hash: "abc123", DateTime.UtcNow.AddDays(-1), PackName);
        if (otherSeries)
        {
            using var db = _db.NewContext();
            db.DownloadQueue.Single(q => q.Id == owner).SeriesId = _db.SeedSeries("Vagabond");
            db.SaveChanges();
        }

        var id = SeedItem(QueueStatus.Queued, hash: null, DateTime.UtcNow.AddHours(-3), PackName);
        _client.Torrents.Add(Torrent("abc123", 0.5, DateTime.UtcNow.AddDays(-1), PackName));

        await RunAsync();

        var item = Reload(id);
        Assert.Null(HashOf(id));
        Assert.Equal(QueueStatus.Failed, item.Status);
        Assert.Equal(CompletedDownloadJob.HeldElsewhereKey, item.ErrorKey);
        Assert.Contains(ownerSeries, item.ErrorParamsJson);
        Assert.False(CompletedDownloadJob.BlamesRelease(item.ErrorKey));
    }

    [Fact]
    public async Task A_held_torrent_is_not_swapped_for_another_one_added_in_the_window()
    {
        var other = _db.SeedSeries("Vagabond");
        var owner = SeedItem(QueueStatus.Downloading, hash: "abc123", DateTime.UtcNow.AddDays(-1), PackName);
        using (var db = _db.NewContext())
        {
            db.DownloadQueue.Single(q => q.Id == owner).SeriesId = other;
            db.SaveChanges();
        }

        var id = SeedItem(QueueStatus.Downloading, hash: null, DateTime.UtcNow.AddMinutes(-10), PackName);
        _client.Torrents.Add(Torrent("abc123", 0.5, DateTime.UtcNow.AddDays(-1), PackName));
        _client.Torrents.Add(Torrent("def456", 0.5, DateTime.UtcNow.AddMinutes(-9), "Berserk v02 (Digital)"));

        await RunAsync();

        Assert.Null(HashOf(id));
        Assert.Equal(QueueStatus.Downloading, Reload(id).Status);
    }

    [Theory]
    [InlineData("Berserk v01-05 (2024-2026) (Digital) (1r0n)", "berserk v01 05 2024 2026 digital 1r0n")]
    [InlineData("Berserk_v01.cbz", "berserk v01")]
    [InlineData("Berserk v01.5", "berserk v01 5")]
    [InlineData("  ", "")]
    public void Names_compare_without_case_punctuation_or_a_file_extension(string name, string expected) =>
        Assert.Equal(expected, CompletedDownloadJob.ComparableName(name));

    /// <summary>
    /// The archive cancels the row, then the import gets the lock it was waiting on and sees the archive.
    /// That row stays Cancelled: it is not a failed download, so nothing is announced and no release is declined.
    /// </summary>
    [Fact]
    public async Task A_series_archived_while_its_import_waited_for_the_lock_leaves_the_row_cancelled_and_silent()
    {
        var content = Path.Combine(Path.GetTempPath(), "maki-job-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(content);
        try
        {
            using (var archive = ZipFile.Open(Path.Combine(content, "Berserk v01 (Digital).cbz"), ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(archive.CreateEntry("Berserk - c001 - p001 [Oak].png").Open());
                writer.Write("page");
            }

            using (var db = _db.NewContext())
            {
                db.Chapters.Add(new Chapter { SeriesId = _seriesId, Number = 1, Volume = 1, Language = "en" });
                db.SaveChanges();
            }

            var id = SeedItem(QueueStatus.Downloading, "abc123", DateTime.UtcNow.AddHours(-1));
            _client.Torrents.Add(new ClientTorrent(
                "abc123", "Berserk v01 (Digital)", 1.0, true, content, DateTimeOffset.UtcNow.AddHours(-1), false, null));

            Task run;
            using (await SeriesLocks.SeriesAsync(_seriesId, CancellationToken.None))
            {
                run = RunAsync();
                await Wait.UntilAsync(() => run.IsCompleted || SeriesLocks.SeriesUsers(_seriesId) >= 2,
                    "the import never asked for the series lock");
                Assert.False(run.IsCompleted);

                using var db = _db.NewContext();
                db.Series.Single(s => s.Id == _seriesId).ArchivedAt = DateTime.UtcNow;
                db.DownloadQueue.Single(q => q.Id == id).Status = QueueStatus.Cancelled;
                db.SaveChanges();
            }

            await run;

            var item = Reload(id);
            Assert.Equal(QueueStatus.Cancelled, item.Status);
            Assert.Empty(_notifications.Sent);
            Assert.Empty(_inbox.RaisedForSeries);
            using var check = _db.NewContext();
            Assert.Empty(check.TorrentProposals);
        }
        finally
        {
            Directory.Delete(content, recursive: true);
        }
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
