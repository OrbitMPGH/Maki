using System.IO.Compression;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Import;
using Maki.Core.Metadata;
using Maki.Metadata.MangaBaka;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// The import scan reads what the files say about themselves: a web link in ComicInfo resolves to
/// an exact series, and a ComicInfo or series.json name is searched ahead of the folder name.
/// </summary>
public class LibraryImportHintsTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "maki-import-hints-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly TitleProvider _provider = new();
    private readonly FakeStore _store = new();

    public LibraryImportHintsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private async Task<List<ImportScanCandidate>> ScanAsync()
    {
        int rootId;
        await using (var seed = _db.NewContext())
        {
            var root = new RootFolder { Path = _root };
            seed.RootFolders.Add(root);
            await seed.SaveChangesAsync();
            rootId = root.Id;
        }

        await using var db = _db.NewContext();
        var service = new LibraryImportService(
            db, [_provider], null!, null!, new SourceMatchQueue(), null!, null!, null!, null!, null!, null!,
            new TestCurrentUser(1), null!, NullLogger<LibraryImportService>.Instance,
            new LibraryImportResolver([_provider], _store));
        return await service.ScanAsync(rootId);
    }

    private void WriteComic(string folder, string name, string? comicInfo = null)
    {
        var dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        using var archive = ZipFile.Open(Path.Combine(dir, name), ZipArchiveMode.Create);
        using (var writer = new StreamWriter(archive.CreateEntry("001.png").Open()))
        {
            writer.Write("page");
        }

        if (comicInfo is not null)
        {
            using var info = new StreamWriter(archive.CreateEntry("ComicInfo.xml").Open());
            info.Write(comicInfo);
        }
    }

    [Fact]
    public async Task A_web_link_in_ComicInfo_is_the_first_match_whatever_the_folder_is_called()
    {
        _store.AniList[30013] = 501;
        _provider.Titles["Totally Wrong"] = [Result("9", "Totally Wrong")];
        WriteComic("Totally Wrong", "v01.cbz",
            "<ComicInfo><Series>Something</Series><Web>https://example.org/x,https://anilist.co/manga/30013</Web></ComicInfo>");

        var candidate = Assert.Single(await ScanAsync());

        Assert.Equal("501", candidate.Matches[0].ProviderId);
        Assert.Equal(ImportMatchSource.Link, candidate.MatchSource);
        Assert.Equal("https://anilist.co/manga/30013", candidate.MatchHint);
        Assert.Contains(candidate.Matches, m => m.ProviderId == "9");
    }

    [Fact]
    public async Task A_MangaBaka_link_like_the_ones_Maki_writes_resolves_directly()
    {
        WriteComic("Berserk [Digital]", "Berserk v01.cbz",
            "<ComicInfo><Series>Berserk</Series><Web>https://mangabaka.org/77</Web></ComicInfo>");

        var candidate = Assert.Single(await ScanAsync());

        Assert.Equal("77", candidate.Matches[0].ProviderId);
        Assert.Equal(ImportMatchSource.Link, candidate.MatchSource);
    }

    [Fact]
    public async Task The_ComicInfo_series_name_is_searched_before_the_folder_name()
    {
        _provider.Titles["Berserk Deluxe"] = [Result("1", "Berserk Deluxe Edition")];
        _provider.Titles["Berserk"] = [Result("2", "Berserk")];
        WriteComic("Berserk Deluxe", "v01.cbz", "<ComicInfo><Series>Berserk</Series></ComicInfo>");

        var candidate = Assert.Single(await ScanAsync());

        Assert.Equal(["2", "1"], candidate.Matches.Select(m => m.ProviderId));
        Assert.Equal(ImportMatchSource.ComicInfo, candidate.MatchSource);
        Assert.Equal("Berserk", candidate.MatchHint);
    }

    [Fact]
    public async Task A_Mylar_series_json_name_is_used_when_no_ComicInfo_names_one()
    {
        _provider.Titles["Monster"] = [Result("3", "Monster")];
        WriteComic("monster scans", "v01.cbz");
        await File.WriteAllTextAsync(Path.Combine(_root, "monster scans", "series.json"),
            """{"version":"1.0.2","metadata":{"type":"comicSeries","name":"Monster","comicid":12345}}""");

        var candidate = Assert.Single(await ScanAsync());

        Assert.Equal("3", candidate.Matches[0].ProviderId);
        Assert.Equal(ImportMatchSource.SeriesJson, candidate.MatchSource);
    }

    [Fact]
    public async Task Without_hints_the_folder_name_is_the_match_source()
    {
        _provider.Titles["Vagabond"] = [Result("4", "Vagabond")];
        WriteComic("Vagabond", "Vagabond v01.cbz");

        var candidate = Assert.Single(await ScanAsync());

        Assert.Equal(ImportMatchSource.Folder, candidate.MatchSource);
        Assert.Null(candidate.MatchHint);
        Assert.Equal(["Vagabond"], _provider.Queries);
    }

    [Fact]
    public void Hints_ignore_a_damaged_ComicInfo_and_a_broken_series_json()
    {
        WriteComic("Broken", "v01.cbz", "<ComicInfo><Series>Unclosed");
        var dir = Path.Combine(_root, "Broken");
        File.WriteAllText(Path.Combine(dir, "series.json"), "{ not json");

        var hints = FolderMetadataHints.Read(dir, ComicSourceScanner.Scan(dir));

        Assert.Null(hints.SeriesName);
        Assert.Empty(hints.WebLinks);
    }

    private static MetadataSearchResult Result(string id, string title) =>
        new(id, title, null, null, SeriesStatus.Unknown, null, null);

    private sealed class TitleProvider : IMetadataProvider
    {
        public Dictionary<string, List<MetadataSearchResult>> Titles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Queries { get; } = [];

        public string Name => "fake";

        public Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(
            string query, string maxContentRating, CancellationToken ct = default)
        {
            lock (Queries)
            {
                Queries.Add(query);
            }

            return Task.FromResult<IReadOnlyList<MetadataSearchResult>>(
                Titles.TryGetValue(query, out var found) ? found : []);
        }

        public Task<SeriesMetadata?> GetAsync(string providerId, CancellationToken ct = default) =>
            Task.FromResult<SeriesMetadata?>(new SeriesMetadata { ProviderId = providerId, Title = $"Series {providerId}" });
    }

    private sealed class FakeStore() : MangaBakaLocalStore(
        new MangaBakaDumpOptions("", Path.GetTempPath()), NullLogger<MangaBakaLocalStore>.Instance)
    {
        public Dictionary<long, long> AniList { get; } = [];

        public override Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public override Task<IReadOnlyDictionary<long, long>> GetIdsByExternalIdsAsync(
            ExternalSource source, IReadOnlyCollection<long> externalIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<long, long>>(source == ExternalSource.AniList
                ? externalIds.Where(AniList.ContainsKey).ToDictionary(id => id, id => AniList[id])
                : new Dictionary<long, long>());

        public override Task<IReadOnlyDictionary<string, long>> GetIdsByMangaUpdatesIdsAsync(
            IReadOnlyCollection<string> slugs, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long>());
    }
}
