using Maki.Api.Hubs;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Inbox;
using Maki.Core.Notifications;
using Maki.Core.Security;
using Maki.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// Writing the rows: who gets one, who is filtered out by their own preferences, and the guard that
/// stops an admin-only event reaching a reader.
/// </summary>
public class InboxServiceTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly TestDb _db = new();
    private readonly InboxService _inbox;

    public InboxServiceTests()
    {
        var scopeFactory = _db.ScopeFactory();
        _inbox = new InboxService(
            scopeFactory,
            new InboxAudienceResolver(scopeFactory),
            new TestUserSettingsStore(_db),
            new EventBroadcaster(new NoopHubContext(), scopeFactory),
            new StoppedClock(T0),
            NullLogger<InboxService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_raise_writes_one_row_per_recipient()
    {
        var one = _db.SeedUser("one", MakiPermission.Admin);
        var two = _db.SeedUser("two", MakiPermission.Admin);

        await _inbox.RaiseAsync(InboxEventType.HealthIssue,
            new InboxMessage("Health issue", "A source is failing", NotificationLevel.Warning),
            InboxAudience.Admins);

        using var db = _db.NewContext();
        var rows = db.UserNotifications.IgnoreQueryFilters().ToList();

        Assert.Contains(rows, r => r.UserId == one);
        Assert.Contains(rows, r => r.UserId == two);
        Assert.All(rows, r =>
        {
            Assert.Equal(InboxEventType.HealthIssue, r.Type);
            Assert.Equal(NotificationLevel.Warning, r.Level);
            Assert.Equal(T0.UtcDateTime, r.CreatedAt);
            Assert.Null(r.ReadAt);
        });
    }

    [Fact]
    public async Task The_owner_is_written_explicitly_and_not_left_to_the_insert_stamp()
    {
        // The service runs on an unrestricted scope, where the IUserOwned stamp deliberately does
        // nothing. A row landing on user 0 would be invisible to everybody.
        var reader = _db.SeedUser("reader", MakiPermission.None);

        await _inbox.RaiseAsync(InboxEventType.LevelUp,
            new InboxMessage("Level 4", "You reached level 4."), InboxAudience.User(reader));

        using var db = _db.NewContext();
        var row = Assert.Single(db.UserNotifications.IgnoreQueryFilters().ToList());
        Assert.Equal(reader, row.UserId);
    }

    [Fact]
    public async Task A_type_the_user_switched_off_is_not_written_for_them()
    {
        var mutes = _db.SeedUser("mutes", MakiPermission.Admin);
        var wants = _db.SeedUser("wants", MakiPermission.Admin);

        _db.SetUserConfig(mutes, (SettingKeys.NotificationsInbox,
            InboxPrefsSpec.Serialize(new InboxPrefsSpec(
                new Dictionary<string, bool> { [InboxEventTypes.Key(InboxEventType.HealthIssue)] = false }))));

        await _inbox.RaiseAsync(InboxEventType.HealthIssue,
            new InboxMessage("Health issue", "A source is failing"), InboxAudience.Admins);

        using var db = _db.NewContext();
        var rows = db.UserNotifications.IgnoreQueryFilters().ToList();

        Assert.Contains(rows, r => r.UserId == wants);
        Assert.DoesNotContain(rows, r => r.UserId == mutes);
    }

    [Fact]
    public async Task A_type_that_defaults_off_is_not_written_until_it_is_switched_on()
    {
        var reader = _db.SeedUser("reader", MakiPermission.None);
        var seriesId = _db.SeedSeries();

        await _inbox.RaiseAsync(InboxEventType.SourceMatchFinished,
            new InboxMessage("Sources matched", "Matched MangaDex"), InboxAudience.User(reader));

        using (var db = _db.NewContext())
        {
            Assert.Empty(db.UserNotifications.IgnoreQueryFilters().ToList());
        }

        _db.SetUserConfig(reader, (SettingKeys.NotificationsInbox,
            InboxPrefsSpec.Serialize(new InboxPrefsSpec(
                new Dictionary<string, bool>
                {
                    [InboxEventTypes.Key(InboxEventType.SourceMatchFinished)] = true,
                }))));

        await _inbox.RaiseAsync(InboxEventType.SourceMatchFinished,
            new InboxMessage("Sources matched", "Matched MangaDex", SeriesId: seriesId),
            InboxAudience.User(reader));

        using (var db = _db.NewContext())
        {
            Assert.Single(db.UserNotifications.IgnoreQueryFilters().ToList());
        }
    }

    [Fact]
    public async Task An_admin_only_event_aimed_at_a_reader_is_dropped_rather_than_delivered()
    {
        var reader = _db.SeedUser("reader", MakiPermission.None);

        await _inbox.RaiseAsync(InboxEventType.UpdateAvailable,
            new InboxMessage("Update available", "Maki 2.0 is out"), InboxAudience.User(reader));

        using var db = _db.NewContext();
        Assert.Empty(db.UserNotifications.IgnoreQueryFilters().ToList());
    }

    [Fact]
    public async Task Unknown_is_never_written()
    {
        var reader = _db.SeedUser("reader", MakiPermission.None);

        await _inbox.RaiseAsync(InboxEventType.Unknown,
            new InboxMessage("?", "?"), InboxAudience.User(reader));

        using var db = _db.NewContext();
        Assert.Empty(db.UserNotifications.IgnoreQueryFilters().ToList());
    }

    /// <summary>Reads and writes the real UserSettings rows, so preference gating runs for real.</summary>
    private sealed class TestUserSettingsStore(TestDb db) : IUserSettingsStore
    {
        public Task<string?> GetAsync(int userId, string key, CancellationToken ct = default)
        {
            using var context = db.NewContext();
            return Task.FromResult(context.UserSettings
                .Where(s => s.UserId == userId && s.Key == key)
                .Select(s => s.Value)
                .FirstOrDefault());
        }

        public Task SetAsync(int userId, string key, string? value, CancellationToken ct = default)
        {
            db.SetUserConfig(userId, (key, value ?? string.Empty));
            return Task.CompletedTask;
        }
    }
}
