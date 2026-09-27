using Maki.Api.Configuration;
using Maki.Api.Jobs;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Quartz;

namespace Maki.Api.Tests;

[Collection(ConfigDirCollection.Name)]
public class UpgradeTrashTests : IDisposable
{
    private readonly UpgradeWorld _world = new();

    public UpgradeTrashTests() => _world.Seed();

    public void Dispose() => _world.Dispose();

    private int SeedTrash(string name, int ageDays, int bytes = 10)
    {
        var (chapterId, fileId) = _world.Chapter(ageDays + 100);
        var relative = $".maki-trash/{_world.SeriesId}/{name}";
        var path = Path.Combine(_world.Library, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        using var db = _world.Db.NewContext();
        var row = new UpgradeHistory
        {
            SeriesId = _world.SeriesId, ChapterId = chapterId, ChapterFileId = fileId!.Value, TrashPath = relative,
            TrashBytes = bytes, CreatedAtUtc = DateTime.UtcNow.AddDays(-ageDays)
        };
        db.UpgradeHistory.Add(row);
        db.SaveChanges();
        return row.Id;
    }

    private async Task HousekeepAsync()
    {
        using var db = _world.Db.NewContext();
        var job = new HousekeepingJob(db, new AppPaths(),
            new UpgradeTrashService(db, _world.Settings, NullLogger<UpgradeTrashService>.Instance),
            NullLogger<HousekeepingJob>.Instance);
        await job.Execute(new TestJobContext());
    }

    [Fact]
    public async Task Housekeeping_purges_trash_past_retention_and_clears_its_path()
    {
        var old = SeedTrash("1-old.cbz", ageDays: 20);
        var fresh = SeedTrash("2-fresh.cbz", ageDays: 2);
        var stray = Path.Combine(_world.Library, ".maki-trash", "999", "stray.cbz");
        Directory.CreateDirectory(Path.GetDirectoryName(stray)!);
        File.WriteAllBytes(stray, [1]);
        File.SetCreationTimeUtc(stray, DateTime.UtcNow.AddDays(-30));
        File.SetLastWriteTimeUtc(stray, DateTime.UtcNow.AddDays(-30));

        await HousekeepAsync();

        using var db = _world.Db.NewContext();
        var oldRow = db.UpgradeHistory.Single(h => h.Id == old);
        Assert.Null(oldRow.TrashPath);
        Assert.Equal(0, oldRow.TrashBytes);
        Assert.False(File.Exists(Path.Combine(_world.Library, ".maki-trash", _world.SeriesId.ToString(), "1-old.cbz")));
        var freshRow = db.UpgradeHistory.Single(h => h.Id == fresh);
        Assert.NotNull(freshRow.TrashPath);
        Assert.True(File.Exists(Path.Combine(_world.Library, freshRow.TrashPath!)));
        Assert.False(File.Exists(stray));
        Assert.False(Directory.Exists(Path.GetDirectoryName(stray)));
    }

    [Fact]
    public async Task A_retention_of_zero_purges_everything_on_the_next_run()
    {
        SeedTrash("1-new.cbz", ageDays: 0);
        _world.Settings.Set(SettingKeys.UpgradesTrashRetentionDays, "0");

        await HousekeepAsync();

        using var db = _world.Db.NewContext();
        Assert.Null(db.UpgradeHistory.Single().TrashPath);
    }

    [Fact]
    public async Task Size_counts_only_rows_still_holding_a_file()
    {
        SeedTrash("1-a.cbz", ageDays: 1, bytes: 100);
        SeedTrash("2-b.cbz", ageDays: 30, bytes: 50);

        using (var db = _world.Db.NewContext())
        {
            Assert.Equal((150L, 2), await new UpgradeTrashService(db, _world.Settings,
                NullLogger<UpgradeTrashService>.Instance).SizeAsync(default));
        }

        await HousekeepAsync();

        using (var db = _world.Db.NewContext())
        {
            Assert.Equal((100L, 1), await new UpgradeTrashService(db, _world.Settings,
                NullLogger<UpgradeTrashService>.Instance).SizeAsync(default));
        }
    }

    [Fact]
    public async Task The_health_row_appears_with_trash_and_goes_once_it_is_empty()
    {
        var id = SeedTrash("1-a.cbz", ageDays: 1);

        await RefreshHealthAsync();
        using (var db = _world.Db.NewContext())
        {
            var row = db.HealthChecks.Single(c => c.Id == "upgrade-trash");
            Assert.Equal("health.check.upgradeTrash", row.MessageKey);
            Assert.Equal("storage", row.Category);
            Assert.Contains("\"files\":1", row.ParamsJson);
        }

        using (var db = _world.Db.NewContext())
        {
            db.UpgradeHistory.Single(h => h.Id == id).TrashPath = null;
            db.SaveChanges();
        }

        await RefreshHealthAsync();
        using (var db = _world.Db.NewContext())
        {
            Assert.DoesNotContain(db.HealthChecks, c => c.Id == "upgrade-trash");
        }
    }

    private async Task RefreshHealthAsync()
    {
        using var db = _world.Db.NewContext();
        var services = new ServiceCollection().AddSingleton(_world.Queue).BuildServiceProvider();
        var monitor = new HealthMonitor(db, null!, _world.Settings, new AppPaths(), _world.Registry, _world.Availability,
            services, new RecordingNotifications(), _world.Inbox, new KeyLocalizer(), new TestUserLocaleResolver(), null!);
        await monitor.RefreshAsync(CancellationToken.None);
    }

    /// <summary>Health notifications pass a dictionary of values, which TestLocalizer cannot reflect over.</summary>
    private sealed class KeyLocalizer : Maki.Api.Localization.ILocalizer
    {
        public string Get(string key, object? args = null) => key;
        public string GetFor(string locale, string key, object? args = null) => key;
    }
}
