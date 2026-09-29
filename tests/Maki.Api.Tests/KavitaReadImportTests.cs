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

    // ---- which Kavita chapters count as read ----

    [Fact]
    public void OnlyFullyReadChaptersCount()
    {
        var numbers = ExternalReadSyncService.ReadChapterNumbers(Volume(
            Chapter(1, 20, 20),
            Chapter(2, 20, 19),
            Chapter(3, 20, 0)));

        Assert.Equal([1m], numbers);
    }

    [Fact]
    public void SpecialsAndSentinelNumbersAreSkipped()
    {
        // Kavita tags uncounted entries with huge sentinel numbers; matching one against a real
        // local chapter number would mark the wrong thing read.
        var numbers = ExternalReadSyncService.ReadChapterNumbers(Volume(
            Chapter(5, 10, 10),
            Chapter(6, 10, 10, special: true),
            Chapter(100_000, 10, 10)));

        Assert.Equal([5m], numbers);
    }

    [Fact]
    public void ZeroPageChaptersAreNotRead()
    {
        // pagesRead >= pages is trivially true at 0/0 — that's an empty entry, not a read one.
        Assert.Empty(ExternalReadSyncService.ReadChapterNumbers(Volume(Chapter(1, 0, 0))));
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

        var marked = await Service().MarkAsync(TestUser, seriesId, [1m, 2m, 3m], CancellationToken.None);

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

        Assert.Equal(2, await Service().MarkAsync(TestUser, seriesId, [1m, 2m], CancellationToken.None));
        Assert.Equal(0, await Service().MarkAsync(TestUser, seriesId, [1m, 2m], CancellationToken.None));

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

        await Service().MarkAsync(TestUser, seriesId, [1m], CancellationToken.None);

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

        Assert.Equal(0, await Service().MarkAsync(TestUser, seriesId, [1m], CancellationToken.None));

        using var after = _db.NewContext();
        Assert.False(after.ChapterProgress.Single().Completed);
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
}
