using System.IO.Compression;
using System.Xml.Linq;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Opds;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// The OPDS catalogue: which entries a feed holds, and the details reading apps actually key off —
/// the PSE streaming link's page count and placeholder, acquisition types, and paging links.
/// </summary>
public sealed class OpdsCatalogTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly ReaderArchiveCache _archives = new(NullLogger<ReaderArchiveCache>.Instance);
    private readonly ReadingProgressGate _gate = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maki-opds-tests", Guid.NewGuid().ToString("N"));
    private static readonly OpdsContext Ctx = new(string.Empty, "tok3n");

    public OpdsCatalogTests() => Directory.CreateDirectory(_root);

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

    private OpdsCatalogService Catalog()
    {
        var context = _db.NewContext();
        var scopeFactory = _db.ScopeFactory();
        var pusher = new KavitaProgressPusher(
            scopeFactory,
            new SettingsService(scopeFactory),
            new UserSettingsStoreService(scopeFactory),
            new KavitaUserResolver(scopeFactory, new SettingsService(scopeFactory)),
            null!,
            NullLogger<KavitaProgressPusher>.Instance);
        var reader = new ReaderService(context, _archives,
            new ReadingProgressService(context, _gate, NullLogger<ReadingProgressService>.Instance),
            pusher, NullLogger<ReaderService>.Instance);
        return new OpdsCatalogService(context, reader, new ContinueReadingService(context));
    }

    // ---- feed shape ----

    [Fact]
    public void RootIsANavigationFeedWithASearchDescriptionLink()
    {
        var xml = XDocument.Parse(OpdsXml.Render(Catalog().Root(Ctx)));
        var links = xml.Root!.Elements(OpdsXml.Atom + "link").ToList();

        Assert.Equal(
            OpdsXml.OpenSearchType,
            links.Single(l => (string)l.Attribute("rel")! == "search").Attribute("type")!.Value);
        Assert.Equal("/api/v1/opds/tok3n/search.xml",
            links.Single(l => (string)l.Attribute("rel")! == "search").Attribute("href")!.Value);
        Assert.Equal(3, xml.Root.Elements(OpdsXml.Atom + "entry").Count());
    }

    [Fact]
    public void RenderedFeedDeclaresUtf8()
    {
        // XmlWriter over a StringBuilder is a UTF-16 sink; letting it write the declaration would
        // claim utf-16 on a body served as UTF-8, and strict parsers reject that.
        var xml = OpdsXml.Render(Catalog().Root(Ctx));

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", xml);
    }

    [Fact]
    public async Task SeriesFeedSkipsSeriesWithNothingDownloaded()
    {
        SeedSeriesWithChapters("Downloaded", "a.cbz", ["001.jpg"], [1m]);
        _db.SeedSeries("Tracked Only");

        var feed = await Catalog().SeriesFeedAsync(Ctx, 0, CancellationToken.None);

        var entry = Assert.Single(feed.Entries);
        Assert.Equal("Downloaded", entry.Title);
        Assert.Equal(1, feed.TotalResults);
    }

    // ---- chapter entries ----

    [Fact]
    public async Task ChapterEntryCarriesAPseStreamAndACbzAcquisitionLink()
    {
        var (seriesId, _) = SeedSeriesWithChapters(
            "Streamed", "s.cbz", ["001.jpg", "002.jpg", "003.jpg"], [5m]);

        var feed = await Catalog().ChaptersFeedAsync(Ctx, seriesId, 0, CancellationToken.None);
        var entry = Assert.Single(feed!.Entries);

        Assert.Equal(OpdsFeedKind.Acquisition, feed.Kind);
        Assert.Equal("Ch.5", entry.Title);
        Assert.Equal(3, entry.Stream!.Count);
        // Zero-based and left verbatim for the client to substitute.
        Assert.EndsWith("/page/{pageNumber}", entry.Stream.HrefTemplate);
        Assert.Null(entry.Stream.LastRead);

        var acquisition = entry.Links!.First(l => l.Rel == OpdsXml.AcquisitionRel);
        Assert.Equal(OpdsXml.ComicBookType, acquisition.Type);
        Assert.EndsWith("/file", acquisition.Href);
        // Readers that only recognise the open-access relation still need a download link.
        Assert.Contains(entry.Links!, l => l.Rel == OpdsXml.OpenAccessRel);
    }

    [Fact]
    public async Task PseCountIsTheChaptersSliceNotTheWholeVolume()
    {
        // One archive backing three chapters: a reader told the volume's page count would page
        // right past the end of the chapter it opened.
        var (seriesId, chapters) = SeedSeriesWithChapters("Volume", "v1.cbz",
            [
                "S - c001 - p001.png", "S - c001 - p002.png",
                "S - c002 - p001.png",
                "S - c003 - p001.png", "S - c003 - p002.png", "S - c003 - p003.png"
            ],
            [1m, 2m, 3m]);

        var feed = await Catalog().ChaptersFeedAsync(Ctx, seriesId, 0, CancellationToken.None);
        var counts = feed!.Entries.ToDictionary(e => e.Title, e => e.Stream!.Count);

        Assert.Equal(2, counts["Ch.1"]);
        Assert.Equal(1, counts["Ch.2"]);
        Assert.Equal(3, counts["Ch.3"]);
        Assert.Equal(3, chapters.Count);
    }

    [Fact]
    public async Task ChapterWhoseFileIsGoneIsDroppedRatherThanListed()
    {
        var (seriesId, _) = SeedSeriesWithChapters("Missing", "gone.cbz", ["001.jpg"], [1m]);
        File.Delete(Path.Combine(_root, "gone.cbz"));

        var feed = await Catalog().ChaptersFeedAsync(Ctx, seriesId, 0, CancellationToken.None);

        Assert.Empty(feed!.Entries);
    }

    [Fact]
    public async Task OneShotsSortAfterNumberedChapters()
    {
        var (seriesId, _) = SeedSeriesWithChapters("Order", "o.cbz", ["001.jpg"], [2m, null, 1m]);

        var feed = await Catalog().ChaptersFeedAsync(Ctx, seriesId, 0, CancellationToken.None);

        Assert.Equal(["Ch.1", "Ch.2", "One-shot"], feed!.Entries.Select(e => e.Title));
    }

    [Fact]
    public async Task LastReadReportsPagesReadNotThePageIndex()
    {
        // pse:lastRead is what a reader renders as "read N of M", so it is one-based.
        var (seriesId, chapters) = SeedSeriesWithChapters(
            "Progress", "p.cbz", ["001.jpg", "002.jpg", "003.jpg"], [1m]);
        await using (var db = _db.NewContext())
        {
            db.ChapterProgress.Add(new ChapterProgress
            {
                UserId = 1,
                SeriesId = seriesId,
                ChapterId = chapters[0],
                PageIndex = 1,
                PageCount = 3,
                StartedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var feed = await Catalog().ChaptersFeedAsync(Ctx, seriesId, 0, CancellationToken.None);

        Assert.Equal(2, Assert.Single(feed!.Entries).Stream!.LastRead);
    }

    [Fact]
    public async Task LastReadDateRidesAlongSoReadersCanResolveSyncConflicts()
    {
        var stamp = new DateTime(2026, 5, 4, 12, 0, 0, DateTimeKind.Utc);
        var (seriesId, chapters) = SeedSeriesWithChapters("Dated", "dt.cbz", ["001.jpg", "002.jpg"], [1m]);
        await using (var db = _db.NewContext())
        {
            db.ChapterProgress.Add(new ChapterProgress
            {
                UserId = 1,
                SeriesId = seriesId,
                ChapterId = chapters[0],
                PageIndex = 0,
                PageCount = 2,
                StartedAt = stamp,
                UpdatedAt = stamp
            });
            await db.SaveChangesAsync();
        }

        var feed = await Catalog().ChaptersFeedAsync(Ctx, seriesId, 0, CancellationToken.None);

        Assert.Equal(stamp, Assert.Single(feed!.Entries).Stream!.LastReadDate);
    }

    // ---- multi-language ----

    [Fact]
    public async Task TheSameChapterInTwoLanguagesIsLabelledWithEach()
    {
        // Chapter identity is (Number, Language), and ChapterLabel renders only the number — two
        // entries both called "Ch.1" would be indistinguishable in a reader.
        var seriesId = SeedMultiLanguage("Bilingual", "bi.cbz", ["001.jpg"], [(1m, "en"), (1m, "es"), (2m, "en")]);

        var feed = await Catalog().ChaptersFeedAsync(Ctx, seriesId, 0, CancellationToken.None);

        Assert.Contains("Ch.1 [en]", feed!.Entries.Select(e => e.Title));
        Assert.Contains("Ch.1 [es]", feed.Entries.Select(e => e.Title));
        // Chapter 2 exists in one language only, so it stays clean.
        Assert.Contains("Ch.2", feed.Entries.Select(e => e.Title));
    }

    [Fact]
    public async Task ASingleLanguageLibraryNeverSeesALanguageTag()
    {
        var (seriesId, _) = SeedSeriesWithChapters("Mono", "mono.cbz", ["001.jpg"], [1m, 2m]);

        var feed = await Catalog().ChaptersFeedAsync(Ctx, seriesId, 0, CancellationToken.None);

        Assert.All(feed!.Entries, e => Assert.DoesNotContain("[", e.Title));
    }

    [Fact]
    public async Task ACompletedChapterReportsEveryPageRead()
    {
        var (seriesId, chapters) = SeedSeriesWithChapters(
            "Done", "d.cbz", ["001.jpg", "002.jpg"], [1m]);
        await using (var db = _db.NewContext())
        {
            db.ChapterProgress.Add(new ChapterProgress
            {
                UserId = 1,
                SeriesId = seriesId,
                ChapterId = chapters[0],
                PageIndex = 1,
                // Kavita-imported rows carry PageCount 0; the count must come off the slice.
                PageCount = 0,
                Completed = true,
                External = true,
                StartedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var feed = await Catalog().ChaptersFeedAsync(Ctx, seriesId, 0, CancellationToken.None);

        Assert.Equal(2, Assert.Single(feed!.Entries).Stream!.LastRead);
    }

    // ---- paging ----

    [Fact]
    public async Task PagingLinksStopAtTheEndOfTheCatalogue()
    {
        for (var i = 0; i < OpdsCatalogService.SeriesPageSize + 1; i++)
        {
            SeedSeriesWithChapters($"Series {i:D3}", $"s{i}.cbz", ["001.jpg"], [1m]);
        }

        var catalog = Catalog();
        var first = await catalog.SeriesFeedAsync(Ctx, 0, CancellationToken.None);
        var second = await catalog.SeriesFeedAsync(Ctx, 1, CancellationToken.None);

        Assert.Contains(first.Links, l => l.Rel == "next");
        Assert.DoesNotContain(first.Links, l => l.Rel == "previous");
        Assert.DoesNotContain(second.Links, l => l.Rel == "next");
        Assert.Contains(second.Links, l => l.Rel == "previous");
        Assert.Single(second.Entries);
    }

    [Fact]
    public async Task SearchMatchesTitleCaseInsensitively()
    {
        SeedSeriesWithChapters("Hajime no Ippo", "h.cbz", ["001.jpg"], [1m]);
        SeedSeriesWithChapters("Vagabond", "v.cbz", ["001.jpg"], [1m]);

        var feed = await Catalog().SearchFeedAsync(Ctx, "IPPO", 0, CancellationToken.None);

        Assert.Equal("Hajime no Ippo", Assert.Single(feed.Entries).Title);
    }

    // ---- shelves ----

    [Fact]
    public async Task OnDeckOffersTheNextUnreadChapterOfARecentlyReadSeries()
    {
        var (seriesId, chapters) = SeedSeriesWithChapters(
            "Deck", "deck.cbz", ["001.jpg"], [1m, 2m]);
        await using (var db = _db.NewContext())
        {
            db.ChapterProgress.Add(new ChapterProgress
            {
                UserId = 1,
                SeriesId = seriesId,
                ChapterId = chapters[0],
                PageIndex = 0,
                PageCount = 1,
                Completed = true,
                StartedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var feed = await Catalog().OnDeckFeedAsync(Ctx, CancellationToken.None);

        // Mixed-series shelves prefix the series title — one bare "Ch.2" among many is useless.
        Assert.Equal("Deck — Ch.2", Assert.Single(feed.Entries).Title);
    }

    [Fact]
    public async Task RecentlyAddedListsTheNewestFilesFirst()
    {
        SeedSeriesWithChapters("Old", "old.cbz", ["001.jpg"], [1m], addedAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        SeedSeriesWithChapters("New", "new.cbz", ["001.jpg"], [1m], addedAt: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        var feed = await Catalog().RecentFeedAsync(Ctx, CancellationToken.None);

        Assert.Equal(["New — Ch.1", "Old — Ch.1"], feed.Entries.Select(e => e.Title));
    }

    // ---- seeding ----

    private int SeedMultiLanguage(
        string title, string cbzName, string[] entries, (decimal? Number, string Language)[] chapters)
    {
        var (seriesId, _) = SeedSeriesWithChapters(title, cbzName, entries, [], languaged: chapters);
        return seriesId;
    }

    private (int SeriesId, List<int> ChapterIds) SeedSeriesWithChapters(
        string title, string cbzName, string[] entries, decimal?[] numbers, DateTime? addedAt = null,
        (decimal? Number, string Language)[]? languaged = null)
    {
        var seriesId = _db.SeedSeries(title);
        using var db = _db.NewContext();
        var series = db.Series.First(s => s.Id == seriesId);
        db.RootFolders.First(r => r.Id == series.RootFolderId).Path = _root;
        series.FolderName = "";
        db.SaveChanges();

        var path = Path.Combine(_root, cbzName);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var entry in entries)
            {
                using var stream = archive.CreateEntry(entry).Open();
                stream.WriteByte(0xFF);
            }
        }

        var file = new ChapterFile
        {
            SeriesId = seriesId,
            RelativePath = cbzName,
            Size = new FileInfo(path).Length,
            SourceName = "Test",
            DateAdded = addedAt ?? DateTime.UtcNow
        };
        db.ChapterFiles.Add(file);
        db.SaveChanges();

        var spec = languaged ?? numbers.Select(n => (Number: n, Language: "en")).ToArray();
        var rows = spec.Select(c => new Chapter
        {
            SeriesId = seriesId,
            Number = c.Number,
            IsOneShot = c.Number is null,
            Language = c.Language,
            ChapterFileId = file.Id
        }).ToList();
        db.Chapters.AddRange(rows);
        db.SaveChanges();

        return (seriesId, rows.Select(r => r.Id).ToList());
    }
}
