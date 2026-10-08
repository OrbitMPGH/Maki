using Maki.Api.Controllers;
using Maki.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public class DownloadSettingsValidationTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private SettingsController Controller() => new(
        localizer: new TestLocalizer(), userLocales: new TestUserLocaleResolver(),
        settings: new SettingsService(_db.ScopeFactory()),
        naming: null!,
        flareSolverr: null!, prowlarr: null!, qbittorrent: null!, kavita: null!,
        sourceRegistry: null!, sourceAvailability: null!, mangaBakaDump: null!, embeddingModel: null!,
        embeddingStore: null!, embeddingStatus: null!, embeddingIndexer: null!,
        prebuiltIndex: null!, recoGraph: null!, recoGraphCache: null!, coReadInstaller: null!,
        coReadCache: null!, readerCohortInstaller: null!, readerCohortCache: null!,
        tasteVectorInstaller: null!, vectorIndexCache: null!, modelSwitcher: null!,
        db: _db.NewContext(), updateCheck: null!, currentUser: null!, userSettings: null!,
        kavitaUser: null!, kavitaLive: null!, schedulerFactory: null!, scopeFactory: _db.ScopeFactory(),
        logger: NullLogger<SettingsController>.Instance);

    private static SettingsController.DownloadSettings Payload(int chaptersLeft = 5, int chapters = 10) =>
        new(ConcurrentChapters: 2, RetryEnabled: true, RetryMaxAttempts: 5,
            SmartDownloadChaptersLeft: chaptersLeft, SmartDownloadChapters: chapters, ItemTimeoutMinutes: 120);

    [Theory]
    [InlineData(0, 10)]
    [InlineData(11, 10)]
    [InlineData(5, 0)]
    [InlineData(5, 21)]
    public async Task Smart_download_values_outside_their_range_are_refused(int chaptersLeft, int chapters)
    {
        var result = await Controller().SetDownload(Payload(chaptersLeft, chapters), CancellationToken.None);

        Assert.IsNotType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Smart_download_values_inside_their_range_are_saved()
    {
        var result = await Controller().SetDownload(Payload(1, 20), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
    }
}
