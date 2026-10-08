using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public class DownloadSettingsValidationTests : IDisposable
{
    private readonly TestDb _db = new();
    private SettingsService Store() => new(_db.ScopeFactory());

    public void Dispose() => _db.Dispose();

    private SettingsController Controller() => new(
        localizer: new TestLocalizer(), userLocales: new TestUserLocaleResolver(),
        settings: Store(),
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

    [Fact]
    public async Task A_stored_zero_reads_back_clamped_so_the_next_save_validates()
    {
        var store = Store();
        await store.SetAsync(SettingKeys.SmartDownloadChaptersLeft, "0");
        await store.SetAsync(SettingKeys.SmartDownloadChaptersCount, "0");

        var result = await Controller().GetDownload(CancellationToken.None);

        var body = Assert.IsType<SettingsController.DownloadSettings>(
            Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(1, body.SmartDownloadChaptersLeft);
        Assert.Equal(1, body.SmartDownloadChapters);
    }
}
