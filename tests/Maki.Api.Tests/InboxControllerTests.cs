using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Inbox;
using Maki.Core.Notifications;
using Maki.Core.Security;
using Maki.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Tests;

/// <summary>
/// The inbox endpoints. The scoping tests are the point: this controller has no explicit
/// <c>UserId ==</c> anywhere and leans entirely on the global query filter, so if that filter ever
/// stopped applying these are what notice.
/// </summary>
public class InboxControllerTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly TestDb _db = new();
    private readonly int _alice;
    private readonly int _bob;

    public InboxControllerTests()
    {
        _alice = _db.SeedUser("alice", MakiPermission.Admin);
        _bob = _db.SeedUser("bob", MakiPermission.None);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task The_feed_shows_newest_first_and_only_the_callers_own_rows()
    {
        Seed(_alice, "Alice one");
        Seed(_bob, "Bob one");
        Seed(_alice, "Alice two");

        var page = Ok<InboxPageDto>(await Controller(_alice).List());

        Assert.Equal(["Alice two", "Alice one"], page.Items.Select(i => i.Title));
        Assert.Equal(2, page.Unread);
    }

    [Fact]
    public async Task Unread_count_is_per_user()
    {
        Seed(_alice, "Alice one");
        Seed(_alice, "Alice two", read: true);
        Seed(_bob, "Bob one");

        Assert.Equal(1, Unread(Ok<object>(await Controller(_alice).UnreadCount(default))));
        Assert.Equal(1, Unread(Ok<object>(await Controller(_bob).UnreadCount(default))));
    }

    [Fact]
    public async Task One_user_cannot_mark_anothers_notification_read()
    {
        var bobsRow = Seed(_bob, "Bob one");

        Assert.IsType<NotFoundResult>(await Controller(_alice).MarkRead(bobsRow, default));

        using var db = _db.NewContext();
        Assert.Null(db.UserNotifications.IgnoreQueryFilters().First(n => n.Id == bobsRow).ReadAt);
    }

    [Fact]
    public async Task One_user_cannot_dismiss_anothers_notification()
    {
        var bobsRow = Seed(_bob, "Bob one");

        Assert.IsType<NotFoundResult>(await Controller(_alice).Dismiss(bobsRow, default));

        using var db = _db.NewContext();
        Assert.True(db.UserNotifications.IgnoreQueryFilters().Any(n => n.Id == bobsRow));
    }

    [Fact]
    public async Task Marking_read_is_idempotent()
    {
        var row = Seed(_alice, "Alice one");

        Assert.IsType<NoContentResult>(await Controller(_alice).MarkRead(row, default));
        Assert.IsType<NoContentResult>(await Controller(_alice).MarkRead(row, default));

        using var db = _db.NewContext();
        Assert.NotNull(db.UserNotifications.IgnoreQueryFilters().First(n => n.Id == row).ReadAt);
    }

    [Fact]
    public async Task Mark_all_read_stops_at_the_callers_own_rows()
    {
        Seed(_alice, "Alice one");
        Seed(_alice, "Alice two");
        Seed(_bob, "Bob one");

        await Controller(_alice).MarkAllRead(default);

        using var db = _db.NewContext();
        Assert.Empty(db.UserNotifications.IgnoreQueryFilters().Where(n => n.UserId == _alice && n.ReadAt == null));
        Assert.Single(db.UserNotifications.IgnoreQueryFilters().Where(n => n.UserId == _bob && n.ReadAt == null));
    }

    [Fact]
    public async Task Clear_empties_the_callers_inbox_and_leaves_everyone_elses_alone()
    {
        Seed(_alice, "Alice one");
        Seed(_alice, "Alice two", read: true);
        Seed(_bob, "Bob one");

        await Controller(_alice).Clear(default);

        using var db = _db.NewContext();
        Assert.Empty(db.UserNotifications.IgnoreQueryFilters().Where(n => n.UserId == _alice));
        Assert.Single(db.UserNotifications.IgnoreQueryFilters().Where(n => n.UserId == _bob));
    }

    [Fact]
    public async Task Paging_walks_backwards_by_id_without_repeating_a_row()
    {
        for (var i = 0; i < 5; i++)
        {
            Seed(_alice, $"Row {i}");
        }

        var first = Ok<InboxPageDto>(await Controller(_alice).List(take: 2));
        Assert.Equal(["Row 4", "Row 3"], first.Items.Select(i => i.Title));
        Assert.NotNull(first.NextCursor);

        var second = Ok<InboxPageDto>(await Controller(_alice).List(before: first.NextCursor, take: 2));
        Assert.Equal(["Row 2", "Row 1"], second.Items.Select(i => i.Title));

        var last = Ok<InboxPageDto>(await Controller(_alice).List(before: second.NextCursor, take: 2));
        Assert.Equal(["Row 0"], last.Items.Select(i => i.Title));
        Assert.Null(last.NextCursor);
    }

    [Fact]
    public async Task Unread_only_and_type_narrow_the_feed()
    {
        Seed(_alice, "Read one", read: true);
        Seed(_alice, "Level up", type: InboxEventType.LevelUp);
        Seed(_alice, "Downloaded", type: InboxEventType.ChapterDownloaded);

        var unread = Ok<InboxPageDto>(await Controller(_alice).List(unreadOnly: true));
        Assert.Equal(["Downloaded", "Level up"], unread.Items.Select(i => i.Title));

        var typed = Ok<InboxPageDto>(await Controller(_alice).List(type: "levelUp"));
        Assert.Equal(["Level up"], typed.Items.Select(i => i.Title));
    }

    [Fact]
    public async Task An_unknown_type_filter_shows_the_feed_rather_than_nothing()
    {
        Seed(_alice, "Level up", type: InboxEventType.LevelUp);

        var page = Ok<InboxPageDto>(await Controller(_alice).List(type: "somethingRetired"));

        Assert.Single(page.Items);
    }

    [Fact]
    public async Task A_notification_about_a_series_carries_its_cover()
    {
        var seriesId = _db.SeedSeries(configure: s => s.CoverPath = "cover.jpg");
        Seed(_alice, "Chapters downloaded", seriesId: seriesId);

        var page = Ok<InboxPageDto>(await Controller(_alice).List());

        var item = Assert.Single(page.Items);
        Assert.NotNull(item.CoverUrl);
        Assert.StartsWith($"/api/v1/mediacover/{seriesId}/cover.jpg", item.CoverUrl);
    }

    [Fact]
    public async Task A_series_with_no_poster_yields_no_cover()
    {
        var seriesId = _db.SeedSeries();
        Seed(_alice, "Chapters downloaded", seriesId: seriesId);

        var page = Ok<InboxPageDto>(await Controller(_alice).List());

        Assert.Null(Assert.Single(page.Items).CoverUrl);
    }

    [Fact]
    public async Task A_notification_naming_a_series_the_caller_cannot_see_yields_no_cover()
    {
        // A notification outlives the grant that produced it. The row still renders — it is the
        // reader's own history — but the poster must not come with it.
        var seriesId = _db.SeedSeries(configure: s => s.CoverPath = "cover.jpg");
        Seed(_bob, "Chapters downloaded", seriesId: seriesId);

        var db = _db.NewContext(_bob, allRootFolders: false);
        var user = new TestCurrentUser(_bob, "bob", MakiPermission.None);
        var controller = new InboxController(db, new UserSettingsService(db, user), user, new StoppedClock(T0));

        var page = Ok<InboxPageDto>(await controller.List());

        var item = Assert.Single(page.Items);
        Assert.Equal(seriesId, item.SeriesId);
        Assert.Null(item.CoverUrl);
    }

    [Fact]
    public async Task A_notification_whose_series_was_deleted_still_renders()
    {
        // SeriesId is deliberately not a foreign key: removing a series breaks the link, it does not
        // erase the record that its chapters once downloaded.
        Seed(_alice, "Chapters downloaded", seriesId: 9999);

        var page = Ok<InboxPageDto>(await Controller(_alice).List());

        var item = Assert.Single(page.Items);
        Assert.Equal(9999, item.SeriesId);
        Assert.Null(item.CoverUrl);
    }

    [Fact]
    public async Task Prefs_round_trip_merged()
    {
        var controller = Controller(_alice);

        var saved = Ok<InboxPrefsSpec>(await controller.SavePrefs(
            new InboxPrefsSpec(
                new Dictionary<string, bool> { [InboxEventTypes.Key(InboxEventType.LevelUp)] = false },
                Toasts: false),
            default));

        Assert.False(saved.Wants(InboxEventType.LevelUp));
        Assert.False(saved.Toasts);
        Assert.Equal(InboxEventTypes.All.Length, saved.Types!.Count);

        var reloaded = Ok<InboxPrefsSpec>(await controller.GetPrefs(default));
        Assert.False(reloaded.Wants(InboxEventType.LevelUp));
        Assert.False(reloaded.Toasts);
    }

    [Fact]
    public async Task A_non_admin_cannot_store_a_preference_for_an_admin_only_event()
    {
        // UpdateAvailable defaults on, so an accepted "false" would show through the merged read.
        var saved = Ok<InboxPrefsSpec>(await Controller(_bob).SavePrefs(
            new InboxPrefsSpec(
                new Dictionary<string, bool>
                {
                    [InboxEventTypes.Key(InboxEventType.UpdateAvailable)] = false,
                    [InboxEventTypes.Key(InboxEventType.LevelUp)] = false,
                }),
            default));

        // Reads back at the build default, not the value they sent: the entry was dropped before it
        // was stored, and the merge then filled the gap the way it fills any absent key.
        Assert.True(saved.Wants(InboxEventType.UpdateAvailable));

        // The rest of the save still lands — the drop is silent, not a rejection of the whole spec.
        Assert.False(saved.Wants(InboxEventType.LevelUp));
    }

    [Fact]
    public async Task An_admin_may_store_a_preference_for_an_admin_only_event()
    {
        var saved = Ok<InboxPrefsSpec>(await Controller(_alice).SavePrefs(
            new InboxPrefsSpec(
                new Dictionary<string, bool>
                {
                    [InboxEventTypes.Key(InboxEventType.UpdateAvailable)] = false,
                }),
            default));

        Assert.False(saved.Wants(InboxEventType.UpdateAvailable));
    }

    private InboxController Controller(int userId)
    {
        var db = _db.NewContext(userId);
        var user = new TestCurrentUser(
            userId,
            userId == _alice ? "alice" : "bob",
            userId == _alice ? MakiPermission.Admin : MakiPermission.None);

        return new InboxController(db, new UserSettingsService(db, user), user, new StoppedClock(T0));
    }

    private int Seed(
        int userId,
        string title,
        bool read = false,
        InboxEventType type = InboxEventType.ChapterDownloaded,
        int? seriesId = null)
    {
        using var db = _db.NewContext();
        var row = new UserNotification
        {
            UserId = userId,
            Type = type,
            Level = NotificationLevel.Info,
            Title = title,
            Body = title,
            SeriesId = seriesId,
            CreatedAt = T0.UtcDateTime,
            ReadAt = read ? T0.UtcDateTime : null,
        };
        db.UserNotifications.Add(row);
        db.SaveChanges();
        return row.Id;
    }

    private static T Ok<T>(IActionResult result) => (T)Assert.IsType<OkObjectResult>(result).Value!;

    /// <summary>Digs the count out of the anonymous `{ count }` the endpoint returns.</summary>
    private static int Unread(object payload) =>
        (int)payload.GetType().GetProperty("count")!.GetValue(payload)!;
}
