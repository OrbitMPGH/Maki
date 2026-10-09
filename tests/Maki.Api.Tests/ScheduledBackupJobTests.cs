using Maki.Api;
using Maki.Api.Configuration;
using Maki.Api.Jobs;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

[Collection(ConfigDirCollection.Name)]
public class ScheduledBackupJobTests : IDisposable
{
    private readonly string _configDir;
    private readonly string? _priorEnv;
    private readonly AppPaths _paths;
    private readonly MakiDbContext _db;
    private readonly FakeAppSettings _settings = new();

    public ScheduledBackupJobTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "maki-scheduled-backup-tests", Guid.NewGuid().ToString("N"));
        _priorEnv = Environment.GetEnvironmentVariable("MAKI_CONFIG_DIR");
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _configDir);

        _paths = new AppPaths();
        _db = new MakiDbContext(new DbContextOptionsBuilder<MakiDbContext>()
            .UseSqlite($"Data Source={_paths.DatabasePath}")
            .Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _priorEnv);
        try
        {
            Directory.Delete(_configDir, recursive: true);
        }
        catch
        {
            // best-effort temp cleanup
        }
    }

    private ScheduledBackupJob Build() => new(
        new BackupService(_paths, _db, _settings, new TestLocalizer(), NullLogger<BackupService>.Instance),
        _settings, _paths, TimeProvider.System, NullLogger<ScheduledBackupJob>.Instance);

    private string[] Zips() => Directory.GetFiles(_paths.BackupDir, "*.zip");

    private void SeedBackup(int ageDays)
    {
        var path = Path.Combine(_paths.BackupDir, $"maki-20200101-000000-000-manual.zip");
        File.WriteAllBytes(path, []);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-ageDays));
    }

    [Fact]
    public async Task Does_nothing_while_the_setting_is_off()
    {
        Assert.False(await Build().RunAsync(default));
        Assert.Empty(Zips());
    }

    [Fact]
    public async Task Takes_a_scheduled_backup_when_none_exists()
    {
        _settings.Set(SettingKeys.BackupScheduled, "true");

        Assert.True(await Build().RunAsync(default));

        Assert.EndsWith("-scheduled.zip", Assert.Single(Zips()));
    }

    [Fact]
    public async Task Skips_when_the_newest_backup_is_fresh()
    {
        _settings.Set(SettingKeys.BackupScheduled, "true");
        SeedBackup(ageDays: 2);

        Assert.False(await Build().RunAsync(default));
        Assert.Single(Zips());
    }

    [Fact]
    public async Task Takes_a_backup_when_the_newest_is_older_than_the_window()
    {
        _settings.Set(SettingKeys.BackupScheduled, "true");
        SeedBackup(ageDays: 7);

        Assert.True(await Build().RunAsync(default));
        Assert.Equal(2, Zips().Length);
    }

    [Fact]
    public async Task Honours_a_shorter_freshness_window_from_the_health_options()
    {
        _settings.Set(SettingKeys.BackupScheduled, "true").Set(SettingKeys.HealthOptions, "{\"backupDays\":1}");
        SeedBackup(ageDays: 2);

        Assert.True(await Build().RunAsync(default));
    }

    [Fact]
    public async Task Keeps_scheduled_backups_to_the_retention_count()
    {
        _settings.Set(SettingKeys.BackupScheduled, "true").Set(SettingKeys.BackupRetention, "1");
        var job = Build();
        Assert.True(await job.RunAsync(default));
        foreach (var zip in Zips())
            File.SetLastWriteTimeUtc(zip, DateTime.UtcNow.AddDays(-30));
        await Task.Delay(5);

        Assert.True(await job.RunAsync(default));

        Assert.Single(Zips());
    }

    [Theory]
    [InlineData(false, 1, "healthy", "health.check.lastBackup")]
    [InlineData(true, 1, "healthy", "health.check.lastBackup")]
    [InlineData(false, 30, "disabled", "health.check.backupScheduleOff")]
    [InlineData(true, 30, "warning", "health.check.lastBackup")]
    public void Health_check_follows_the_schedule_setting(bool scheduled, int ageDays, string status, string key)
    {
        var now = DateTime.UtcNow;
        Assert.Equal((status, key), HealthMonitor.BackupCheck(now.AddDays(-ageDays), now, 7, scheduled));
    }

    [Fact]
    public void Health_check_with_no_backup_is_disabled_when_unscheduled_and_a_warning_when_scheduled()
    {
        var now = DateTime.UtcNow;
        Assert.Equal(("disabled", "health.check.backupScheduleOff"),
            HealthMonitor.BackupCheck(DateTime.MinValue, now, 7, scheduled: false));
        Assert.Equal(("warning", "health.check.noBackup"),
            HealthMonitor.BackupCheck(DateTime.MinValue, now, 7, scheduled: true));
    }
}
