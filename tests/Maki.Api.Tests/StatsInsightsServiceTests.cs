using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Security;
using Maki.Data.Identity;
using Microsoft.Extensions.Caching.Memory;

namespace Maki.Api.Tests;

/// <summary>
/// The windowed insights: rhythm bucketing in the reader's zone, sittings, the taste mix and its
/// scoping.
/// </summary>
public sealed class StatsInsightsServiceTests : IDisposable
{
    private const int Owner = 1;

    private readonly TestDb _db = new();
    private readonly SettingsStore _settings = new();

    public void Dispose() => _db.Dispose();

    private sealed class SettingsStore : IUserSettingsStore
    {
        private readonly Dictionary<(int, string), string?> _values = [];

        public void Set(int userId, string key, string value) => _values[(userId, key)] = value;

        public Task<string?> GetAsync(int userId, string key, CancellationToken ct = default) =>
            Task.FromResult(_values.GetValueOrDefault((userId, key)));

        public Task SetAsync(int userId, string key, string? value, CancellationToken ct = default)
        {
            _values[(userId, key)] = value;
            return Task.CompletedTask;
        }
    }

    private StatsInsightsService Service(int caller = Owner, bool allRootFolders = true) => new(
        _db.NewContext(caller, allRootFolders),
        _settings,
        new MemoryCache(new MemoryCacheOptions()),
        new TestCurrentUser(caller));

    private void AddEvent(StatsEventType type, DateTime utc, int value, int? seriesId = null,
        int userId = Owner, string? seriesKey = null, string? payload = null, string title = "S")
    {
        using var db = _db.NewContext();
        db.StatsEvents.Add(new StatsEvent
        {
            Type = type,
            Timestamp = utc,
            UserId = type == StatsEventType.SeriesRemoved ? null : userId,
            SeriesId = seriesId,
            SeriesKey = seriesKey,
            SeriesTitle = title,
            Value = value,
            PayloadJson = payload
        });
        db.SaveChanges();
    }

    private static int Cell(int weekday, int hour) => weekday * 24 + hour;

    private static readonly DateOnly YearStart = new(2026, 1, 1);
    private static readonly DateOnly YearEnd = new(2026, 12, 31);

    // ---- rhythm ----

    [Fact]
    public async Task Stored_zone_buckets_by_local_time_across_dst()
    {
        _settings.Set(Owner, SettingKeys.UserTimeZone, "Europe/Oslo");
        // Thursday 15 January, 10:00 UTC is 11:00 in Oslo (CET). Thursday 16 July is 12:00 (CEST).
        AddEvent(StatsEventType.ReadingTime, new DateTime(2026, 1, 15, 10, 0, 30, DateTimeKind.Utc), 60);
        AddEvent(StatsEventType.ReadingTime, new DateTime(2026, 7, 16, 10, 0, 30, DateTimeKind.Utc), 60);

        // The offset argument is ignored once a zone is stored.
        var dto = await Service().GetAsync(Owner, YearStart, YearEnd, 300, CancellationToken.None);

        Assert.Equal("Europe/Oslo", dto.TimeZone);
        Assert.Equal(60, dto.Rhythm.SecondsByWeekdayHour[Cell(3, 11)]);
        Assert.Equal(60, dto.Rhythm.SecondsByWeekdayHour[Cell(3, 12)]);
        Assert.Equal(120, dto.Rhythm.TotalSeconds);
    }

    [Fact]
    public async Task Without_a_stored_zone_the_browser_offset_is_used()
    {
        // Wednesday 22:30 UTC at UTC+2 is Thursday 00:30.
        AddEvent(StatsEventType.ReadingTime, new DateTime(2026, 3, 4, 22, 30, 30, DateTimeKind.Utc), 60);

        var dto = await Service().GetAsync(Owner, YearStart, YearEnd, -120, CancellationToken.None);

        Assert.Equal("UTC+02:00", dto.TimeZone);
        Assert.Equal(60, dto.Rhythm.SecondsByWeekdayHour[Cell(3, 0)]);
    }

    [Fact]
    public async Task Reading_time_is_credited_at_the_middle_of_the_chunk()
    {
        // Flushed at 10:04 after ten minutes: the reading happened mostly before 10:00.
        AddEvent(StatsEventType.ReadingTime, new DateTime(2026, 3, 2, 10, 4, 0, DateTimeKind.Utc), 600);

        var dto = await Service().GetAsync(Owner, YearStart, YearEnd, 0, CancellationToken.None);

        Assert.Equal(600, dto.Rhythm.SecondsByWeekdayHour[Cell(0, 9)]);
    }

    [Fact]
    public void Prime_window_wraps_midnight()
    {
        var byHour = new int[24];
        byHour[23] = 100;
        byHour[0] = 100;
        byHour[1] = 100;
        byHour[12] = 150;

        Assert.Equal((23, 300), StatsInsightsService.PrimeWindow(byHour));
    }

    [Fact]
    public void Rhythm_derives_busiest_day_and_weekend_share()
    {
        var matrix = new int[168];
        matrix[Cell(0, 20)] = 100; // Monday
        matrix[Cell(5, 10)] = 200; // Saturday
        matrix[Cell(6, 10)] = 100; // Sunday

        var rhythm = StatsInsightsService.Rhythm(matrix);

        Assert.Equal(400, rhythm.TotalSeconds);
        Assert.Equal(5, rhythm.BusiestWeekday);
        Assert.Equal(0.75, rhythm.WeekendShare!.Value, 5);
        Assert.Equal(0.75, rhythm.PrimeShare!.Value, 5);
    }

    [Fact]
    public void Empty_rhythm_answers_with_nulls()
    {
        var rhythm = StatsInsightsService.Rhythm(new int[168]);

        Assert.Equal(0, rhythm.TotalSeconds);
        Assert.Null(rhythm.PrimeStartHour);
        Assert.Null(rhythm.PrimeShare);
        Assert.Null(rhythm.BusiestWeekday);
        Assert.Null(rhythm.WeekendShare);
    }

    // ---- sittings, bookmarks ----

    [Fact]
    public async Task Sittings_summarise_sessions_in_the_window()
    {
        using (var db = _db.NewContext())
        {
            void Add(DateTime start, int seconds, int chapters) => db.ReadingSessions.Add(new ReadingSession
            {
                UserId = Owner, StartedAt = start, EndedAt = start.AddSeconds(seconds),
                ActiveSeconds = seconds, ChaptersCompleted = chapters
            });
            Add(new DateTime(2026, 3, 2, 20, 0, 0, DateTimeKind.Utc), 600, 1);
            Add(new DateTime(2026, 3, 3, 20, 0, 0, DateTimeKind.Utc), 1800, 3);
            Add(new DateTime(2026, 3, 5, 20, 0, 0, DateTimeKind.Utc), 1200, 2);
            Add(new DateTime(2026, 4, 5, 20, 0, 0, DateTimeKind.Utc), 9999, 9); // outside
            db.SaveChanges();
        }

        // UTC+2 in the browser: the longest start still comes back in UTC.
        var dto = await Service().GetAsync(Owner, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 14), -120, CancellationToken.None);

        var sittings = Assert.IsType<Maki.Api.Dtos.SittingsDto>(dto.Sittings);
        Assert.Equal(3, sittings.Count);
        Assert.Equal(1200, sittings.MedianSeconds);
        Assert.Equal(1800, sittings.LongestSeconds);
        Assert.Equal(new DateTime(2026, 3, 3, 20, 0, 0, DateTimeKind.Utc), sittings.LongestStartedAt);
        Assert.Equal(DateTimeKind.Utc, sittings.LongestStartedAt.Kind);
        Assert.Equal(1.5, sittings.PerWeek, 5);
        Assert.Equal(2, sittings.ChaptersPerSittingMedian);
    }

    [Fact]
    public async Task No_sessions_means_no_sittings()
    {
        var dto = await Service().GetAsync(Owner, YearStart, YearEnd, 0, CancellationToken.None);

        Assert.Null(dto.Sittings);
        Assert.Equal(0, dto.BookmarksAdded);
    }

    [Fact]
    public async Task Bookmarks_count_in_the_window()
    {
        var seriesId = _db.SeedSeries("Marked");
        using (var db = _db.NewContext())
        {
            var chapter = new Chapter { SeriesId = seriesId, Number = 1 };
            db.Chapters.Add(chapter);
            db.SaveChanges();
            db.ReaderBookmarks.Add(new ReaderBookmark
            {
                UserId = Owner, SeriesId = seriesId, ChapterId = chapter.Id,
                CreatedAt = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)
            });
            db.ReaderBookmarks.Add(new ReaderBookmark
            {
                UserId = Owner, SeriesId = seriesId, ChapterId = chapter.Id, PageIndex = 3,
                CreatedAt = new DateTime(2025, 5, 1, 0, 0, 0, DateTimeKind.Utc)
            });
            db.SaveChanges();
        }

        var dto = await Service().GetAsync(Owner, YearStart, YearEnd, 0, CancellationToken.None);

        Assert.Equal(1, dto.BookmarksAdded);
    }

    [Fact]
    public async Task Bookmarks_skip_hidden_series_and_folders_the_caller_cannot_see()
    {
        var reader = _db.SeedUser("restricted", MakiPermission.None, allRootFolders: false);
        var granted = _db.SeedSeries("Granted");
        var secret = _db.SeedSeries("Secret", configure: s => s.Incognito = IncognitoMode.Full);
        var outside = _db.SeedSeries("Outside");
        using (var db = _db.NewContext())
        {
            var root = db.Series.Single(s => s.Id == granted).RootFolderId;
            db.UserRootFolders.Add(new UserRootFolder { UserId = reader, RootFolderId = root });
            db.SaveChanges();
            // Each seeded series has its own folder: keep the secret one visible so only incognito hides it.
            db.Series.Single(s => s.Id == secret).RootFolderId = root;
            db.SaveChanges();

            foreach (var seriesId in new[] { granted, secret, outside })
            {
                var chapter = new Chapter { SeriesId = seriesId, Number = 1 };
                db.Chapters.Add(chapter);
                db.SaveChanges();
                db.ReaderBookmarks.Add(new ReaderBookmark
                {
                    UserId = reader, SeriesId = seriesId, ChapterId = chapter.Id,
                    CreatedAt = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)
                });
            }

            db.SaveChanges();
        }

        var dto = await Service(reader, allRootFolders: false)
            .GetAsync(reader, YearStart, YearEnd, 0, CancellationToken.None);

        Assert.Equal(1, dto.BookmarksAdded);
    }

    // ---- taste mix ----

    private static StatsInsightsService.SeriesFacts Facts(int? year, string? type, params string[] genres) =>
        new(genres, type, year);

    [Fact]
    public void Lean_is_read_share_against_library_share()
    {
        var reads = new List<(StatsInsightsService.SeriesFacts, int)>
        {
            (Facts(2015, "manga", "Action", "Shounen"), 8),
            (Facts(1995, "manhwa", "Romance"), 2),
        };
        List<List<string>> library =
        [
            ["Action", "Shounen"], ["Romance"], ["Romance"], ["Romance"], ["Horror"],
        ];

        var taste = StatsInsightsService.Taste(reads, library);

        var action = taste.Lean.Single(l => l.Name == "Action");
        Assert.Equal(0.8, action.ReadShare, 5);
        Assert.Equal(0.2, action.LibraryShare, 5);
        // Over-read first, then under-read, each by the size of the gap.
        Assert.Equal(["Action", "Shounen", "Romance", "Horror"], taste.Lean.Select(l => l.Name));

        Assert.Equal(8, taste.Demographics.Single(d => d.Name == "Shounen").Count);
        Assert.Equal(2, taste.Demographics.Single(d => d.Name == "").Count);
        Assert.Equal(8, taste.Types.Single(t => t.Name == "manga").Count);
        Assert.Equal([(int?)1990, 2010], taste.Eras.Select(e => e.Decade));
    }

    [Fact]
    public void Tiny_genres_on_both_sides_are_left_out_of_the_lean()
    {
        var reads = new List<(StatsInsightsService.SeriesFacts, int)> { (Facts(null, null, "Action"), 100) };
        var library = Enumerable.Range(0, 100).Select(i => i == 0 ? new List<string> { "Rare" } : ["Action"]).ToList();

        var taste = StatsInsightsService.Taste(reads, library);

        Assert.DoesNotContain(taste.Lean, l => l.Name == "Rare");
        var era = Assert.Single(taste.Eras);
        Assert.Null(era.Decade);
        Assert.Equal(100, era.Chapters);
    }

    [Fact]
    public async Task Taste_skips_fully_incognito_series()
    {
        var secret = _db.SeedSeries("Secret", configure: s =>
        {
            s.Incognito = IncognitoMode.Full;
            s.Genres = ["Horror"];
        });
        var open = _db.SeedSeries("Open", configure: s => s.Genres = ["Comedy"]);
        AddEvent(StatsEventType.ChaptersRead, new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), 5, secret);
        AddEvent(StatsEventType.ChaptersRead, new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), 5, open);

        var dto = await Service().GetAsync(Owner, YearStart, YearEnd, 0, CancellationToken.None);

        Assert.Equal(5, dto.Taste.Types.Sum(t => t.Count));
        Assert.Equal(1.0, dto.Taste.Lean.Single(l => l.Name == "Comedy").ReadShare, 5);
        Assert.Equal(0.0, dto.Taste.Lean.Single(l => l.Name == "Horror").ReadShare, 5);
    }

    [Fact]
    public async Task Taste_skips_series_outside_the_callers_root_folders()
    {
        var reader = _db.SeedUser("restricted", MakiPermission.None, allRootFolders: false);
        var hidden = _db.SeedSeries("Hidden", configure: s => s.Genres = ["Horror"]);
        var granted = _db.SeedSeries("Granted", configure: s => s.Genres = ["Comedy"]);
        using (var db = _db.NewContext())
        {
            var root = db.Series.Single(s => s.Id == granted).RootFolderId;
            db.UserRootFolders.Add(new UserRootFolder { UserId = reader, RootFolderId = root });
            db.SaveChanges();
        }

        AddEvent(StatsEventType.ChaptersRead, new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), 7, hidden, reader);
        AddEvent(StatsEventType.ChaptersRead, new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), 3, granted, reader);

        var dto = await Service(reader, allRootFolders: false)
            .GetAsync(reader, YearStart, YearEnd, 0, CancellationToken.None);

        Assert.Equal(3, dto.Taste.Types.Sum(t => t.Count));
        Assert.DoesNotContain(dto.Taste.Lean, l => l.Name == "Horror");
    }

    [Fact]
    public async Task Removed_series_contribute_genres_from_their_snapshot()
    {
        AddEvent(StatsEventType.ChaptersRead, new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), 4,
            seriesKey: "mb:9");
        AddEvent(StatsEventType.SeriesRemoved, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), 1,
            seriesKey: "mb:9", payload: """{"genres":["Seinen","Drama"],"tags":[]}""");
        _db.SeedSeries("Other", configure: s => s.Genres = ["Comedy"]);

        var dto = await Service().GetAsync(Owner, YearStart, YearEnd, 0, CancellationToken.None);

        Assert.Equal(4, dto.Taste.Demographics.Single(d => d.Name == "Seinen").Count);
        Assert.Equal(1.0, dto.Taste.Lean.Single(l => l.Name == "Drama").ReadShare, 5);
    }

    [Fact]
    public async Task Admin_sees_the_named_readers_numbers_not_their_own()
    {
        var other = _db.SeedUser("other", MakiPermission.None);
        AddEvent(StatsEventType.ReadingTime, new DateTime(2026, 3, 2, 10, 0, 30, DateTimeKind.Utc), 60, userId: other);
        AddEvent(StatsEventType.ReadingTime, new DateTime(2026, 3, 2, 10, 0, 30, DateTimeKind.Utc), 999, userId: Owner);

        var dto = await Service(Owner).GetAsync(other, YearStart, YearEnd, 0, CancellationToken.None);

        Assert.Equal(60, dto.Rhythm.TotalSeconds);
    }
}
