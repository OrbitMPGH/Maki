using System.Text.Json;
using Maki.Api.Jobs;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Download;
using Maki.Core.Entities;
using Maki.Core.Images;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// Removing an imported torrent from the download client: what an import decides and records, and
/// what <see cref="TorrentRemovalJob"/> does with it later.
/// </summary>
public class TorrentRemovalTests : IDisposable
{
    private const string Hash = "abc123";

    private readonly TestDb _db = new();
    private readonly FakeTorrentClient _client = new();
    private readonly FakeAppSettings _settings = new FakeAppSettings()
        .Set(SettingKeys.DownloadClientUrl, "http://qbt.test")
        .Set(SettingKeys.DownloadClientRemoveCompleted, TorrentRemovalModes.AfterImport);
    private readonly string _download = Path.Combine(Path.GetTempPath(), "maki-removal-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly int _seriesId;

    public TorrentRemovalTests()
    {
        Directory.CreateDirectory(_download);
        _seriesId = _db.SeedSeries("Berserk");
        TorrentRemovalJob.FailureLogged.Clear();
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_download, recursive: true);
    }

    private static ClientTorrent Torrent(string hash = Hash) =>
        new(hash, "Berserk v01", 1.0, true, "/dl/Berserk v01", DateTimeOffset.UtcNow, false, null);

    private string Fingerprint() =>
        DownloadClientSettingsStore.Fingerprint(DownloadClientSettingsStore.LoadAsync(_settings).GetAwaiter().GetResult());

    private static string Release(string? hash, string? contentPath = null, bool? addedBeforeGrab = false) =>
        JsonSerializer.Serialize(new ReleaseInfo(
            "guid-" + Guid.NewGuid().ToString("N"), "Berserk v01", "Nyaa", hash, contentPath, addedBeforeGrab));

    private int SeedPending(
        bool deleteData = true, string? client = null, DateTime? pendingSince = null, string? contentPath = null,
        string? hash = Hash)
    {
        using var db = _db.NewContext();
        var item = new DownloadQueueItem
        {
            SeriesId = _seriesId,
            Protocol = AcquisitionProtocol.Torrent,
            Status = QueueStatus.Completed,
            Title = "Berserk v01",
            ReleaseInfoJson = Release(hash, contentPath ?? _download),
            QueuedAt = DateTime.UtcNow.AddHours(-2),
            CompletedAt = DateTime.UtcNow.AddHours(-1),
            ClientRemovalPendingAt = pendingSince ?? DateTime.UtcNow.AddMinutes(-5),
            ClientRemovalMode = TorrentRemovalModes.AfterImport,
            ClientRemovalDeleteData = deleteData,
            ClientRemovalClient = client ?? Fingerprint(),
        };
        db.DownloadQueue.Add(item);
        db.SaveChanges();
        return item.Id;
    }

    private void SeedLive(QueueStatus status, string? hash, int? seriesId = null)
    {
        using var db = _db.NewContext();
        db.DownloadQueue.Add(new DownloadQueueItem
        {
            SeriesId = seriesId ?? _seriesId,
            Protocol = AcquisitionProtocol.Torrent,
            Status = status,
            Title = "Berserk v02",
            ReleaseInfoJson = Release(hash),
            QueuedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    private async Task RunAsync()
    {
        using var db = _db.NewContext();
        await new TorrentRemovalJob(db, _settings, _client.Registry(), NullLogger<TorrentRemovalJob>.Instance)
            .RunAsync(CancellationToken.None);
    }

    private DownloadQueueItem Reload(int id)
    {
        using var db = _db.NewContext();
        return db.DownloadQueue.Single(q => q.Id == id);
    }

    private static TorrentImportOutcome Imported(int files = 1, bool placedFresh = true) =>
        new(true, null, files, files, 0, 0, 0, [], PlacedFresh: placedFresh);

    private async Task<DownloadQueueItem> MarkAsync(
        TorrentImportOutcome outcome, bool settledByUser = false, string? hash = Hash, bool? addedBeforeGrab = false)
    {
        using var db = _db.NewContext();
        var importer = new TorrentImportService(
            db, null!, null!, null!, null!, null!, _settings, null!, new RecordingInbox(),
            NullLogger<TorrentImportService>.Instance);
        var item = new DownloadQueueItem { SeriesId = _seriesId, ReleaseInfoJson = Release(hash, addedBeforeGrab: addedBeforeGrab) };
        await importer.MarkForClientRemovalAsync(item, outcome, settledByUser, CancellationToken.None);
        return item;
    }

    [Fact]
    public async Task An_import_under_afterimport_records_what_the_removal_will_do()
    {
        var item = await MarkAsync(Imported());

        Assert.NotNull(item.ClientRemovalPendingAt);
        Assert.Equal(TorrentRemovalModes.AfterImport, item.ClientRemovalMode);
        Assert.True(item.ClientRemovalDeleteData);
        Assert.Equal(Fingerprint(), item.ClientRemovalClient);
    }

    [Fact]
    public async Task Nothing_is_recorded_while_the_setting_is_off()
    {
        _settings.Set(SettingKeys.DownloadClientRemoveCompleted, TorrentRemovalModes.Never);

        var item = await MarkAsync(Imported());

        Assert.Null(item.ClientRemovalPendingAt);
    }

    [Fact]
    public async Task A_file_the_import_adopted_rather_than_placed_keeps_the_download()
    {
        var item = await MarkAsync(Imported(placedFresh: false));

        Assert.NotNull(item.ClientRemovalPendingAt);
        Assert.False(item.ClientRemovalDeleteData);
    }

    [Fact]
    public async Task Turning_file_deletion_off_keeps_the_download()
    {
        _settings.Set(SettingKeys.DownloadClientRemoveData, "false");

        var item = await MarkAsync(Imported());

        Assert.False(item.ClientRemovalDeleteData);
    }

    [Fact]
    public async Task An_unattended_import_that_brought_nothing_in_leaves_the_torrent_alone()
    {
        var item = await MarkAsync(Imported(files: 0, placedFresh: true));

        Assert.Null(item.ClientRemovalPendingAt);
    }

    [Fact]
    public async Task A_decision_the_user_made_removes_the_torrent_even_when_nothing_came_in_but_keeps_its_files()
    {
        var item = await MarkAsync(Imported(files: 0, placedFresh: true), settledByUser: true);

        Assert.NotNull(item.ClientRemovalPendingAt);
        Assert.False(item.ClientRemovalDeleteData);
    }

    [Fact]
    public async Task A_row_with_no_hash_is_not_recorded()
    {
        var item = await MarkAsync(Imported(), hash: null);

        Assert.Null(item.ClientRemovalPendingAt);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public async Task A_torrent_the_client_had_before_the_grab_or_that_was_never_seen_is_never_marked(bool? addedBeforeGrab)
    {
        var item = await MarkAsync(Imported(), addedBeforeGrab: addedBeforeGrab);

        Assert.Null(item.ClientRemovalPendingAt);
    }

    [Fact]
    public async Task A_pending_torrent_is_removed_with_its_files_and_the_row_settles()
    {
        var id = SeedPending();
        _client.Torrents.Add(Torrent());

        await RunAsync();

        Assert.Equal([(Hash, true)], _client.Removed);
        var item = Reload(id);
        Assert.Null(item.ClientRemovalPendingAt);
        Assert.NotNull(item.ClientRemovedAt);
    }

    [Fact]
    public async Task A_row_whose_release_info_cannot_be_read_settles_without_stopping_the_rest()
    {
        var broken = SeedPending(pendingSince: DateTime.UtcNow.AddHours(-1));
        using (var db = _db.NewContext())
        {
            db.DownloadQueue.Single(q => q.Id == broken).ReleaseInfoJson = "{not json";
            db.SaveChanges();
        }

        var id = SeedPending();
        _client.Torrents.Add(Torrent());

        await RunAsync();

        Assert.Null(Reload(broken).ClientRemovalPendingAt);
        Assert.Equal([(Hash, true)], _client.Removed);
        Assert.NotNull(Reload(id).ClientRemovedAt);
    }

    [Fact]
    public async Task Files_maki_cannot_see_are_kept_but_the_torrent_still_goes()
    {
        SeedPending(contentPath: Path.Combine(_download, "not there"));
        _client.Torrents.Add(Torrent());

        await RunAsync();

        Assert.Equal([(Hash, false)], _client.Removed);
    }

    [Fact]
    public async Task A_torrent_a_live_row_still_needs_waits()
    {
        var id = SeedPending();
        SeedLive(QueueStatus.Downloading, Hash);
        _client.Torrents.Add(Torrent());

        await RunAsync();

        Assert.Empty(_client.Removed);
        Assert.NotNull(Reload(id).ClientRemovalPendingAt);
    }

    [Fact]
    public async Task A_hashless_grab_for_the_same_series_holds_every_removal_there()
    {
        var id = SeedPending();
        SeedLive(QueueStatus.Downloading, hash: null);
        _client.Torrents.Add(Torrent());

        await RunAsync();

        Assert.Empty(_client.Removed);
        Assert.NotNull(Reload(id).ClientRemovalPendingAt);
    }

    [Fact]
    public async Task A_different_client_than_the_one_imported_from_gets_nothing()
    {
        var id = SeedPending(client: "qbittorrent|http://other.test|maki");
        _client.Torrents.Add(Torrent());

        await RunAsync();

        Assert.Empty(_client.Removed);
        var item = Reload(id);
        Assert.Null(item.ClientRemovalPendingAt);
        Assert.Null(item.ClientRemovedAt);
    }

    [Fact]
    public async Task A_torrent_already_gone_settles_without_counting_as_removed()
    {
        var id = SeedPending();

        await RunAsync();

        var item = Reload(id);
        Assert.Null(item.ClientRemovalPendingAt);
        Assert.Null(item.ClientRemovedAt);
    }

    [Fact]
    public async Task A_torrent_moved_out_of_makis_category_is_left_and_the_row_settles()
    {
        var id = SeedPending();
        _client.Torrents.Add(Torrent());
        _client.OutsideLabel.Add(Hash);

        await RunAsync();

        Assert.Single(_client.Torrents);
        var item = Reload(id);
        Assert.Null(item.ClientRemovalPendingAt);
        Assert.Null(item.ClientRemovedAt);
    }

    [Fact]
    public async Task A_client_that_is_down_leaves_the_removal_pending_for_the_next_run()
    {
        var id = SeedPending();
        _client.Torrents.Add(Torrent());
        _client.RemoveFailure = new TorrentClientException(TorrentClientFailure.Unreachable, "connection refused");

        await RunAsync();

        var item = Reload(id);
        Assert.Equal(QueueStatus.Completed, item.Status);
        Assert.NotNull(item.ClientRemovalPendingAt);
    }

    [Fact]
    public async Task A_removal_pending_past_the_limit_is_given_up()
    {
        var id = SeedPending(pendingSince: DateTime.UtcNow - TorrentRemovalJob.GiveUpAfter - TimeSpan.FromHours(1));
        _client.Torrents.Add(Torrent());
        SeedLive(QueueStatus.Downloading, Hash);

        await RunAsync();

        Assert.Empty(_client.Removed);
        Assert.Null(Reload(id).ClientRemovalPendingAt);
    }

    [Fact]
    public async Task Deleting_the_download_lets_page_conversion_look_at_a_hardlinked_import_again()
    {
        SeedPending();
        _client.Torrents.Add(Torrent());
        int hardSkipped, other;
        using (var db = _db.NewContext())
        {
            var linked = new ChapterFile
            {
                SeriesId = _seriesId, RelativePath = "Berserk/v01.cbz", ReleaseHash = Hash,
                CheckedWith = PlanAction.HardSkipName, ConversionCheckedAtUtc = DateTime.UtcNow,
            };
            var unrelated = new ChapterFile
            {
                SeriesId = _seriesId, RelativePath = "Berserk/v02.cbz", ReleaseHash = "other",
                CheckedWith = PlanAction.HardSkipName, ConversionCheckedAtUtc = DateTime.UtcNow,
            };
            db.ChapterFiles.AddRange(linked, unrelated);
            db.SaveChanges();
            (hardSkipped, other) = (linked.Id, unrelated.Id);
        }

        await RunAsync();

        using var check = _db.NewContext();
        var released = check.ChapterFiles.Single(f => f.Id == hardSkipped);
        Assert.Null(released.CheckedWith);
        Assert.Null(released.ConversionCheckedAtUtc);
        Assert.Equal(PlanAction.HardSkipName, check.ChapterFiles.Single(f => f.Id == other).CheckedWith);
    }

    [Fact]
    public void Housekeeping_keeps_a_row_until_its_removal_settles()
    {
        var waiting = SeedPending();
        int settled;
        using (var db = _db.NewContext())
        {
            foreach (var row in db.DownloadQueue)
            {
                row.CompletedAt = DateTime.UtcNow.AddDays(-40);
            }

            var done = new DownloadQueueItem
            {
                SeriesId = _seriesId, Protocol = AcquisitionProtocol.Torrent, Status = QueueStatus.Completed,
                Title = "done", QueuedAt = DateTime.UtcNow.AddDays(-41), CompletedAt = DateTime.UtcNow.AddDays(-40),
            };
            db.DownloadQueue.Add(done);
            db.SaveChanges();
            settled = done.Id;
        }

        using var check = _db.NewContext();
        var stale = HousekeepingJob.StaleSettled(check.DownloadQueue, DateTime.UtcNow.AddDays(-30)).Select(q => q.Id).ToList();

        Assert.Equal([settled], stale);
        Assert.DoesNotContain(waiting, stale);
    }

    [Fact]
    public async Task Turning_removal_off_settles_waiting_rows_without_removing_anything()
    {
        var id = SeedPending();
        _client.Torrents.Add(Torrent());
        _settings.Set(SettingKeys.DownloadClientRemoveCompleted, TorrentRemovalModes.Never);

        await RunAsync();

        Assert.Empty(_client.Removed);
        Assert.Null(Reload(id).ClientRemovalPendingAt);
    }

    [Fact]
    public async Task Turning_file_deletion_off_keeps_the_files_of_rows_already_waiting()
    {
        SeedPending(deleteData: true);
        _client.Torrents.Add(Torrent());
        _settings.Set(SettingKeys.DownloadClientRemoveData, "false");

        await RunAsync();

        Assert.Equal([(Hash, false)], _client.Removed);
    }

    [Fact]
    public async Task A_download_inside_a_library_root_folder_keeps_its_files()
    {
        SeedPending();
        _client.Torrents.Add(Torrent());
        using (var db = _db.NewContext())
        {
            db.RootFolders.Add(new RootFolder { Path = Path.GetDirectoryName(_download)! });
            db.SaveChanges();
        }

        await RunAsync();

        Assert.Equal([(Hash, false)], _client.Removed);
    }

    [Fact]
    public async Task With_no_client_configured_waiting_rows_settle_instead_of_waiting_forever()
    {
        var id = SeedPending();
        _settings.Set(SettingKeys.DownloadClientUrl, "");

        await RunAsync();

        Assert.Null(Reload(id).ClientRemovalPendingAt);
    }

    [Fact]
    public async Task A_failed_row_on_the_same_torrent_holds_it_for_a_retry()
    {
        var id = SeedPending();
        SeedLive(QueueStatus.Failed, Hash, seriesId: _db.SeedSeries("Vagabond"));
        _client.Torrents.Add(Torrent());

        await RunAsync();

        Assert.Empty(_client.Removed);
        Assert.NotNull(Reload(id).ClientRemovalPendingAt);
    }

    [Fact]
    public async Task A_failed_hashless_grab_does_not_hold_the_series()
    {
        SeedPending();
        SeedLive(QueueStatus.Failed, hash: null);
        _client.Torrents.Add(Torrent());

        await RunAsync();

        Assert.Single(_client.Removed);
    }
}
