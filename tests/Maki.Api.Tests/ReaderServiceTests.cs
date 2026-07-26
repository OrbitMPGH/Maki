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
        var context = _db.NewContext();
        // The Kavita pusher no-ops without the reader.pushtokavita setting, so a real one with an
        // empty settings store is inert here.
        var scopeFactory = _db.ScopeFactory();
        var pusher = new KavitaProgressPusher(scopeFactory, new SettingsService(scopeFactory), null!,
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

        Assert.False(await reader.SaveProgressAsync(slice!, 0, null, CancellationToken.None));
        Assert.True(await reader.SaveProgressAsync(slice!, 1, null, CancellationToken.None));

        var e = Assert.Single(Events());
        Assert.Equal(StatsEventType.ChaptersRead, e.Type);
        Assert.Equal(4, e.Value);

        using var db = _db.NewContext();
        Assert.Equal(4, db.ReadingStates.Single(r => r.SeriesId == seriesId).MaxChapter);
    }

    [Fact]
    public async Task ReachingTheLastPageTwiceOnlyCountsOnce()
    {
        var (_, chapters) = SeedFromCbz("twice.cbz", ["001.jpg", "002.jpg"], [(1m, null)]);
        var reader = Reader();
        var slice = await reader.SliceAsync(chapters[1m], CancellationToken.None);

        await reader.SaveProgressAsync(slice!, 1, null, CancellationToken.None);
        await reader.SaveProgressAsync(slice!, 0, null, CancellationToken.None);
        await reader.SaveProgressAsync(slice!, 1, null, CancellationToken.None);

        Assert.Single(Events());
    }

    [Fact]
    public async Task MarkingUnreadLeavesATombstoneAndNeverLowersTheSharedMark()
    {
        var (seriesId, chapters) = SeedFromCbz("unread.cbz", ["001.jpg"], [(6m, null)]);
        var reader = Reader();
        var slice = await reader.SliceAsync(chapters[6m], CancellationToken.None);
        await reader.SaveProgressAsync(slice!, 0, true, CancellationToken.None);

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
        await reader.SaveProgressAsync(slice!, 0, true, CancellationToken.None);
        await reader.ClearProgressAsync(chapters[6m], CancellationToken.None);

        await reader.SaveProgressAsync(slice!, 0, true, CancellationToken.None);

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
        await reader.SaveProgressAsync(first!, 0, true, CancellationToken.None);

        using (var db = _db.NewContext())
        {
            Assert.Equal(0, db.ReadingStates.Single(r => r.SeriesId == seriesId).MaxVolume);
        }

        var second = await reader.SliceAsync(chapters[11m], CancellationToken.None);
        await reader.SaveProgressAsync(second!, 0, true, CancellationToken.None);

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
        await reader.SaveProgressAsync(slice!, 0, true, CancellationToken.None);

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
                KavitaSeriesId = 1, SeriesId = seriesId, Title = "Reader Series",
                MaxChapter = 10, LastProgressAt = old, UpdatedAt = old,
            });
            db.ReadingStates.Add(new ReadingState
            {
                KavitaSeriesId = 2, SeriesId = seriesId, Title = "Reader Series",
                MaxChapter = 5, LastProgressAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
        }

        var reader = Reader();
        var slice = await reader.SliceAsync(chapters[11m], CancellationToken.None);
        Assert.True(await reader.SaveProgressAsync(slice!, 1, null, CancellationToken.None));

        // One chapter read, so one chapter counted. Ordering by UpdatedAt would land on the row
        // sitting at 5 and bill Rewind for six chapters.
        var e = Assert.Single(Events());
        Assert.Equal(StatsEventType.ChaptersRead, e.Type);
        Assert.Equal(1, e.Value);

        using var after = _db.NewContext();
        Assert.Equal(11, after.ReadingStates.Single(r => r.KavitaSeriesId == 1).MaxChapter);
        Assert.Equal(5, after.ReadingStates.Single(r => r.KavitaSeriesId == 2).MaxChapter);
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
