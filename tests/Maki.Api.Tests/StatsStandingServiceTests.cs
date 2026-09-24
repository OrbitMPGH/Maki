using Maki.Api.Dtos;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Security;
using Maki.Data.Identity;
using Maki.Metadata.MangaBaka;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>Where a reader stands overall: backlog, series in progress, creators, ratings.</summary>
public sealed class StatsStandingServiceTests : IDisposable
{
    private const int Owner = 1;
    private static readonly DateTime Now = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private sealed class NoSettings : IUserSettingsStore
    {
        public Task<string?> GetAsync(int userId, string key, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task SetAsync(int userId, string key, string? value, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private StatsStandingService Service(int caller = Owner)
    {
        var settings = new NoSettings();
        var clock = new StoppedClock(new DateTimeOffset(Now));
        var cache = new MemoryCache(new MemoryCacheOptions());
        var db = _db.NewContext(caller);
        return new StatsStandingService(
            db,
            new ReadingBehaviourService(_db.ScopeFactory(), settings, NullLogger<ReadingBehaviourService>.Instance),
            new UserMetricsService(_db.NewContext(), settings, cache, clock),
            new MangaBakaLocalStore(new MangaBakaDumpOptions("", ""), new FakeAppSettings(),
                NullLogger<MangaBakaLocalStore>.Instance),
            new TestCurrentUser(caller),
            cache,
            clock);
    }

    private Task<StatsStandingDto> StandingAsync(int userId = Owner, int caller = Owner) =>
        Service(caller).GetAsync(userId, CancellationToken.None);

    private int SeedSeries(string title, Action<Series>? configure = null) =>
        _db.SeedSeries(title, configure: configure);

    /// <summary>
    /// <paramref name="downloaded"/> chapters on disk, the first <paramref name="read"/> completed, of
    /// which the first <paramref name="watched"/> are only ticked off.
    /// </summary>
    private void Seed(int seriesId, int downloaded, int read, int watched = 0, DateTime? at = null,
        int readSeconds = 300, int userId = Owner)
    {
        using var db = _db.NewContext();
        for (var i = 1; i <= downloaded; i++)
        {
            var file = new ChapterFile { SeriesId = seriesId, RelativePath = $"{seriesId}-{i}.cbz", DateAdded = Now };
            db.ChapterFiles.Add(file);
            db.SaveChanges();

            var chapter = new Chapter { SeriesId = seriesId, Number = i, ChapterFileId = file.Id };
            db.Chapters.Add(chapter);
            db.SaveChanges();

            if (i > read)
            {
                continue;
            }

            var isWatched = i <= watched;
            db.ChapterProgress.Add(new ChapterProgress
            {
                UserId = userId,
                SeriesId = seriesId,
                ChapterId = chapter.Id,
                PageCount = isWatched ? 0 : 20,
                Completed = true,
                Watched = isWatched,
                ReadSeconds = isWatched ? 0 : readSeconds,
                StartedAt = at ?? Now,
                UpdatedAt = at ?? Now
            });
            db.SaveChanges();
        }
    }

    // ---- backlog ----

    [Fact]
    public async Task Watched_chapters_count_as_read_in_the_backlog()
    {
        Seed(SeedSeries("Anime first"), downloaded: 10, read: 4, watched: 2);

        var backlog = (await StandingAsync()).Backlog;

        Assert.Equal(6, backlog.UnreadChapters);
        Assert.Equal(1, backlog.SeriesWithUnread);
        var top = Assert.Single(backlog.Top);
        Assert.Equal(4, top.Read);
        Assert.Equal(6, top.Unread);
        Assert.Null(backlog.HoursAtPace); // four timed chapters is no pace yet
    }

    [Fact]
    public async Task Hours_at_pace_use_the_median_chapter()
    {
        Seed(SeedSeries("Long"), downloaded: 24, read: 12, readSeconds: 300);
        Seed(SeedSeries("Unopened"), downloaded: 6, read: 0);

        var standing = await StandingAsync();

        // The unopened series' six chapters are part of what is left.
        Assert.Equal(18 * 300 / 3600.0, standing.Backlog.HoursAtPace!.Value, 5);
        Assert.Equal(12 * 300, Assert.Single(standing.Midway).EtaSeconds);
    }

    [Fact]
    public async Task Fully_incognito_series_stay_out_of_the_backlog()
    {
        Seed(SeedSeries("Secret", s => s.Incognito = IncognitoMode.Full), downloaded: 10, read: 2);
        Seed(SeedSeries("Secret unopened", s => s.Incognito = IncognitoMode.Full), downloaded: 10, read: 0);

        var standing = await StandingAsync();

        Assert.Equal(0, standing.Backlog.UnreadChapters);
        Assert.Equal(0, standing.Backlog.SeriesWithUnread);
        Assert.Empty(standing.Midway);
    }

    [Fact]
    public async Task Unstarted_series_count_toward_totals_but_not_the_top_list()
    {
        Seed(SeedSeries("Untouched"), downloaded: 10, read: 0);
        Seed(SeedSeries("Done"), downloaded: 5, read: 5);
        Seed(SeedSeries("Started"), downloaded: 6, read: 2);

        var backlog = (await StandingAsync()).Backlog;

        Assert.Equal(14, backlog.UnreadChapters);
        Assert.Equal(2, backlog.SeriesWithUnread);
        Assert.Equal("Started", Assert.Single(backlog.Top).Title);
    }

    // ---- midway ----

    [Fact]
    public async Task Midway_stops_at_sixty_days_without_reading()
    {
        Seed(SeedSeries("Recent"), downloaded: 10, read: 3, at: Now.AddDays(-59));
        Seed(SeedSeries("Stale"), downloaded: 10, read: 3, at: Now.AddDays(-61));

        var standing = await StandingAsync();

        var midway = Assert.Single(standing.Midway);
        Assert.Equal("Recent", midway.Title);
        Assert.Equal(3, midway.Read);
        Assert.Equal(10, midway.Held);
        Assert.Equal(2, standing.Backlog.SeriesWithUnread); // the stale one is still backlog
    }

    [Fact]
    public async Task Midway_needs_a_real_read_and_ignores_watched_and_imported_touches()
    {
        // Only ticked off, recently: nothing was read.
        Seed(SeedSeries("Watched only"), downloaded: 10, read: 3, watched: 3, at: Now.AddDays(-1));

        // Read long ago, then a watched tick and an import yesterday: not being read now.
        var stale = SeedSeries("Stale read");
        Seed(stale, downloaded: 10, read: 2, at: Now.AddDays(-90));
        using (var db = _db.NewContext())
        {
            var chapters = db.Chapters.Where(c => c.SeriesId == stale).OrderBy(c => c.Number).Skip(2).Take(2).ToList();
            db.ChapterProgress.Add(new ChapterProgress
            {
                UserId = Owner, SeriesId = stale, ChapterId = chapters[0].Id, Completed = true, Watched = true,
                StartedAt = Now.AddDays(-1), UpdatedAt = Now.AddDays(-1)
            });
            db.ChapterProgress.Add(new ChapterProgress
            {
                UserId = Owner, SeriesId = stale, ChapterId = chapters[1].Id, Completed = true, PageCount = 0,
                StartedAt = Now.AddDays(-1), UpdatedAt = Now.AddDays(-1)
            });
            db.SaveChanges();
        }

        var standing = await StandingAsync();

        Assert.Empty(standing.Midway);
    }

    // ---- creators ----

    [Fact]
    public async Task Creators_split_credits_and_need_two_series()
    {
        Seed(SeedSeries("One", s => { s.AuthorStory = "Ana, Bo"; s.AuthorArt = "Ana"; }), downloaded: 3, read: 2);
        Seed(SeedSeries("Two", s => s.AuthorStory = " Ana "), downloaded: 3, read: 1);
        Seed(SeedSeries("Three", s => s.AuthorArt = "bo"), downloaded: 3, read: 3);
        Seed(SeedSeries("Solo", s => s.AuthorStory = "Cy"), downloaded: 3, read: 3);
        // Watched only: not a creator somebody reads.
        Seed(SeedSeries("Watched", s => s.AuthorStory = "Cy"), downloaded: 3, read: 3, watched: 3);

        var creators = (await StandingAsync()).Creators;

        Assert.Equal(["Bo", "Ana"], creators.Select(c => c.Name));
        var ana = creators[1];
        Assert.True(ana.Story);
        Assert.True(ana.Art);
        Assert.Equal(2, ana.SeriesRead);
        Assert.Equal(3, ana.ChaptersRead);
        var bo = creators[0];
        Assert.True(bo.Story);
        Assert.True(bo.Art);
        Assert.Equal(5, bo.ChaptersRead);
    }

    // ---- ratings ----

    [Fact]
    public async Task Ratings_are_null_without_the_dump()
    {
        for (var i = 1; i <= 4; i++)
        {
            var id = SeedSeries($"Rated {i}", s => s.MangaBakaId = 100 + i);
            using var db = _db.NewContext();
            db.UserSeriesStates.Add(new UserSeriesState { UserId = Owner, SeriesId = id, Rating = 7 });
            db.SaveChanges();
        }

        Assert.Null((await StandingAsync()).Ratings);
    }

    [Fact]
    public void Rating_gap_needs_three_pairs_and_ranks_by_distance()
    {
        RatedSeriesDto Pair(int id, int yours, double community) => new(id, $"S{id}", null, yours, community);

        Assert.Null(StatsStandingService.RatingGap([Pair(1, 8, 7), Pair(2, 5, 7)]));

        var gap = StatsStandingService.RatingGap([Pair(1, 8, 7), Pair(2, 4, 7.5), Pair(3, 9, 9)])!;

        Assert.Equal(3, gap.Rated);
        Assert.Equal((1 - 3.5 + 0) / 3, gap.MeanGap, 5);
        Assert.Equal([2, 1, 3], gap.Series.Select(s => s.SeriesId));
    }

    // ---- scoping ----

    [Fact]
    public async Task Admin_sees_the_named_readers_standing()
    {
        var other = _db.SeedUser("other", MakiPermission.None);
        Seed(SeedSeries("Theirs"), downloaded: 10, read: 4, userId: other);
        Seed(SeedSeries("Mine"), downloaded: 10, read: 1);

        var standing = await StandingAsync(userId: other, caller: Owner);

        var top = Assert.Single(standing.Backlog.Top);
        Assert.Equal("Theirs", top.Title);
        Assert.Equal(4, standing.Behaviour.ChaptersRead);
    }

    [Fact]
    public async Task Series_outside_the_callers_root_folders_are_left_out()
    {
        var reader = _db.SeedUser("restricted", MakiPermission.None, allRootFolders: false);
        var hidden = SeedSeries("Hidden");
        var granted = SeedSeries("Granted");
        using (var db = _db.NewContext())
        {
            db.UserRootFolders.Add(new UserRootFolder
            {
                UserId = reader, RootFolderId = db.Series.Single(s => s.Id == granted).RootFolderId
            });
            db.SaveChanges();
        }

        Seed(hidden, downloaded: 10, read: 2, userId: reader);
        Seed(granted, downloaded: 10, read: 2, userId: reader);

        var settings = new NoSettings();
        var clock = new StoppedClock(new DateTimeOffset(Now));
        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new StatsStandingService(
            _db.NewContext(reader, allRootFolders: false),
            new ReadingBehaviourService(_db.ScopeFactory(), settings, NullLogger<ReadingBehaviourService>.Instance),
            new UserMetricsService(_db.NewContext(), settings, cache, clock),
            new MangaBakaLocalStore(new MangaBakaDumpOptions("", ""), new FakeAppSettings(),
                NullLogger<MangaBakaLocalStore>.Instance),
            new RestrictedUser(reader),
            cache,
            clock);

        var standing = await service.GetAsync(reader, CancellationToken.None);

        Assert.Equal("Granted", Assert.Single(standing.Backlog.Top).Title);
        Assert.Equal(8, standing.Backlog.UnreadChapters);
        Assert.Equal(2, standing.Behaviour.ChaptersRead);
    }

    [Fact]
    public async Task Behaviour_lists_are_trimmed_to_the_callers_folders_when_viewing_someone_else()
    {
        var admin = _db.SeedUser("folder-admin", MakiPermission.Admin, allRootFolders: false);
        var target = _db.SeedUser("target", MakiPermission.None);
        var hidden = SeedSeries("Hidden");
        var granted = SeedSeries("Granted");
        using (var db = _db.NewContext())
        {
            db.UserRootFolders.Add(new UserRootFolder
            {
                UserId = admin, RootFolderId = db.Series.Single(s => s.Id == granted).RootFolderId
            });
            db.SaveChanges();
        }

        Seed(hidden, downloaded: 10, read: 4, userId: target);
        Seed(granted, downloaded: 10, read: 4, userId: target);

        var settings = new NoSettings();
        var clock = new StoppedClock(new DateTimeOffset(Now));
        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new StatsStandingService(
            _db.NewContext(admin, allRootFolders: false),
            new ReadingBehaviourService(_db.ScopeFactory(), settings, NullLogger<ReadingBehaviourService>.Instance),
            new UserMetricsService(_db.NewContext(), settings, cache, clock),
            new MangaBakaLocalStore(new MangaBakaDumpOptions("", ""), new FakeAppSettings(),
                NullLogger<MangaBakaLocalStore>.Instance),
            new RestrictedUser(admin),
            cache,
            clock);

        var behaviour = (await service.GetAsync(target, CancellationToken.None)).Behaviour;

        // The aggregates are the target's own; the named series are only the caller's.
        Assert.Equal(8, behaviour.ChaptersRead);
        Assert.Equal("Granted", Assert.Single(behaviour.Abandoned).Title);
        Assert.Equal("Granted", Assert.Single(behaviour.Savoured).Title);
        Assert.Equal("Granted", Assert.Single(behaviour.Devoured).Title);
    }

    private sealed class RestrictedUser(int userId) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public int UserId => userId;
        public string UserName => "restricted";
        public MakiPermission Permissions => MakiPermission.None;
        public bool AllRootFolders => false;
        public IReadOnlySet<int> RootFolderIds => new HashSet<int>();
        public string MaxContentRating => "erotica";
    }
}
