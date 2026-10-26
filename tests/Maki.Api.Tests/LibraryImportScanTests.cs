using System.IO.Compression;
using Maki.Api.Hubs;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Kavita;
using Maki.Core.Metadata;
using Maki.Core.Security;
using Maki.Core.Sources;
using Maki.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// Which folders the import page offers. A folder a series already has files in is gone for good,
/// wherever <see cref="Series.FolderName"/> points; a folder of a series with no files yet is offered
/// only when it holds something to link, and is flagged so the page can hide it by default.
/// </summary>
public class LibraryImportScanTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "maki-import-scan-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly CountingProvider _provider = new();
    private readonly FakeAppSettings _settings = new();

    public LibraryImportScanTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private async Task<List<ImportScanCandidate>> ScanAsync(int rootFolderId, ICurrentUser? currentUser = null)
    {
        await using var db = _db.NewContext();
        var service = new LibraryImportService(
            db, [_provider], null!, null!, new SourceMatchQueue(), null!, null!, null!, null!, null!, null!,
            currentUser ?? new TestCurrentUser(1),
            null!, NullLogger<LibraryImportService>.Instance);
        return await service.ScanAsync(rootFolderId);
    }

    private async Task<ImportResult> ImportAsync(
        int rootFolderId, string folderName, ICurrentUser? currentUser = null)
    {
        await using var db = _db.NewContext();
        var service = new LibraryImportService(
            db, [_provider], null!, null!, new SourceMatchQueue(), null!, null!, null!, null!, null!, new TestLocalizer(),
            currentUser ?? new TestCurrentUser(1),
            null!, NullLogger<LibraryImportService>.Instance);
        return await service.ImportAsync(rootFolderId, new ImportRequestItem(folderName, "1"));
    }

    /// <summary>An import that links files and restores an archived series for real.</summary>
    private async Task<ImportResult> LinkingImportAsync(int rootFolderId, string folderName, string providerId)
    {
        await using var db = _db.NewContext();
        var registry = new SourceRegistry([]);
        var link = new CbzLinkService(
            db, registry,
            new KavitaScanService(
                new KavitaClient(new StubHttpClientFactory("{}")), _settings, _db.ScopeFactory(),
                NullLogger<KavitaScanService>.Instance),
            new StatsEventService(db), new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance),
            new SourceAvailability(_settings, registry), TestQuality.Create(registry), _settings,
            NullLogger<CbzLinkService>.Instance);
        var archive = new SeriesArchiveService(
            db, null!, null!, null!, null!, null!, null!, null!, null!, new TestCurrentUser(1), null!,
            TimeProvider.System, _db.ScopeFactory(), NullLogger<SeriesArchiveService>.Instance);
        var service = new LibraryImportService(
            db, [_provider], null!, link, new SourceMatchQueue(),
            new EventBroadcaster(new NoopHubContext(), _db.ScopeFactory()), _settings, new NamingService(_settings),
            new StatsEventService(db), null!, new TestLocalizer(), new TestCurrentUser(1), archive,
            NullLogger<LibraryImportService>.Instance);
        return await service.ImportAsync(
            rootFolderId, new ImportRequestItem(folderName, providerId), updateComicInfo: false);
    }

    private int SeedRoot()
    {
        using var db = _db.NewContext();
        var root = new RootFolder { Path = _root };
        db.RootFolders.Add(root);
        db.SaveChanges();
        return root.Id;
    }

    private Series SeedSeries(int rootFolderId, string folderName, int? mangaBakaId = null, params string[] files)
    {
        using var db = _db.NewContext();
        var series = new Series
        {
            Title = folderName,
            SortTitle = folderName,
            FolderName = folderName,
            RootFolderId = rootFolderId,
            MangaBakaId = mangaBakaId
        };
        db.Series.Add(series);
        db.SaveChanges();
        foreach (var file in files)
        {
            db.ChapterFiles.Add(new ChapterFile { SeriesId = series.Id, RelativePath = file, SourceName = "import" });
        }

        db.SaveChanges();
        return series;
    }

    private void WriteComic(string folder, string name)
    {
        var dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        using var archive = ZipFile.Open(Path.Combine(dir, name), ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry("001.png").Open());
        writer.Write("page");
    }

    [Fact]
    public async Task AFolderASeriesHasFilesInIsNotOfferedEvenWhenFolderNameMovedOn()
    {
        var root = SeedRoot();
        // The keep-new-standard shape: FolderName names the standard folder, the files sit in the original.
        SeedSeries(root, "Berserk (1989)", 1, Path.Combine("berserk raws", "Berserk v01.cbz"));
        WriteComic("berserk raws", "Berserk v01.cbz");

        var candidates = await ScanAsync(root);

        Assert.DoesNotContain(candidates, c => c.FolderName == "berserk raws");
    }

    [Fact]
    public async Task TheEmptyFolderOfASeriesWithNoFilesIsNotOffered()
    {
        var root = SeedRoot();
        SeedSeries(root, "Vagabond", 2);
        Directory.CreateDirectory(Path.Combine(_root, "Vagabond"));

        var candidates = await ScanAsync(root);

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task ASeriesFolderWithUnlinkedComicsIsFlaggedAndMatchedToThatSeries()
    {
        var root = SeedRoot();
        var series = SeedSeries(root, "Vagabond", 2);
        WriteComic("Vagabond", "Vagabond v01.cbz");
        WriteComic("Monster", "Monster v01.cbz");

        var candidates = await ScanAsync(root);

        var inLibrary = Assert.Single(candidates, c => c.FolderName == "Vagabond");
        Assert.Equal(series.Id, inLibrary.ExistingSeriesId);
        var match = Assert.Single(inLibrary.Matches);
        Assert.Equal("2", match.ProviderId);

        var fresh = Assert.Single(candidates, c => c.FolderName == "Monster");
        Assert.Null(fresh.ExistingSeriesId);
        // Only the new folder needed a provider search.
        Assert.Equal(["Monster"], _provider.Queries);
    }

    [Fact]
    public async Task AnArchivedSeriesFolderWithNoFilesIsOfferedAsARestoreAndImportingRestoresItAndLinks()
    {
        var root = SeedRoot();
        var series = SeedSeries(root, "Vagabond", 2);
        await using (var db = _db.NewContext())
        {
            var row = await db.Series.SingleAsync(s => s.Id == series.Id);
            row.ArchivedAt = DateTime.UtcNow.AddDays(-3);
            row.ArchivedByUserId = 1;
            db.Chapters.Add(new Chapter
            {
                SeriesId = series.Id, Number = 1, Language = "en", FileRemovedAt = DateTime.UtcNow.AddDays(-3),
            });
            await db.SaveChangesAsync();
        }

        WriteComic("Vagabond", "Vagabond 001.cbz");

        var candidate = Assert.Single(await ScanAsync(root));
        Assert.Equal(ImportCandidateKind.Restore, candidate.Kind);
        Assert.Equal(series.Id, candidate.ExistingSeriesId);

        var result = await LinkingImportAsync(root, "Vagabond", "2");

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, result.FilesLinked);
        await using var check = _db.NewContext();
        var restored = await check.Series.SingleAsync(s => s.Id == series.Id);
        Assert.Null(restored.ArchivedAt);
        Assert.Null(restored.ArchivedByUserId);
        var chapter = await check.Chapters.SingleAsync(c => c.SeriesId == series.Id);
        Assert.NotNull(chapter.ChapterFileId);
        Assert.Null(chapter.FileRemovedAt);
    }

    [Fact]
    public async Task AnArchivedSeriesThatKeptItsFilesStillRefusesAnImportAsAlreadyInTheLibrary()
    {
        var root = SeedRoot();
        var series = SeedSeries(root, "Vagabond", 2, Path.Combine("Vagabond", "Vagabond 001.cbz"));
        var archivedAt = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        await using (var db = _db.NewContext())
        {
            (await db.Series.SingleAsync(s => s.Id == series.Id)).ArchivedAt = archivedAt;
            await db.SaveChangesAsync();
        }

        WriteComic("Vagabond", "Vagabond 001.cbz");
        WriteComic("vagabond raws", "Vagabond 002.cbz");

        var result = await LinkingImportAsync(root, "vagabond raws", "2");

        Assert.False(result.Success);
        Assert.StartsWith("error.libraryImport.alreadyInLibrary", result.Error);
        await using var check = _db.NewContext();
        Assert.Equal(archivedAt,
            DateTime.SpecifyKind((await check.Series.SingleAsync(s => s.Id == series.Id)).ArchivedAt!.Value, DateTimeKind.Utc));
        Assert.Single(check.ChapterFiles.Where(f => f.SeriesId == series.Id));
    }

    private LibraryImportService IgnoreService(Maki.Data.MakiDbContext db, ICurrentUser? currentUser = null) => new(
        db, [_provider], null!, null!, new SourceMatchQueue(), null!, null!, null!, null!, null!, null!,
        currentUser ?? new TestCurrentUser(1), null!, NullLogger<LibraryImportService>.Instance);

    [Fact]
    public async Task AnIgnoredFolderIsSkippedBeforeAnySearchUntilItIsUnignored()
    {
        var root = SeedRoot();
        WriteComic("Extras", "art book.cbz");
        WriteComic("Monster", "Monster v01.cbz");

        await using (var db = _db.NewContext())
        {
            Assert.Null(await IgnoreService(db).IgnoreFolderAsync(root, "Extras", CancellationToken.None));
            // Twice is not an error, and does not add a second row.
            Assert.Null(await IgnoreService(db).IgnoreFolderAsync(root, "extras", CancellationToken.None));
        }

        var candidates = await ScanAsync(root);

        Assert.Equal(["Monster"], candidates.Select(c => c.FolderName));
        Assert.Equal(["Monster"], _provider.Queries);

        await using (var db = _db.NewContext())
        {
            var ignored = Assert.Single(await IgnoreService(db).IgnoredFoldersAsync(root, CancellationToken.None));
            Assert.Equal("Extras", ignored.FolderName);
            Assert.True(await IgnoreService(db).UnignoreFolderAsync(ignored.Id, CancellationToken.None));
        }

        Assert.Contains(await ScanAsync(root), c => c.FolderName == "Extras");
    }

    [Fact]
    public async Task IgnoringOneFolderInTwoCasesAtOnceKeepsOneRowAndUnignoringClearsEveryCase()
    {
        var root = SeedRoot();
        WriteComic("Extras", "art book.cbz");
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            await using var db = _db.NewContext();
            await IgnoreService(db).IgnoreFolderAsync(root, i % 2 == 0 ? "Extras" : "extras", CancellationToken.None);
        }));

        await using (var check = _db.NewContext())
        {
            var kept = Assert.Single(check.ImportIgnoredFolders);
            // A row from before ignoring was serialized, in the other case.
            check.ImportIgnoredFolders.Add(new ImportIgnoredFolder
            {
                RootFolderId = root,
                FolderName = kept.FolderName == "Extras" ? "extras" : "Extras",
                CreatedAt = DateTime.UtcNow,
            });
            await check.SaveChangesAsync();
            Assert.True(await IgnoreService(check).UnignoreFolderAsync(kept.Id, CancellationToken.None));
        }

        await using (var check = _db.NewContext())
        {
            Assert.Empty(check.ImportIgnoredFolders);
        }

        Assert.Contains(await ScanAsync(root), c => c.FolderName == "Extras");
    }

    [Fact]
    public async Task IgnoringAFolderSomebodyIgnoredAMomentAgoIsNotAnError()
    {
        var root = SeedRoot();
        await using (var first = _db.NewContext())
        {
            await IgnoreService(first).InsertIgnoredFolderAsync(root, "Extras", CancellationToken.None);
        }

        // The second of two ignores that both passed the existence check: the unique index refuses it.
        await using (var second = _db.NewContext())
        {
            await IgnoreService(second).InsertIgnoredFolderAsync(root, "Extras", CancellationToken.None);
        }

        await using var check = _db.NewContext();
        Assert.Single(check.ImportIgnoredFolders);
    }

    [Fact]
    public async Task TheIgnoreListIsPerRootFolder()
    {
        var root = SeedRoot();
        var other = SeedRoot();
        WriteComic("Extras", "art book.cbz");

        await using (var db = _db.NewContext())
        {
            Assert.Null(await IgnoreService(db).IgnoreFolderAsync(other, "Extras", CancellationToken.None));
        }

        Assert.Contains(await ScanAsync(root), c => c.FolderName == "Extras");
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("..")]
    [InlineData("")]
    public async Task IgnoringRefusesAnythingButAPlainFolderName(string folderName)
    {
        var root = SeedRoot();
        await using var db = _db.NewContext();

        Assert.Equal("error.libraryImport.invalidFolderName",
            await IgnoreService(db).IgnoreFolderAsync(root, folderName, CancellationToken.None));
    }

    [Fact]
    public async Task TheIgnoreListOfARootTheCallerHasNoGrantOnIsOutOfReach()
    {
        var root = SeedRoot();
        var restricted = new RestrictedCurrentUser([root + 1]);
        int id;
        await using (var db = _db.NewContext())
        {
            await IgnoreService(db).IgnoreFolderAsync(root, "Extras", CancellationToken.None);
            id = Assert.Single(await IgnoreService(db).IgnoredFoldersAsync(root, CancellationToken.None)).Id;
        }

        await using (var db = _db.NewContext())
        {
            var service = IgnoreService(db, restricted);
            Assert.Empty(await service.IgnoredFoldersAsync(root, CancellationToken.None));
            Assert.Equal("error.series.rootFolderNotFound",
                await service.IgnoreFolderAsync(root, "Art", CancellationToken.None));
            Assert.False(await service.UnignoreFolderAsync(id, CancellationToken.None));
        }
    }

    [Fact]
    public async Task AFolderWithNoComicsIsNotOfferedOrSearched()
    {
        var root = SeedRoot();
        Directory.CreateDirectory(Path.Combine(_root, "Notes"));
        File.WriteAllText(Path.Combine(_root, "Notes", "readme.txt"), "not a comic");
        WriteComic("Monster", "Monster v01.cbz");

        var candidates = await ScanAsync(root);

        Assert.Equal(["Monster"], candidates.Select(c => c.FolderName));
        Assert.Equal(["Monster"], _provider.Queries);
    }

    [Fact]
    public async Task ScanRefusesARootFolderTheCallerHasNoGrantOn()
    {
        var root = SeedRoot();
        var restricted = new RestrictedCurrentUser([2]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ScanAsync(root, restricted));
    }

    [Fact]
    public async Task ImportRefusesARootFolderTheCallerHasNoGrantOn()
    {
        var root = SeedRoot();
        WriteComic("Monster", "Monster v01.cbz");
        var restricted = new RestrictedCurrentUser([2]);

        var result = await ImportAsync(root, "Monster", restricted);

        Assert.False(result.Success);
        Assert.Equal("error.series.rootFolderNotFound", result.Error);
        Assert.True(File.Exists(Path.Combine(_root, "Monster", "Monster v01.cbz")));
    }

    [Theory]
    [InlineData("...")]
    [InlineData("....")]
    [InlineData("... ")]
    [InlineData(". .")]
    public async Task ImportRefusesADotsAndSpacesFolderNameThatWindowsCollapsesToTheRoot(string folderName)
    {
        var root = SeedRoot();
        WriteComic("Monster", "Monster v01.cbz");

        var result = await ImportAsync(root, folderName);

        Assert.False(result.Success);
        Assert.Equal("error.libraryImport.invalidFolderName", result.Error);
        Assert.True(File.Exists(Path.Combine(_root, "Monster", "Monster v01.cbz")));
    }

    [Fact]
    public async Task ImportRefusesAFolderNameThatWalksOutOfTheRoot()
    {
        var root = SeedRoot();

        var result = await ImportAsync(root, "../sibling");

        Assert.False(result.Success);
        Assert.Equal("error.libraryImport.invalidFolderName", result.Error);
        Assert.False(Directory.Exists(Path.Combine(_root, "..", "sibling")));
    }

    [Fact]
    public async Task ImportRefusesAFolderNameWithAnEmbeddedTraversal()
    {
        var root = SeedRoot();

        var result = await ImportAsync(root, Path.Combine("a", "..", "..", "sibling"));

        Assert.False(result.Success);
        Assert.Equal("error.libraryImport.invalidFolderName", result.Error);
    }

    [Fact]
    public async Task ImportRefusesAnAbsoluteFolderName()
    {
        var root = SeedRoot();
        var absolute = Path.Combine(Path.GetTempPath(), "maki-import-outside-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(absolute);
        try
        {
            var result = await ImportAsync(root, absolute);

            Assert.False(result.Success);
            Assert.Equal("error.libraryImport.invalidFolderName", result.Error);
        }
        finally
        {
            Directory.Delete(absolute, recursive: true);
        }
    }

    /// <summary>An <see cref="ICurrentUser"/> granted only the listed root folders.</summary>
    private sealed class RestrictedCurrentUser(IEnumerable<int> rootFolderIds) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public int UserId => 1;
        public string UserName => "test";
        public MakiPermission Permissions => MakiPermission.ImportLibrary;
        public bool AllRootFolders => false;
        public IReadOnlySet<int> RootFolderIds { get; } = rootFolderIds.ToHashSet();
        public string MaxContentRating => "erotica";
    }

    private sealed class CountingProvider : IMetadataProvider
    {
        public List<string> Queries { get; } = [];

        public string Name => "fake";

        public Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(
            string query, string maxContentRating, CancellationToken ct = default)
        {
            lock (Queries)
            {
                Queries.Add(query);
            }

            return Task.FromResult<IReadOnlyList<MetadataSearchResult>>([]);
        }

        public Task<SeriesMetadata?> GetAsync(string providerId, CancellationToken ct = default) =>
            Task.FromResult<SeriesMetadata?>(new SeriesMetadata
            {
                ProviderId = providerId, Title = "Vagabond", MangaBakaId = int.Parse(providerId),
            });
    }
}
