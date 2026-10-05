using System.IO.Compression;
using Maki.Api.Hubs;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Kavita;
using Maki.Core.Metadata;
using Maki.Core.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// Import and rescan act on the user's own library, so none of them may be the reason a file
/// disappears: not a folder merged into itself, not a name collision, not an unmounted share.
/// </summary>
public class LibraryFolderSafetyTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeAppSettings _settings = new();
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "maki-folder-safety-" + Guid.NewGuid().ToString("N")[..8]);

    public LibraryFolderSafetyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static void WriteComic(string path, string page = "page")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry("001.png").Open());
        writer.Write(page);
    }

    private CbzLinkService LinkService(Maki.Data.MakiDbContext db)
    {
        var registry = new SourceRegistry([]);
        return new CbzLinkService(
            db,
            registry,
            new KavitaScanService(
                new KavitaClient(new StubHttpClientFactory("{}")),
                _settings,
                _db.ScopeFactory(),
                NullLogger<KavitaScanService>.Instance),
            new StatsEventService(db),
            new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance),
            new SourceAvailability(_settings, registry),
            TestQuality.Create(registry),
            _settings,
            NullLogger<CbzLinkService>.Instance);
    }

    private (int RootId, int SeriesId) Seed(string folderName, int? mangaBakaId = null, params string[] files)
    {
        using var db = _db.NewContext();
        var root = new RootFolder { Path = _root };
        db.RootFolders.Add(root);
        db.SaveChanges();
        var series = new Series
        {
            Title = "Chainsaw Man", SortTitle = "Chainsaw Man", FolderName = folderName,
            RootFolderId = root.Id, MangaBakaId = mangaBakaId
        };
        db.Series.Add(series);
        db.SaveChanges();
        db.Chapters.AddRange(Enumerable.Range(1, 2).Select(n => new Chapter { SeriesId = series.Id, Number = n, Language = "en" }));
        foreach (var file in files)
        {
            db.ChapterFiles.Add(new ChapterFile { SeriesId = series.Id, RelativePath = file, SourceName = "import" });
        }

        db.SaveChanges();
        return (root.Id, series.Id);
    }

    [Fact]
    public void MergeKeepsTheSourceCopyOfEveryCollidingFile()
    {
        var source = Path.Combine(_root, "old");
        var target = Path.Combine(_root, "new");
        WriteComic(Path.Combine(source, "c001.cbz"), "mine");
        WriteComic(Path.Combine(source, "sub", "c002.cbz"));
        File.WriteAllText(Path.Combine(source, "cover.jpg"), "user cover");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "cover.jpg"), "maki cover");

        var leftBehind = LibraryImportService.MergeDirectory(source, target);

        Assert.Equal(["cover.jpg"], leftBehind);
        Assert.Equal("user cover", File.ReadAllText(Path.Combine(source, "cover.jpg")));
        Assert.Equal("maki cover", File.ReadAllText(Path.Combine(target, "cover.jpg")));
        Assert.True(File.Exists(Path.Combine(target, "c001.cbz")));
        Assert.True(File.Exists(Path.Combine(target, "sub", "c002.cbz")));
        Assert.False(Directory.Exists(Path.Combine(source, "sub")));
    }

    [Fact]
    public void MergeRemovesTheSourceOnlyOnceEverythingMoved()
    {
        var source = Path.Combine(_root, "old");
        var target = Path.Combine(_root, "new");
        WriteComic(Path.Combine(source, "nested", "c001.cbz"));
        Directory.CreateDirectory(target);

        Assert.Empty(LibraryImportService.MergeDirectory(source, target));
        Assert.False(Directory.Exists(source));
        Assert.True(File.Exists(Path.Combine(target, "nested", "c001.cbz")));
    }

    [Fact]
    public async Task ReimportIntoAFolderThatDiffersOnlyInCaseKeepsEveryFile()
    {
        var (rootId, seriesId) = Seed("Chainsaw Man", mangaBakaId: 42);
        WriteComic(Path.Combine(_root, "chainsaw man", "Chainsaw Man c001.cbz"));
        WriteComic(Path.Combine(_root, "chainsaw man", "Chainsaw Man c002.cbz"));

        ImportResult result;
        await using (var db = _db.NewContext())
        {
            var service = new LibraryImportService(
                db, [new FixedProvider(42)], null!, null!, null!, LinkService(db),
                new EventBroadcaster(new NoopHubContext(), _db.ScopeFactory()),
                _settings, new NamingService(_settings), null!, null!, new TestLocalizer(),
                new TestCurrentUser(1), new RecordingNotifications(), new TestUserLocaleResolver(), new TestLocalizer(),
                NullLogger<LibraryImportService>.Instance);
            result = await service.ImportAsync(rootId, new ImportRequestItem("chainsaw man", "42"));
        }

        Assert.True(result.Success, result.Error);
        var comics = Directory.EnumerateFiles(_root, "*.cbz", SearchOption.AllDirectories).ToList();
        Assert.Equal(2, comics.Count);
        Assert.All(comics, c => Assert.Equal("Chainsaw Man", Path.GetFileName(Path.GetDirectoryName(c))));
        await using var check = _db.NewContext();
        Assert.Equal(2, await check.ChapterFiles.CountAsync(f => f.SeriesId == seriesId));
        Assert.Equal("Chainsaw Man", (await check.Series.SingleAsync(s => s.Id == seriesId)).FolderName);
    }

    [Fact]
    public async Task RescanFreesRowsWhenTheSeriesFolderIsGoneFromAReadableRoot()
    {
        var (_, seriesId) = Seed("Gone", files: [Path.Combine("Gone", "Chainsaw Man c001.cbz")]);
        Directory.CreateDirectory(Path.Combine(_root, "Other Series"));

        RescanResult result;
        await using (var db = _db.NewContext())
        {
            result = await LinkService(db).RescanSeriesAsync(
                await db.Series.Include(s => s.RootFolder).SingleAsync(s => s.Id == seriesId));
        }

        Assert.Equal(1, result.Removed);
        await using var check = _db.NewContext();
        Assert.Equal(0, await check.ChapterFiles.CountAsync(f => f.SeriesId == seriesId));
    }

    [Fact]
    public async Task RescanKeepsEveryRowWhenTheRootIsEmpty()
    {
        var (_, seriesId) = Seed("Gone", files: [Path.Combine("Gone", "Chainsaw Man c001.cbz")]);

        RescanResult result;
        await using (var db = _db.NewContext())
        {
            result = await LinkService(db).RescanSeriesAsync(
                await db.Series.Include(s => s.RootFolder).SingleAsync(s => s.Id == seriesId));
        }

        Assert.Equal(0, result.Removed);
        await using var check = _db.NewContext();
        Assert.Equal(1, await check.ChapterFiles.CountAsync(f => f.SeriesId == seriesId));
    }

    [Fact]
    public async Task RescanChangesNothingWhenTheRootIsUnreachable()
    {
        var (_, seriesId) = Seed("Chainsaw Man", files: [Path.Combine("Chainsaw Man", "Chainsaw Man c001.cbz")]);
        Directory.Delete(_root, recursive: true);

        RescanResult result;
        await using (var db = _db.NewContext())
        {
            result = await LinkService(db).RescanSeriesAsync(
                await db.Series.Include(s => s.RootFolder).SingleAsync(s => s.Id == seriesId));
        }

        Assert.True(result.RootUnavailable);
        await using var check = _db.NewContext();
        Assert.Equal(1, await check.ChapterFiles.CountAsync(f => f.SeriesId == seriesId));
    }

    [Fact]
    public async Task RescanMatchesARowStoredWithTheOtherSeparator()
    {
        var other = Path.DirectorySeparatorChar == '\\' ? '/' : '\\';
        var (_, seriesId) = Seed("Chainsaw Man", files: [$"Chainsaw Man{other}Chainsaw Man c001.cbz"]);
        WriteComic(Path.Combine(_root, "Chainsaw Man", "Chainsaw Man c001.cbz"));

        RescanResult result;
        await using (var db = _db.NewContext())
        {
            result = await LinkService(db).RescanSeriesAsync(
                await db.Series.Include(s => s.RootFolder).SingleAsync(s => s.Id == seriesId));
        }

        Assert.Equal(0, result.Removed);
        Assert.Equal(0, result.NewFiles);
        await using var check = _db.NewContext();
        Assert.Equal(1, await check.ChapterFiles.CountAsync(f => f.SeriesId == seriesId));
    }

    [Fact]
    public async Task RescanLeavesComicInfoAloneWhenWritingItIsOff()
    {
        _settings.Set(SettingKeys.LibraryWriteComicInfo, "false");
        var (_, seriesId) = Seed("Chainsaw Man");
        var path = Path.Combine(_root, "Chainsaw Man", "Chainsaw Man c001.cbz");
        WriteComic(path);
        var before = await File.ReadAllBytesAsync(path);

        await using (var db = _db.NewContext())
        {
            await LinkService(db).RescanSeriesAsync(
                await db.Series.Include(s => s.RootFolder).SingleAsync(s => s.Id == seriesId));
        }

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        await using var check = _db.NewContext();
        Assert.Equal(1, await check.ChapterFiles.CountAsync(f => f.SeriesId == seriesId));
    }

    [Fact]
    public void AFolderNameAnotherSeriesUsesIsDisambiguated()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Berserk", "Berserk [mb-7]" };

        Assert.Equal("Monster", SeriesCreationService.FreeFolderName("Monster", 7, n => !taken.Contains(n)));
        Assert.Equal("Berserk (2)", SeriesCreationService.FreeFolderName("Berserk", 7, n => !taken.Contains(n)));
        Assert.Equal("Berserk [mb-8]", SeriesCreationService.FreeFolderName("Berserk", 8, n => !taken.Contains(n)));
    }

    private sealed class FixedProvider(int mangaBakaId) : IMetadataProvider
    {
        public string Name => "fake";

        public Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(
            string query, string maxContentRating, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MetadataSearchResult>>([]);

        public Task<SeriesMetadata?> GetAsync(string providerId, CancellationToken ct = default) =>
            Task.FromResult<SeriesMetadata?>(new SeriesMetadata
            {
                ProviderId = providerId, Title = "Chainsaw Man", MangaBakaId = mangaBakaId
            });
    }
}
