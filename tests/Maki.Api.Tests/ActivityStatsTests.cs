using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// The activity-stats pipeline: read-delta tracking (<see cref="ReadingProgressService"/>),
/// the one-time backfill, and ActivityStatsService aggregation/bucketing.
/// </summary>
public sealed class ActivityStatsTests : IDisposable
{
    /// <summary>Activity stats are per-user; every read these tests record belongs to this one.</summary>
    private const int TestUser = 1;

    private readonly TestDb _db = new();
    private readonly ReadingProgressGate _gate = new();

    public void Dispose() => _db.Dispose();

    /// <summary>A tracker over a fresh context, mirroring the per-scope usage in production.</summary>
    private ReadingProgressService Progress() =>
        new(_db.NewContext(), _gate, NullLogger<ReadingProgressService>.Instance);

    private List<StatsEvent> Events()
    {
        using var db = _db.NewContext();
        return db.StatsEvents.OrderBy(e => e.Id).ToList();
    }

    // ---- read tracking: the Kavita source ----

    [Fact]
    public async Task FirstEncounterIsSilentBaseline()
    {
        await Progress().TrackKavitaAsync(TestUser, 7, "Ippo", null, 240, 0, CancellationToken.None);

        Assert.Empty(Events());
        using var db = _db.NewContext();
        var state = Assert.Single(db.ReadingStates.ToList());
        Assert.Equal(240, state.MaxChapter);
        Assert.False(state.Finished);
    }

    [Fact]
    public async Task ForwardDeltaEmitsChaptersRead()
    {
        await Progress().TrackKavitaAsync(TestUser, 7, "Ippo", null, 240, 0, CancellationToken.None);
        await Progress().TrackKavitaAsync(TestUser, 7, "Ippo", null, 245.5, 0, CancellationToken.None);

        var e = Assert.Single(Events());
        Assert.Equal(StatsEventType.ChaptersRead, e.Type);
        Assert.Equal(5, e.Value);
        Assert.Equal(7, e.KavitaSeriesId);
    }

    [Fact]
    public async Task BackwardsMovementIsIgnored()
    {
        await Progress().TrackKavitaAsync(TestUser, 7, "Ippo", null, 240, 0, CancellationToken.None);
        var marks = await Progress().TrackKavitaAsync(TestUser, 7, "Ippo", null, 100, 0, CancellationToken.None);

        Assert.Empty(Events());
        Assert.Equal(240, marks.MaxChapter);
        using var db = _db.NewContext();
        Assert.Equal(240, db.ReadingStates.Single().MaxChapter);
    }

    [Fact]
    public async Task VolumeOnlySeriesEmitsVolumesRead()
    {
        await Progress().TrackKavitaAsync(TestUser, 9, "Omnibus", null, 0, 2, CancellationToken.None);
        await Progress().TrackKavitaAsync(TestUser, 9, "Omnibus", null, 0, 4, CancellationToken.None);

        var e = Assert.Single(Events());
        Assert.Equal(StatsEventType.VolumesRead, e.Type);
        Assert.Equal(2, e.Value);
    }

    [Fact]
    public async Task FinishFiresOnceForCompletedSeries()
    {
        var seriesId = SeedCompleted("Done Series", 11, 12);

        await Progress().TrackKavitaAsync(TestUser, 7, "Done Series", seriesId, 10, 0, CancellationToken.None);
        await Progress().TrackKavitaAsync(TestUser, 7, "Done Series", seriesId, 12, 0, CancellationToken.None);
        await Progress().TrackKavitaAsync(TestUser, 7, "Done Series", seriesId, 12, 0, CancellationToken.None);

        var events = Events();
        Assert.Single(events, e => e.Type == StatsEventType.SeriesFinished);
        Assert.Single(events, e => e.Type == StatsEventType.ChaptersRead && e.Value == 2);
    }

    [Fact]
    public async Task AlreadyFinishedAtBaselineStaysSilent()
    {
        var seriesId = SeedCompleted("Old Finish", 5);

        await Progress().TrackKavitaAsync(TestUser, 7, "Old Finish", seriesId, 5, 0, CancellationToken.None);
        await Progress().TrackKavitaAsync(TestUser, 7, "Old Finish", seriesId, 5, 0, CancellationToken.None);

        Assert.Empty(Events());
        using var db2 = _db.NewContext();
        Assert.True(db2.ReadingStates.Single().Finished);
    }

    // ---- read tracking: the built-in reader, and the merge between the two ----

    [Fact]
    public async Task NativeFirstReadEmitsImmediately()
    {
        // No silent baseline on the native path: nothing predates Maki here, the read just
        // happened in it.
        var seriesId = _db.SeedSeries("Native");

        await Progress().TrackNativeAsync(TestUser, seriesId, "Native", 1, 0, CancellationToken.None);

        var e = Assert.Single(Events());
        Assert.Equal(StatsEventType.ChaptersRead, e.Type);
        Assert.Equal(1, e.Value);
        Assert.Equal(seriesId, e.SeriesId);
        Assert.Null(e.KavitaSeriesId);

        using var db = _db.NewContext();
        var state = Assert.Single(db.ReadingStates.ToList());
        Assert.Null(state.KavitaSeriesId);
        Assert.Equal(seriesId, state.SeriesId);
    }

    [Fact]
    public async Task KavitaAdoptsNativeRowSilentlyAndKeepsTheHigherMark()
    {
        var seriesId = _db.SeedSeries("Shared");
        await Progress().TrackNativeAsync(TestUser, seriesId, "Shared", 5, 0, CancellationToken.None);

        // Kavita shows up carrying 20 chapters of pre-Maki history for the same series.
        var marks = await Progress().TrackKavitaAsync(TestUser, 20, "Shared", seriesId, 20, 2, CancellationToken.None);

        Assert.Equal(20, marks.MaxChapter);
        Assert.Equal(2, marks.MaxVolume);

        // Only the native read is recorded — adoption itself emits nothing.
        var e = Assert.Single(Events());
        Assert.Equal(5, e.Value);

        using var db = _db.NewContext();
        var state = Assert.Single(db.ReadingStates.ToList());
        Assert.Equal(20, state.KavitaSeriesId);
        Assert.Equal(20, state.MaxChapter);
    }

    [Fact]
    public async Task AdoptionNeverLowersTheNativeMark()
    {
        var seriesId = _db.SeedSeries("Ahead");
        await Progress().TrackNativeAsync(TestUser, seriesId, "Ahead", 40, 0, CancellationToken.None);

        var marks = await Progress().TrackKavitaAsync(TestUser, 3, "Ahead", seriesId, 12, 0, CancellationToken.None);

        Assert.Equal(40, marks.MaxChapter);
    }

    [Fact]
    public async Task KavitaEchoOfANativeReadCountsOnce()
    {
        // The anti-double-count property: read natively, then let Kavita report the same
        // number on its next tick. The delta against the stored mark is zero.
        var seriesId = _db.SeedSeries("Echo");
        await Progress().TrackNativeAsync(TestUser, seriesId, "Echo", 7, 0, CancellationToken.None);
        await Progress().TrackKavitaAsync(TestUser, 11, "Echo", seriesId, 7, 0, CancellationToken.None);
        await Progress().TrackKavitaAsync(TestUser, 11, "Echo", seriesId, 7, 0, CancellationToken.None);

        var e = Assert.Single(Events());
        Assert.Equal(7, e.Value);
    }

    [Fact]
    public async Task NativeReadAfterAdoptionStillEmitsAndScrobbles()
    {
        var seriesId = _db.SeedSeries("Ongoing");
        await Progress().TrackKavitaAsync(TestUser, 4, "Ongoing", seriesId, 20, 0, CancellationToken.None);

        // Reading on in Maki must both record stats and raise the mark the Kavita pass
        // scrobbles, otherwise those chapters never reach a tracker.
        await Progress().TrackNativeAsync(TestUser, seriesId, "Ongoing", 25, 0, CancellationToken.None);
        var marks = await Progress().TrackKavitaAsync(TestUser, 4, "Ongoing", seriesId, 20, 0, CancellationToken.None);

        var e = Assert.Single(Events());
        Assert.Equal(5, e.Value);
        Assert.Equal(4, e.KavitaSeriesId);
        Assert.Equal(25, marks.MaxChapter);
    }

    [Fact]
    public async Task SpecialAdvancesTheMarkWithoutEmitting()
    {
        var seriesId = _db.SeedSeries("Specials");
        await Progress().TrackNativeAsync(TestUser, seriesId, "Specials", 10, 0, CancellationToken.None);
        await Progress().TrackNativeAsync(TestUser, seriesId, "Specials", 10.5, 0, CancellationToken.None);

        Assert.Single(Events());

        // ...and the next whole chapter is still worth exactly one.
        await Progress().TrackNativeAsync(TestUser, seriesId, "Specials", 11, 0, CancellationToken.None);
        var events = Events();
        Assert.Equal(2, events.Count);
        Assert.Equal(1, events[1].Value);
    }

    [Fact]
    public async Task UnnumberedReadEmitsWithoutMovingTheMark()
    {
        var seriesId = _db.SeedSeries("One Shot");
        await Progress().TrackNativeAsync(TestUser, seriesId, "One Shot", 3, 0, CancellationToken.None);
        await Progress().RecordUnnumberedReadAsync(TestUser, seriesId, "One Shot", CancellationToken.None);

        var events = Events();
        Assert.Equal(2, events.Count);
        Assert.Equal(1, events[1].Value);

        using var db = _db.NewContext();
        Assert.Equal(3, db.ReadingStates.Single().MaxChapter);
    }

    [Fact]
    public async Task ImportedKavitaHistoryStaysOutOfActivityStats()
    {
        var seriesId = _db.SeedSeries("Imported");

        await Progress().ImportSilentAsync(TestUser, seriesId, 12, "Imported", 300, 20, CancellationToken.None);

        // Kavita can't say when those 300 chapters were read; dating them today would pile a whole
        // back catalogue onto one day of the year in review.
        Assert.Empty(Events());

        using var db = _db.NewContext();
        var state = Assert.Single(db.ReadingStates.ToList());
        Assert.Equal(300, state.MaxChapter);
        Assert.Equal(12, state.KavitaSeriesId);
    }

    [Fact]
    public async Task ReadingAfterAnImportIsMeasuredFromTheImportedMark()
    {
        // The reason the import must still raise the mark: without it the next genuine read would
        // emit a delta of hundreds.
        var seriesId = _db.SeedSeries("Imported");
        await Progress().ImportSilentAsync(TestUser, seriesId, null, "Imported", 300, 0, CancellationToken.None);

        await Progress().TrackNativeAsync(TestUser, seriesId, "Imported", 301, 0, CancellationToken.None);

        var e = Assert.Single(Events());
        Assert.Equal(StatsEventType.ChaptersRead, e.Type);
        Assert.Equal(1, e.Value);
    }

    [Fact]
    public async Task ImportNeverLowersAnExistingMark()
    {
        var seriesId = _db.SeedSeries("Ahead");
        await Progress().TrackNativeAsync(TestUser, seriesId, "Ahead", 50, 0, CancellationToken.None);

        await Progress().ImportSilentAsync(TestUser, seriesId, 5, "Ahead", 10, 0, CancellationToken.None);

        using var db = _db.NewContext();
        Assert.Equal(50, db.ReadingStates.Single().MaxChapter);
    }

    [Fact]
    public async Task NativeFinishFiresForCompletedSeries()
    {
        var seriesId = SeedCompleted("Native Done", 1, 2);

        await Progress().TrackNativeAsync(TestUser, seriesId, "Native Done", 2, 0, CancellationToken.None);

        var events = Events();
        Assert.Single(events, e => e.Type == StatsEventType.SeriesFinished);
        Assert.Single(events, e => e.Type == StatsEventType.ChaptersRead && e.Value == 2);
    }

    private int SeedCompleted(string title, params decimal[] chapterNumbers)
    {
        var seriesId = _db.SeedSeries(title, configure: s => s.Status = SeriesStatus.Completed);
        using var db = _db.NewContext();
        db.Chapters.AddRange(chapterNumbers.Select(n =>
            new Chapter { SeriesId = seriesId, Number = n, Language = "en" }));
        db.SaveChanges();
        return seriesId;
    }

    // ---- backfill ----

    [Fact]
    public async Task BackfillSeedsOnceAndGroupsDownloadsByDay()
    {
        var seriesId = _db.SeedSeries("Backfilled");
        using (var db = _db.NewContext())
        {
            db.ChapterFiles.AddRange(
                new ChapterFile { SeriesId = seriesId, RelativePath = "a", SourceName = "x", DateAdded = new DateTime(2026, 3, 10, 8, 0, 0, DateTimeKind.Utc) },
                new ChapterFile { SeriesId = seriesId, RelativePath = "b", SourceName = "x", DateAdded = new DateTime(2026, 3, 10, 21, 0, 0, DateTimeKind.Utc) },
                new ChapterFile { SeriesId = seriesId, RelativePath = "c", SourceName = "x", DateAdded = new DateTime(2026, 3, 11, 1, 0, 0, DateTimeKind.Utc) });
            db.SaveChanges();
        }

        using (var db = _db.NewContext())
        {
            await new StatsBackfillService(db, NullLogger<StatsBackfillService>.Instance).RunOnceAsync();
        }

        using (var db = _db.NewContext())
        {
            await new StatsBackfillService(db, NullLogger<StatsBackfillService>.Instance).RunOnceAsync();
        }

        var events = Events();
        Assert.Single(events, e => e.Type == StatsEventType.SeriesAdded);
        var downloads = events.Where(e => e.Type == StatsEventType.ChapterDownloaded).ToList();
        Assert.Equal(2, downloads.Count); // two distinct days
        Assert.Equal(2, downloads.Single(d => d.Timestamp.Day == 10).Value);
    }

    // ---- aggregation ----

    private ActivityStatsService Activity(DateTimeOffset? now = null, bool kavita = false)
    {
        var settings = new FakeAppSettings();
        if (kavita)
        {
            settings.Set(SettingKeys.KavitaUrl, "http://kavita").Set(SettingKeys.KavitaApiKey, "k");
        }

        return new ActivityStatsService(_db.NewContext(), settings,
            new StoppedClock(now ?? new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero)));
    }

    /// <summary>
    /// Defaults to a library-wide event (null user), matching what <c>StatsEventService</c> writes.
    /// Pass <paramref name="userId"/> for the reader-owned kinds.
    /// </summary>
    private void AddEvent(StatsEventType type, DateTime utc, int value = 1, int? seriesId = null,
        string title = "S", string? payload = null, int? userId = null)
    {
        using var db = _db.NewContext();
        db.StatsEvents.Add(new StatsEvent
        {
            Type = type,
            Timestamp = utc,
            UserId = userId,
            SeriesId = seriesId,
            SeriesTitle = title,
            Value = value,
            PayloadJson = payload
        });
        db.SaveChanges();
    }

    private static readonly DateOnly Y26Start = new(2026, 1, 1);
    private static readonly DateOnly Y26End = new(2026, 12, 31);

    [Fact]
    public async Task OffsetShiftsEventsIntoLocalBuckets()
    {
        // UTC+2 (offset −120): 22:30 UTC on 31 March is already 1 April locally.
        AddEvent(StatsEventType.ChaptersRead, new DateTime(2026, 3, 31, 22, 30, 0, DateTimeKind.Utc), 3);
        // And 23:00 UTC on 31 Dec 2025 belongs to 2026 locally.
        AddEvent(StatsEventType.ChaptersRead, new DateTime(2025, 12, 31, 23, 0, 0, DateTimeKind.Utc), 2);

        var stats = await Activity().StatsAsync(
            TestUser, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), -120, CancellationToken.None);

        Assert.Equal(5, stats.Totals.ChaptersRead);
        Assert.Contains(stats.Timeline, p => p.Bucket == "2026-04" && p.ChaptersRead == 3);
        Assert.Contains(stats.Timeline, p => p.Bucket == "2026-01" && p.ChaptersRead == 2);
    }

    [Fact]
    public async Task ShortRangesUseDayBucketsAndExcludeOutsideEvents()
    {
        AddEvent(StatsEventType.ChaptersRead, new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc), 4);
        AddEvent(StatsEventType.ChaptersRead, new DateTime(2026, 4, 2, 12, 0, 0, DateTimeKind.Utc), 9);

        var stats = await Activity().StatsAsync(
            TestUser, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), 0, CancellationToken.None);

        Assert.Equal(4, stats.Totals.ChaptersRead);
        var point = Assert.Single(stats.Timeline);
        Assert.Equal("2026-03-10", point.Bucket);
    }

    [Fact]
    public async Task RemovedSeriesContributeGenresViaSnapshot()
    {
        AddEvent(StatsEventType.SeriesRemoved, new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            title: "Gone", payload: """{"genres":["Action"],"tags":["Ninja"]}""");

        var stats = await Activity().StatsAsync(
            TestUser, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), 0, CancellationToken.None);

        Assert.Contains(stats.TopGenres, g => g.Name == "Action");
        Assert.Contains(stats.TopTags, t => t.Name == "Ninja");
        Assert.Equal(1, stats.Totals.SeriesRemoved);
    }

    [Fact]
    public async Task DroppedRequiresStaleProgressInsideRange()
    {
        using (var db = _db.NewContext())
        {
            db.ReadingStates.AddRange(
                new ReadingState { UserId = 1, KavitaSeriesId = 1, Title = "Stale", MaxChapter = 12, LastProgressAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc) },
                new ReadingState { UserId = 1, KavitaSeriesId = 2, Title = "Active", MaxChapter = 30, LastProgressAt = new DateTime(2026, 12, 20, 0, 0, 0, DateTimeKind.Utc) },
                new ReadingState { UserId = 1, KavitaSeriesId = 3, Title = "Finished", MaxChapter = 40, Finished = true, LastProgressAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc) });
            db.SaveChanges();
        }

        var stats = await Activity().StatsAsync(
            TestUser, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), 0, CancellationToken.None);

        var dropped = Assert.Single(stats.Dropped);
        Assert.Equal("Stale", dropped.Title);
        Assert.Equal(1, stats.Totals.SeriesDropped);
    }

    [Fact]
    public async Task ReadingTimeTotalsAndRanksSeparatelyFromChapterCounts()
    {
        var deep = _db.SeedSeries("Slow Read");
        var quick = _db.SeedSeries("Quick Read");
        // The two orders disagree on purpose: Quick Read wins on chapters, Slow Read on minutes.
        AddEvent(StatsEventType.ChaptersRead, new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), 40, quick, "Quick Read");
        AddEvent(StatsEventType.ChaptersRead, new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), 3, deep, "Slow Read");
        AddEvent(StatsEventType.ReadingTime, new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), 600, quick, "Quick Read");
        AddEvent(StatsEventType.ReadingTime, new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc), 300, deep, "Slow Read");
        AddEvent(StatsEventType.ReadingTime, new DateTime(2026, 5, 3, 0, 0, 0, DateTimeKind.Utc), 1500, deep, "Slow Read");

        var stats = await Activity().StatsAsync(
            TestUser, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), 0, CancellationToken.None);

        Assert.Equal(2400, stats.Totals.ReadingSeconds);
        Assert.Equal("Slow Read", stats.TopByTime[0].Title);
        Assert.Equal(1800, stats.TopByTime[0].Seconds);

        // And seconds never leak into the chapter counts they sit beside in the same log.
        Assert.Equal(43, stats.Totals.ChaptersRead);
        Assert.Equal("Quick Read", stats.TopRead[0].Title);
        Assert.Equal(40, stats.TopRead[0].Count);
    }

    [Fact]
    public async Task ReadTrackingFlagFollowsKavitaConfig()
    {
        var without = await Activity().StatsAsync(
            TestUser, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), 0, CancellationToken.None);
        var with = await Activity(kavita: true).StatsAsync(
            TestUser, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), 0, CancellationToken.None);

        Assert.False(without.ReadTrackingAvailable);
        Assert.True(with.ReadTrackingAvailable);
    }

    [Fact]
    public async Task ReadTrackingIsAvailableFromTheBuiltInReaderAlone()
    {
        var seriesId = _db.SeedSeries("Reader Only");
        await Progress().TrackNativeAsync(TestUser, seriesId, "Reader Only", 1, 0, CancellationToken.None);

        var stats = await Activity().StatsAsync(
            TestUser, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), 0, CancellationToken.None);

        Assert.True(stats.ReadTrackingAvailable);
    }

    // ---- per-user scoping ----

    [Fact]
    public async Task ReadsBelongToOneUserButLibraryEventsBelongToEveryone()
    {
        var otherUser = _db.SeedUser("other");
        AddEvent(StatsEventType.ChaptersRead, new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), 7,
            title: "Mine", userId: TestUser);
        AddEvent(StatsEventType.ChaptersRead, new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), 40,
            title: "Theirs", userId: otherUser);
        // Null user: a fact about the instance, so it counts for both of them.
        AddEvent(StatsEventType.SeriesAdded, new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc),
            title: "Shared");

        var mine = await Activity().StatsAsync(TestUser, Y26Start, Y26End, 0, CancellationToken.None);
        var theirs = await Activity().StatsAsync(otherUser, Y26Start, Y26End, 0, CancellationToken.None);

        Assert.Equal(7, mine.Totals.ChaptersRead);
        Assert.Equal(40, theirs.Totals.ChaptersRead);
        Assert.DoesNotContain(mine.TopRead, s => s.Title == "Theirs");
        Assert.Equal(1, mine.Totals.SeriesAdded);
        Assert.Equal(1, theirs.Totals.SeriesAdded);
    }

    [Fact]
    public async Task YearsAreScopedToTheUserToo()
    {
        var otherUser = _db.SeedUser("other");
        AddEvent(StatsEventType.ChaptersRead, new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            userId: TestUser);
        AddEvent(StatsEventType.ChaptersRead, new DateTime(2019, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            userId: otherUser);

        Assert.Equal([2024], await Activity().YearsAsync(TestUser, CancellationToken.None));
        Assert.Equal([2019], await Activity().YearsAsync(otherUser, CancellationToken.None));
    }

    [Fact]
    public async Task DroppedSeriesAreScopedToTheUser()
    {
        var otherUser = _db.SeedUser("other");
        using (var db = _db.NewContext())
        {
            var stale = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
            db.ReadingStates.AddRange(
                new ReadingState { UserId = TestUser, KavitaSeriesId = 1, Title = "Mine", MaxChapter = 12, LastProgressAt = stale },
                new ReadingState { UserId = otherUser, KavitaSeriesId = 2, Title = "Theirs", MaxChapter = 12, LastProgressAt = stale });
            db.SaveChanges();
        }

        var stats = await Activity().StatsAsync(TestUser, Y26Start, Y26End, 0, CancellationToken.None);

        var dropped = Assert.Single(stats.Dropped);
        Assert.Equal("Mine", dropped.Title);
    }

    // ---- the new headline numbers ----

    [Fact]
    public async Task DaysActiveCountsDistinctLocalDates()
    {
        // Two events on one local day, one on another, plus a UTC timestamp that crosses midnight
        // into a third local day at UTC+2 — three distinct local dates, four events.
        AddEvent(StatsEventType.ChaptersRead, new DateTime(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc), 2);
        AddEvent(StatsEventType.ChaptersRead, new DateTime(2026, 5, 1, 20, 0, 0, DateTimeKind.Utc), 3);
        AddEvent(StatsEventType.ChaptersRead, new DateTime(2026, 5, 4, 12, 0, 0, DateTimeKind.Utc), 1);
        AddEvent(StatsEventType.ChaptersRead, new DateTime(2026, 5, 9, 23, 0, 0, DateTimeKind.Utc), 1);
        // Downloads are not reading and must not raise the count.
        AddEvent(StatsEventType.ChapterDownloaded, new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc), 5);

        var stats = await Activity().StatsAsync(TestUser, Y26Start, Y26End, -120, CancellationToken.None);

        Assert.Equal(3, stats.Totals.DaysActive);
        // The 23:00 UTC read landed on 10 May locally, not 9 May.
        Assert.Contains(stats.Timeline, p => p.Bucket == "2026-05");
    }

    [Fact]
    public async Task TimelineCarriesReadingSecondsPerBucket()
    {
        AddEvent(StatsEventType.ReadingTime, new DateTime(2026, 3, 4, 12, 0, 0, DateTimeKind.Utc), 600);
        AddEvent(StatsEventType.ReadingTime, new DateTime(2026, 3, 9, 12, 0, 0, DateTimeKind.Utc), 300);
        AddEvent(StatsEventType.ReadingTime, new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc), 60);

        var stats = await Activity().StatsAsync(TestUser, Y26Start, Y26End, 0, CancellationToken.None);

        Assert.Equal(900, stats.Timeline.Single(p => p.Bucket == "2026-03").ReadingSeconds);
        Assert.Equal(60, stats.Timeline.Single(p => p.Bucket == "2026-04").ReadingSeconds);
        Assert.Equal(960, stats.Totals.ReadingSeconds);
    }

    [Fact]
    public async Task RankedSeriesCarryACoverAndRemovedOnesDoNot()
    {
        var live = _db.SeedSeries("Live");
        using (var db = _db.NewContext())
        {
            var series = db.Series.Single(s => s.Id == live);
            series.CoverPath = "covers/live.jpg";
            db.SaveChanges();
        }

        AddEvent(StatsEventType.ChaptersRead, new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), 5, live, "Live");
        // No SeriesId: the row survives its series, so it ranks with a null cover rather than none.
        AddEvent(StatsEventType.ChaptersRead, new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), 2, null, "Gone");

        var stats = await Activity().StatsAsync(TestUser, Y26Start, Y26End, 0, CancellationToken.None);

        Assert.NotNull(stats.TopRead.Single(s => s.Title == "Live").CoverUrl);
        Assert.Null(stats.TopRead.Single(s => s.Title == "Gone").CoverUrl);
    }
}