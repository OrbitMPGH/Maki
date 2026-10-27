using System.Text.Json;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Download;
using Maki.Core.Entities;
using Maki.Core.Indexers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// Where a parked torrent's files are when the import decision is made: the client's answer while it
/// lists the torrent, the path recorded when it finished once it does not.
/// </summary>
public class TorrentContentPathTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTorrentClient _client = new();
    private readonly string _mounted = Path.Combine(Path.GetTempPath(), "maki-content-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly int _seriesId;

    public TorrentContentPathTests()
    {
        Directory.CreateDirectory(Path.Combine(_mounted, "Berserk v01"));
        _db.SetConfig(
            (SettingKeys.DownloadClientUrl, "http://qbt.test"),
            (SettingKeys.DownloadClientPathMapFrom, "/dl"),
            (SettingKeys.DownloadClientPathMapTo, _mounted));
        _seriesId = _db.SeedSeries("Berserk");
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_mounted, recursive: true);
    }

    private DownloadQueueItem SeedParked(string? contentPath)
    {
        using var db = _db.NewContext();
        var item = new DownloadQueueItem
        {
            SeriesId = _seriesId,
            Protocol = AcquisitionProtocol.Torrent,
            Status = QueueStatus.AwaitingImport,
            Title = "Berserk v01",
            ReleaseInfoJson = JsonSerializer.Serialize(new ReleaseInfo("guid-1", "Berserk v01", "Nyaa", "abc123", contentPath)),
            QueuedAt = DateTime.UtcNow.AddHours(-1),
            SortOrder = 1,
        };
        db.DownloadQueue.Add(item);
        db.SaveChanges();
        return item;
    }

    private TorrentImportService Importer(Maki.Data.MakiDbContext db)
    {
        var settings = new SettingsService(_db.ScopeFactory());
        var releases = new ReleaseService(
            db, TestIndexers.Registry(new StubHttpClientFactory("[]")), _client.Registry(), settings, NullLogger<ReleaseService>.Instance);
        return new TorrentImportService(
            db, releases, _client.Registry(), null!, null!, null!, settings, null!, new RecordingInbox(),
            NullLogger<TorrentImportService>.Instance);
    }

    private async Task<string?> ResolveAsync(DownloadQueueItem item)
    {
        using var db = _db.NewContext();
        return await Importer(db).ResolveContentPathAsync(item, CancellationToken.None);
    }

    [Fact]
    public async Task A_listed_torrent_answers_with_the_clients_current_path()
    {
        var item = SeedParked("/dl/old place");
        _client.Torrents.Add(new ClientTorrent(
            "abc123", "Berserk v01", 1.0, true, "/dl/Berserk v01", DateTimeOffset.UtcNow, false, null));

        Assert.Equal(Path.Combine(_mounted, "Berserk v01"), await ResolveAsync(item));
    }

    [Fact]
    public async Task A_torrent_the_client_no_longer_lists_answers_with_the_path_recorded_when_it_finished()
    {
        var item = SeedParked("/dl/Berserk v01");

        Assert.Equal(Path.Combine(_mounted, "Berserk v01"), await ResolveAsync(item));
    }

    [Fact]
    public async Task A_client_that_is_down_falls_back_to_the_recorded_path_instead_of_throwing()
    {
        var item = SeedParked("/dl/Berserk v01");
        _client.ListFailure = new TorrentClientException(TorrentClientFailure.Unreachable, "connection refused");

        Assert.Equal(Path.Combine(_mounted, "Berserk v01"), await ResolveAsync(item));
    }

    [Fact]
    public async Task A_recorded_path_that_is_gone_is_still_returned_so_the_plan_can_name_it()
    {
        var item = SeedParked("/dl/Somewhere else");

        using var db = _db.NewContext();
        var importer = Importer(db);
        var path = await importer.ResolveContentPathAsync(item, CancellationToken.None);
        var plan = await importer.PlanAsync(item, db.Series.Single(s => s.Id == _seriesId), path, CancellationToken.None);

        Assert.Equal("error.torrentImport.pathNotAccessible", plan.ErrorKey);
    }

    [Fact]
    public async Task A_torrent_never_seen_finished_and_gone_from_the_client_has_no_path()
    {
        var item = SeedParked(contentPath: null);

        using var db = _db.NewContext();
        var importer = Importer(db);
        var path = await importer.ResolveContentPathAsync(item, CancellationToken.None);
        var plan = await importer.PlanAsync(item, db.Series.Single(s => s.Id == _seriesId), path, CancellationToken.None);

        Assert.Null(path);
        Assert.Equal("error.torrentImport.notInClient", plan.ErrorKey);
    }

    [Fact]
    public async Task A_parked_download_whose_torrent_is_gone_is_planned_from_the_recorded_path()
    {
        var item = SeedParked("/dl/Berserk v01");

        using var db = _db.NewContext();
        var importer = Importer(db);
        var path = await importer.ResolveContentPathAsync(item, CancellationToken.None);
        var plan = await importer.PlanAsync(item, db.Series.Single(s => s.Id == _seriesId), path, CancellationToken.None);

        // The folder is empty, so the plan reads it and finds nothing, rather than giving up on the client.
        Assert.Equal("error.torrentImport.noComicsEmpty", plan.ErrorKey);
    }
}
