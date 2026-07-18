using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Metadata.MangaBaka;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Metadata.Tests;

public class MangaBakaLocalStoreTests : IDisposable
{
    private readonly DumpDbBuilder _db = new();
    private readonly FakeAppSettings _settings = new();

    private MangaBakaLocalStore Store => new(
        new MangaBakaDumpOptions(_db.Path, Path.GetTempPath()),
        _settings,
        NullLogger<MangaBakaLocalStore>.Instance);

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Search_finds_by_primary_title_case_insensitive()
    {
        _db.AddSeries(377, "ONE PIECE", status: "releasing", year: 1997, totalChapters: "1187")
            .AddSeries(1, "Berserk")
            .BuildSearchIndex();

        var results = await Store.SearchAsync("one piece");

        var hit = Assert.Single(results);
        Assert.Equal("377", hit.ProviderId);
        Assert.Equal("ONE PIECE", hit.Title);
        Assert.Equal(1997, hit.Year);
        Assert.Equal(SeriesStatus.Ongoing, hit.Status);
        Assert.Equal(1187, hit.TotalChapters);
    }

    [Fact]
    public async Task Search_finds_by_alternative_title_from_titles_json()
    {
        _db.AddSeries(42, "Shingeki no Kyojin",
                titlesJson: """[{"title": "Attack on Titan", "language": "en", "is_primary": true}]""")
            .BuildSearchIndex();

        var results = await Store.SearchAsync("attack on titan");

        Assert.Equal("42", Assert.Single(results).ProviderId);
    }

    [Fact]
    public async Task Search_matches_prefix_of_last_token()
    {
        _db.AddSeries(7, "Fullmetal Alchemist").BuildSearchIndex();

        var results = await Store.SearchAsync("fullmetal alch");

        Assert.Single(results);
    }

    [Fact]
    public async Task Search_excludes_merged_series()
    {
        _db.AddSeries(10, "Naruto", state: "merged", mergedWith: "11")
            .AddSeries(11, "Naruto")
            .BuildSearchIndex();

        var results = await Store.SearchAsync("naruto");

        Assert.Equal("11", Assert.Single(results).ProviderId);
    }

    [Fact]
    public async Task Search_ranks_popular_series_first_on_equal_match()
    {
        _db.AddSeries(1, "Bleach", popularity: 500)
            .AddSeries(2, "Bleach", popularity: 3)
            .BuildSearchIndex();

        var results = await Store.SearchAsync("bleach");

        Assert.Equal("2", results[0].ProviderId);
    }

    [Fact]
    public async Task Get_maps_all_fields()
    {
        _db.AddSeries(377, "ONE PIECE",
            nativeTitle: "ワンピース",
            description: "Pirates.",
            year: 1997,
            status: "releasing",
            finalVolume: "115",
            totalChapters: "1187",
            authorsJson: """["Eiichirou Oda"]""",
            artistsJson: """["Eiichirou Oda", "Someone Else"]""",
            genresJson: """["Action", "Adventure"]""",
            tagsJson: """["Pirates"]""",
            coverUrl: "https://images.mangabaka.dev/cover.png",
            aniListId: 30013,
            malId: 13,
            mangaUpdatesId: "pb8uwds");

        var metadata = await Store.GetAsync("377");

        Assert.NotNull(metadata);
        Assert.Equal("377", metadata.ProviderId);
        Assert.Equal("ONE PIECE", metadata.Title);
        Assert.Equal("ワンピース", metadata.OriginalTitle);
        Assert.Equal("Pirates.", metadata.Description);
        Assert.Equal(1997, metadata.Year);
        Assert.Equal(SeriesStatus.Ongoing, metadata.Status);
        Assert.Equal(115, metadata.TotalVolumes);
        Assert.Equal(1187, metadata.TotalChapters);
        Assert.Equal("Eiichirou Oda", metadata.AuthorStory);
        Assert.Equal("Eiichirou Oda, Someone Else", metadata.AuthorArt);
        Assert.Equal(["Action", "Adventure"], metadata.Genres);
        Assert.Equal(["Pirates"], metadata.Tags);
        Assert.Equal("https://images.mangabaka.dev/cover.png", metadata.CoverUrl);
        Assert.Equal("https://mangabaka.org/377", metadata.WebUrl);
        Assert.Equal(377, metadata.MangaBakaId);
        Assert.Equal(30013, metadata.AniListId);
        Assert.Equal(13, metadata.MalId);
        Assert.Equal("pb8uwds", metadata.MangaUpdatesId);
    }

    [Fact]
    public async Task Get_parses_fractional_chapter_counts()
    {
        _db.AddSeries(5, "Some Series", totalChapters: "112.5");

        var metadata = await Store.GetAsync("5");

        Assert.Equal(112, metadata!.TotalChapters);
    }

    [Fact]
    public async Task Get_follows_merged_chain_to_canonical_series()
    {
        _db.AddSeries(10, "Old Entry", state: "merged", mergedWith: "20")
            .AddSeries(20, "Canonical Entry");

        var metadata = await Store.GetAsync("10");

        Assert.Equal("20", metadata!.ProviderId);
        Assert.Equal("Canonical Entry", metadata.Title);
    }

    [Fact]
    public async Task Get_returns_null_for_unknown_or_invalid_ids()
    {
        _db.AddSeries(1, "Something");

        Assert.Null(await Store.GetAsync("999"));
        Assert.Null(await Store.GetAsync("not-a-number"));
    }

    [Fact]
    public async Task IsAvailable_depends_on_file_and_setting()
    {
        Assert.True(await Store.IsAvailableAsync());

        _settings.Values[SettingKeys.MangaBakaUseLocalDb] = "false";
        Assert.False(await Store.IsAvailableAsync());

        _settings.Values[SettingKeys.MangaBakaUseLocalDb] = "true";
        var missingFile = new MangaBakaLocalStore(
            new MangaBakaDumpOptions(Path.Combine(Path.GetTempPath(), "does-not-exist.db"), Path.GetTempPath()),
            _settings,
            NullLogger<MangaBakaLocalStore>.Instance);
        Assert.False(await missingFile.IsAvailableAsync());
    }

    [Theory]
    [InlineData("one piece", "\"one\" \"piece\" *")]
    [InlineData("solo", "\"solo\" *")]
    [InlineData("with \"quotes\"", "\"with\" \"quotes\" *")]
    [InlineData("   ", null)]
    [InlineData("\"\"", null)]
    public void BuildMatchExpression_quotes_tokens_and_prefixes_last(string query, string? expected)
    {
        Assert.Equal(expected, MangaBakaLocalStore.BuildMatchExpression(query));
    }
}
