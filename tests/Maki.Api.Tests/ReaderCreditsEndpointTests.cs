using System.IO.Compression;
using System.Text.Json;
using Maki.Api.Configuration;
using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Api.Services.Ocr;
using Maki.Api.Services.Upscaling;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Images;
using Maki.Core.Reading;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public sealed class ReaderCreditsEndpointTests : IDisposable
{
    private const int W = 64;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly TestDb _db = new();
    private readonly string _configDir = Path.Combine(Path.GetTempPath(), "maki-credits-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly int _seriesId;
    private readonly ReaderArchiveCache _archives = new(NullLogger<ReaderArchiveCache>.Instance);

    /// <summary>The colour rule alone: no OCR models where this reader looks, as on an install without them.</summary>
    private ReaderCreditsCache _credits;

    private CreditStore _store;

    private static readonly string NoOcr = Path.Combine(Path.GetTempPath(), "maki-no-ocr-" + Guid.NewGuid().ToString("N"));

    /// <summary>A search with its own OCR reader, storing into this test's database and strips folder.</summary>
    private ReaderCreditsCache Credits(string ocrFolder) => Credits(new PageTextReader(NullLogger<PageTextReader>.Instance, ocrFolder));

    private CreditBank _bank = null!;
    private CreditStrips _strips = null!;
    private CreditNoticeStore _notices = null!;

    private ReaderCreditsCache Credits(IPageTextReader text)
    {
        _store = new CreditStore(_db.ScopeFactory(), text, NullLogger<CreditStore>.Instance);
        _bank = new CreditBank(_db.ScopeFactory(), NullLogger<CreditBank>.Instance);
        _strips = new CreditStrips(new AppPaths(_configDir), NullLogger<CreditStrips>.Instance);
        _notices = new CreditNoticeStore(_db.ScopeFactory(), _store, NullLogger<CreditNoticeStore>.Instance);
        return new ReaderCreditsCache(
            new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance), text, _store, _strips, _bank, _notices,
            NullLogger<ReaderCreditsCache>.Instance);
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "ocr", name);

    public ReaderCreditsEndpointTests()
    {
        _root = Path.Combine(_configDir, "library");
        using var db = _db.NewContext();
        var folder = new RootFolder { Path = _root };
        db.RootFolders.Add(folder);
        db.SaveChanges();
        var series = new Series { Title = "S", SortTitle = "s", FolderName = "S", RootFolderId = folder.Id };
        db.Series.Add(series);
        db.SaveChanges();
        _seriesId = series.Id;
        _credits = Credits(NoOcr);
        _store ??= null!;
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            Directory.Delete(_configDir, true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Seeded smooth noise: a page that never repeats unless the same seed is used again.</summary>
    private static byte[] Noise(int seed, int rows)
    {
        var random = new Random(seed);
        var grid = new double[(rows / 24 + 2) * 9];
        for (var g = 0; g < grid.Length; g++)
        {
            grid[g] = random.Next(20, 236);
        }

        var page = new byte[rows * W];
        for (var r = 0; r < rows; r++)
        {
            var fy = r / 24.0 - r / 24;
            for (var x = 0; x < W; x++)
            {
                var (cx, fx) = (x / 8, x / 8.0 - x / 8);
                double At(int row) => grid[row * 9 + cx] * (1 - fx) + grid[row * 9 + Math.Min(cx + 1, 8)] * fx;
                page[r * W + x] = (byte)(At(r / 24) * (1 - fy) + At(r / 24 + 1) * fy);
            }
        }

        return page;
    }

    private int AddChapter(int number, IEnumerable<byte[]> pages) =>
        AddEncodedChapter(number, pages.Select(pixels =>
        {
            using var image = NetVips.Image.NewFromMemory(pixels, W, pixels.Length / (W * 3), 3, NetVips.Enums.BandFormat.Uchar);
            return image.PngsaveBuffer();
        }));

    private int AddEncodedChapter(int number, IEnumerable<byte[]> pages, int? volume = null, int? seriesId = null, string folder = "S")
    {
        var name = $"c{number}.cbz";
        var path = Path.Combine(_root, folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var index = 1;
            foreach (var png in pages)
            {
                using var entry = zip.CreateEntry($"{index++:000}.png").Open();
                entry.Write(png);
            }
        }

        using var db = _db.NewContext();
        var file = new ChapterFile
        {
            SeriesId = seriesId ?? _seriesId, RelativePath = $"{folder}/{name}", Size = new FileInfo(path).Length,
            SourceName = "test", DateAdded = DateTime.UtcNow
        };
        db.ChapterFiles.Add(file);
        db.SaveChanges();
        var chapter = volume is null
            ? new Chapter { SeriesId = seriesId ?? _seriesId, Number = number, ChapterFileId = file.Id }
            : new Chapter { SeriesId = seriesId ?? _seriesId, Volume = volume, ChapterFileId = file.Id };
        db.Chapters.Add(chapter);
        db.SaveChanges();
        return chapter.Id;
    }

    /// <summary>A black and white page as RGB.</summary>
    private static byte[] Grey(byte[] page) => [.. page.SelectMany(v => new[] { v, v, v })];

    /// <summary>The same page in colour: its channels pulled apart, as a scanlator's coloured page.</summary>
    private static byte[] Tinted(byte[] page) => [.. page.SelectMany(v => new[] { v, (byte)(v / 2), (byte)(255 - v) })];

    private static IEnumerable<byte[]> Story(int seed, int count) =>
        Enumerable.Range(0, count).Select(n => Grey(Noise(seed * 100 + n, 90)));

    private ReaderController Controller(Maki.Data.MakiDbContext db) => new(
        new TestLocalizer(), db,
        new ReaderService(db, _archives, null!, null!, new ReadingSessionService(db), NullLogger<ReaderService>.Instance),
        null!, null!, new ReadingProfileService(db, new UserSettingsService(db, new TestCurrentUser(1))), null!, null!,
        new AppPaths(_configDir), new TestCurrentUser(1), null!, new FakeAppSettings())
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
    };

    private async Task<IActionResult> CreditsAsync(int chapterId)
    {
        using var db = _db.NewContext(1);
        return await Controller(db).Credits(chapterId, _credits, _store, default);
    }

    /// <summary>The admin's background credit scan switch, which the warm-up follows. On unless a test turns it off.</summary>
    private bool _scanOn = true;

    private async Task<IActionResult> WarmAsync(int chapterId)
    {
        using var db = _db.NewContext(1);
        var scan = Worker();
        scan.SetEnabled(_scanOn);
        return await Controller(db).WarmCredits(chapterId, _credits, _store, scan, default);
    }

    private async Task<JsonElement> FoundAsync(int chapterId) =>
        JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await CreditsAsync(chapterId)).Value, Web);

    [Fact]
    public async Task Hides_the_credit_page_nearby_chapters_open_and_close_with()
    {
        var credit = Tinted(Noise(7, 90));
        var recruit = Tinted(Noise(8, 120));
        var chapters = Enumerable.Range(1, 4)
            .Select(n => AddChapter(n, [credit, .. Story(n, 8), recruit]))
            .ToList();

        foreach (var chapter in chapters)
        {
            var found = await FoundAsync(chapter);
            Assert.Equal(1, found.GetProperty("headPages").GetInt32());
            Assert.Equal(0, found.GetProperty("headOffset").GetDouble());
            Assert.Equal(1, found.GetProperty("tailPages").GetInt32());
        }
    }

    [Fact]
    public async Task Keeps_a_black_and_white_page_every_chapter_repeats()
    {
        var colophon = Grey(Noise(7, 90));
        var chapters = Enumerable.Range(1, 4).Select(n => AddChapter(n, [colophon, .. Story(n, 8)])).ToList();

        var found = await FoundAsync(chapters[1]);

        Assert.Equal(0, found.GetProperty("headPages").GetInt32());
        Assert.Equal(0, found.GetProperty("tailPages").GetInt32());
    }

    [Fact]
    public async Task A_chapter_without_repeated_pages_or_neighbours_hides_nothing()
    {
        var alone = AddChapter(1, Story(1, 9));

        var found = await FoundAsync(alone);

        Assert.Equal(0, found.GetProperty("headPages").GetInt32());
        Assert.Equal(0, found.GetProperty("headOffset").GetDouble());
        Assert.Equal(0, found.GetProperty("tailPages").GetInt32());
    }

    [Fact]
    public async Task An_unknown_chapter_is_not_found()
    {
        Assert.IsNotType<OkObjectResult>(await CreditsAsync(999));
    }

    private List<int> ChaptersOpeningWith(byte[] png) =>
        [.. Enumerable.Range(1, 4).Select(n => AddEncodedChapter(n, [png, .. Story(n, 8).Select(pixels =>
        {
            using var image = NetVips.Image.NewFromMemory(pixels, W, pixels.Length / (W * 3), 3, NetVips.Enums.BandFormat.Uchar);
            return image.PngsaveBuffer();
        })]))];

    private static bool HidesTheHead(JsonElement found) =>
        found.GetProperty("headPages").GetInt32() > 0 || found.GetProperty("headOffset").GetDouble() > 0;

    [Fact]
    public async Task Reads_a_repeated_credit_page_and_hides_it()
    {
        _credits = Credits(Path.Combine(AppContext.BaseDirectory, PageTextReader.FolderName));
        var chapters = ChaptersOpeningWith(await File.ReadAllBytesAsync(Fixture("ocr-credit-page.png")));

        Assert.True(HidesTheHead(await FoundAsync(chapters[1])));
    }

    [Fact]
    public async Task Reads_a_repeated_colour_colophon_and_keeps_it_where_colour_alone_would_hide_it()
    {
        using var colophon = NetVips.Image.NewFromFile(Fixture("ocr-colophon.png"));
        using var tinted = colophon.Linear([1.0, 0.6, 0.8], [0.0, 0.0, 0.0]).Cast(NetVips.Enums.BandFormat.Uchar);
        var chapters = ChaptersOpeningWith(tinted.PngsaveBuffer());

        Assert.True(HidesTheHead(await FoundAsync(chapters[1])));

        _credits = Credits(Path.Combine(AppContext.BaseDirectory, PageTextReader.FolderName));
        var found = await FoundAsync(chapters[1]);
        Assert.Equal(0, found.GetProperty("headPages").GetInt32());
        Assert.Equal(0, found.GetProperty("headOffset").GetDouble());
    }

    [Fact]
    public async Task Warming_searches_nothing_while_the_setting_is_off()
    {
        var credit = Tinted(Noise(7, 90));
        var chapters = Enumerable.Range(1, 4).Select(n => AddChapter(n, [credit, .. Story(n, 8)])).ToList();

        Assert.IsType<NoContentResult>(await WarmAsync(chapters[1]));
    }

    [Fact]
    public async Task Warming_searches_nothing_while_the_background_scan_is_off()
    {
        _db.SetUserConfig(1, (SettingKeys.ReaderPrefs, ReaderPrefsSpec.Serialize(new ReaderPrefsSpec(HideCredits: true))));
        var credit = Tinted(Noise(7, 90));
        var chapters = Enumerable.Range(1, 4).Select(n => AddChapter(n, [credit, .. Story(n, 8)])).ToList();
        _scanOn = false;

        Assert.IsType<NoContentResult>(await WarmAsync(chapters[1]));
        Assert.Null(await RowAsync(chapters[1]));
    }

    [Fact]
    public async Task Warming_starts_the_search_the_reader_then_finds()
    {
        _db.SetUserConfig(1, (SettingKeys.ReaderPrefs, ReaderPrefsSpec.Serialize(new ReaderPrefsSpec(HideCredits: true))));
        var credit = Tinted(Noise(7, 90));
        var chapters = Enumerable.Range(1, 4).Select(n => AddChapter(n, [credit, .. Story(n, 8)])).ToList();

        Assert.IsType<AcceptedResult>(await WarmAsync(chapters[1]));

        Assert.Equal(1, (await FoundAsync(chapters[1])).GetProperty("headPages").GetInt32());
    }

    private async Task<Maki.Core.Entities.ChapterCredits?> RowAsync(int chapterId)
    {
        using var db = _db.NewContext();
        return await db.ChapterCredits.AsNoTracking().FirstOrDefaultAsync(c => c.ChapterId == chapterId);
    }

    private async Task SetStoredHeadAsync(int chapterId, int headPages)
    {
        using var db = _db.NewContext();
        await db.ChapterCredits.Where(c => c.ChapterId == chapterId)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.HeadPages, headPages));
    }

    private string PathOf(int number) => Path.Combine(_root, "S", $"c{number}.cbz");

    /// <summary>Rewrites a chapter's ComicInfo.xml the way <c>ComicInfoUpdater</c> does: every page copied, the file's size and stamp new.</summary>
    private void RewriteComicInfo(int number, int chapterId)
    {
        var path = PathOf(number);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            zip.GetEntry("ComicInfo.xml")?.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("ComicInfo.xml").Open());
            writer.Write($"<ComicInfo><Number>{number}</Number><Notes>{Guid.NewGuid()}</Notes></ComicInfo>");
        }

        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
        using var db = _db.NewContext();
        var fileId = db.Chapters.Where(c => c.Id == chapterId).Select(c => c.ChapterFileId).First();
        db.ChapterFiles.Where(f => f.Id == fileId).ExecuteUpdate(u => u.SetProperty(f => f.Size, new FileInfo(path).Length));
        _archives.Invalidate(fileId!.Value);
    }

    [Fact]
    public async Task A_search_stores_its_answer_and_the_next_request_reads_it()
    {
        var credit = Tinted(Noise(7, 90));
        var chapters = Enumerable.Range(1, 4).Select(n => AddChapter(n, [credit, .. Story(n, 8)])).ToList();

        Assert.Equal(1, (await FoundAsync(chapters[1])).GetProperty("headPages").GetInt32());
        var row = await RowAsync(chapters[1]);
        Assert.NotNull(row);
        Assert.Equal(1, row.HeadPages);
        Assert.False(string.IsNullOrEmpty(row.ReferencesKey));

        // Served from the row, not searched again.
        await SetStoredHeadAsync(chapters[1], 2);
        Assert.Equal(2, (await FoundAsync(chapters[1])).GetProperty("headPages").GetInt32());
    }

    [Fact]
    public async Task A_ComicInfo_rewrite_keeps_the_stored_answer()
    {
        var credit = Tinted(Noise(7, 90));
        var chapters = Enumerable.Range(1, 4).Select(n => AddChapter(n, [credit, .. Story(n, 8)])).ToList();
        await FoundAsync(chapters[1]);
        await SetStoredHeadAsync(chapters[1], 2);

        RewriteComicInfo(2, chapters[1]);

        Assert.Equal(2, (await FoundAsync(chapters[1])).GetProperty("headPages").GetInt32());
    }

    [Fact]
    public async Task New_pages_are_searched_again_instead_of_served_the_old_answer()
    {
        var credit = Tinted(Noise(7, 90));
        var chapters = Enumerable.Range(1, 4).Select(n => AddChapter(n, [credit, .. Story(n, 8)])).ToList();
        await FoundAsync(chapters[1]);
        await SetStoredHeadAsync(chapters[1], 2);

        // The same chapter row, its file replaced by one whose first page is a story page.
        var path = PathOf(2);
        File.Delete(path);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var index = 1;
            foreach (var pixels in Story(20, 9))
            {
                using var image = NetVips.Image.NewFromMemory(pixels, W, pixels.Length / (W * 3), 3, NetVips.Enums.BandFormat.Uchar);
                using var entry = zip.CreateEntry($"{index++:000}.png").Open();
                entry.Write(image.PngsaveBuffer());
            }
        }

        using (var db = _db.NewContext())
        {
            var fileId = db.Chapters.Where(c => c.Id == chapters[1]).Select(c => c.ChapterFileId).First()!.Value;
            db.ChapterFiles.Where(f => f.Id == fileId).ExecuteUpdate(u => u.SetProperty(f => f.Size, new FileInfo(path).Length));
            _archives.Invalidate(fileId);
        }

        Assert.Equal(0, (await FoundAsync(chapters[1])).GetProperty("headPages").GetInt32());
    }

    [Fact]
    public async Task An_answer_the_colour_rule_gave_alone_is_found_again_once_OCR_can_read()
    {
        using var colophon = NetVips.Image.NewFromFile(Fixture("ocr-colophon.png"));
        using var tinted = colophon.Linear([1.0, 0.6, 0.8], [0.0, 0.0, 0.0]).Cast(NetVips.Enums.BandFormat.Uchar);
        var chapters = ChaptersOpeningWith(tinted.PngsaveBuffer());
        Assert.True(HidesTheHead(await FoundAsync(chapters[1])));
        Assert.Equal(CreditJudge.Colour, (await RowAsync(chapters[1]))!.Judge);

        _credits = Credits(Path.Combine(AppContext.BaseDirectory, PageTextReader.FolderName));

        Assert.False(HidesTheHead(await FoundAsync(chapters[1])));
        Assert.Equal(CreditJudge.Ocr, (await RowAsync(chapters[1]))!.Judge);
    }

    [Fact]
    public async Task Warming_starts_nothing_for_a_chapter_with_a_stored_answer()
    {
        _db.SetUserConfig(1, (SettingKeys.ReaderPrefs, ReaderPrefsSpec.Serialize(new ReaderPrefsSpec(HideCredits: true))));
        var credit = Tinted(Noise(7, 90));
        var chapters = Enumerable.Range(1, 4).Select(n => AddChapter(n, [credit, .. Story(n, 8)])).ToList();
        await FoundAsync(chapters[1]);

        Assert.IsType<NoContentResult>(await WarmAsync(chapters[1]));
    }

    [Fact]
    public async Task The_manifest_carries_a_stored_answer_while_credit_pages_are_hidden()
    {
        var credit = Tinted(Noise(7, 90));
        var chapters = Enumerable.Range(1, 4).Select(n => AddChapter(n, [credit, .. Story(n, 8)])).ToList();
        await FoundAsync(chapters[1]);

        Assert.Equal(JsonValueKind.Null, (await ManifestAsync(chapters[1])).GetProperty("credits").ValueKind);

        _db.SetUserConfig(1, (SettingKeys.ReaderPrefs, ReaderPrefsSpec.Serialize(new ReaderPrefsSpec(HideCredits: true))));
        Assert.Equal(1, (await ManifestAsync(chapters[1])).GetProperty("credits").GetProperty("headPages").GetInt32());
        Assert.Equal(JsonValueKind.Null, (await ManifestAsync(chapters[2])).GetProperty("credits").ValueKind);
    }

    private async Task<JsonElement> ManifestAsync(int chapterId)
    {
        using var db = _db.NewContext(1);
        var result = await Controller(db).Manifest(
            chapterId, new EnhanceAvailability(new FakeAppSettings(), null!), _store, new ReadingTimeEstimateService(db), default);
        return JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value, Web);
    }

    private CreditScanWorker Worker()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddScoped(_ => _db.NewContext());
        services.AddScoped(sp => new ReaderService(sp.GetRequiredService<Maki.Data.MakiDbContext>(), _archives, null!, null!,
            new ReadingSessionService(sp.GetRequiredService<Maki.Data.MakiDbContext>()), NullLogger<ReaderService>.Instance));
        var scopes = services.BuildServiceProvider().GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>();
        var settings = new FakeAppSettings();
        return new CreditScanWorker(scopes, settings, _credits, _store, _archives,
            new PageConversionWorker(scopes, settings, NullLogger<PageConversionWorker>.Instance), _bank, _strips,
            NullLogger<CreditScanWorker>.Instance);
    }

    [Fact]
    public async Task The_scan_stores_an_answer_for_every_chapter_of_a_series()
    {
        var credit = Tinted(Noise(7, 90));
        var chapters = Enumerable.Range(1, 5).Select(n => AddChapter(n, [credit, .. Story(n, 8)])).ToList();
        var worker = Worker();

        Assert.Equal((_seriesId, "en"), await worker.NextSeriesAsync(default));
        await worker.ScanSeriesAsync(_seriesId, "en", default);

        foreach (var chapter in chapters)
        {
            Assert.Equal(1, (await RowAsync(chapter))!.HeadPages);
        }

        Assert.Null(await worker.NextSeriesAsync(default));
    }

    [Fact]
    public async Task The_scan_leaves_volumes_out_when_their_edges_read_as_no_scanlators()
    {
        var credit = Tinted(Noise(7, 90));
        var volumes = Enumerable.Range(1, 4)
            .Select(n => AddEncodedChapter(n, Story(n, 8).Prepend(credit).Select(pixels =>
            {
                using var image = NetVips.Image.NewFromMemory(pixels, W, pixels.Length / (W * 3), 3, NetVips.Enums.BandFormat.Uchar);
                return image.PngsaveBuffer();
            }), volume: n))
            .ToList();

        await Worker().ScanSeriesAsync(_seriesId, "en", default);

        foreach (var volume in volumes)
        {
            var row = (await RowAsync(volume))!;
            Assert.True(row.Skipped);
            Assert.Equal(0, row.HeadPages);
        }
    }

    [Fact]
    public async Task A_neighbour_arriving_searches_the_chapters_beside_it_again()
    {
        var credit = Tinted(Noise(7, 90));
        var chapters = Enumerable.Range(1, 3).Select(n => AddChapter(n, [credit, .. Story(n, 8)])).ToList();
        var worker = Worker();
        await worker.ScanSeriesAsync(_seriesId, "en", default);
        var before = (await RowAsync(chapters[2]))!.ReferencesKey;

        AddChapter(4, [credit, .. Story(4, 8)]);
        await worker.ScanSeriesAsync(_seriesId, "en", default);

        Assert.NotEqual(before, (await RowAsync(chapters[2]))!.ReferencesKey);
    }

    /// <summary>A reader whose every read throws, as a share dropping mid-search would.</summary>
    private sealed class ThrowingTextReader : IPageTextReader
    {
        public bool Available => true;

        public Task<string?> ReadAsync(byte[] image, double share, CancellationToken ct) => throw new IOException("share went away");

        public Task<string?> ReadBottomAsync(byte[] image, double share, CancellationToken ct) => throw new IOException("share went away");

        public Task PrepareAsync() => Task.CompletedTask;
    }

    /// <summary>A reader that reads a scanlator's notice off every page, and counts its reads.</summary>
    private sealed class CountingTextReader : IPageTextReader
    {
        public int Reads;

        /// <summary>The shares read off the bottom of a page.</summary>
        public readonly List<double> Bottoms = [];

        public bool Available => true;

        public Task<string?> ReadAsync(byte[] image, double share, CancellationToken ct)
        {
            Interlocked.Increment(ref Reads);
            return Task.FromResult<string?>("Join our Discord");
        }

        public Task<string?> ReadBottomAsync(byte[] image, double share, CancellationToken ct)
        {
            Interlocked.Increment(ref Reads);
            lock (Bottoms)
            {
                Bottoms.Add(share);
            }

            return Task.FromResult<string?>("Join our Discord");
        }

        public Task PrepareAsync() => Task.CompletedTask;
    }

    [Fact]
    public async Task Chapters_carrying_the_same_credit_page_read_it_once()
    {
        var reader = new CountingTextReader();
        _credits = Credits(reader);
        var credit = Tinted(Noise(7, 90));
        var chapters = Enumerable.Range(1, 4).Select(n => AddChapter(n, [credit, .. Story(n, 8)])).ToList();

        Assert.Equal(1, (await FoundAsync(chapters[1])).GetProperty("headPages").GetInt32());
        Assert.Equal(1, (await FoundAsync(chapters[2])).GetProperty("headPages").GetInt32());

        Assert.Equal(1, reader.Reads);
        using var db = _db.NewContext();
        Assert.Equal(2, db.ChapterCreditNotices.Count());
        Assert.Single(db.CreditNotices);
    }

    [Fact]
    public async Task A_failing_notice_store_falls_back_to_reading_the_page()
    {
        var reader = new CountingTextReader();
        _credits = Credits(reader);
        var credit = Tinted(Noise(7, 90));
        var chapters = Enumerable.Range(1, 4).Select(n => AddChapter(n, [credit, .. Story(n, 8)])).ToList();
        using (var db = _db.NewContext())
        {
            // Every store call now throws, as a broken database would.
            await db.Database.ExecuteSqlRawAsync("DROP TABLE CreditNotices");
        }

        Assert.Equal(1, (await FoundAsync(chapters[1])).GetProperty("headPages").GetInt32());
        Assert.False((await RowAsync(chapters[1]))!.Failed);
        Assert.Equal(1, reader.Reads);
    }

    [Fact]
    public async Task A_search_that_fails_is_stored_as_failed_and_not_as_no_credits()
    {
        _credits = Credits(new ThrowingTextReader());
        var credit = Tinted(Noise(7, 90));
        var chapters = Enumerable.Range(1, 4).Select(n => AddChapter(n, [credit, .. Story(n, 8)])).ToList();
        var worker = Worker();

        await worker.ScanSeriesAsync(_seriesId, "en", default);

        foreach (var chapter in chapters)
        {
            Assert.True((await RowAsync(chapter))!.Failed);
        }

        // Not served, and not picked again before its retry.
        Assert.Equal(0, (await FoundAsync(chapters[1])).GetProperty("headPages").GetInt32());
        Assert.Null(await worker.NextSeriesAsync(default));
    }

    [Fact]
    public async Task A_series_waits_while_any_of_its_files_is_queued_for_conversion()
    {
        var credit = Tinted(Noise(7, 90));
        var chapters = Enumerable.Range(1, 4).Select(n => AddChapter(n, [credit, .. Story(n, 8)])).ToList();
        using (var db = _db.NewContext())
        {
            var fileId = db.Chapters.Where(c => c.Id == chapters[2]).Select(c => c.ChapterFileId).First();
            db.ChapterFiles.Where(f => f.Id == fileId)
                .ExecuteUpdate(u => u.SetProperty(f => f.ConversionQueuedAtUtc, DateTime.UtcNow));
        }

        Assert.Null(await Worker().NextSeriesAsync(default));
    }

    /// <summary>Another series in the same root folder, with one chapter opening with <paramref name="png"/>.</summary>
    private int AddSeries(string name)
    {
        using var db = _db.NewContext();
        var folder = db.RootFolders.First();
        var series = new Series { Title = name, SortTitle = name.ToLowerInvariant(), FolderName = name, RootFolderId = folder.Id };
        db.Series.Add(series);
        db.SaveChanges();
        return series.Id;
    }

    private static IEnumerable<byte[]> StoryPngs(int seed, int count) => Story(seed, count).Select(pixels =>
    {
        using var image = NetVips.Image.NewFromMemory(pixels, W, pixels.Length / (W * 3), 3, NetVips.Enums.BandFormat.Uchar);
        return image.PngsaveBuffer();
    });

    private (int SeriesId, int ChapterId) LoneChapterOpeningWith(byte[] png, string name = "T")
    {
        var seriesId = AddSeries(name);
        return (seriesId, AddEncodedChapter(1, [png, .. StoryPngs(30, 8)], seriesId: seriesId, folder: name));
    }

    /// <summary>The credit fixture under a band of colour art of its own width: another group's notice, whose first rows differ.</summary>
    private static byte[] OtherNotice(byte[] credit)
    {
        using var page = NetVips.Image.NewFromBuffer(credit);
        using var noise = NetVips.Image.NewFromMemory(Tinted(Noise(41, 60)), W, 60, 3, NetVips.Enums.BandFormat.Uchar);
        using var band = noise.Resize((double)page.Width / W);
        using var flat = page.Bands == 4 ? page.Flatten() : page.Copy();
        using var joined = NetVips.Image.Arrayjoin([band.Cast(NetVips.Enums.BandFormat.Uchar), flat.Cast(NetVips.Enums.BandFormat.Uchar)], across: 1);
        return joined.PngsaveBuffer();
    }

    private int BankCount()
    {
        using var db = _db.NewContext();
        return db.CreditBank.Count();
    }

    [Fact]
    public async Task A_credit_seen_in_one_series_is_caught_in_a_lone_chapter_of_another()
    {
        _credits = Credits(Path.Combine(AppContext.BaseDirectory, PageTextReader.FolderName));
        var png = await File.ReadAllBytesAsync(Fixture("ocr-credit-page.png"));
        var chapters = ChaptersOpeningWith(png);
        Assert.True(HidesTheHead(await FoundAsync(chapters[1])));
        Assert.Equal(1, BankCount());

        // The other chapters of the series find the same notice: a hit, not a second entry.
        Assert.True(HidesTheHead(await FoundAsync(chapters[2])));
        Assert.Equal(1, BankCount());

        var (_, lone) = LoneChapterOpeningWith(png);
        Assert.True(HidesTheHead(await FoundAsync(lone)));
    }

    [Fact]
    public async Task Nothing_is_learned_from_what_the_colour_rule_judged_alone()
    {
        var credit = Tinted(Noise(7, 90));
        var chapters = Enumerable.Range(1, 4).Select(n => AddChapter(n, [credit, .. Story(n, 8)])).ToList();

        Assert.Equal(1, (await FoundAsync(chapters[1])).GetProperty("headPages").GetInt32());
        Assert.Equal(0, BankCount());
    }

    [Fact]
    public async Task A_chapter_searched_before_the_bank_knew_a_notice_is_searched_again_once_it_does()
    {
        _credits = Credits(Path.Combine(AppContext.BaseDirectory, PageTextReader.FolderName));
        var png = await File.ReadAllBytesAsync(Fixture("ocr-credit-page.png"));
        var (loneSeries, lone) = LoneChapterOpeningWith(png);
        var worker = Worker();
        await worker.ScanSeriesAsync(loneSeries, "en", default);
        Assert.False(HidesTheHead(await FoundAsync(lone)));

        var chapters = ChaptersOpeningWith(png);
        await worker.ScanSeriesAsync(_seriesId, "en", default);
        Assert.Equal(1, BankCount());

        Assert.True(await worker.RecheckBankAsync(default) > 0);
        Assert.Equal(0, (await RowAsync(lone))!.DetectorVersion);
        await worker.ScanSeriesAsync(loneSeries, "en", default);

        Assert.True(HidesTheHead(await FoundAsync(lone)));
        Assert.Equal(0, await worker.RecheckBankAsync(default));
    }

    [Fact]
    public async Task A_chapter_requeued_for_a_new_entry_is_searched_against_it_and_settles()
    {
        _credits = Credits(Path.Combine(AppContext.BaseDirectory, PageTextReader.FolderName));
        var credit = await File.ReadAllBytesAsync(Fixture("ocr-credit-page.png"));
        var other = OtherNotice(credit);
        var worker = Worker();

        // The bank knows one notice; a lone chapter carrying another is searched, matches nothing, and
        // its run stays in the in-memory results.
        ChaptersOpeningWith(credit);
        await worker.ScanSeriesAsync(_seriesId, "en", default);
        Assert.Equal(1, BankCount());
        var (loneSeries, lone) = LoneChapterOpeningWith(other);
        await worker.ScanSeriesAsync(loneSeries, "en", default);
        Assert.False(HidesTheHead(await FoundAsync(lone)));

        // Another series teaches the second notice.
        var teacher = AddSeries("U");
        foreach (var n in Enumerable.Range(1, 4))
        {
            AddEncodedChapter(n, [other, .. StoryPngs(50 + n, 8)], seriesId: teacher, folder: "U");
        }

        await worker.ScanSeriesAsync(teacher, "en", default);
        Assert.Equal(2, BankCount());

        Assert.True(await worker.RecheckBankAsync(default) > 0);
        await worker.ScanSeriesAsync(loneSeries, "en", default);

        Assert.True(HidesTheHead(await FoundAsync(lone)));
        Assert.Equal(0, await worker.RecheckBankAsync(default));
    }

    /// <summary>A scanlator's notice, the whole last page of one chapter and the bottom of the last slice of its neighbours.</summary>
    private static readonly byte[] TailNotice = Tinted(Noise(9, 90));

    private const int NoticeRows = 40;

    private static byte[] Png(byte[] pixels)
    {
        using var image = NetVips.Image.NewFromMemory(pixels, W, pixels.Length / (W * 3), 3, NetVips.Enums.BandFormat.Uchar);
        return image.PngsaveBuffer();
    }

    /// <summary>
    /// Four chapters of a series ending in the bottom <see cref="NoticeRows"/> rows of <see cref="TailNotice"/>
    /// under story art, except the second, which ends in the whole notice: repetition finds only that bottom
    /// share at its tail, where a bank entry of the whole page would find all of it.
    /// </summary>
    private List<int> ChaptersEndingInTheNoticesBottom()
    {
        var bottom = TailNotice[^(NoticeRows * W * 3)..];
        return [.. Enumerable.Range(1, 4).Select(n => AddChapter(n,
            [.. Story(n + 10, 8), n == 2 ? TailNotice : [.. Grey(Noise(n * 7 + 300, 300)), .. bottom]]))];
    }

    /// <summary>Another series whose chapters end in the whole notice, so its search teaches the bank the page.</summary>
    private async Task TeachTheWholeNoticeAsync()
    {
        var teacher = AddSeries("U");
        var chapters = Enumerable.Range(1, 4)
            .Select(n => AddEncodedChapter(n, [.. StoryPngs(50 + n, 8), Png(TailNotice)], seriesId: teacher, folder: "U"))
            .ToList();
        Assert.Equal(1, (await FoundAsync(chapters[1])).GetProperty("tailPages").GetInt32());
        Assert.Equal(1, BankCount());
    }

    [Fact]
    public async Task A_banner_repeated_at_the_bottom_of_the_last_slice_is_cut_off_and_read_from_the_bottom()
    {
        var reader = new CountingTextReader();
        _credits = Credits(reader);
        var chapters = ChaptersEndingInTheNoticesBottom();

        var found = await FoundAsync(chapters[1]);

        Assert.Equal(0, found.GetProperty("tailPages").GetInt32());
        // Smooth noise lets a few rows above the notice match too.
        Assert.InRange(found.GetProperty("tailOffset").GetDouble(), NoticeRows / 90.0 - 0.03, NoticeRows / 90.0 + 0.1);
        Assert.Equal(found.GetProperty("tailOffset").GetDouble(), Assert.Single(reader.Bottoms), 4);
        // The bank learns nothing from a tail partial: that route was not measured.
        Assert.Equal(0, BankCount());
        Assert.Equal(found.GetProperty("tailOffset").GetDouble(), (await RowAsync(chapters[1]))!.TailOffset);

        _db.SetUserConfig(1, (SettingKeys.ReaderPrefs, ReaderPrefsSpec.Serialize(new ReaderPrefsSpec(HideCredits: true))));
        Assert.Equal(found.GetProperty("tailOffset").GetDouble(),
            (await ManifestAsync(chapters[1])).GetProperty("credits").GetProperty("tailOffset").GetDouble());
    }

    [Fact]
    public async Task A_tail_whose_only_repetition_is_a_bottom_share_still_takes_a_bank_answer_hiding_the_whole_page()
    {
        _credits = Credits(new CountingTextReader());
        await TeachTheWholeNoticeAsync();
        var chapters = ChaptersEndingInTheNoticesBottom();

        var found = await FoundAsync(chapters[1]);

        Assert.Equal(1, found.GetProperty("tailPages").GetInt32());
        Assert.Equal(0, found.GetProperty("tailOffset").GetDouble());
    }

    [Fact]
    public async Task The_bank_recheck_still_asks_about_a_tail_whose_only_repetition_is_a_bottom_share()
    {
        _credits = Credits(new CountingTextReader());
        var chapters = ChaptersEndingInTheNoticesBottom();
        Assert.True((await FoundAsync(chapters[1])).GetProperty("tailOffset").GetDouble() > 0);
        var worker = Worker();

        await TeachTheWholeNoticeAsync();
        await worker.RecheckBankAsync(default);

        // Sent back, as it was when a partial tail counted as nothing, and searched again against the entry.
        Assert.Equal(0, (await RowAsync(chapters[1]))!.DetectorVersion);
        Assert.Equal(1, (await FoundAsync(chapters[1])).GetProperty("tailPages").GetInt32());
    }

    [Fact]
    public async Task A_chapter_too_short_to_search_is_never_sent_back_by_the_bank_recheck()
    {
        _credits = Credits(Path.Combine(AppContext.BaseDirectory, PageTextReader.FolderName));
        var credit = await File.ReadAllBytesAsync(Fixture("ocr-credit-page.png"));
        var worker = Worker();
        ChaptersOpeningWith(credit);
        await worker.ScanSeriesAsync(_seriesId, "en", default);
        Assert.Equal(1, BankCount());

        var shortSeries = AddSeries("Short");
        var shortChapter = AddEncodedChapter(1, StoryPngs(60, CreditDetector.MinPages - 2), seriesId: shortSeries, folder: "Short");
        await worker.ScanSeriesAsync(shortSeries, "en", default);
        Assert.Equal(CreditDetector.Version, (await RowAsync(shortChapter))!.DetectorVersion);

        await worker.RecheckBankAsync(default);
        Assert.Equal(CreditDetector.Version, (await RowAsync(shortChapter))!.DetectorVersion);
        Assert.Equal(0, await worker.RecheckBankAsync(default));
    }
}
