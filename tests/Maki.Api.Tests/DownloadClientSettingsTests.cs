using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Download;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public sealed class DownloadClientSettingsTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private SettingsController Controller(SettingsService settings, ITorrentClient? client = null) => new(
        localizer: new TestLocalizer(), userLocales: new TestUserLocaleResolver(),
        settings: settings, naming: null!, flareSolverr: null!, indexers: null!,
        torrentClients: new TorrentClientRegistry([client ?? new FakeTorrentClient()]),
        kavita: null!, sourceRegistry: null!, sourceAvailability: null!,
        mangaBakaDump: null!, embeddingModel: null!, embeddingStore: null!, embeddingStatus: null!,
        embeddingIndexer: null!, prebuiltIndex: null!, recoGraph: null!,
        recoGraphCache: null!, coReadInstaller: null!, coReadCache: null!, readerCohortInstaller: null!,
        readerCohortCache: null!, tasteVectorInstaller: null!, vectorIndexCache: null!,
        modelSwitcher: null!, db: _db.NewContext(), updateCheck: null!, currentUser: new TestCurrentUser(1),
        userSettings: null!,
        kavitaUser: new KavitaUserResolver(_db.ScopeFactory(), settings),
        kavitaLive: new KavitaLiveReadSync(settings, null!, null!, null!, null!, null!,
            NullLogger<KavitaLiveReadSync>.Instance),
        schedulerFactory: null!, scopeFactory: null!,
        logger: NullLogger<SettingsController>.Instance);

    private static SettingsController.DownloadClientSettings Request(string? kind = "qbittorrent", string? url = "http://qbt:8080", string? label = "maki") =>
        new(kind, url, "admin", "secret", label, null, null);

    [Theory]
    [InlineData("Manga Downloads")]
    [InlineData("books/maki")]
    [InlineData("maki")]
    public async Task A_label_with_spaces_or_slashes_saves(string label)
    {
        var settings = new SettingsService(_db.ScopeFactory());

        var result = await Controller(settings).SetDownloadClient(Request(label: label), default);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(label, await settings.GetAsync(SettingKeys.DownloadClientLabel));
    }

    [Theory]
    [InlineData(" maki")]
    [InlineData("maki ")]
    [InlineData("ma\tki")]
    [InlineData("ma\nki")]
    public async Task A_label_with_edge_whitespace_or_control_characters_is_refused(string label)
    {
        var settings = new SettingsService(_db.ScopeFactory());

        var result = await Controller(settings).SetDownloadClient(Request(label: label), default);

        Assert.IsNotType<OkObjectResult>(result);
        Assert.Null(await settings.GetAsync(SettingKeys.DownloadClientLabel));
    }

    [Theory]
    [InlineData("Manga Downloads", false)]
    [InlineData("books/maki", false)]
    [InlineData("Maki_books-1.0", true)]
    [InlineData(null, true)]
    public async Task A_deluge_label_must_be_one_deluge_can_store(string? label, bool saves)
    {
        var settings = new SettingsService(_db.ScopeFactory());

        var result = await Controller(settings).SetDownloadClient(Request(kind: "deluge", label: label), default);

        Assert.Equal(saves, result is OkObjectResult);
        Assert.Equal(saves ? "deluge" : null, await settings.GetAsync(SettingKeys.DownloadClientKind));
    }

    [Fact]
    public async Task A_label_over_64_characters_is_refused()
    {
        var settings = new SettingsService(_db.ScopeFactory());

        var result = await Controller(settings).SetDownloadClient(Request(label: new string('a', 65)), default);

        Assert.IsNotType<OkObjectResult>(result);
    }

    [Theory]
    [InlineData("transmission", "maki", false, Maki.Core.Entities.QueueStatus.Downloading)]
    [InlineData("qbittorrent", "manga", false, Maki.Core.Entities.QueueStatus.Downloading)]
    [InlineData("qbittorrent", null, true, Maki.Core.Entities.QueueStatus.Downloading)]
    [InlineData("qbittorrent", "maki", true, Maki.Core.Entities.QueueStatus.Downloading)]
    [InlineData("transmission", "maki", false, Maki.Core.Entities.QueueStatus.AwaitingImport)]
    [InlineData("qbittorrent", "manga", false, Maki.Core.Entities.QueueStatus.Importing)]
    [InlineData("transmission", "maki", true, Maki.Core.Entities.QueueStatus.Completed)]
    [InlineData("transmission", "maki", true, Maki.Core.Entities.QueueStatus.Failed)]
    public async Task A_kind_or_label_change_is_refused_while_a_torrent_is_in_flight(
        string kind, string? label, bool saves, Maki.Core.Entities.QueueStatus status)
    {
        var seriesId = _db.SeedSeries("Berserk");
        using (var db = _db.NewContext())
        {
            db.DownloadQueue.Add(new Maki.Core.Entities.DownloadQueueItem
            {
                SeriesId = seriesId, Protocol = Maki.Core.Entities.AcquisitionProtocol.Torrent,
                Status = status, Title = "Berserk v01", QueuedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
        }

        var settings = new SettingsService(_db.ScopeFactory());

        var result = await Controller(settings).SetDownloadClient(Request(kind: kind, url: "http://new:8080", label: label), default);

        Assert.Equal(saves, result is OkObjectResult);
        Assert.Equal(saves ? "http://new:8080" : null, await settings.GetAsync(SettingKeys.DownloadClientUrl));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("utorrent")]
    public async Task Saving_without_a_known_kind_changes_nothing(string? kind)
    {
        _db.SetConfig((SettingKeys.DownloadClientKind, "transmission"));
        var settings = new SettingsService(_db.ScopeFactory());

        var result = await Controller(settings).SetDownloadClient(Request(kind: kind), default);

        Assert.IsNotType<OkObjectResult>(result);
        Assert.Equal("transmission", await settings.GetAsync(SettingKeys.DownloadClientKind));
    }

    [Fact]
    public async Task Testing_a_url_without_a_scheme_is_refused_instead_of_throwing()
    {
        var settings = new SettingsService(_db.ScopeFactory());

        var result = await Controller(settings).TestDownloadClient(Request(url: "nas:9091"), default);

        Assert.IsNotType<OkObjectResult>(result);
        Assert.NotEqual(StatusCodes.Status502BadGateway, (result as ObjectResult)?.StatusCode);
    }

    [Fact]
    public async Task Testing_a_driver_that_throws_anything_answers_no_response()
    {
        var settings = new SettingsService(_db.ScopeFactory());

        var result = await Controller(settings, new ThrowingClient()).TestDownloadClient(Request(), default);

        Assert.Equal(StatusCodes.Status502BadGateway, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    private sealed class ThrowingClient : ITorrentClient
    {
        public TorrentClientKind Kind => TorrentClientKind.QBittorrent;
        public bool UsesUsername => true;
        public string DefaultUrl => "http://localhost:8080";
        public Task PingAsync(TorrentClientSettings settings, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> AddAsync(TorrentClientSettings settings, string magnetOrUrl, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AddFileAsync(TorrentClientSettings settings, byte[] torrent, string infoHash, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ClientTorrent>> ListAsync(TorrentClientSettings settings, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
