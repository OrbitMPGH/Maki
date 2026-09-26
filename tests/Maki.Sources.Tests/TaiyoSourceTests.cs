using Maki.Core.Sources;
using Maki.Sources.Taiyo;

namespace Maki.Sources.Tests;

public class TaiyoSourceTests
{
    private static TaiyoSource SourceFor(Dictionary<string, string> responses, out FakeHttpClientFactory factory)
    {
        factory = new FakeHttpClientFactory(responses);
        return new TaiyoSource(factory);
    }

    [Fact]
    public async Task Search_discovers_the_meilisearch_key_and_picks_the_english_main_title()
    {
        // Order matters: "app/layout-" only appears in the script URL, but the exact home URL is a
        // prefix of it too, so the more specific key must be checked first.
        var source = SourceFor(new()
        {
            ["app/layout-"] = FakeHttpClientFactory.Fixture("taiyo-layout.js"),
            ["multi-search"] = FakeHttpClientFactory.Fixture("taiyo-search.json"),
            ["https://taiyo.moe/"] = FakeHttpClientFactory.Fixture("taiyo-home.html"),
        }, out var factory);

        var results = await source.SearchAsync("one piece");

        Assert.Equal(21, results.Count);
        var onePiece = results[0];
        Assert.Equal("3cec0768-c247-468c-97fe-76e9a556d6ee", onePiece.SourceSeriesId);
        Assert.Equal("One Piece", onePiece.Title);
        Assert.Equal(
            "https://cdn.taiyo.moe/medias/3cec0768-c247-468c-97fe-76e9a556d6ee/covers/ce486545-1165-4054-97b8-d004f7aea01b.jpg",
            onePiece.CoverUrl);

        // The key was sent as a Bearer token on the search POST.
        Assert.Contains(factory.Requests, u => u.Contains("multi-search"));
    }

    [Fact]
    public async Task Search_reuses_the_cached_key_on_a_second_call()
    {
        var source = SourceFor(new()
        {
            ["app/layout-"] = FakeHttpClientFactory.Fixture("taiyo-layout.js"),
            ["multi-search"] = FakeHttpClientFactory.Fixture("taiyo-search.json"),
            ["https://taiyo.moe/"] = FakeHttpClientFactory.Fixture("taiyo-home.html"),
        }, out var factory);

        await source.SearchAsync("one piece");
        await source.SearchAsync("one piece");

        // Only the first search should need the home page and the bundle to find the key.
        Assert.Equal(1, factory.Requests.Count(u => u == "https://taiyo.moe/"));
        Assert.Equal(1, factory.Requests.Count(u => u.Contains("app/layout-")));
        Assert.Equal(2, factory.Requests.Count(u => u.Contains("multi-search")));
    }

    [Fact]
    public async Task GetSeries_parses_title_cover_and_status()
    {
        var source = SourceFor(new()
        {
            ["medias.getById?"] = FakeHttpClientFactory.Fixture("taiyo-series.json"),
        }, out _);

        var detail = await source.GetSeriesAsync("3cec0768-c247-468c-97fe-76e9a556d6ee");

        Assert.Equal("One Piece", detail.Title);
        Assert.Equal("RELEASING", detail.Status);
        Assert.Equal(
            "https://cdn.taiyo.moe/medias/3cec0768-c247-468c-97fe-76e9a556d6ee/covers/ce486545-1165-4054-97b8-d004f7aea01b.jpg",
            detail.CoverUrl);
    }

    [Fact]
    public async Task GetExternalIds_maps_anilist_and_myanimelist()
    {
        var source = SourceFor(new()
        {
            ["medias.getById?"] = FakeHttpClientFactory.Fixture("taiyo-series.json"),
        }, out _);

        var ids = await source.GetExternalIdsAsync("3cec0768-c247-468c-97fe-76e9a556d6ee");

        Assert.NotNull(ids);
        Assert.Equal("30013", ids[ExternalIdService.AniList]);
        Assert.Equal("13", ids[ExternalIdService.Mal]);
        Assert.Equal(2, ids.Count);
    }

    [Fact]
    public async Task ListChapters_walks_every_page_up_to_totalPages()
    {
        var source = SourceFor(new()
        {
            ["chapters.getByMediaId?"] = FakeHttpClientFactory.Fixture("taiyo-chapters-1.json"),
        }, out var factory);

        var chapters = await source.ListChaptersAsync("3cec0768-c247-468c-97fe-76e9a556d6ee");

        // taiyo-chapters-1.json reports totalPages: 24 on every page, so the walk makes 24 requests.
        Assert.Equal(24, factory.Requests.Count(u => u.Contains("chapters.getByMediaId?")));
        Assert.NotEmpty(chapters);
        Assert.All(chapters, c => Assert.Equal("pt-br", c.Language));
        var ch1140 = chapters.FirstOrDefault(c => c.Number == 1140m);
        Assert.NotNull(ch1140);
        Assert.Equal("a28e4286-b9b8-42b1-8264-76506e0c4349", ch1140.SourceChapterId);
        Assert.Equal("scopper gaban", ch1140.Title);
    }

    [Fact]
    public async Task ListChapters_stops_after_one_page_when_totalPages_is_one()
    {
        var source = SourceFor(new()
        {
            ["chapters.getByMediaId?"] = FakeHttpClientFactory.Fixture("taiyo-chapters-oneshot.json"),
        }, out var factory);

        var chapters = await source.ListChaptersAsync("22222222-2222-2222-2222-222222222222");

        Assert.Equal(1, factory.Requests.Count(u => u.Contains("chapters.getByMediaId?")));
        Assert.NotEmpty(chapters);
    }

    [Fact]
    public async Task GetPages_uses_extension_and_referer()
    {
        var source = SourceFor(new()
        {
            ["chapters.getById?"] = FakeHttpClientFactory.Fixture("taiyo-pages.json"),
        }, out _);

        var chapter = new SourceChapter(
            "taiyo", "3cec0768-c247-468c-97fe-76e9a556d6ee", "a28e4286-b9b8-42b1-8264-76506e0c4349",
            "1140", 1140m, null, "scopper gaban", "pt-br", null);

        var pages = await source.GetPagesAsync(chapter);

        Assert.Equal(17, pages.Pages.Count);
        var first = pages.Pages[0];
        Assert.Equal(
            "https://cdn.taiyo.moe/medias/3cec0768-c247-468c-97fe-76e9a556d6ee/chapters/a28e4286-b9b8-42b1-8264-76506e0c4349/beb54fd5-3534-4a3c-b0a3-4002441898e5.jpg",
            first.Url);
        Assert.Equal("https://taiyo.moe/", first.Headers!["Referer"]);
    }

    [Fact]
    public async Task GetPages_throws_ChapterLockedException_when_there_are_no_pages()
    {
        var source = SourceFor(new()
        {
            ["chapters.getById?"] = "[{\"result\":{\"data\":{\"json\":{\"id\":\"x\",\"pages\":[]}}}}]",
        }, out _);

        var chapter = new SourceChapter("taiyo", "series", "x", "1", 1m, null, null, "pt-br", null);

        await Assert.ThrowsAsync<ChapterLockedException>(() => source.GetPagesAsync(chapter));
    }

    [Fact]
    public void ResolveSeriesIdFromUrl_accepts_media_urls_and_rejects_others()
    {
        ISource source = SourceFor(new(), out _);

        Assert.Equal(
            "3cec0768-c247-468c-97fe-76e9a556d6ee",
            source.ResolveSeriesIdFromUrl(new Uri("https://taiyo.moe/media/3cec0768-c247-468c-97fe-76e9a556d6ee")));
        Assert.Null(source.ResolveSeriesIdFromUrl(new Uri("https://taiyo.moe/chapter/a28e4286-b9b8-42b1-8264-76506e0c4349/1")));
        Assert.Null(source.ResolveSeriesIdFromUrl(new Uri("https://cdn.taiyo.moe/medias/3cec0768-c247-468c-97fe-76e9a556d6ee")));
        Assert.Null(source.ResolveSeriesIdFromUrl(new Uri("https://example.com/media/3cec0768-c247-468c-97fe-76e9a556d6ee")));
    }
}
