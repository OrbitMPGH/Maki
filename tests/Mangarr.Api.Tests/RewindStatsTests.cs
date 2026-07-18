using Mangarr.Api.Services;
using Mangarr.Core.Configuration;
using Mangarr.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mangarr.Api.Tests;

/// <summary>
/// The Rewind pipeline: read-delta tracking (ScrobbleService.TrackReadingAsync),
/// the one-time backfill, and RewindService aggregation/bucketing.
/// </summary>
public sealed class RewindStatsTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private ScrobbleService Scrobbler()
    {
        var scopeFactory = _db.ScopeFactory();
        // Only the scope factory is exercised by TrackReadingAsync; the Kavita client and
        // trackers are never touched.
        return new ScrobbleService(scopeFactory, new SettingsService(scopeFactory),
            null!, null!, null!, null!, NullLogger<ScrobbleService>.Instance);
    }

    private List<StatsEvent> Events()
    {
        using var db = _db.NewContext();
        return db.StatsEvents.OrderBy(e => e.Id).ToList();
    }

    // ---- read tracking ----

    [Fact]
    public async Task FirstEncounterIsSilentBaseline()
    {
        await Scrobbler().TrackReadingAsync(7, "Ippo", null, 240, 0, CancellationToken.None);

        Assert.Empty(Events());
        using var db = _db.NewContext();
        var state = Assert.Single(db.ReadingStates.ToList());
        Assert.Equal(240, state.MaxChapter);
        Assert.False(state.Finished);
    }

    [Fact]
    public async Task ForwardDeltaEmitsChaptersRead()
    {
        var scrobbler = Scrobbler();
        await scrobbler.TrackReadingAsync(7, "Ippo", null, 240, 0, CancellationToken.None);
        await scrobbler.TrackReadingAsync(7, "Ippo", null, 245.5, 0, CancellationToken.None);

        var e = Assert.Single(Events());
        Assert.Equal(StatsEventType.ChaptersRead, e.Type);
        Assert.Equal(5, e.Value);
        Assert.Equal(7, e.KavitaSeriesId);
    }

    [Fact]
    public async Task BackwardsMovementIsIgnored()
    {
        var scrobbler = Scrobbler();
        await scrobbler.TrackReadingAsync(7, "Ippo", null, 240, 0, CancellationToken.None);
        await scrobbler.TrackReadingAsync(7, "Ippo", null, 100, 0, CancellationToken.None);

        Assert.Empty(Events());
        using var db = _db.NewContext();
        Assert.Equal(240, db.ReadingStates.Single().MaxChapter);
    }

    [Fact]
    public async Task VolumeOnlySeriesEmitsVolumesRead()
    {
        var scrobbler = Scrobbler();
        await scrobbler.TrackReadingAsync(9, "Omnibus", null, 0, 2, CancellationToken.None);
        await scrobbler.TrackReadingAsync(9, "Omnibus", null, 0, 4, CancellationToken.None);

        var e = Assert.Single(Events());
        Assert.Equal(StatsEventType.VolumesRead, e.Type);
        Assert.Equal(2, e.Value);
    }

    [Fact]
    public async Task FinishFiresOnceForCompletedSeries()
    {
        var seriesId = _db.SeedSeries("Done Series", configure: s => s.Status = SeriesStatus.Completed);
        using (var db = _db.NewContext())
        {
            db.Chapters.AddRange(
                new Chapter { SeriesId = seriesId, Number = 11, Language = "en" },
                new Chapter { SeriesId = seriesId, Number = 12, Language = "en" });
            db.SaveChanges();
        }

        var scrobbler = Scrobbler();
        await scrobbler.TrackReadingAsync(7, "Done Series", seriesId, 10, 0, CancellationToken.None);
        await scrobbler.TrackReadingAsync(7, "Done Series", seriesId, 12, 0, CancellationToken.None);
        await scrobbler.TrackReadingAsync(7, "Done Series", seriesId, 12, 0, CancellationToken.None);

        var events = Events();
        Assert.Single(events, e => e.Type == StatsEventType.SeriesFinished);
        Assert.Single(events, e => e.Type == StatsEventType.ChaptersRead && e.Value == 2);
    }

    [Fact]
    public async Task AlreadyFinishedAtBaselineStaysSilent()
    {
        var seriesId = _db.SeedSeries("Old Finish", configure: s => s.Status = SeriesStatus.Completed);
        using (var db = _db.NewContext())
        {
            db.Chapters.Add(new Chapter { SeriesId = seriesId, Number = 5, Language = "en" });
            db.SaveChanges();
        }

        var scrobbler = Scrobbler();
        await scrobbler.TrackReadingAsync(7, "Old Finish", seriesId, 5, 0, CancellationToken.None);
        await scrobbler.TrackReadingAsync(7, "Old Finish", seriesId, 5, 0, CancellationToken.None);

        Assert.Empty(Events());
        using var db2 = _db.NewContext();
        Assert.True(db2.ReadingStates.Single().Finished);
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

    private RewindService Rewind(DateTimeOffset? now = null, bool kavita = false)
    {
        var settings = new FakeAppSettings();
        if (kavita)
        {
            settings.Set(SettingKeys.KavitaUrl, "http://kavita").Set(SettingKeys.KavitaApiKey, "k");
        }

        return new RewindService(_db.NewContext(), settings,
            new StoppedClock(now ?? new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero)));
    }

    private void AddEvent(StatsEventType type, DateTime utc, int value = 1, int? seriesId = null,
        string title = "S", string? payload = null)
    {
        using var db = _db.NewContext();
        db.StatsEvents.Add(new StatsEvent
        {
            Type = type,
            Timestamp = utc,
            SeriesId = seriesId,
            SeriesTitle = title,
            Value = value,
            PayloadJson = payload
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task OffsetShiftsEventsIntoLocalBuckets()
    {
        // UTC+2 (offset −120): 22:30 UTC on 31 March is already 1 April locally.
        AddEvent(StatsEventType.ChaptersRead, new DateTime(2026, 3, 31, 22, 30, 0, DateTimeKind.Utc), 3);
        // And 23:00 UTC on 31 Dec 2025 belongs to 2026 locally.
        AddEvent(StatsEventType.ChaptersRead, new DateTime(2025, 12, 31, 23, 0, 0, DateTimeKind.Utc), 2);

        var stats = await Rewind().StatsAsync(
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), -120, CancellationToken.None);

        Assert.Equal(5, stats.Totals.ChaptersRead);
        Assert.Contains(stats.Timeline, p => p.Bucket == "2026-04" && p.ChaptersRead == 3);
        Assert.Contains(stats.Timeline, p => p.Bucket == "2026-01" && p.ChaptersRead == 2);
    }

    [Fact]
    public async Task ShortRangesUseDayBucketsAndExcludeOutsideEvents()
    {
        AddEvent(StatsEventType.ChaptersRead, new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc), 4);
        AddEvent(StatsEventType.ChaptersRead, new DateTime(2026, 4, 2, 12, 0, 0, DateTimeKind.Utc), 9);

        var stats = await Rewind().StatsAsync(
            new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), 0, CancellationToken.None);

        Assert.Equal(4, stats.Totals.ChaptersRead);
        var point = Assert.Single(stats.Timeline);
        Assert.Equal("2026-03-10", point.Bucket);
    }

    [Fact]
    public async Task RemovedSeriesContributeGenresViaSnapshot()
    {
        AddEvent(StatsEventType.SeriesRemoved, new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            title: "Gone", payload: """{"genres":["Action"],"tags":["Ninja"]}""");

        var stats = await Rewind().StatsAsync(
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), 0, CancellationToken.None);

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
                new ReadingState { KavitaSeriesId = 1, Title = "Stale", MaxChapter = 12, LastProgressAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc) },
                new ReadingState { KavitaSeriesId = 2, Title = "Active", MaxChapter = 30, LastProgressAt = new DateTime(2026, 12, 20, 0, 0, 0, DateTimeKind.Utc) },
                new ReadingState { KavitaSeriesId = 3, Title = "Finished", MaxChapter = 40, Finished = true, LastProgressAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc) });
            db.SaveChanges();
        }

        var stats = await Rewind().StatsAsync(
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), 0, CancellationToken.None);

        var dropped = Assert.Single(stats.Dropped);
        Assert.Equal("Stale", dropped.Title);
        Assert.Equal(1, stats.Totals.SeriesDropped);
    }

    [Fact]
    public async Task ReadTrackingFlagFollowsKavitaConfig()
    {
        var without = await Rewind().StatsAsync(
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), 0, CancellationToken.None);
        var with = await Rewind(kavita: true).StatsAsync(
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), 0, CancellationToken.None);

        Assert.False(without.ReadTrackingAvailable);
        Assert.True(with.ReadTrackingAvailable);
    }
}