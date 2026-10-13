using Maki.Api.Services;
using Maki.Core.Metadata;
using Maki.Metadata.MangaBaka;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// What a pasted id or link in the import review resolves to. AniList and MyAnimeList go through
/// the dump's cross-references; a MangaBaka id goes straight to the provider.
/// </summary>
public class LibraryImportResolverTests
{
    [Theory]
    [InlineData("12345", ImportIdService.MangaBaka, 12345)]
    [InlineData("  777 ", ImportIdService.MangaBaka, 777)]
    [InlineData("https://mangabaka.org/9001", ImportIdService.MangaBaka, 9001)]
    [InlineData("mangabaka.dev/series/88", ImportIdService.MangaBaka, 88)]
    [InlineData("https://anilist.co/manga/30013/One-Piece/", ImportIdService.AniList, 30013)]
    [InlineData("anilist.co/manga/30013", ImportIdService.AniList, 30013)]
    [InlineData("https://myanimelist.net/manga/13/One_Piece", ImportIdService.MyAnimeList, 13)]
    [InlineData("al:30013", ImportIdService.AniList, 30013)]
    [InlineData("MAL: 13", ImportIdService.MyAnimeList, 13)]
    [InlineData("mb:5", ImportIdService.MangaBaka, 5)]
    public void Parses_ids_and_links(string input, ImportIdService service, long id)
    {
        Assert.Equal((service, id), LibraryImportResolver.Parse(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("one piece")]
    [InlineData("0")]
    [InlineData("-4")]
    [InlineData("https://anilist.co.evil.example/manga/1")]
    [InlineData("https://mangadex.org/title/abc")]
    public void Refuses_anything_that_names_no_id(string input)
    {
        Assert.Null(LibraryImportResolver.Parse(input));
    }

    [Fact]
    public async Task An_AniList_link_resolves_through_the_dump_to_the_MangaBaka_series()
    {
        var store = new FakeStore();
        store.AniList[30013] = 501;
        var resolver = new LibraryImportResolver([new FakeProvider()], store);

        var result = await resolver.ResolveAsync("https://anilist.co/manga/30013", CancellationToken.None);

        Assert.Null(result.ErrorKey);
        Assert.Equal("501", result.Match!.ProviderId);
        Assert.Equal("Series 501", result.Match.Title);
    }

    [Fact]
    public async Task A_MyAnimeList_id_with_no_cross_reference_is_not_found()
    {
        var resolver = new LibraryImportResolver([new FakeProvider()], new FakeStore());

        var result = await resolver.ResolveAsync("mal:13", CancellationToken.None);

        Assert.Null(result.Match);
        Assert.Equal("error.libraryImport.idNotFound", result.ErrorKey);
    }

    [Fact]
    public async Task An_external_id_without_the_dump_says_so()
    {
        var store = new FakeStore { Available = false };
        store.AniList[30013] = 501;
        var resolver = new LibraryImportResolver([new FakeProvider()], store);

        var result = await resolver.ResolveAsync("al:30013", CancellationToken.None);

        Assert.Equal("error.libraryImport.idNeedsDump", result.ErrorKey);
    }

    [Fact]
    public async Task A_MangaBaka_id_skips_the_dump_and_asks_the_provider()
    {
        var store = new FakeStore { Available = false };
        var resolver = new LibraryImportResolver([new FakeProvider()], store);

        Assert.Equal("77", (await resolver.ResolveAsync("77", CancellationToken.None)).Match!.ProviderId);
        Assert.Equal("error.libraryImport.idNotFound",
            (await resolver.ResolveAsync(FakeProvider.Missing.ToString(), CancellationToken.None)).ErrorKey);
        Assert.Equal("error.libraryImport.idUnrecognized",
            (await resolver.ResolveAsync("not an id", CancellationToken.None)).ErrorKey);
    }

    private sealed class FakeStore() : MangaBakaLocalStore(
        new MangaBakaDumpOptions("", Path.GetTempPath()), new FakeAppSettings(), NullLogger<MangaBakaLocalStore>.Instance)
    {
        public Dictionary<long, long> AniList { get; } = [];
        public bool Available { get; set; } = true;

        public override Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(Available);

        public override Task<IReadOnlyDictionary<long, long>> GetIdsByExternalIdsAsync(
            ExternalSource source, IReadOnlyCollection<long> externalIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<long, long>>(source == ExternalSource.AniList
                ? externalIds.Where(AniList.ContainsKey).ToDictionary(id => id, id => AniList[id])
                : new Dictionary<long, long>());
    }

    private sealed class FakeProvider : IMetadataProvider
    {
        public const long Missing = 404;

        public string Name => "fake";

        public Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(
            string query, string maxContentRating, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MetadataSearchResult>>([]);

        public Task<SeriesMetadata?> GetAsync(string providerId, CancellationToken ct = default) =>
            Task.FromResult(providerId == Missing.ToString()
                ? null
                : new SeriesMetadata { ProviderId = providerId, Title = $"Series {providerId}" });
    }
}
