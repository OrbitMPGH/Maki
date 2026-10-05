using System.Net;
using System.Text;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Kavita;
using Maki.Core.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// Recording read state observed in Kavita: which Kavita chapters count as read, and which local
/// chapters get marked. Shared by the one-off import and the recurring scrobble tick, so these are
/// the rules for both. The silent-merge half (that none of this reaches Rewind) is covered in
/// <see cref="RewindStatsTests"/>.
/// </summary>
public sealed class KavitaReadImportTests : IDisposable
{
    /// <summary>
    /// The user every read in these tests belongs to. Reading is per-user now; the specific id is
    /// arbitrary, but it has to be non-zero — a row owned by user 0 is one the query filters hide
    /// from everybody, which is the failure mode <c>IUserOwned</c> exists to make loud.
    /// </summary>
    private const int TestUser = 1;

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private ExternalReadSyncService Service() => new(_db.ScopeFactory());

    private static KavitaProgress.KavitaChapterDto Chapter(
        double number, int pages, int pagesRead, bool special = false) =>
        new(number, number, pages, pagesRead, special);

    private static List<KavitaProgress.KavitaVolumeDto> Volume(params KavitaProgress.KavitaChapterDto[] chapters) =>
        [new(1, 1, chapters.Sum(c => c.Pages), chapters.Sum(c => c.PagesRead), chapters.ToList())];

    /// <summary>Kavita reporting exactly these chapter numbers as fully read.</summary>
    private static KavitaProgress.SeriesProgress Read(params decimal[] numbers) =>
        KavitaProgress.Compute(Volume(numbers.Select(n => Chapter((double)n, 20, 20)).ToArray()));

    // ---- which Kavita chapters count as read ----

    [Fact]
    public void OnlyFullyReadChaptersCount()
    {
        var progress = KavitaProgress.Compute(Volume(
            Chapter(1, 20, 20),
            Chapter(2, 20, 19),
            Chapter(3, 20, 0)));

        Assert.Equal([(1m, 1m)], progress.Chapters);
    }

    [Fact]
    public void SpecialsAndSentinelNumbersAreSkipped()
    {
        // Kavita tags uncounted entries with huge sentinel numbers; matching one against a real
        // local chapter number would mark the wrong thing read.
        var progress = KavitaProgress.Compute(Volume(
            Chapter(5, 10, 10),
            Chapter(6, 10, 10, special: true),
            Chapter(100_000, 10, 10)));

        Assert.Equal([(5m, 5m)], progress.Chapters);
    }

    [Fact]
    public void ZeroPageChaptersAreNotRead()
    {
        // pagesRead >= pages is trivially true at 0/0 — that's an empty entry, not a read one.
        Assert.Empty(KavitaProgress.Compute(Volume(Chapter(1, 0, 0))).Chapters);
    }

    // ---- which local chapters get marked ----

    [Fact]
    public async Task MarksOnlyDownloadedChaptersWithMatchingNumbers()
    {
        var seriesId = Seed(
            (1m, true),
            (2m, true),
            (3m, false), // known but not downloaded — nothing to read
            (4m, true)); // not in Kavita's read set

        var marked = await Service().MarkAsync(TestUser, seriesId, Read(1m, 2m, 3m), CancellationToken.None);

        Assert.Equal(2, marked);
        using var db = _db.NewContext();
        var rows = db.ChapterProgress.ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.True(r.Completed));
        // Flagged as read elsewhere: the chapter table shows these differently from a read the
        // built-in reader observed, and no page position is known for them.
        Assert.All(rows, r => Assert.True(r.External));
    }

    [Fact]
    public async Task ImportIsIdempotent()
    {
        var seriesId = Seed((1m, true), (2m, true));

        Assert.Equal(2, await Service().MarkAsync(TestUser, seriesId, Read(1m, 2m), CancellationToken.None));
        Assert.Equal(0, await Service().MarkAsync(TestUser, seriesId, Read(1m, 2m), CancellationToken.None));

        using var db = _db.NewContext();
        Assert.Equal(2, db.ChapterProgress.Count());
    }

    [Fact]
    public async Task ImportCompletesAnInProgressChapterButKeepsItsPosition()
    {
        var seriesId = Seed((1m, true));
        using (var db = _db.NewContext())
        {
            db.ChapterProgress.Add(new ChapterProgress
            {
                UserId = 1,
                SeriesId = seriesId,
                ChapterId = db.Chapters.Single().Id,
                PageIndex = 4,
                PageCount = 20,
                Completed = false,
                StartedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
        }

        await Service().MarkAsync(TestUser, seriesId, Read(1m), CancellationToken.None);

        using var after = _db.NewContext();
        var row = after.ChapterProgress.Single();
        Assert.True(row.Completed);
        Assert.Equal(4, row.PageIndex);
        Assert.Equal(20, row.PageCount);
        // Read here first, so it is not an external-only read.
        Assert.False(row.External);
    }

    [Fact]
    public async Task ChaptersMarkedUnreadInMakiAreNotReMarked()
    {
        // Kavita keeps reporting the chapter as read, and this runs on every scrobble tick — so
        // without the tombstone an explicit mark-unread would silently undo itself within the hour.
        var seriesId = Seed((1m, true));
        using (var db = _db.NewContext())
        {
            db.ChapterProgress.Add(new ChapterProgress
            {
                UserId = 1,
                SeriesId = seriesId,
                ChapterId = db.Chapters.Single().Id,
                PageIndex = 0,
                PageCount = 20,
                Completed = false,
                UnreadAt = DateTime.UtcNow,
                StartedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
        }

        Assert.Equal(0, await Service().MarkAsync(TestUser, seriesId, Read(1m), CancellationToken.None));

        using var after = _db.NewContext();
        Assert.False(after.ChapterProgress.Single().Completed);
    }

    [Fact]
    public async Task A_fully_read_volume_marks_every_chapter_in_its_archive()
    {
        // The discussion #109 case: chapters 1-3 live in one "Vol. 01" archive, which Kavita sees as
        // volume 1 with no chapter number. Chapter 4 is its own file and was not read.
        var seriesId = SeedFiles(("Series/Series Vol. 01.cbz", [1m, 2m, 3m]), ("Series/Series Ch. 004.cbz", [4m]));
        var read = KavitaProgress.Compute(
        [
            new KavitaProgress.KavitaVolumeDto(1, 1, 60, 60, [new(-100000, -100000, 60, 60, false)]),
            new KavitaProgress.KavitaVolumeDto(2, 2, 20, 0, [new(4, 4, 20, 0, false)]),
        ]);

        Assert.Equal(3, await Service().MarkAsync(TestUser, seriesId, read, CancellationToken.None));

        using var db = _db.NewContext();
        var markedIds = db.ChapterProgress.Select(p => p.ChapterId).ToList();
        var marked = db.Chapters.Where(c => markedIds.Contains(c.Id)).Select(c => c.Number).OrderBy(n => n).ToList();
        Assert.Equal([1m, 2m, 3m], marked);
    }

    [Fact]
    public void A_half_read_volume_archive_counts_the_chapters_already_finished()
    {
        // 25 of 60 pages into volume 1, whose chapters start at pages 0, 20 and 40: chapter 1 done.
        var boundaries = new Dictionary<int, Maki.Core.Scrobbling.VolumeChapterProgress.ChapterFileBoundaries>
        {
            [1] = new(60, [(1m, 0), (2m, 20), (3m, 40)]),
        };
        var read = KavitaProgress.Compute(
            [new KavitaProgress.KavitaVolumeDto(1, 1, 60, 25, [new(-100000, -100000, 60, 25, false)])],
            boundaries);

        Assert.True(read.CoversChapter(1m));
        Assert.False(read.CoversChapter(2m));
        Assert.Empty(read.Volumes);
        // The tracker push reads the same result, so Maki and MAL agree on chapter 1.
        Assert.Equal(1, read.MaxChapter);
    }

    [Fact]
    public async Task A_fully_read_chapter_range_marks_every_chapter_in_it()
    {
        var seriesId = Seed((1m, true), (2m, true), (2.5m, true), (3m, true), (4m, true));
        var read = KavitaProgress.Compute(Volume(new KavitaProgress.KavitaChapterDto(1, 3, 60, 60, false)));

        Assert.Equal(4, await Service().MarkAsync(TestUser, seriesId, read, CancellationToken.None));
    }

    // ---- RunAsync: a series whose volume fetch fails ----

    private sealed class KavitaFakeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("Plugin/authenticate"))
            {
                return Json(HttpStatusCode.OK, """{"token":"jwt"}""");
            }

            if (path.Contains("Series/all-v2"))
            {
                return Json(HttpStatusCode.OK,
                    """[{"id":10,"name":"Imported Series","localizedName":null,"libraryId":1,"pages":10,"pagesRead":5}]""");
            }

            if (path.Contains("Series/volumes"))
            {
                // Simulates a Kavita call that fails mid-import, e.g. a timeout on one series.
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json(HttpStatusCode status, string json) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private KavitaReadImportService BuildService(IServiceScopeFactory scopeFactory)
    {
        var settings = new SettingsService(scopeFactory);
        var kavita = new KavitaClient(new FakeHttpClientFactory(new KavitaFakeHandler()));
        return new KavitaReadImportService(
            scopeFactory,
            settings,
            kavita,
            new ExternalReadSyncService(scopeFactory),
            new VolumeBoundaryService(scopeFactory),
            new KavitaUserResolver(scopeFactory, settings),
            NullLogger<KavitaReadImportService>.Instance);
    }

    [Fact]
    public async Task A_series_whose_volume_fetch_fails_is_reported_rather_than_silently_skipped()
    {
        _db.SeedUser("admin", MakiPermission.Admin);
        _db.SeedSeries("Imported Series");
        _db.SetConfig(("kavita.url", "http://kavita.test"), ("kavita.apikey", "secret"));

        var service = BuildService(_db.ScopeFactory());
        Assert.True(service.Start());

        for (var i = 0; i < 200 && service.State.Running; i++)
        {
            await Task.Delay(10);
        }

        Assert.False(service.State.Running);
        var result = service.State.Result;
        Assert.NotNull(result);
        Assert.Equal(0, result!.SeriesMatched);
        Assert.Equal(0, result.SeriesUnmatched);
        Assert.Equal(1, result.SeriesFailed);
        Assert.Equal(["Imported Series"], result.FailedTitles);
    }

    [Fact]
    public void Start_clears_the_previous_runs_result_and_finished_time()
    {
        var service = BuildService(_db.ScopeFactory());
        service.State.Result = new KavitaReadImportService.ImportResult(1, 2, 0, 0, []);
        service.State.FinishedAt = DateTime.UtcNow;

        Assert.True(service.Start());

        Assert.Null(service.State.Result);
        Assert.Null(service.State.FinishedAt);
    }

    private int Seed(params (decimal Number, bool Downloaded)[] chapters)
    {
        var seriesId = _db.SeedSeries("Imported Series");
        using var db = _db.NewContext();
        var file = new ChapterFile
        {
            SeriesId = seriesId,
            RelativePath = "x.cbz",
            SourceName = "Test",
            DateAdded = DateTime.UtcNow,
        };
        db.ChapterFiles.Add(file);
        db.SaveChanges();

        db.Chapters.AddRange(chapters.Select(c => new Chapter
        {
            SeriesId = seriesId,
            Number = c.Number,
            Language = "en",
            ChapterFileId = c.Downloaded ? file.Id : null,
        }));
        db.SaveChanges();
        return seriesId;
    }

    private int SeedFiles(params (string Path, decimal[] Chapters)[] files)
    {
        var seriesId = _db.SeedSeries("Series");
        using var db = _db.NewContext();
        foreach (var (path, numbers) in files)
        {
            var file = new ChapterFile
            {
                SeriesId = seriesId,
                RelativePath = path,
                SourceName = "Test",
                DateAdded = DateTime.UtcNow,
            };
            db.ChapterFiles.Add(file);
            db.SaveChanges();

            db.Chapters.AddRange(numbers.Select(n => new Chapter
            {
                SeriesId = seriesId,
                Number = n,
                Language = "en",
                ChapterFileId = file.Id,
            }));
        }

        db.SaveChanges();
        return seriesId;
    }
}
