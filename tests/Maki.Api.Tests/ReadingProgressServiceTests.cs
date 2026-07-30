using Maki.Api.Services;
using Maki.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// The single writer for <c>ReadingState</c>: the forward-only merge, the silent baselines, native-row
/// adoption, and the stable pick when a series has several rows.
/// <para>
/// This is the most invariant-dense code in the repository and it had no direct coverage — every rule
/// here exists because breaking it double-counts a user's reading into Rewind or reports chapters read
/// that were never opened, and both are silent. Written deliberately <em>before</em> the per-user data
/// split adds a <c>UserId</c> to this table, so that change has a real regression net under it rather
/// than one written to match whatever it happens to do afterwards.
/// </para>
/// </summary>
public sealed class ReadingProgressServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private ReadingProgressService NewService() =>
        new(_db.NewContext(), new ReadingProgressGate(), NullLogger<ReadingProgressService>.Instance);

    private List<StatsEvent> Events() =>
        _db.NewContext().StatsEvents.OrderBy(e => e.Id).ToList();

    private List<ReadingState> States() =>
        _db.NewContext().ReadingStates.OrderBy(r => r.Id).ToList();

    private void SeedChapters(int seriesId, params decimal[] numbers)
    {
        using var db = _db.NewContext();
        foreach (var number in numbers)
        {
            db.Chapters.Add(new Chapter { SeriesId = seriesId, Number = number, Language = "en" });
        }
        db.SaveChanges();
    }

    private void SeedState(int? kavitaSeriesId, int? seriesId, double maxChapter, double maxVolume = 0)
    {
        using var db = _db.NewContext();
        db.ReadingStates.Add(new ReadingState
        {
            KavitaSeriesId = kavitaSeriesId,
            SeriesId = seriesId,
            Title = "Seeded",
            MaxChapter = maxChapter,
            MaxVolume = maxVolume,
            LastProgressAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });
        db.SaveChanges();
    }

    // ---- the Kavita path ----

    [Fact]
    public async Task AFirstKavitaSightingIsASilentBaseline()
    {
        var seriesId = _db.SeedSeries();

        await NewService().TrackKavitaAsync(42, "Berserk", seriesId, 120, 12, default);

        // Everything read before Maki started watching must not land in today's stats. Never "fix"
        // this into emitting: it would dump a whole back catalogue onto one day of Rewind.
        Assert.Empty(Events());
        var state = Assert.Single(States());
        Assert.Equal(120, state.MaxChapter);
        Assert.Equal(42, state.KavitaSeriesId);
    }

    [Fact]
    public async Task ASubsequentKavitaAdvanceEmitsTheDelta()
    {
        var seriesId = _db.SeedSeries();
        var service = NewService();

        await service.TrackKavitaAsync(42, "Berserk", seriesId, 120, 12, default);
        await NewService().TrackKavitaAsync(42, "Berserk", seriesId, 123, 12, default);

        var read = Assert.Single(Events());
        Assert.Equal(StatsEventType.ChaptersRead, read.Type);
        Assert.Equal(3, read.Value);
    }

    [Fact]
    public async Task BackwardsMovementIsIgnored()
    {
        var seriesId = _db.SeedSeries();
        SeedState(42, seriesId, maxChapter: 120);

        await NewService().TrackKavitaAsync(42, "Berserk", seriesId, 100, 0, default);

        // A Kavita rescan, a boundary refinement or a mark-unread can all move the number backwards.
        // The mark is a high-water mark: it must neither drop nor emit a negative delta.
        Assert.Empty(Events());
        Assert.Equal(120, Assert.Single(States()).MaxChapter);
    }

    [Fact]
    public async Task RereportingTheSameProgressYieldsNoDelta()
    {
        var seriesId = _db.SeedSeries();
        SeedState(42, seriesId, maxChapter: 120);

        await NewService().TrackKavitaAsync(42, "Berserk", seriesId, 120, 0, default);

        // The merge into one row is exactly what makes double-counting impossible.
        Assert.Empty(Events());
    }

    [Fact]
    public async Task DeltasAreCountedOnWholeChapters()
    {
        var seriesId = _db.SeedSeries();
        SeedState(42, seriesId, maxChapter: 10);

        await NewService().TrackKavitaAsync(42, "Berserk", seriesId, 10.5, 0, default);

        // Reading half of chapter 11 is not a chapter read; the mark still moves.
        Assert.Empty(Events());
        Assert.Equal(10.5, Assert.Single(States()).MaxChapter);
    }

    // ---- adoption of a native row ----

    [Fact]
    public async Task AFirstKavitaSightingAdoptsTheNativeRowInsteadOfInsertingASecond()
    {
        var seriesId = _db.SeedSeries();
        await NewService().TrackNativeAsync(seriesId, "Berserk", 5, 0, default);

        await NewService().TrackKavitaAsync(42, "Berserk", seriesId, 3, 0, default);

        var state = Assert.Single(States());
        Assert.Equal(42, state.KavitaSeriesId);
        Assert.Equal(seriesId, state.SeriesId);
    }

    [Fact]
    public async Task AdoptionTakesTheFurthestOfBothMarks()
    {
        var seriesId = _db.SeedSeries();
        SeedState(null, seriesId, maxChapter: 5, maxVolume: 1);

        await NewService().TrackKavitaAsync(42, "Berserk", seriesId, 3, 0, default);

        // Kavita is behind the reader here; adopting its lower numbers would lose progress and then
        // re-emit those chapters as new reading on the next tick.
        var state = Assert.Single(States());
        Assert.Equal(5, state.MaxChapter);
        Assert.Equal(1, state.MaxVolume);
    }

    [Fact]
    public async Task AdoptionEmitsNothing()
    {
        var seriesId = _db.SeedSeries();
        SeedState(null, seriesId, maxChapter: 5);

        await NewService().TrackKavitaAsync(42, "Berserk", seriesId, 20, 0, default);

        // Adoption is a first sighting like any other: the history Kavita carries predates Maki
        // watching this series through Kavita, so it is not today's reading.
        Assert.Empty(Events());
        Assert.Equal(20, Assert.Single(States()).MaxChapter);
    }

    [Fact]
    public async Task AdoptionReturnsTheMergedMarksNotKavitasRawNumbers()
    {
        var seriesId = _db.SeedSeries();
        SeedState(null, seriesId, maxChapter: 5, maxVolume: 2);

        var marks = await NewService().TrackKavitaAsync(42, "Berserk", seriesId, 3, 1, default);

        // Load-bearing: the scrobble pass pushes what this returns. Returning Kavita's raw 3 would
        // mean reading on in Maki past what Kavita knows never reaches a tracker, because
        // ScrobblePlanner is forward-only and would never push again.
        Assert.Equal(5, marks.MaxChapter);
        Assert.Equal(2, marks.MaxVolume);
    }

    [Fact]
    public async Task AnOngoingKavitaTickAlsoReturnsMergedMarks()
    {
        var seriesId = _db.SeedSeries();
        SeedState(42, seriesId, maxChapter: 10);

        var marks = await NewService().TrackKavitaAsync(42, "Berserk", seriesId, 4, 0, default);

        Assert.Equal(10, marks.MaxChapter);
    }

    // ---- the native path ----

    [Fact]
    public async Task AFirstNativeReadEmitsImmediately()
    {
        var seriesId = _db.SeedSeries();

        await NewService().TrackNativeAsync(seriesId, "Berserk", 1, 0, default);

        // No baseline on this path, unlike Kavita's: nothing here predates Maki, because the reading
        // demonstrably just happened in it.
        var read = Assert.Single(Events());
        Assert.Equal(StatsEventType.ChaptersRead, read.Type);
        Assert.Equal(1, read.Value);
    }

    [Fact]
    public async Task NativeReadPicksTheFurthestRowWhenASeriesHasSeveral()
    {
        var seriesId = _db.SeedSeries();
        // Two Kavita series resolving to one local series is legal, so duplicate rows are legal.
        SeedState(101, seriesId, maxChapter: 3);
        SeedState(102, seriesId, maxChapter: 9);

        await NewService().TrackNativeAsync(seriesId, "Berserk", 10, 0, default);

        // Picking by MaxChapter and not UpdatedAt is the whole point: the Kavita pass restamps
        // UpdatedAt on every row it touches each tick, so an UpdatedAt pick would flip between calls
        // and measure this delta against the lagging row — counting chapters 4..10 into Rewind twice.
        var read = Assert.Single(Events());
        Assert.Equal(1, read.Value);

        var advanced = States().Single(r => r.KavitaSeriesId == 102);
        Assert.Equal(10, advanced.MaxChapter);
        Assert.Equal(3, States().Single(r => r.KavitaSeriesId == 101).MaxChapter);
    }

    [Fact]
    public async Task DuplicateRowsAreNotCollapsedByAWrite()
    {
        var seriesId = _db.SeedSeries();
        SeedState(101, seriesId, maxChapter: 3);
        SeedState(102, seriesId, maxChapter: 9);

        await NewService().TrackNativeAsync(seriesId, "Berserk", 10, 0, default);

        // A plain unique index on SeriesId would have thrown here — which is why the real index is
        // filtered to native rows only.
        Assert.Equal(2, States().Count(r => r.SeriesId == seriesId));
    }

    // ---- the silent import ----

    [Fact]
    public async Task ImportAdvancesTheMarkWithoutEmitting()
    {
        var seriesId = _db.SeedSeries();

        await NewService().ImportSilentAsync(seriesId, null, "Berserk", 300, 30, default);

        // Kavita doesn't say *when* those chapters were read, so dating them today would dump the
        // whole back catalogue onto one day of the year in review.
        Assert.Empty(Events());
        // Advancing is still mandatory: it is the baseline the next genuine read is measured against,
        // so skipping it would make the first post-import chapter emit a delta of hundreds.
        Assert.Equal(300, Assert.Single(States()).MaxChapter);
    }

    [Fact]
    public async Task AReadAfterAnImportEmitsOnlyItsOwnDelta()
    {
        var seriesId = _db.SeedSeries();
        await NewService().ImportSilentAsync(seriesId, null, "Berserk", 300, 0, default);

        await NewService().TrackNativeAsync(seriesId, "Berserk", 301, 0, default);

        var read = Assert.Single(Events());
        Assert.Equal(1, read.Value);
    }

    [Fact]
    public async Task ImportDoesNotTouchLastProgressAt()
    {
        var seriesId = _db.SeedSeries();
        SeedState(null, seriesId, maxChapter: 5);
        var before = States().Single().LastProgressAt;

        await NewService().ImportSilentAsync(seriesId, null, "Berserk", 300, 0, default);

        // That field is what Rewind's "dropped series" staleness is measured from, and this reading
        // did not happen now.
        Assert.Equal(before, States().Single().LastProgressAt);
    }

    [Fact]
    public async Task ImportNeverLowersTheMark()
    {
        var seriesId = _db.SeedSeries();
        SeedState(null, seriesId, maxChapter: 300);

        await NewService().ImportSilentAsync(seriesId, null, "Berserk", 10, 0, default);

        Assert.Equal(300, Assert.Single(States()).MaxChapter);
    }

    // ---- one-shots ----

    [Fact]
    public async Task AnUnnumberedReadEmitsAnEventButNoMark()
    {
        var seriesId = _db.SeedSeries();

        await NewService().RecordUnnumberedReadAsync(seriesId, "A One-Shot", default);

        var read = Assert.Single(Events());
        Assert.Equal(StatsEventType.ChaptersRead, read.Type);
        Assert.Equal(1, read.Value);
        // There is no number to raise the mark to, and inventing one would mis-count
        // SmartDownloadJob's unread and falsely fill the library's progress ring.
        Assert.Empty(States());
    }

    [Fact]
    public async Task AnUnnumberedReadLeavesAnExistingMarkAlone()
    {
        var seriesId = _db.SeedSeries();
        SeedState(42, seriesId, maxChapter: 7);

        await NewService().RecordUnnumberedReadAsync(seriesId, "A One-Shot", default);

        Assert.Equal(7, Assert.Single(States()).MaxChapter);
        // Carries the Kavita id off the picked row so the event still aggregates with the series.
        Assert.Equal(42, Assert.Single(Events()).KavitaSeriesId);
    }

    // ---- volume-only series ----

    [Fact]
    public async Task AVolumeOnlySeriesCountsVolumes()
    {
        var seriesId = _db.SeedSeries();
        SeedState(42, seriesId, maxChapter: 0, maxVolume: 1);

        await NewService().TrackKavitaAsync(42, "Volume Only", seriesId, 0, 3, default);

        var read = Assert.Single(Events());
        Assert.Equal(StatsEventType.VolumesRead, read.Type);
        Assert.Equal(2, read.Value);
    }

    [Fact]
    public async Task ASeriesWithChapterNumbersDoesNotAlsoCountVolumes()
    {
        var seriesId = _db.SeedSeries();
        SeedState(42, seriesId, maxChapter: 10, maxVolume: 1);

        await NewService().TrackKavitaAsync(42, "Berserk", seriesId, 20, 2, default);

        // Chapters and volumes describe the same reading; emitting both would double-count it.
        var read = Assert.Single(Events());
        Assert.Equal(StatsEventType.ChaptersRead, read.Type);
        Assert.Equal(10, read.Value);
    }

    // ---- a finished series ----

    [Fact]
    public async Task ReachingTheEndOfACompletedSeriesEmitsSeriesFinishedOnce()
    {
        // "Finished" is measured against the highest chapter actually held, not against the metadata
        // provider's reported total — that total lags the sources on active titles, and sources carry
        // specials the provider never counts.
        var seriesId = _db.SeedSeries(configure: s => s.Status = SeriesStatus.Completed);
        SeedChapters(seriesId, 1, 5, 10);
        SeedState(42, seriesId, maxChapter: 5);

        await NewService().TrackKavitaAsync(42, "Berserk", seriesId, 10, 0, default);
        await NewService().TrackKavitaAsync(42, "Berserk", seriesId, 10, 0, default);

        Assert.Single(Events(), e => e.Type == StatsEventType.SeriesFinished);
        Assert.True(States().Single().Finished);
    }
}
