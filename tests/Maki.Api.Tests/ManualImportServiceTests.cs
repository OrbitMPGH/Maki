using System.IO.Compression;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Kavita;
using Maki.Core.Security;
using Maki.Core.Sources;
using Maki.Core.Tests;
using Maki.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// Manual import from a server path: who may read which path, which paths are refused because they
/// are the library itself, and the import going through the torrent pipeline with a move or a copy.
/// </summary>
public class ManualImportServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeAppSettings _settings = new();
    private readonly string _base =
        Path.Combine(Path.GetTempPath(), "maki-manual-import-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _root;
    private readonly string _outside;

    public ManualImportServiceTests()
    {
        _root = Path.Combine(_base, "library");
        _outside = Path.Combine(_base, "incoming");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_base))
        {
            Directory.Delete(_base, recursive: true);
        }
    }

    private static void WriteZip(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry("001.png").Open());
        writer.Write("page " + Path.GetFileName(path));
    }

    private (int RootId, int SeriesId) Seed()
    {
        using var db = _db.NewContext();
        var root = new RootFolder { Path = _root };
        db.RootFolders.Add(root);
        db.SaveChanges();
        var series = new Series
        {
            Title = "Vagabond", SortTitle = "Vagabond", FolderName = "Vagabond", RootFolderId = root.Id,
        };
        db.Series.Add(series);
        db.SaveChanges();
        db.Chapters.AddRange(
            new Chapter { SeriesId = series.Id, Number = 1, Language = "en" },
            new Chapter { SeriesId = series.Id, Number = 2, Language = "en" });
        db.SaveChanges();
        return (root.Id, series.Id);
    }

    private ManualImportService Service(MakiDbContext db, ICurrentUser? user = null)
    {
        var scans = new KavitaScanService(
            new KavitaClient(new StubHttpClientFactory("{}")), _settings, _db.ScopeFactory(),
            NullLogger<KavitaScanService>.Instance);
        var archives = new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance);
        var registry = new SourceRegistry([]);
        var linker = new CbzLinkService(
            db, registry, scans, new StatsEventService(db), archives, new SourceAvailability(_settings, registry),
            TestQuality.Create(registry), _settings, NullLogger<CbzLinkService>.Instance);
        var rename = new SeriesRenameService(
            db, new NamingService(_settings), scans, new TestLocalizer(), NullLogger<SeriesRenameService>.Instance);
        var importer = new TorrentImportService(
            db, null!, null!, linker, rename, archives, _settings,
            new UpgradeEvaluationService(db, TestQuality.Create(registry)), new RecordingInbox(),
            NullLogger<TorrentImportService>.Instance);
        return new ManualImportService(db, importer, user ?? new TestCurrentUser(1),
            NullLogger<ManualImportService>.Instance);
    }

    [Fact]
    public async Task Only_an_admin_may_read_a_path_outside_the_root_folders()
    {
        var (rootId, _) = Seed();
        Directory.CreateDirectory(Path.Combine(_root, "unsorted"));
        await using var db = _db.NewContext();
        var granted = new TestCurrentUser(2, permissions: MakiPermission.ImportLibrary, allRootFolders: false,
            rootFolderIds: new HashSet<int> { rootId });
        var ungranted = new TestCurrentUser(3, permissions: MakiPermission.ImportLibrary, allRootFolders: false,
            rootFolderIds: new HashSet<int>());

        Assert.NotNull(await Service(db).ReadablePathAsync(_outside, default));
        Assert.Null(await Service(db, granted).ReadablePathAsync(_outside, default));
        Assert.NotNull(await Service(db, granted).ReadablePathAsync(Path.Combine(_root, "unsorted"), default));
        Assert.Null(await Service(db, granted).ReadablePathAsync(Path.Combine(_root, "..", "incoming"), default));
        Assert.Null(await Service(db, ungranted).ReadablePathAsync(Path.Combine(_root, "unsorted"), default));

        var top = await Service(db, granted).BrowseAsync(null, default);
        Assert.Equal([_root], top!.Folders.Select(f => f.Path));
    }

    [Fact]
    public async Task Browsing_lists_comics_with_their_parsed_numbers()
    {
        Seed();
        WriteZip(Path.Combine(_outside, "Vagabond v03 c021.cbz"));
        File.WriteAllText(Path.Combine(_outside, "readme.txt"), "not a comic");
        Directory.CreateDirectory(Path.Combine(_outside, "more"));
        await using var db = _db.NewContext();

        var listing = await Service(db).BrowseAsync(_outside, default);

        var file = Assert.Single(listing!.Files);
        Assert.Equal("21", file.Chapter);
        Assert.Equal(3, file.Volume);
        Assert.Equal(["more"], listing.Folders.Select(f => f.Name));
    }

    [Fact]
    public async Task A_path_that_is_a_series_folder_or_holds_a_root_is_refused()
    {
        var (_, seriesId) = Seed();
        WriteZip(Path.Combine(_root, "Vagabond", "Vagabond 001.cbz"));
        await using var db = _db.NewContext();
        var service = Service(db);

        Assert.Equal("error.libraryImport.folderOwnedByOtherSeries",
            (await service.SourceProblemAsync(Path.Combine(_root, "Vagabond"), default))?.Key);
        Assert.Equal("error.libraryImport.folderOwnedByOtherSeries",
            (await service.SourceProblemAsync(_base, default))?.Key);
        Assert.Null(await service.SourceProblemAsync(_outside, default));

        var plan = await service.PlanAsync(_root, seriesId, default);
        Assert.Equal("error.libraryImport.folderOwnedByOtherSeries", plan!.ErrorKey);
    }

    [Fact]
    public async Task A_move_import_places_the_files_links_them_and_records_the_history_row()
    {
        var (_, seriesId) = Seed();
        WriteZip(Path.Combine(_outside, "Vagabond 001.cbz"));
        WriteZip(Path.Combine(_outside, "Vagabond 002.cbz"));
        await using var db = _db.NewContext();

        var plan = await Service(db).PlanAsync(_outside, seriesId, default);
        Assert.Null(plan!.ErrorKey);
        Assert.Equal(2, plan.NewChapterCount);

        var result = await Service(db).ImportAsync(
            _outside, seriesId, TorrentImportMode.SkipExisting, null, ImportPlacement.Move, default);

        Assert.True(result!.Applied, result.ErrorKey);
        Assert.Equal(2, result.Imported);
        Assert.Empty(Directory.GetFiles(_outside));

        await using var check = _db.NewContext();
        Assert.Equal(2, await check.Chapters.CountAsync(c => c.ChapterFileId != null));
        Assert.All(await check.ChapterFiles.ToListAsync(), f => Assert.Equal(ManualImportService.ManualSourceName, f.SourceName));
        var history = await check.DownloadQueue.SingleAsync();
        Assert.Equal(QueueStatus.Completed, history.Status);
        Assert.Equal("incoming", history.Title);
    }

    [Fact]
    public async Task A_copy_import_leaves_the_originals_and_a_hidden_series_is_not_found()
    {
        var (_, seriesId) = Seed();
        WriteZip(Path.Combine(_outside, "Vagabond 001.cbz"));
        await using var db = _db.NewContext();

        var result = await Service(db).ImportAsync(
            Path.Combine(_outside, "Vagabond 001.cbz"), seriesId, TorrentImportMode.SkipExisting, null,
            ImportPlacement.LinkOrCopy, default);

        Assert.True(result!.Applied, result.ErrorKey);
        Assert.True(File.Exists(Path.Combine(_outside, "Vagabond 001.cbz")));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "Vagabond"), "*.cbz"));
        Assert.Null(await Service(db).ImportAsync(
            _outside, seriesId + 100, TorrentImportMode.SkipExisting, null, ImportPlacement.Move, default));
    }
}
