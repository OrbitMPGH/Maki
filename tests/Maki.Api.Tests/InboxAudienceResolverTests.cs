using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Inbox;
using Maki.Core.Security;
using Maki.Data;
using Maki.Data.Identity;

namespace Maki.Api.Tests;

/// <summary>
/// Who a notification reaches. This is the security-shaped half of the feature: a wrong answer here
/// tells one reader what another reader is downloading, or tells nobody at all and makes the whole
/// thing look broken.
/// </summary>
public class InboxAudienceResolverTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly InboxAudienceResolver _resolver;

    public InboxAudienceResolverTests() => _resolver = new InboxAudienceResolver(_db.ScopeFactory());

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_named_user_resolves_to_themselves()
    {
        var reader = _db.SeedUser("reader", MakiPermission.None);

        var ids = await _resolver.ResolveAsync(InboxAudience.User(reader));

        Assert.Equal([reader], ids);
    }

    [Fact]
    public async Task A_disabled_account_receives_nothing()
    {
        var reader = _db.SeedUser("gone", MakiPermission.None, configure: u => u.Disabled = true);

        Assert.Empty(await _resolver.ResolveAsync(InboxAudience.User(reader)));
    }

    [Fact]
    public async Task An_unclaimed_placeholder_account_receives_nothing()
    {
        var pending = _db.SeedUser("placeholder", MakiPermission.Admin, configure: u => u.PendingSetup = true);

        Assert.Empty(await _resolver.ResolveAsync(InboxAudience.User(pending)));
        Assert.DoesNotContain(pending, await _resolver.ResolveAsync(InboxAudience.Admins));
    }

    [Fact]
    public async Task Admins_resolves_to_the_admins_and_nobody_else()
    {
        var admin = _db.SeedUser("admin", MakiPermission.Admin);
        var reader = _db.SeedUser("reader", MakiPermission.DownloadChapters);

        var ids = await _resolver.ResolveAsync(InboxAudience.Admins);

        Assert.Contains(admin, ids);
        Assert.DoesNotContain(reader, ids);
    }

    [Fact]
    public async Task Series_trackers_are_the_people_with_progress_a_reading_state_or_a_request()
    {
        var seriesId = _db.SeedSeries();
        var rootFolderId = RootFolderOf(seriesId);

        var byProgress = _db.SeedUser("progress", MakiPermission.None);
        var byState = _db.SeedUser("state", MakiPermission.None);
        var byRequest = _db.SeedUser("requester", MakiPermission.None);
        var stranger = _db.SeedUser("stranger", MakiPermission.None);

        SeedProgress(byProgress, seriesId);
        SeedReadingState(byState, seriesId);
        SeedRequest(byRequest, seriesId);

        var ids = await _resolver.ResolveAsync(InboxAudience.SeriesTrackers(seriesId, rootFolderId));

        Assert.Contains(byProgress, ids);
        Assert.Contains(byState, ids);
        Assert.Contains(byRequest, ids);
        Assert.DoesNotContain(stranger, ids);
    }

    [Fact]
    public async Task A_tracker_who_cannot_see_the_root_folder_is_dropped()
    {
        var seriesId = _db.SeedSeries();
        var rootFolderId = RootFolderOf(seriesId);

        // Read a chapter, then lost the grant. The reading rows survive; the notifications must not.
        var revoked = _db.SeedUser("revoked", MakiPermission.None, allRootFolders: false);
        SeedProgress(revoked, seriesId);

        var granted = _db.SeedUser("granted", MakiPermission.None, allRootFolders: false);
        SeedProgress(granted, seriesId);
        SeedGrant(granted, rootFolderId);

        var ids = await _resolver.ResolveAsync(InboxAudience.SeriesTrackers(seriesId, rootFolderId));

        Assert.Contains(granted, ids);
        Assert.DoesNotContain(revoked, ids);
    }

    [Fact]
    public async Task Nobody_tracking_it_falls_back_to_the_admins_who_can_see_it()
    {
        // A series added an hour ago that nobody has opened. "Your download finished" is exactly the
        // message that matters here, so it must not be silently dropped.
        var seriesId = _db.SeedSeries();
        var rootFolderId = RootFolderOf(seriesId);

        var admin = _db.SeedUser("admin", MakiPermission.Admin);
        var blindAdmin = _db.SeedUser("blind-admin", MakiPermission.Admin, allRootFolders: false);
        var reader = _db.SeedUser("reader", MakiPermission.DownloadChapters);

        var ids = await _resolver.ResolveAsync(InboxAudience.SeriesTrackers(seriesId, rootFolderId));

        Assert.Contains(admin, ids);
        Assert.DoesNotContain(blindAdmin, ids);
        Assert.DoesNotContain(reader, ids);
    }

    [Fact]
    public async Task A_tracked_series_does_not_also_notify_every_admin()
    {
        var seriesId = _db.SeedSeries();
        var rootFolderId = RootFolderOf(seriesId);

        var admin = _db.SeedUser("admin", MakiPermission.Admin);
        var reader = _db.SeedUser("reader", MakiPermission.None);
        SeedProgress(reader, seriesId);

        var ids = await _resolver.ResolveAsync(InboxAudience.SeriesTrackers(seriesId, rootFolderId));

        Assert.Equal([reader], ids);
        Assert.DoesNotContain(admin, ids);
    }

    [Fact]
    public async Task A_user_who_tracks_a_series_three_ways_is_named_once()
    {
        var seriesId = _db.SeedSeries();
        var rootFolderId = RootFolderOf(seriesId);
        var reader = _db.SeedUser("reader", MakiPermission.None);

        SeedProgress(reader, seriesId);
        SeedReadingState(reader, seriesId);
        SeedRequest(reader, seriesId);

        var ids = await _resolver.ResolveAsync(InboxAudience.SeriesTrackers(seriesId, rootFolderId));

        Assert.Equal([reader], ids);
    }

    private int RootFolderOf(int seriesId)
    {
        using var db = _db.NewContext();
        return db.Series.First(s => s.Id == seriesId).RootFolderId;
    }

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
            StartedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    private void SeedReadingState(int userId, int seriesId)
    {
        using var db = _db.NewContext();
        db.ReadingStates.Add(new ReadingState
        {
            UserId = userId,
            SeriesId = seriesId,
            Title = "Test Series",
            MaxChapter = 4,
            LastProgressAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    private void SeedRequest(int userId, int seriesId)
    {
        using var db = _db.NewContext();
        db.SeriesRequests.Add(new SeriesRequest
        {
            UserId = userId,
            SeriesId = seriesId,
            Kind = SeriesRequestKind.Chapters,
            Status = SeriesRequestStatus.Approved,
            Title = "Test Series",
            Created = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    private void SeedGrant(int userId, int rootFolderId)
    {
        using var db = _db.NewContext();
        db.UserRootFolders.Add(new UserRootFolder { UserId = userId, RootFolderId = rootFolderId });
        db.SaveChanges();
    }
}
