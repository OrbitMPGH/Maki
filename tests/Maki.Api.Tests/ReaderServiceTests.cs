using System.IO.Compression;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Parsing;
using Maki.Core.Reading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// The built-in reader's backend: page ordering agreement with the volume scanner, chapter
/// slicing inside a multi-chapter archive, and how completing a chapter feeds the shared
/// <see cref="ReadingState"/> aggregate.
/// </summary>
public sealed class ReaderServiceTests : IDisposable
{
    /// <summary>Whose reading these tests record. Non-zero, or the query filters hide every row.</summary>
    private const int TestUser = 1;

    private readonly TestDb _db = new();
    private readonly ReadingProgressGate _gate = new();
    private readonly ReaderArchiveCache _archives = new(NullLogger<ReaderArchiveCache>.Instance);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maki-reader-tests", Guid.NewGuid().ToString("N"));

    public ReaderServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    private ReaderService Reader()
    {
        // Narrowed the way a request is: ReaderService reads its owner off the scope.
        var context = _db.NewContext(TestUser);
        // The Kavita pusher no-ops without the reader.pushtokavita setting, so a real one with an
        // empty settings store is inert here.
        var scopeFactory = _db.ScopeFactory();
        var pusher = new KavitaProgressPusher(
            scopeFactory,
            new SettingsService(scopeFactory),
            new UserSettingsStoreService(scopeFactory),
            new KavitaUserResolver(scopeFactory, new SettingsService(scopeFactory)),
            null!,
            NullLogger<KavitaProgressPusher>.Instance);
        return new ReaderService(context, _archives,
            new ReadingProgressService(context, _gate, NullLogger<ReadingProgressService>.Instance),
            pusher, NullLogger<ReaderService>.Instance);
    }

    private List<StatsEvent> Events()
    {
        using var db = _db.NewContext();
        return db.StatsEvents.OrderBy(e => e.Id).ToList();
    }

    /// <summary>Writes a CBZ containing the named entries (contents are a 1x1 JPEG-ish stub).</summary>
    private string WriteCbz(string name, params string[] entries)
    {
        var path = Path.Combine(_root, name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            using var stream = archive.CreateEntry(entry).Open();
            stream.WriteByte(0xFF);
        }

        return path;
    }

    // ---- page ordering ----

    [Fact]
    public void PageNamesMatchTheVolumeScannersOrdering()
    {
        // Deliberately out of order in the zip, and with a non-image entry mixed in — both
        // readers must land on the same list or chapter slices point at the wrong pages.
        var path = WriteCbz("order.cbz",
            "Series - c002 - p002.png",
            "ComicInfo.xml",
            "Series - c001 - p010.png",
            "Series - c001 - p002.png",
            "Series - c002 - p001.png");

        var pages = CbzReader.PageNames(path);
        var (total, boundaries) = VolumeChapterScanner.ScanCbzBoundaries(path);

        Assert.Equal(total, pages.Count);
        Assert.Equal(4, pages.Count);
        Assert.Equal("Series - c001 - p002.png", pages[0]);
        Assert.Equal([(1m, 0), (2m, 2)], boundaries);
    }

    // ---- slicing ----

    [Fact]
    public async Task SingleChapterArchiveServesEveryPage()
    {
        var (seriesId, chapters) = SeedFromCbz("solo.cbz",
            ["001.jpg", "002.jpg", "003.jpg"], [(7m, null)]);

        var slice = await Reader().SliceAsync(chapters[7m], CancellationToken.None);

        Assert.NotNull(slice);
        Assert.Equal(0, slice.StartPage);
        Assert.Equal(3, slice.PageCount);
        Assert.Equal(seriesId, slice.Series.Id);
    }

    [Fact]
    public async Task VolumeArchiveSlicesEachChapterToItsOwnPages()
    {
        var (_, chapters) = SeedFromCbz("vol1.cbz",
            [
                "S - c001 - p001.png", "S - c001 - p002.png",
                "S - c002 - p001.png",
                "S - c003 - p001.png", "S - c003 - p002.png", "S - c003 - p003.png"
            ],
            [(1m, 1), (2m, 1), (3m, 1)]);

        var reader = Reader();
        var first = await reader.SliceAsync(chapters[1m], CancellationToken.None);
        var middle = await reader.SliceAsync(chapters[2m], CancellationToken.None);
        var last = await reader.SliceAsync(chapters[3m], CancellationToken.None);

        Assert.Equal((0, 2), (first!.StartPage, first.PageCount));
        Assert.Equal((2, 1), (middle!.StartPage, middle.PageCount));
        Assert.Equal((3, 3), (last!.StartPage, last.PageCount));
    }

    [Fact]
    public async Task ChapterWithoutAMarkerFallsBackToTheWholeArchive()
    {
        // Chapter 9 is linked to the file but no page names mention it — serving everything
        // is recoverable, silently serving nothing is not.
        var (_, chapters) = SeedFromCbz("gap.cbz",
            ["S - c001 - p001.png", "S - c002 - p001.png"], [(1m, 1), (9m, 1)]);

        var slice = await Reader().SliceAsync(chapters[9m], CancellationToken.None);

        Assert.Equal((0, 2), (slice!.StartPage, slice.PageCount));
    }

    [Fact]
    public async Task ChapterWhosePagesAreNotContiguousFallsBackToTheWholeArchive()
    {
        // Chapter 1's pages appear in two runs, either side of chapter 2 — scanlation names that
        // sort this way do exist. The boundary list therefore holds c1 twice, and slicing to the
        // first run alone would silently drop the second. Serving everything is the recoverable
        // failure; skipping pages is not.
        // Names chosen so the sorted page order really is c1, c2, c1 — the marker is not what
        // decides ordering, the whole filename is.
        var (_, chapters) = SeedFromCbz("split.cbz",
            [
                "a - c001 - p001.png",
                "b - c002 - p001.png",
                "c - c001 - p002.png",
            ],
            [(1m, 1), (2m, 1)]);

        var slice = await Reader().SliceAsync(chapters[1m], CancellationToken.None);

        Assert.Equal((0, 3), (slice!.StartPage, slice.PageCount));
    }

    // ---- progress ----

    [Fact]
    public async Task CompletingAChapterAdvancesTheSharedMark()
    {
        var (seriesId, chapters) = SeedFromCbz("read.cbz", ["001.jpg", "002.jpg"], [(4m, null)]);
        var reader = Reader();
        var slice = await reader.SliceAsync(chapters[4m], CancellationToken.None);

        Assert.False(await reader.SaveProgressAsync(slice!, 0, null, ReaderService.TimeReport.None, CancellationToken.None));
        Assert.True(await reader.SaveProgressAsync(slice!, 1, null, ReaderService.TimeReport.None, CancellationToken.None));

        var e = Assert.Single(Events());
        Assert.Equal(StatsEventType.ChaptersRead, e.Type);
        Assert.Equal(4, e.Value);

        using var db = _db.NewContext();
        Assert.Equal(4, db.ReadingStates.Single(r => r.SeriesId == seriesId).MaxChapter);
    }

    [Fact]
    public async Task IndependentWritersForOneChapterShareASingleRow()
    {
        // Each request gets its own scope, so each writer holds a DbContext that has never seen
        // the row. OPDS page streaming is the first caller where several of those overlap for one
        // chapter (a reading app prefetching pages), and the unique index over ChapterId is what
        // keeps them from stacking duplicates. Sequential here on purpose: TestDb shares a single
        // SqliteConnection between contexts, so genuinely parallel writers would be racing the
        // fixture's connection rather than the index. The lost-insert retry in SaveProgressAsync
        // covers the interleaving this cannot reproduce.
        var (_, chapters) = SeedFromCbz("race.cbz",
            ["001.jpg", "002.jpg", "003.jpg", "004.jpg", "005.jpg"], [(1m, null)]);

        foreach (var page in (int[])[0, 2, 1, 3])
        {
            var reader = Reader();
            var slice = await reader.SliceAsync(chapters[1m], CancellationToken.None);
            await reader.SaveProgressAsync(slice!, page, null, ReaderService.TimeReport.None, CancellationToken.None);
        }

        using var db = _db.NewContext();
        var row = Assert.Single(db.ChapterProgress);
        Assert.Equal(chapters[1m], row.ChapterId);
        // The resume position is absolute and free to move backwards, so the last write wins.
        Assert.Equal(3, row.PageIndex);
        Assert.False(row.Completed);
    }

    [Fact]
    public async Task ReachingTheLastPageTwiceOnlyCountsOnce()
    {
        var (_, chapters) = SeedFromCbz("twice.cbz", ["001.jpg", "002.jpg"], [(1m, null)]);
        var reader = Reader();
        var slice = await reader.SliceAsync(chapters[1m], CancellationToken.None);

        await reader.SaveProgressAsync(slice!, 1, null, ReaderService.TimeReport.None, CancellationToken.None);
        await reader.SaveProgressAsync(slice!, 0, null, ReaderService.TimeReport.None, CancellationToken.None);
        await reader.SaveProgressAsync(slice!, 1, null, ReaderService.TimeReport.None, CancellationToken.None);

        Assert.Single(Events());
    }

    [Fact]
    public async Task MarkingUnreadLeavesATombstoneAndNeverLowersTheSharedMark()
    {
        var (seriesId, chapters) = SeedFromCbz("unread.cbz", ["001.jpg"], [(6m, null)]);
        var reader = Reader();
        var slice = await reader.SliceAsync(chapters[6m], CancellationToken.None);
        await reader.SaveProgressAsync(slice!, 0, true, ReaderService.TimeReport.None, CancellationToken.None);

        await reader.ClearProgressAsync(chapters[6m], CancellationToken.None);

        using var db = _db.NewContext();
        // The row survives as a tombstone rather than being deleted: for a Kavita-tracked series the
        // next scrobble tick would otherwise re-mark the chapter read from Kavita's own flag.
        var row = db.ChapterProgress.Single();
        Assert.False(row.Completed);
        Assert.Equal(0, row.PageIndex);
        Assert.NotNull(row.UnreadAt);
        Assert.Equal(6, db.ReadingStates.Single(r => r.SeriesId == seriesId).MaxChapter);
    }

    [Fact]
    public async Task ReadingAgainAfterMarkingUnreadClearsTheTombstone()
    {
        var (_, chapters) = SeedFromCbz("again.cbz", ["001.jpg"], [(6m, null)]);
        var reader = Reader();
        var slice = await reader.SliceAsync(chapters[6m], CancellationToken.None);
        await reader.SaveProgressAsync(slice!, 0, true, ReaderService.TimeReport.None, CancellationToken.None);
        await reader.ClearProgressAsync(chapters[6m], CancellationToken.None);

        await reader.SaveProgressAsync(slice!, 0, true, ReaderService.TimeReport.None, CancellationToken.None);

        using var db = _db.NewContext();
        var row = db.ChapterProgress.Single();
        Assert.True(row.Completed);
        Assert.Null(row.UnreadAt);
    }

    [Fact]
    public async Task VolumeMarkOnlyAdvancesWhenEveryChapterInItIsRead()
    {
        var (seriesId, chapters) = SeedFromCbz("v2.cbz",
            ["S - c010 - p001.png", "S - c011 - p001.png"], [(10m, 2), (11m, 2)]);
        var reader = Reader();

        var first = await reader.SliceAsync(chapters[10m], CancellationToken.None);
        await reader.SaveProgressAsync(first!, 0, true, ReaderService.TimeReport.None, CancellationToken.None);

        using (var db = _db.NewContext())
        {
            Assert.Equal(0, db.ReadingStates.Single(r => r.SeriesId == seriesId).MaxVolume);
        }

        var second = await reader.SliceAsync(chapters[11m], CancellationToken.None);
        await reader.SaveProgressAsync(second!, 0, true, ReaderService.TimeReport.None, CancellationToken.None);

        using (var db = _db.NewContext())
        {
            Assert.Equal(2, db.ReadingStates.Single(r => r.SeriesId == seriesId).MaxVolume);
        }
    }

    [Fact]
    public async Task OneShotRecordsAReadWithoutMovingTheMark()
    {
        var seriesId = SeedSeriesAt();
        var path = WriteCbz("oneshot.cbz", "001.jpg");
        var chapterId = LinkChapters(seriesId, path, [(null, null)]).Single().Id;

        var reader = Reader();
        var slice = await reader.SliceAsync(chapterId, CancellationToken.None);
        await reader.SaveProgressAsync(slice!, 0, true, ReaderService.TimeReport.None, CancellationToken.None);

        var e = Assert.Single(Events());
        Assert.Equal(StatsEventType.ChaptersRead, e.Type);
        Assert.Equal(1, e.Value);

        // No high-water row at all: there is no number to raise the mark to, and the library's
        // read count only ever counts numbered chapters, so the two stay consistent.
        using var db = _db.NewContext();
        Assert.Empty(db.ReadingStates.Where(r => r.SeriesId == seriesId).ToList());
    }

    [Fact]
    public async Task NativeReadPicksTheFurthestRowWhenASeriesHasSeveral()
    {
        // Two Kavita series resolving to one local series is legal, so this series carries two
        // reading states. The lagging row was touched most recently, which is exactly what the
        // Kavita pass does to every row it processes on every tick.
        var (seriesId, chapters) = SeedFromCbz("dupes.cbz", ["001.jpg", "002.jpg"], [(11m, null)]);
        using (var db = _db.NewContext())
        {
            var old = DateTime.UtcNow.AddHours(-1);
            db.ReadingStates.Add(new ReadingState
            {
                UserId = 1,
                KavitaSeriesId = 1, SeriesId = seriesId, Title = "Reader Series",
                MaxChapter = 10, LastProgressAt = old, UpdatedAt = old,
            });
            db.ReadingStates.Add(new ReadingState
            {
                UserId = 1,
                KavitaSeriesId = 2, SeriesId = seriesId, Title = "Reader Series",
                MaxChapter = 5, LastProgressAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
        }

        var reader = Reader();
        var slice = await reader.SliceAsync(chapters[11m], CancellationToken.None);
        Assert.True(await reader.SaveProgressAsync(slice!, 1, null, ReaderService.TimeReport.None, CancellationToken.None));

        // One chapter read, so one chapter counted. Ordering by UpdatedAt would land on the row
        // sitting at 5 and bill Rewind for six chapters.
        var e = Assert.Single(Events());
        Assert.Equal(StatsEventType.ChaptersRead, e.Type);
        Assert.Equal(1, e.Value);

        using var after = _db.NewContext();
        Assert.Equal(11, after.ReadingStates.Single(r => r.KavitaSeriesId == 1).MaxChapter);
        Assert.Equal(5, after.ReadingStates.Single(r => r.KavitaSeriesId == 2).MaxChapter);
    }

    // ---- reading time ----

    [Fact]
    public async Task ReadingTimeBanksUntilItIsWorthAnEvent()
    {
        var (seriesId, chapters) = SeedFromCbz("clock.cbz", ["001.jpg", "002.jpg", "003.jpg"], [(1m, null)]);
        var reader = Reader();
        var slice = await reader.SliceAsync(chapters[1m], CancellationToken.None);

        // Four minutes across two page turns is under the flush threshold: it is on the row, and
        // not yet in the log. One event per page turn would append a row every few seconds.
        await reader.SaveProgressAsync(slice!, 0, null, new(120, false), CancellationToken.None);
        await reader.SaveProgressAsync(slice!, 1, null, new(120, false), CancellationToken.None);

        Assert.Empty(Events());
        using (var db = _db.NewContext())
        {
            var row = db.ChapterProgress.Single();
            Assert.Equal(240, row.ReadSeconds);
            Assert.Equal(0, row.ReportedSeconds);
        }

        await reader.SaveProgressAsync(slice!, 1, null, new(120, false), CancellationToken.None);

        var e = Assert.Single(Events());
        Assert.Equal(StatsEventType.ReadingTime, e.Type);
        Assert.Equal(360, e.Value);
        Assert.Equal(seriesId, e.SeriesId);

        using var after = _db.NewContext();
        Assert.Equal(360, after.ChapterProgress.Single().ReportedSeconds);
    }

    [Fact]
    public async Task FinishingAChapterFlushesTheRemainder()
    {
        // The leftover under the threshold is real time spent, and once the chapter is done no
        // further page turn will ever come along to carry it over.
        var (_, chapters) = SeedFromCbz("last.cbz", ["001.jpg", "002.jpg"], [(1m, null)]);
        var reader = Reader();
        var slice = await reader.SliceAsync(chapters[1m], CancellationToken.None);

        await reader.SaveProgressAsync(slice!, 0, null, new(30, false), CancellationToken.None);
        await reader.SaveProgressAsync(slice!, 1, null, new(45, false), CancellationToken.None);

        var time = Assert.Single(Events(), e => e.Type == StatsEventType.ReadingTime);
        Assert.Equal(75, time.Value);
    }

    [Fact]
    public async Task EndingASittingFlushesAChapterThatWasNeverFinished()
    {
        // Three minutes on a chapter, then the tab closes. Waiting for the threshold would hold
        // that time until the chapter was completed, which for an abandoned one is never.
        var (_, chapters) = SeedFromCbz("abandoned.cbz",
            ["001.jpg", "002.jpg", "003.jpg"], [(1m, null)]);
        var reader = Reader();
        var slice = await reader.SliceAsync(chapters[1m], CancellationToken.None);

        await reader.SaveProgressAsync(slice!, 0, null, new(90, false), CancellationToken.None);
        Assert.Empty(Events());

        await reader.SaveProgressAsync(slice!, 1, null, new(90, true), CancellationToken.None);

        var e = Assert.Single(Events());
        Assert.Equal(StatsEventType.ReadingTime, e.Type);
        Assert.Equal(180, e.Value);
        using var db = _db.NewContext();
        var row = db.ChapterProgress.Single();
        Assert.False(row.Completed);
        Assert.Equal(180, row.ReportedSeconds);
    }

    [Fact]
    public async Task AnEndOfSittingWriteWithNoTimeLogsNothing()
    {
        // A hide/show cycle with nothing banked must not append an empty row.
        var (_, chapters) = SeedFromCbz("nosit.cbz", ["001.jpg", "002.jpg"], [(1m, null)]);
        var reader = Reader();
        var slice = await reader.SliceAsync(chapters[1m], CancellationToken.None);

        await reader.SaveProgressAsync(slice!, 0, null, new(0, true), CancellationToken.None);

        Assert.Empty(Events());
    }

    [Fact]
    public async Task ASingleReportCannotBillMoreThanTheCap()
    {
        var (_, chapters) = SeedFromCbz("cap.cbz", ["001.jpg", "002.jpg"], [(1m, null)]);
        var reader = Reader();
        var slice = await reader.SliceAsync(chapters[1m], CancellationToken.None);

        // A client claiming a day of reading in one write gets the cap, not the day.
        await reader.SaveProgressAsync(slice!, 0, null, new(86_400, false), CancellationToken.None);

        var e = Assert.Single(Events());
        Assert.Equal(900, e.Value);
    }

    [Fact]
    public async Task ReadingTimeIsNotCountedTwiceAcrossFlushes()
    {
        var (_, chapters) = SeedFromCbz("twiceclock.cbz", ["001.jpg", "002.jpg", "003.jpg"], [(1m, null)]);
        var reader = Reader();
        var slice = await reader.SliceAsync(chapters[1m], CancellationToken.None);

        await reader.SaveProgressAsync(slice!, 0, null, new(400, false), CancellationToken.None);
        await reader.SaveProgressAsync(slice!, 1, null, new(400, false), CancellationToken.None);
        await reader.SaveProgressAsync(slice!, 2, null, ReaderService.TimeReport.None, CancellationToken.None);

        var total = Events().Where(e => e.Type == StatsEventType.ReadingTime).Sum(e => e.Value);
        Assert.Equal(800, total);
        using var db = _db.NewContext();
        Assert.Equal(800, db.ChapterProgress.Single().ReadSeconds);
    }

    [Fact]
    public async Task AFullyIncognitoSeriesLogsNoTimeAndBanksNoBacklog()
    {
        var (seriesId, chapters) = SeedFromCbz("hidden.cbz", ["001.jpg", "002.jpg"], [(1m, null)]);
        using (var db = _db.NewContext())
        {
            db.Series.Single(s => s.Id == seriesId).Incognito = IncognitoMode.Full;
            db.SaveChanges();
        }

        var reader = Reader();
        var slice = await reader.SliceAsync(chapters[1m], CancellationToken.None);
        await reader.SaveProgressAsync(slice!, 0, null, new(400, false), CancellationToken.None);

        Assert.Empty(Events());
        // Reported keeps pace with the total, so taking the series back out of incognito can't
        // dump the hidden backlog into Rewind on the next page turn.
        using var db2 = _db.NewContext();
        var row = db2.ChapterProgress.Single();
        Assert.Equal(400, row.ReadSeconds);
        Assert.Equal(400, row.ReportedSeconds);
    }

    [Fact]
    public async Task MarkingUnreadKeepsTheTimeAlreadySpentAndLogged()
    {
        var (_, chapters) = SeedFromCbz("keeptime.cbz", ["001.jpg", "002.jpg"], [(1m, null)]);
        var reader = Reader();
        var slice = await reader.SliceAsync(chapters[1m], CancellationToken.None);
        await reader.SaveProgressAsync(slice!, 0, true, new(200, false), CancellationToken.None);

        await reader.ClearProgressAsync(chapters[1m], CancellationToken.None);
        await reader.SaveProgressAsync(slice!, 0, null, new(50, false), CancellationToken.None);

        // 200 logged on completion, 50 banked after: never a negative delta, never a re-emit.
        var total = Events().Where(e => e.Type == StatsEventType.ReadingTime).Sum(e => e.Value);
        Assert.Equal(200, total);
        using var db = _db.NewContext();
        var row = db.ChapterProgress.Single();
        Assert.Equal(250, row.ReadSeconds);
        Assert.Equal(200, row.ReportedSeconds);
    }

    [Fact]
    public async Task NeighboursSkipChaptersWithoutAFile()
    {
        var (seriesId, chapters) = SeedFromCbz("n.cbz", ["001.jpg"], [(1m, null)]);
        var path = WriteCbz("n3.cbz", "001.jpg");
        var third = LinkChapters(seriesId, path, [(3m, null)]).Single().Id;
        using (var db = _db.NewContext())
        {
            // Chapter 2 is known but not downloaded — the reader must jump straight to 3.
            db.Chapters.Add(new Chapter { SeriesId = seriesId, Number = 2, Language = "en" });
            db.SaveChanges();
        }

        var reader = Reader();
        var slice = await reader.SliceAsync(chapters[1m], CancellationToken.None);
        var (previous, next) = await reader.NeighboursAsync(slice!.Chapter, CancellationToken.None);

        Assert.Null(previous);
        Assert.Equal(third, next);
    }

    // ---- watched ----

    /// <summary>
    /// The whole point of the state: it stops the chapters counting as unread without putting a
    /// day's worth of invented reading into Rewind.
    /// </summary>
    [Fact]
    public async Task MarkingWatchedCompletesTheChaptersWithoutEmittingAnything()
    {
        var (seriesId, chapters) = SeedFromCbz("watched.cbz", ["001.jpg", "002.jpg"],
            [(1m, null), (2m, null)]);
        var reader = Reader();

        var updated = await reader.MarkWatchedAsync([chapters[1m], chapters[2m]], CancellationToken.None);

        Assert.Equal(2, updated);
        using var db = _db.NewContext(TestUser);
        var rows = db.ChapterProgress.Where(p => p.SeriesId == seriesId).ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.True(r.Completed));
        Assert.All(rows, r => Assert.True(r.Watched));
        Assert.All(rows, r => Assert.Null(r.UnreadAt));
        Assert.Empty(Events());
    }

    /// <summary>
    /// The mark still has to rise, or the first chapter genuinely read after a watched season would
    /// emit a delta of the whole season. It rises silently, the way the Kavita import raises it.
    /// </summary>
    [Fact]
    public async Task MarkingWatchedRaisesTheHighWaterMarkSilently()
    {
        var (seriesId, chapters) = SeedFromCbz("watchedmark.cbz", ["001.jpg"],
            [(1m, null), (2m, null), (3m, null)]);
        var reader = Reader();

        await reader.MarkWatchedAsync([chapters[1m], chapters[2m]], CancellationToken.None);

        using (var db = _db.NewContext(TestUser))
        {
            Assert.Equal(2, db.ReadingStates.Single(r => r.SeriesId == seriesId).MaxChapter);
        }

        Assert.Empty(Events());

        // And the next genuine read is a delta of one, not of three.
        var slice = await reader.SliceAsync(chapters[3m], CancellationToken.None);
        await reader.SaveProgressAsync(slice!, 0, true, ReaderService.TimeReport.None, CancellationToken.None);

        var read = Events().Single(e => e.Type == StatsEventType.ChaptersRead);
        Assert.Equal(1, read.Value);
    }

    /// <summary>
    /// Reading a watched chapter is what un-watches it. The sticky <c>Completed</c> flag is already
    /// set, so the completion transition has to key off <c>Watched</c> or the read would be
    /// swallowed as a re-read and record nothing.
    /// </summary>
    [Fact]
    public async Task ReadingAWatchedChapterClearsTheFlagAndCounts()
    {
        var (_, chapters) = SeedFromCbz("upgrade.cbz", ["001.jpg", "002.jpg"], [(1m, null)]);
        var reader = Reader();
        await reader.MarkWatchedAsync([chapters[1m]], CancellationToken.None);

        var slice = await reader.SliceAsync(chapters[1m], CancellationToken.None);
        var completed = await reader.SaveProgressAsync(slice!, 1, null, new(120, true), CancellationToken.None);

        Assert.True(completed);
        using var db = _db.NewContext(TestUser);
        var row = db.ChapterProgress.Single(p => p.ChapterId == chapters[1m]);
        Assert.True(row.Completed);
        Assert.False(row.Watched);
        Assert.Contains(Events(), e => e.Type == StatsEventType.ReadingTime && e.Value == 120);
    }

    /// <summary>A watched chapter re-read a second time is a plain re-read and emits nothing more.</summary>
    [Fact]
    public async Task ReadingAnUnwatchedChapterAgainIsStillIdempotent()
    {
        var (_, chapters) = SeedFromCbz("upgradetwice.cbz", ["001.jpg"], [(1m, null)]);
        var reader = Reader();
        await reader.MarkWatchedAsync([chapters[1m]], CancellationToken.None);

        var slice = await reader.SliceAsync(chapters[1m], CancellationToken.None);
        Assert.True(await reader.SaveProgressAsync(slice!, 0, true, ReaderService.TimeReport.None, CancellationToken.None));
        Assert.False(await reader.SaveProgressAsync(slice!, 0, true, ReaderService.TimeReport.None, CancellationToken.None));
    }

    /// <summary>Marking unread has to clear both flags, or the row comes back unread-but-watched.</summary>
    [Fact]
    public async Task MarkingAWatchedChapterUnreadClearsBothFlags()
    {
        var (_, chapters) = SeedFromCbz("unwatch.cbz", ["001.jpg"], [(1m, null)]);
        var reader = Reader();
        await reader.MarkWatchedAsync([chapters[1m]], CancellationToken.None);

        await reader.ClearProgressAsync(chapters[1m], CancellationToken.None);

        using var db = _db.NewContext(TestUser);
        var row = db.ChapterProgress.Single(p => p.ChapterId == chapters[1m]);
        Assert.False(row.Completed);
        Assert.False(row.Watched);
        Assert.NotNull(row.UnreadAt);
    }

    /// <summary>
    /// A chapter that was genuinely read is left alone: re-flagging it would move a read that has
    /// already been counted in Rewind out of the progression counts.
    /// </summary>
    [Fact]
    public async Task MarkingWatchedLeavesAlreadyReadChaptersAlone()
    {
        var (_, chapters) = SeedFromCbz("keepread.cbz", ["001.jpg"], [(1m, null)]);
        var reader = Reader();
        var slice = await reader.SliceAsync(chapters[1m], CancellationToken.None);
        await reader.SaveProgressAsync(slice!, 0, true, ReaderService.TimeReport.None, CancellationToken.None);

        var updated = await reader.MarkWatchedAsync([chapters[1m]], CancellationToken.None);

        Assert.Equal(0, updated);
        using var db = _db.NewContext(TestUser);
        Assert.False(db.ChapterProgress.Single(p => p.ChapterId == chapters[1m]).Watched);
    }

    /// <summary>
    /// The UI count includes watched chapters, so they stop reading as unread; the progression
    /// count excludes them, so a watched season hands out no "fully read" achievement.
    /// </summary>
    [Fact]
    public async Task ReadCountsIncludesWatchedForTheUiAndExcludesItForProgression()
    {
        var (seriesId, chapters) = SeedFromCbz("counts.cbz", ["001.jpg"], [(1m, null), (2m, null)]);
        var reader = Reader();
        await reader.MarkWatchedAsync([chapters[1m]], CancellationToken.None);
        var slice = await reader.SliceAsync(chapters[2m], CancellationToken.None);
        await reader.SaveProgressAsync(slice!, 0, true, ReaderService.TimeReport.None, CancellationToken.None);

        using var db = _db.NewContext(TestUser);
        Assert.Equal(2, ReadCounts.Read(db).Count(p => p.SeriesId == seriesId));
        Assert.Equal(1, ReadCounts.ReadFor(db, TestUser).Count(p => p.SeriesId == seriesId));
    }

    // ---- seeding ----

    private int SeedSeriesAt()
    {
        var seriesId = _db.SeedSeries("Reader Series");
        using var db = _db.NewContext();
        var series = db.Series.Include(s => s.RootFolder).First(s => s.Id == seriesId);
        db.RootFolders.First(r => r.Id == series.RootFolderId).Path = _root;
        series.FolderName = "";
        db.SaveChanges();
        return seriesId;
    }

    private (int SeriesId, Dictionary<decimal, int> Chapters) SeedFromCbz(
        string cbzName, string[] entries, (decimal? Number, int? Volume)[] chapters)
    {
        var seriesId = SeedSeriesAt();
        var path = WriteCbz(cbzName, entries);
        var ids = LinkChapters(seriesId, path, chapters);
        return (seriesId, ids.Where(r => r.Number is not null)
            .ToDictionary(r => r.Number!.Value, r => r.Id));
    }

    private List<(decimal? Number, int Id)> LinkChapters(
        int seriesId, string cbzPath, (decimal? Number, int? Volume)[] chapters)
    {
        using var db = _db.NewContext();
        var file = new ChapterFile
        {
            SeriesId = seriesId,
            RelativePath = Path.GetFileName(cbzPath),
            Size = new FileInfo(cbzPath).Length,
            SourceName = "Test",
            DateAdded = DateTime.UtcNow
        };
        db.ChapterFiles.Add(file);
        db.SaveChanges();

        var rows = chapters.Select(c => new Chapter
        {
            SeriesId = seriesId,
            Number = c.Number,
            Volume = c.Volume,
            IsOneShot = c.Number is null,
            Language = "en",
            ChapterFileId = file.Id
        }).ToList();
        db.Chapters.AddRange(rows);
        db.SaveChanges();

        return rows.Select(r => (r.Number, r.Id)).ToList();
    }
}
