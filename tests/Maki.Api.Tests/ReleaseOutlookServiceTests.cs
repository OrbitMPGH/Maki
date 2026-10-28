using Maki.Api.Dtos;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Releases;
using Maki.Core.Security;
using Maki.Core.Sources;
using Maki.Data;
using Maki.Data.Identity;

namespace Maki.Api.Tests;

public class ReleaseOutlookServiceTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    private static readonly FakeSource Source = new() { Name = "fake" };

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private sealed class FixedTime(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);
    }

    private static ReleaseOutlookService Service(MakiDbContext db) =>
        new(db, new SourceRegistry([Source]), Sources.AllEnabled, new FakeAppSettings(), new FixedTime(Now));

    private int SeedSeries(
        string title, IEnumerable<DateTime> releases, DateTime? announced = null, Action<Series>? configure = null)
    {
        var mapping = new SourceMapping
        {
            SourceName = "fake", SourceSeriesId = title, Url = "u", AnnouncedNextReleaseAt = announced
        };
        var id = _db.SeedSeries(title, configure: configure, mappings: mapping);

        using var db = _db.NewContext();
        var mappingId = db.SourceMappings.Single(m => m.SeriesId == id).Id;
        var number = 1;
        foreach (var at in releases.Order())
        {
            var chapter = new Chapter { SeriesId = id, Number = number++, Language = "en" };
            db.Chapters.Add(chapter);
            db.SaveChanges();
            db.ChapterSourceLinks.Add(new ChapterSourceLink
            {
                ChapterId = chapter.Id, SourceMappingId = mappingId, ReleaseDate = at
            });
        }

        db.SaveChanges();
        return id;
    }

    private static IEnumerable<DateTime> Weekly(int count = 12) =>
        Enumerable.Range(0, count).Select(i => Now.AddDays(-2 - 7 * i));

    private static IEnumerable<DateTime> Wandering()
    {
        int[] gaps = [3, 19, 6, 30, 2, 14, 41, 5, 9, 25, 4, 17];
        var at = Now.AddDays(-1);
        yield return at;
        foreach (var gap in gaps)
        {
            at = at.AddDays(-gap);
            yield return at;
        }
    }

    private async Task<ReleaseOutlookDto> Outlook(int id)
    {
        using var db = _db.NewContext();
        return (await Service(db).ForSeriesAsync(id, CancellationToken.None))!;
    }

    private async Task<List<(Series Series, ReleaseOutlookDto Outlook)>> Upcoming()
    {
        using var db = _db.NewContext();
        return await Service(db).UpcomingAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_steady_series_gets_a_window_after_its_last_chapter()
    {
        var id = SeedSeries("Weekly", Weekly());

        var outlook = await Outlook(id);

        Assert.Equal(CadenceState.Expected, outlook.State);
        var nextDue = Now.AddDays(-2).AddDays(7);
        Assert.True(outlook.WindowStart < nextDue);
        Assert.True(outlook.WindowEnd > nextDue);
        Assert.Null(outlook.AnnouncedAt);
        Assert.True(outlook.Refreshing);
    }

    [Fact]
    public async Task A_series_with_too_little_history_is_unknown_rather_than_irregular()
    {
        var id = SeedSeries("Fresh", Weekly(5));

        var outlook = await Outlook(id);

        Assert.Equal(CadenceState.Unknown, outlook.State);
        Assert.Null(outlook.WindowStart);
    }

    [Fact]
    public async Task The_schedule_is_planned_from_now_when_the_series_was_never_checked()
    {
        var id = SeedSeries("Slowing", Weekly().Select(d => d.AddDays(-18)));

        var outlook = await Outlook(id);

        Assert.Null(outlook.LastCheckAt);
        Assert.Equal(RefreshTier.Slowing, outlook.RefreshTier);
    }

    [Fact]
    public async Task A_finished_series_that_is_caught_up_is_not_refreshing()
    {
        var done = SeedSeries("Done", Weekly(5), configure: s =>
        {
            s.Status = SeriesStatus.Completed;
            s.TotalChapters = 5;
        });
        var running = SeedSeries("Running", Weekly(5));

        Assert.False((await Outlook(done)).Refreshing);
        Assert.True((await Outlook(running)).Refreshing);
    }

    [Fact]
    public async Task An_announcement_inside_the_watch_window_shows_with_its_source()
    {
        var announced = Now.AddHours(-2);
        var id = SeedSeries("Announced", Weekly(5), announced);

        var outlook = await Outlook(id);

        Assert.Equal(announced, outlook.AnnouncedAt);
        Assert.Equal("fake", outlook.AnnouncedBy);
        Assert.Equal(RefreshTier.Announced, outlook.RefreshTier);
    }

    [Fact]
    public async Task An_announcement_older_than_the_watch_window_is_dropped()
    {
        var id = SeedSeries("Stale", Weekly(5), Now - RefreshSchedule.AnnouncedWatch - TimeSpan.FromMinutes(1));

        var outlook = await Outlook(id);

        Assert.Null(outlook.AnnouncedAt);
        Assert.Null(outlook.AnnouncedBy);
    }

    [Fact]
    public async Task An_announcement_whose_chapter_has_been_seen_is_history()
    {
        var announced = Now.AddHours(-2);
        var id = SeedSeries("Out", Weekly(5).Append(announced.AddMinutes(10)), announced);

        Assert.Null((await Outlook(id)).AnnouncedAt);
    }

    [Fact]
    public async Task Upcoming_lists_windows_and_announcements_and_leaves_the_rest_out()
    {
        var steady = SeedSeries("Steady", Weekly());
        var announced = SeedSeries("Announced", Weekly(5), Now.AddHours(30));
        SeedSeries("Wandering", Wandering());
        SeedSeries("Fresh", Weekly(5));
        SeedSeries("Stalled", Weekly().Select(d => d.AddDays(-60)));

        var upcoming = await Upcoming();

        Assert.Equal([announced, steady], upcoming.Select(x => x.Series.Id));
        Assert.Equal(Now.AddHours(30), upcoming[0].Outlook.AnnouncedAt);
    }

    [Fact]
    public async Task Upcoming_keeps_a_series_that_is_running_late()
    {
        var late = SeedSeries("Late", Weekly().Select(d => d.AddDays(-9)));

        var entry = Assert.Single(await Upcoming());

        Assert.Equal(late, entry.Series.Id);
        Assert.Equal(CadenceState.Late, entry.Outlook.State);
    }

    [Fact]
    public async Task A_user_without_access_to_the_folder_sees_no_outlook_and_no_upcoming()
    {
        var id = SeedSeries("Weekly", Weekly());
        var limited = _db.SeedUser("limited", MakiPermission.None, allRootFolders: false);

        using var db = _db.NewContext(limited, allRootFolders: false);
        var service = Service(db);

        Assert.Null(await service.ForSeriesAsync(id, CancellationToken.None));
        Assert.Empty(await service.UpcomingAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_user_with_the_folder_sees_the_series()
    {
        var id = SeedSeries("Weekly", Weekly());
        var user = _db.SeedUser("limited", MakiPermission.None, allRootFolders: false);
        using (var setup = _db.NewContext())
        {
            var folder = setup.Series.Where(s => s.Id == id).Select(s => s.RootFolderId).Single();
            setup.UserRootFolders.Add(new UserRootFolder { UserId = user, RootFolderId = folder });
            setup.SaveChanges();
        }

        using var db = _db.NewContext(user, allRootFolders: false);
        var service = Service(db);

        Assert.NotNull(await service.ForSeriesAsync(id, CancellationToken.None));
        Assert.Single(await service.UpcomingAsync(CancellationToken.None));
    }
}
