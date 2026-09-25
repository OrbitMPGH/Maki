using System.IO.Compression;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Metadata;
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

    public LibraryImportScanTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private async Task<List<ImportScanCandidate>> ScanAsync(int rootFolderId)
    {
        await using var db = _db.NewContext();
        var service = new LibraryImportService(
            db, [_provider], null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            NullLogger<LibraryImportService>.Instance);
        return await service.ScanAsync(rootFolderId);
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

    private sealed class CountingProvider : IMetadataProvider
    {
        public List<string> Queries { get; } = [];

        public string Name => "fake";

        public Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(
            string query, string maxContentRating, CancellationToken ct = default)
        {
            Queries.Add(query);
            return Task.FromResult<IReadOnlyList<MetadataSearchResult>>([]);
        }

        public Task<SeriesMetadata?> GetAsync(string providerId, CancellationToken ct = default) =>
            Task.FromResult<SeriesMetadata?>(null);
    }
}
