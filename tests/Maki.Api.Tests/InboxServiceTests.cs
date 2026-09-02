using Maki.Api.Hubs;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Inbox;
using Maki.Core.Notifications;
using Maki.Core.Security;
using Maki.Data;
using Maki.Data.Identity;
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

    [Fact]
    public async Task A_muted_series_is_not_written_for_the_user_who_muted_it()
    {
        var mutes = _db.SeedUser("mutes", MakiPermission.None);
        var wants = _db.SeedUser("wants", MakiPermission.None);
        var seriesId = _db.SeedSeries();
        SetSeriesMode(mutes, seriesId, SeriesNotificationMode.Muted);

        await RaiseNewChapter(seriesId, mutes, wants);

        using var db = _db.NewContext();
        var rows = db.UserNotifications.IgnoreQueryFilters().ToList();

        Assert.Contains(rows, r => r.UserId == wants);
        Assert.DoesNotContain(rows, r => r.UserId == mutes);
    }

    [Fact]
    public async Task Reading_delivers_to_a_reader_with_progress_and_nobody_else()
    {
        var reading = _db.SeedUser("reading", MakiPermission.None);
        var idle = _db.SeedUser("idle", MakiPermission.None);
        var seriesId = _db.SeedSeries();
        SetSeriesMode(reading, seriesId, SeriesNotificationMode.Reading);
        SetSeriesMode(idle, seriesId, SeriesNotificationMode.Reading);
        SeedProgress(reading, seriesId);

        await RaiseNewChapter(seriesId, reading, idle);

        using var db = _db.NewContext();
        var rows = db.UserNotifications.IgnoreQueryFilters().ToList();

        Assert.Contains(rows, r => r.UserId == reading);
        Assert.DoesNotContain(rows, r => r.UserId == idle);
    }

    [Fact]
    public async Task Reading_drops_a_series_the_user_marked_finished()
    {
        // The high-water mark says they reached the end and stopped. Someone merely caught up on an
        // ongoing series has no Finished row and still counts as reading it.
        var finished = _db.SeedUser("finished", MakiPermission.None);
        var caughtUp = _db.SeedUser("caughtup", MakiPermission.None);
        var seriesId = _db.SeedSeries();
        SetSeriesMode(finished, seriesId, SeriesNotificationMode.Reading);
        SetSeriesMode(caughtUp, seriesId, SeriesNotificationMode.Reading);
        SeedReadingState(finished, seriesId, finished: true);
        SeedReadingState(caughtUp, seriesId, finished: false);

        await RaiseNewChapter(seriesId, finished, caughtUp);

        using var db = _db.NewContext();
        var rows = db.UserNotifications.IgnoreQueryFilters().ToList();

        Assert.Contains(rows, r => r.UserId == caughtUp);
        Assert.DoesNotContain(rows, r => r.UserId == finished);
    }

    [Fact]
    public async Task A_global_default_of_Reading_applies_to_series_with_no_setting_of_their_own()
    {
        var reader = _db.SeedUser("reader", MakiPermission.None);
        var seriesId = _db.SeedSeries();
        SetSeriesDefault(reader, SeriesDefaults.Reading);

        await RaiseNewChapter(seriesId, reader);

        using (var db = _db.NewContext())
        {
            Assert.Empty(db.UserNotifications.IgnoreQueryFilters().ToList());
        }

        // Pinning the one series they do care about to All overrides that default.
        SetSeriesMode(reader, seriesId, SeriesNotificationMode.All);
        await RaiseNewChapter(seriesId, reader);

        using (var db = _db.NewContext())
        {
            Assert.Single(db.UserNotifications.IgnoreQueryFilters().ToList());
        }
    }

    [Fact]
    public async Task A_series_set_to_All_cannot_reinstate_a_type_switched_off_globally()
    {
        // The per-series mode only ever removes recipients. Otherwise "no new-chapter mail, ever"
        // would quietly stop meaning that the moment one series was pinned.
        var reader = _db.SeedUser("reader", MakiPermission.None);
        var seriesId = _db.SeedSeries();
        _db.SetUserConfig(reader, (SettingKeys.NotificationsInbox,
            InboxPrefsSpec.Serialize(new InboxPrefsSpec(
                new Dictionary<string, bool>
                {
                    [InboxEventTypes.Key(InboxEventType.NewChapterAvailable)] = false,
                }))));
        SetSeriesMode(reader, seriesId, SeriesNotificationMode.All);

        await RaiseNewChapter(seriesId, reader);

        using var db = _db.NewContext();
        Assert.Empty(db.UserNotifications.IgnoreQueryFilters().ToList());
    }

    [Fact]
    public async Task An_event_that_names_no_series_ignores_the_series_rules_entirely()
    {
        // Level-ups, backups, health issues: there is nothing to be muted against, so a global
        // default of Reading must not silence them.
        var reader = _db.SeedUser("reader", MakiPermission.None);
        SetSeriesDefault(reader, SeriesDefaults.Reading);

        await _inbox.RaiseAsync(InboxEventType.LevelUp,
            new InboxMessage("Level 4", "You reached level 4."), InboxAudience.User(reader));

        using var db = _db.NewContext();
        Assert.Single(db.UserNotifications.IgnoreQueryFilters().ToList());
    }

    [Fact]
    public async Task A_muted_series_still_reports_its_download_failures_to_an_admin()
    {
        // Muting is a reading preference. The person who has to go and fix the source is not
        // opting out of knowing it broke.
        var admin = _db.SeedUser("admin", MakiPermission.Admin);
        var reader = _db.SeedUser("reader", MakiPermission.None);
        var seriesId = _db.SeedSeries();
        SetSeriesMode(admin, seriesId, SeriesNotificationMode.Muted);
        SetSeriesMode(reader, seriesId, SeriesNotificationMode.Muted);

        await RaiseForSeries(InboxEventType.DownloadFailed, seriesId, admin, reader);

        using var db = _db.NewContext();
        var rows = db.UserNotifications.IgnoreQueryFilters().ToList();

        Assert.Contains(rows, r => r.UserId == admin);
        Assert.DoesNotContain(rows, r => r.UserId == reader);
    }

    [Fact]
    public async Task The_operational_carve_out_does_not_extend_to_new_chapters()
    {
        // Narrow on purpose: an admin who muted a series wanted to stop hearing about it. Only the
        // events that say the instance is broken survive that.
        var admin = _db.SeedUser("admin", MakiPermission.Admin);
        var seriesId = _db.SeedSeries();
        SetSeriesMode(admin, seriesId, SeriesNotificationMode.Muted);

        await RaiseNewChapter(seriesId, admin);

        using var db = _db.NewContext();
        Assert.Empty(db.UserNotifications.IgnoreQueryFilters().ToList());
    }

    [Fact]
    public async Task An_admin_who_switched_download_failures_off_globally_still_stays_off()
    {
        // The carve-out is against the *per-series* layer only. An explicit global "no" is still a
        // no — otherwise the switch on the settings card would silently do nothing for admins.
        var admin = _db.SeedUser("admin", MakiPermission.Admin);
        var seriesId = _db.SeedSeries();
        _db.SetUserConfig(admin, (SettingKeys.NotificationsInbox,
            InboxPrefsSpec.Serialize(new InboxPrefsSpec(
                new Dictionary<string, bool>
                {
                    [InboxEventTypes.Key(InboxEventType.DownloadFailed)] = false,
                }))));
        SetSeriesMode(admin, seriesId, SeriesNotificationMode.Muted);

        await RaiseForSeries(InboxEventType.DownloadFailed, seriesId, admin);

        using var db = _db.NewContext();
        Assert.Empty(db.UserNotifications.IgnoreQueryFilters().ToList());
    }

    [Fact]
    public async Task A_reader_partway_through_still_hears_their_own_download_failures()
    {
        // The carve-out is about admins, but a failure is a reader's business too: it was their
        // chapter that did not arrive. Reading mode must not turn into an admin-only rule.
        var reader = _db.SeedUser("reader", MakiPermission.None);
        var seriesId = _db.SeedSeries();
        SetSeriesMode(reader, seriesId, SeriesNotificationMode.Reading);
        SeedProgress(reader, seriesId);

        await RaiseForSeries(InboxEventType.DownloadFailed, seriesId, reader);

        using var db = _db.NewContext();
        Assert.Single(db.UserNotifications.IgnoreQueryFilters().ToList());
    }

    private Task RaiseNewChapter(int seriesId, params int[] recipients) =>
        RaiseForSeries(InboxEventType.NewChapterAvailable, seriesId, recipients);

    /// <summary>
    /// Addressed per user rather than through <c>SeriesTrackers</c> so these tests exercise the
    /// preference layer alone — who <em>could</em> see an event is InboxAudienceResolverTests'
    /// subject, and routing through it here would make progress rows do two jobs at once.
    /// </summary>
    private async Task RaiseForSeries(InboxEventType type, int seriesId, params int[] recipients)
    {
        foreach (var userId in recipients)
        {
            await _inbox.RaiseAsync(
                type,
                new InboxMessage("Chapter 12", "Something happened to it", SeriesId: seriesId),
                InboxAudience.User(userId));
        }
    }

    private void SetSeriesMode(int userId, int seriesId, SeriesNotificationMode mode)
    {
        using var db = _db.NewContext();
        var state = db.UserSeriesStates
            .IgnoreQueryFilters()
            .FirstOrDefault(s => s.UserId == userId && s.SeriesId == seriesId);

        if (state is null)
        {
            state = new UserSeriesState { UserId = userId, SeriesId = seriesId };
            db.UserSeriesStates.Add(state);
        }

        state.NotificationMode = mode;
        state.UpdatedAt = T0.UtcDateTime;
        db.SaveChanges();
    }

    private void SetSeriesDefault(int userId, string seriesDefault) =>
        _db.SetUserConfig(userId, (SettingKeys.NotificationsInbox,
            InboxPrefsSpec.Serialize(new InboxPrefsSpec(SeriesDefault: seriesDefault))));

    private void SeedProgress(int userId, int seriesId)
    {
        using var db = _db.NewContext();
        var chapter = new Chapter { SeriesId = seriesId, Number = 1m, Language = "en" };
        db.Chapters.Add(chapter);
        db.SaveChanges();

        db.ChapterProgress.Add(new ChapterProgress
        {
            UserId = userId,
            SeriesId = seriesId,
            ChapterId = chapter.Id,
            PageIndex = 3,
            PageCount = 20,
            StartedAt = T0.UtcDateTime,
            UpdatedAt = T0.UtcDateTime,
        });
        db.SaveChanges();
    }

    private void SeedReadingState(int userId, int seriesId, bool finished)
    {
        using var db = _db.NewContext();
        db.ReadingStates.Add(new ReadingState
        {
            UserId = userId,
            SeriesId = seriesId,
            Title = "Test Series",
            MaxChapter = 11,
            Finished = finished,
            LastProgressAt = T0.UtcDateTime,
            UpdatedAt = T0.UtcDateTime,
        });
        db.SaveChanges();
    }
}
