using Maki.Api.Jobs;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Quartz;

namespace Maki.Api.Tests;

[Collection(ConfigDirCollection.Name)]
public class UpgradeScanJobTests : IDisposable
{
    private readonly UpgradeWorld _world = new();

    public UpgradeScanJobTests()
    {
        _world.Seed();
        _world.Chapter(1);
        _world.Settings.Set(SettingKeys.UpgradesScanHour, "0");
    }

    public void Dispose() => _world.Dispose();

    private Task RunJobAsync(bool force, Maki.Data.MakiDbContext? db = null) =>
        RunJobAsync(force ? new JobDataMap { [UpgradeScanJob.ForceKey] = true } : null, db);

    private readonly UpgradeScanTracker _tracker = new();

    private async Task RunJobAsync(JobDataMap? data, Maki.Data.MakiDbContext? db = null, CancellationToken ct = default)
    {
        using var own = _world.Db.NewContext();
        using var batches = _world.Batches();
        var job = new UpgradeScanJob(_world.Scanner(db ?? own, batches), _world.Settings, TimeProvider.System,
            NullLogger<UpgradeScanJob>.Instance, _tracker);
        await job.Execute(new TestJobContext(data, ct));
    }

    [Fact]
    public async Task A_shutdown_mid_scan_leaves_the_day_open()
    {
        _world.Settings.Set(SettingKeys.UpgradesEnabled, "true");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await RunJobAsync(null, ct: cts.Token);

        Assert.Null(await _world.Settings.GetAsync(SettingKeys.UpgradesLastScanDate));
        Assert.Equal(0, UpgradeRows());
    }

    private int UpgradeRows()
    {
        using var db = _world.Db.NewContext();
        return db.DownloadQueue.AsNoTracking().Count(q => q.Origin == DownloadOrigin.Upgrade);
    }

    [Fact]
    public async Task The_scheduled_run_honours_the_global_switch()
    {
        await RunJobAsync(force: false);

        Assert.Equal(0, UpgradeRows());
        Assert.Null(await _world.Settings.GetAsync(SettingKeys.UpgradesLastScanDate));

        _world.Settings.Set(SettingKeys.UpgradesEnabled, "true");
        await RunJobAsync(force: false);

        Assert.Equal(1, UpgradeRows());
        Assert.NotNull(await _world.Settings.GetAsync(SettingKeys.UpgradesLastScanDate));
    }

    [Fact]
    public async Task A_forced_run_scans_with_the_switch_off()
    {
        await RunJobAsync(force: true);

        Assert.Equal(1, UpgradeRows());
    }

    [Fact]
    public async Task A_series_run_scans_that_series_with_the_switch_off_and_leaves_the_marker()
    {
        await RunJobAsync(new JobDataMap { [UpgradeScanJob.SeriesKey] = _world.SeriesId });

        Assert.Equal(1, UpgradeRows());
        Assert.Null(await _world.Settings.GetAsync(SettingKeys.UpgradesLastScanDate));
        var status = _tracker.Status(_world.SeriesId)!;
        Assert.Equal(("done", 1, 1), (status.State, status.Probed, status.Queued));
        Assert.True(status.ChaptersChecked > 0);
        Assert.NotNull(status.FinishedAtUtc);
    }

    [Fact]
    public async Task A_crashed_scan_still_writes_the_marker()
    {
        _world.Settings.Set(SettingKeys.UpgradesEnabled, "true");
        var broken = _world.Db.NewContext();
        await broken.DisposeAsync();

        await RunJobAsync(force: false, broken);

        Assert.Equal(UpgradeOptions.MarkerDate(UpgradeOptions.LocalNow(TimeProvider.System)),
            await _world.Settings.GetAsync(SettingKeys.UpgradesLastScanDate));
        Assert.False(UpgradeScanService.IsRunning);
    }
}
