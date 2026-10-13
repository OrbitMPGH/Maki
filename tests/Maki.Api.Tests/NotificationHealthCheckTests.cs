using Maki.Api.Configuration;
using Maki.Api.Services;
using Maki.Core.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace Maki.Api.Tests;

[Collection(ConfigDirCollection.Name)]
public class NotificationHealthCheckTests : IDisposable
{
    private readonly UpgradeWorld _world = new();

    public NotificationHealthCheckTests() => _world.Seed();

    public void Dispose() => _world.Dispose();

    private int AddConnection(int failures, bool enabled = true)
    {
        using var db = _world.Db.NewContext();
        var row = new Notification
        {
            Name = "ops channel", Type = NotificationType.Discord, Enabled = enabled, ConsecutiveFailures = failures
        };
        db.Notifications.Add(row);
        db.SaveChanges();
        return row.Id;
    }

    private void SetFailures(int id, int failures)
    {
        using var db = _world.Db.NewContext();
        db.Notifications.Single(n => n.Id == id).ConsecutiveFailures = failures;
        db.SaveChanges();
    }

    private async Task RefreshAsync()
    {
        using var db = _world.Db.NewContext();
        var services = new ServiceCollection().AddSingleton(_world.Queue).BuildServiceProvider();
        var monitor = new HealthMonitor(db, null!, _world.Settings, new AppPaths(), _world.Registry, _world.Availability,
            services, new RecordingNotifications(), _world.Inbox, new KeyLocalizer(), new TestUserLocaleResolver(), null!);
        await monitor.RefreshAsync(CancellationToken.None);
    }

    private HealthCheckRecord? Check(int id)
    {
        using var db = _world.Db.NewContext();
        return db.HealthChecks.SingleOrDefault(c => c.Id == $"notification:{id}");
    }

    [Fact]
    public async Task Five_misses_in_a_row_raise_a_warning_that_clears_on_recovery()
    {
        var id = AddConnection(failures: 4);
        await RefreshAsync();
        Assert.Null(Check(id));

        SetFailures(id, 5);
        await RefreshAsync();
        var warning = Check(id)!;
        Assert.Equal("warning", warning.Status);
        Assert.Equal("health.check.notificationFailing", warning.MessageKey);
        Assert.Equal("connections", warning.Category);

        SetFailures(id, 0);
        await RefreshAsync();
        var recovered = Check(id)!;
        Assert.Equal("healthy", recovered.Status);
        Assert.Equal("health.check.notificationDelivering", recovered.MessageKey);
    }

    [Fact]
    public async Task A_deleted_or_disabled_connection_takes_its_row_with_it()
    {
        var deleted = AddConnection(failures: 9);
        var disabled = AddConnection(failures: 9);
        await RefreshAsync();
        Assert.NotNull(Check(deleted));
        Assert.NotNull(Check(disabled));

        using (var db = _world.Db.NewContext())
        {
            db.Notifications.Remove(db.Notifications.Single(n => n.Id == deleted));
            db.Notifications.Single(n => n.Id == disabled).Enabled = false;
            db.SaveChanges();
        }

        await RefreshAsync();
        Assert.Null(Check(deleted));
        Assert.Null(Check(disabled));
    }

    private sealed class KeyLocalizer : Maki.Api.Localization.ILocalizer
    {
        public string Get(string key, object? args = null) => key;
        public string GetFor(string locale, string key, object? args = null) => key;
    }
}
