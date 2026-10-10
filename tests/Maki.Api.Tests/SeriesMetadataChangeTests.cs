using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Inbox;
using Maki.Core.Kavita;
using Maki.Core.Metadata;
using Maki.Core.Notifications;
using Maki.Core.Security;
using Maki.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// The change history records what really changed, from which side, and a status change tells the
/// people reading the series once the change is saved, never before and never for a locked field.
/// </summary>
public class SeriesMetadataChangeTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly RecordingInbox _inbox = new();
    private readonly RecordingNotifications _notifications = new();

    public void Dispose() => _db.Dispose();

    private static SeriesMetadata Provider(SeriesStatus status = SeriesStatus.Completed, int? chapters = 120) => new()
    {
        ProviderId = "42",
        Title = "Berserk",
        Status = status,
        TotalChapters = chapters,
        TotalVolumes = 41,
    };

    private int SeedOngoing(SeriesMetadataField locked = SeriesMetadataField.None) =>
        _db.SeedSeries("Berserk", configure: s =>
        {
            s.MangaBakaId = 42;
            s.Status = SeriesStatus.Ongoing;
            s.TotalChapters = 120;
            s.TotalVolumes = 41;
            s.LockedFields = locked;
        });

    private async Task<bool> RefreshAndSave(int id, SeriesMetadata metadata)
    {
        await using var db = _db.NewContext();
        var log = ChangeLog(db);
        var series = await db.Series.SingleAsync(s => s.Id == id);
        var refreshed = await new SeriesMetadataRefreshService([new StubProvider(metadata)], null!, log)
            .RefreshAsync(series, includeCover: false);
        await db.SaveChangesAsync();
        await log.PublishAsync();
        return refreshed;
    }

    [Fact]
    public async Task A_refresh_records_only_the_fields_that_changed()
    {
        var id = SeedOngoing();

        await RefreshAndSave(id, Provider());

        var change = Assert.Single(Changes(id));
        Assert.Equal(SeriesMetadataField.Status, change.Field);
        Assert.Equal("Ongoing", change.OldValue);
        Assert.Equal("Completed", change.NewValue);
        Assert.Equal(MetadataChangeSource.Refresh, change.Source);
        Assert.Null(change.UserId);
    }

    [Fact]
    public async Task A_second_identical_refresh_records_nothing()
    {
        var id = SeedOngoing();

        await RefreshAndSave(id, Provider());
        await RefreshAndSave(id, Provider());

        Assert.Single(Changes(id));
        Assert.Single(_inbox.Raised);
    }

    [Fact]
    public async Task Counts_are_recorded_as_invariant_text()
    {
        var id = SeedOngoing();

        await RefreshAndSave(id, Provider(SeriesStatus.Ongoing, chapters: 1250));

        var change = Assert.Single(Changes(id));
        Assert.Equal(SeriesMetadataField.TotalChapters, change.Field);
        Assert.Equal(("120", "1250"), (change.OldValue, change.NewValue));
    }

    [Fact]
    public async Task A_locked_status_changes_nothing_and_tells_nobody()
    {
        var id = SeedOngoing(SeriesMetadataField.Status);

        await RefreshAndSave(id, Provider());

        Assert.Empty(Changes(id));
        Assert.Empty(_inbox.Raised);
        Assert.Empty(_notifications.Sent);
    }

    [Fact]
    public async Task A_status_change_notifies_the_series_trackers_and_the_outbound_connections()
    {
        var id = SeedOngoing();

        await RefreshAndSave(id, Provider());

        var (type, message, audience) = Assert.Single(_inbox.Raised);
        Assert.Equal(InboxEventType.SeriesStatusChanged, type);
        Assert.Equal(InboxAudience.SeriesTrackers(id, RootFolderOf(id)), audience);
        Assert.Equal(id, message.SeriesId);
        Assert.Equal("ongoing", message.Params!["from"]);
        Assert.Equal("completed", message.Params!["to"]);
        Assert.False(message.Params.ContainsKey("series"));

        var (eventType, outbound) = Assert.Single(_notifications.Sent);
        Assert.Equal(NotificationEventType.SeriesStatusChanged, eventType);
        Assert.Equal(id, outbound.SeriesId);
    }

    [Fact]
    public async Task Nothing_is_announced_until_the_change_is_published()
    {
        var id = SeedOngoing();
        await using var db = _db.NewContext();
        var log = ChangeLog(db);
        var series = await db.Series.SingleAsync(s => s.Id == id);

        await new SeriesMetadataRefreshService([new StubProvider(Provider())], null!, log).RefreshAsync(series, false);

        Assert.Empty(_inbox.Raised);
        Assert.Empty(Changes(id));
        await db.SaveChangesAsync();
        await log.PublishAsync();
        Assert.Single(_inbox.Raised);
    }

    [Fact]
    public async Task A_status_found_for_the_first_time_is_recorded_but_not_announced()
    {
        var id = _db.SeedSeries("Berserk", configure: s => s.MangaBakaId = 42);

        await RefreshAndSave(id, Provider(SeriesStatus.Ongoing, chapters: null) with { TotalVolumes = null });

        Assert.Equal(SeriesMetadataField.Status, Assert.Single(Changes(id)).Field);
        Assert.Empty(_inbox.Raised);
    }

    [Fact]
    public async Task The_audience_skips_trackers_without_folder_access_and_people_not_tracking()
    {
        var id = SeedOngoing();
        var reader = _db.SeedUser("reader", MakiPermission.None);
        var revoked = _db.SeedUser("revoked", MakiPermission.None, allRootFolders: false);
        var stranger = _db.SeedUser("stranger", MakiPermission.None);
        SeedProgress(reader, id);
        SeedProgress(revoked, id);

        await RefreshAndSave(id, Provider());

        var audience = Assert.Single(_inbox.Raised).Audience;
        var recipients = await new InboxAudienceResolver(_db.ScopeFactory()).ResolveAsync(audience);
        Assert.Equal([reader], recipients);
        Assert.DoesNotContain(stranger, recipients);
    }

    [Fact]
    public async Task A_user_edit_is_recorded_with_who_made_it_and_announced()
    {
        var id = SeedOngoing();
        var editor = _db.SeedUser("editor", MakiPermission.EditMetadata, configure: u => u.DisplayName = "Ed");
        await using (var db = _db.NewContext())
        {
            await Controller(db, editor).Edit(id, new SeriesMetadataController.EditMetadataRequest(
                ["status", "overview", "title"], Title: "Berserk", Status: "Hiatus", Overview: "Mine"), CancellationToken.None);
        }

        var changes = Changes(id);
        Assert.Equal(
            [SeriesMetadataField.Overview, SeriesMetadataField.Status],
            changes.Select(c => c.Field).Order().ToArray());
        Assert.All(changes, c => Assert.Equal((MetadataChangeSource.User, editor), (c.Source, c.UserId)));
        Assert.Null(changes.Single(c => c.Field == SeriesMetadataField.Overview).NewValue);
        Assert.Equal(InboxEventType.SeriesStatusChanged, Assert.Single(_inbox.Raised).Type);

        await using var read = _db.NewContext();
        var ok = Assert.IsType<OkObjectResult>(await Controller(read, editor).History(id, CancellationToken.None));
        var history = Assert.IsAssignableFrom<IEnumerable<SeriesMetadataController.MetadataChangeDto>>(ok.Value).ToList();
        Assert.Equal(2, history.Count);
        Assert.All(history, h => Assert.Equal(("user", "Ed"), (h.Source, h.UserName)));
        Assert.Contains(history, h => h is { Field: "status", OldValue: "Ongoing", NewValue: "Hiatus" });
    }

    [Fact]
    public async Task History_is_newest_first()
    {
        var id = SeedOngoing();
        await using (var db = _db.NewContext())
        {
            db.SeriesMetadataChanges.AddRange(
                new SeriesMetadataChange { SeriesId = id, Field = SeriesMetadataField.Title, ChangedAtUtc = new DateTime(2026, 1, 1) },
                new SeriesMetadataChange { SeriesId = id, Field = SeriesMetadataField.Status, ChangedAtUtc = new DateTime(2026, 3, 1) },
                new SeriesMetadataChange { SeriesId = id, Field = SeriesMetadataField.Genres, ChangedAtUtc = new DateTime(2026, 2, 1) });
            await db.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var ok = Assert.IsType<OkObjectResult>(await Controller(read, 1).History(id, CancellationToken.None));
        var history = Assert.IsAssignableFrom<IEnumerable<SeriesMetadataController.MetadataChangeDto>>(ok.Value);

        Assert.Equal(["status", "genres", "title"], history.Select(h => h.Field));
    }

    [Fact]
    public async Task History_of_a_series_out_of_reach_is_not_found()
    {
        var id = SeedOngoing();
        var reader = _db.SeedUser("restricted", MakiPermission.None, allRootFolders: false);
        await using var db = _db.NewContext(reader, allRootFolders: false);

        Assert.IsType<NotFoundResult>(await Controller(db, reader).History(id, CancellationToken.None));
    }

    private SeriesMetadataChangeLog ChangeLog(MakiDbContext db) =>
        new(db, _inbox, _notifications, new TestUserLocaleResolver(), new TestLocalizer(), TimeProvider.System,
            NullLogger<SeriesMetadataChangeLog>.Instance);

    private SeriesMetadataController Controller(MakiDbContext db, int userId) =>
        new(new TestLocalizer(), db, new SeriesMetadataRefreshService([new StubProvider(Provider())], null!),
            ChangeLog(db), new SeriesIdentityService(db, NullLogger<SeriesIdentityService>.Instance),
            new TestCurrentUser(userId),
            new KavitaScanService(new KavitaClient(new StubHttpClientFactory("{}")), new FakeAppSettings(),
                _db.ScopeFactory(), NullLogger<KavitaScanService>.Instance));

    private List<SeriesMetadataChange> Changes(int seriesId)
    {
        using var db = _db.NewContext();
        return db.SeriesMetadataChanges.AsNoTracking().Where(c => c.SeriesId == seriesId).ToList();
    }

    private int RootFolderOf(int seriesId)
    {
        using var db = _db.NewContext();
        return db.Series.First(s => s.Id == seriesId).RootFolderId;
    }

    private void SeedProgress(int userId, int seriesId)
    {
        using var db = _db.NewContext();
        var chapter = new Chapter { SeriesId = seriesId, Number = db.Chapters.Count() + 1, Language = "en" };
        db.Chapters.Add(chapter);
        db.SaveChanges();
        db.ChapterProgress.Add(new ChapterProgress
        {
            UserId = userId,
            SeriesId = seriesId,
            ChapterId = chapter.Id,
            PageIndex = 3,
            PageCount = 20,
            StartedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    private sealed class StubProvider(SeriesMetadata metadata) : IMetadataProvider
    {
        public string Name => "stub";

        public Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(
            string query, string maxContentRating, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MetadataSearchResult>>([]);

        public Task<SeriesMetadata?> GetAsync(string providerId, CancellationToken ct = default) =>
            Task.FromResult<SeriesMetadata?>(metadata);
    }
}
