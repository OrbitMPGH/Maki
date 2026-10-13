using Maki.Api.Controllers;
using Maki.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public class BackupSettingsTests : IDisposable
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

    private async Task<SettingsController.BackupSettings> Get() =>
        Assert.IsType<SettingsController.BackupSettings>(
            Assert.IsType<OkObjectResult>(await Controller().GetBackup(CancellationToken.None)).Value);

    [Fact]
    public async Task Defaults_to_five_backups_and_no_schedule()
    {
        var body = await Get();

        Assert.Equal(5, body.Retention);
        Assert.False(body.Scheduled);
    }

    [Fact]
    public async Task The_schedule_flag_round_trips_without_touching_retention()
    {
        await Controller().SetBackup(new(Retention: 8, Scheduled: true), CancellationToken.None);
        Assert.Equal(new SettingsController.BackupSettings(8, true), await Get());

        await Controller().SetBackup(new(Retention: 8, Scheduled: false), CancellationToken.None);
        Assert.Equal(new SettingsController.BackupSettings(8, false), await Get());
    }

    [Fact]
    public async Task A_request_without_the_flag_leaves_the_stored_schedule_alone()
    {
        await Controller().SetBackup(new(Retention: 5, Scheduled: true), CancellationToken.None);

        await Controller().SetBackup(new(Retention: 12), CancellationToken.None);

        Assert.Equal(new SettingsController.BackupSettings(12, true), await Get());
    }
}
