using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// How somebody reads, rather than what. Most of these are about the two signals that mean
/// "unknown" rather than a value: a zero <c>ReadSeconds</c> is an untimed read, and a zero
/// <c>PageCount</c> is a Kavita import that knows what was read but not when.
/// </summary>
public class ReadingBehaviourServiceTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    /// <summary>No stored zone, so day boundaries land in UTC and the dates below are predictable.</summary>
    private sealed class UtcSettingsStore : IUserSettingsStore
    {
        public Task<string?> GetAsync(int userId, string key, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task SetAsync(int userId, string key, string? value, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private ReadingBehaviourService Service() => new(
        _db.ScopeFactory(),
        new UtcSettingsStore(),
        NullLogger<ReadingBehaviourService>.Instance);

    private int SeedSeries(string title = "Series", IncognitoMode incognito = IncognitoMode.Off) =>
        _db.SeedSeries(title, configure: s => s.Incognito = incognito);

    /// <summary>
    /// Seeds <paramref name="downloaded"/> chapters on disk and completes the first
    /// <paramref name="read"/> of them.
    /// </summary>
    private void Seed(
        int seriesId,
        int downloaded,
        int read,
        int readSeconds = 300,
        int pageCount = 20,
        DateTime? at = null,
        int userId = 1)
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

            db.ChapterProgress.Add(new ChapterProgress
            {
                UserId = userId,
                SeriesId = seriesId,
                ChapterId = chapter.Id,
                PageCount = pageCount,
                Completed = true,
                ReadSeconds = readSeconds,
                StartedAt = at ?? Now,
                UpdatedAt = at ?? Now
            });
            db.SaveChanges();
        }
    }

    private Task<ReadingBehaviour> BehaviourAsync(int userId = 1) =>
        Service().GetAsync(new TestCurrentUser(userId), refresh: true);

    [Fact]
    public async Task Finish_rate_counts_series_read_to_the_end_of_what_is_held()
    {
        Seed(SeedSeries("Finished"), downloaded: 5, read: 5);
        Seed(SeedSeries("Dropped"), downloaded: 20, read: 4);

        var behaviour = await BehaviourAsync();

        Assert.Equal(2, behaviour.SeriesStarted);
        Assert.Equal(1, behaviour.SeriesFinished);
        Assert.Equal(0.5, behaviour.FinishRate);
    }

    [Fact]
    public async Task Stop_point_is_how_far_into_the_ones_they_gave_up_on()
    {
        Seed(SeedSeries("Dropped"), downloaded: 20, read: 5);

        // A quarter in. The finished series is not part of this: it has no stop point.
        Seed(SeedSeries("Finished"), downloaded: 3, read: 3);

        Assert.Equal(0.25, (await BehaviourAsync()).MedianStopPoint!.Value, 5);
    }

    [Fact]
    public async Task A_single_chapter_sampled_is_not_an_abandonment()
    {
        // One of forty is a look, not a verdict, so it must not drag the stop point toward zero.
        Seed(SeedSeries("Sampled"), downloaded: 40, read: 1);

        var behaviour = await BehaviourAsync();

        Assert.Null(behaviour.MedianStopPoint);
        Assert.Empty(behaviour.Abandoned);
    }

    [Fact]
    public async Task Untimed_reads_never_count_as_fast_reading()
    {
        // Kavita imports and OPDS fetches carry no time. Scored as zero seconds they would report a
        // reader who finishes a chapter instantly.
        Seed(SeedSeries("Imported"), downloaded: 20, read: 20, readSeconds: 0, pageCount: 0);

        var behaviour = await BehaviourAsync();

        Assert.Null(behaviour.MedianSecondsPerChapter);
        Assert.Equal(0, behaviour.TimedChapters);
        // The read itself still counts: the import knows what was read.
        Assert.Equal(20, behaviour.ChaptersRead);
        Assert.Equal(1, behaviour.SeriesFinished);
    }

    [Fact]
    public async Task Pace_needs_enough_timed_chapters_to_be_worth_reporting()
    {
        Seed(SeedSeries("Few"), downloaded: 4, read: 4, readSeconds: 240);

        var behaviour = await BehaviourAsync();

        Assert.Equal(4, behaviour.TimedChapters);
        Assert.Null(behaviour.MedianSecondsPerChapter); // under the floor
    }

    [Fact]
    public async Task Pace_is_a_median_so_one_tab_left_open_does_not_move_it()
    {
        var seriesId = SeedSeries("Paced");
        Seed(seriesId, downloaded: 12, read: 12, readSeconds: 300);

        // One chapter left open for three hours.
        using (var db = _db.NewContext())
        {
            var row = db.ChapterProgress.First(p => p.SeriesId == seriesId);
            row.ReadSeconds = 10_800;
            db.SaveChanges();
        }

        Assert.Equal(300, (await BehaviourAsync()).MedianSecondsPerChapter);
    }

    [Fact]
    public async Task Imports_never_become_the_biggest_day()
    {
        // A whole back catalogue stamped with one date would otherwise be a reading day for ever.
        Seed(SeedSeries("Imported"), downloaded: 50, read: 50, readSeconds: 0, pageCount: 0);
        Seed(SeedSeries("Actually read"), downloaded: 3, read: 3, at: Now);

        var behaviour = await BehaviourAsync();

        Assert.Equal(3, behaviour.BiggestDayCount);
        Assert.Equal(1, behaviour.ReadingDays);
    }

    [Fact]
    public async Task Biggest_day_counts_chapters_finished_that_day()
    {
        Seed(SeedSeries("Monday"), downloaded: 2, read: 2, at: Now);
        Seed(SeedSeries("Tuesday"), downloaded: 7, read: 7, at: Now.AddDays(1));

        var behaviour = await BehaviourAsync();

        Assert.Equal(7, behaviour.BiggestDayCount);
        Assert.Equal(new DateOnly(2026, 6, 16), behaviour.BiggestDay);
        Assert.Equal(2, behaviour.ReadingDays);
    }

    [Fact]
    public async Task Savoured_and_devoured_rank_by_pace()
    {
        Seed(SeedSeries("Slow"), downloaded: 6, read: 6, readSeconds: 900);
        Seed(SeedSeries("Quick"), downloaded: 6, read: 6, readSeconds: 120);

        var behaviour = await BehaviourAsync();

        Assert.Equal("Slow", behaviour.Savoured[0].Title);
        Assert.Equal("Quick", behaviour.Devoured[0].Title);
        Assert.Equal("15 min", behaviour.Savoured[0].Value);
        Assert.Equal("2 min", behaviour.Devoured[0].Value);
    }

    [Fact]
    public async Task Fully_incognito_reading_is_invisible_here_too()
    {
        Seed(SeedSeries("Secret", IncognitoMode.Full), downloaded: 10, read: 10);

        var behaviour = await BehaviourAsync();

        Assert.Equal(0, behaviour.ChaptersRead);
        Assert.Equal(0, behaviour.SeriesStarted);
    }

    [Fact]
    public async Task Another_users_reading_never_leaks_in()
    {
        var other = _db.SeedUser("other");
        Seed(SeedSeries("Theirs"), downloaded: 10, read: 10, userId: other);

        Assert.Equal(0, (await BehaviourAsync()).ChaptersRead);
        Assert.Equal(10, (await BehaviourAsync(other)).ChaptersRead);
    }

    [Fact]
    public async Task No_reading_answers_with_nulls_rather_than_zeroes()
    {
        SeedSeries("Untouched");

        var behaviour = await BehaviourAsync();

        // "No answer" and "the answer is zero" are different, and a reader who has read nothing has
        // not finished 0% of what they started.
        Assert.Null(behaviour.FinishRate);
        Assert.Null(behaviour.MedianSecondsPerChapter);
        Assert.Null(behaviour.BiggestDayCount);
        Assert.Empty(behaviour.Savoured);
    }
}
