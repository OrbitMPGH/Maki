using Maki.Sources.MangaPlus;

namespace Maki.Sources.Tests;

/// <summary>
/// Fixtures are real protobuf bodies recorded off the API, trimmed to a few entries. They are
/// binary on purpose: MANGA Plus dropped <c>?format=json</c> (the edge 403s any request carrying
/// it) and now answers protobuf only, so a JSON fixture would test a shape the site no longer serves.
/// </summary>
public class MangaPlusSourceTests
{
    private static MangaPlusSource SourceFor(Dictionary<string, byte[]> responses) =>
        new(new FakeHttpClientFactory([], responses));

    private static MangaPlusSource WithCatalog() =>
        SourceFor(new() { ["title_list/allV2"] = FakeHttpClientFactory.BinaryFixture("mangaplus-titles.pb") });

    private static MangaPlusSource WithDetail() =>
        SourceFor(new() { ["title_detailV3"] = FakeHttpClientFactory.BinaryFixture("mangaplus-detail.pb") });

    private static MangaPlusSource WithViewer() =>
        SourceFor(new() { ["manga_viewer"] = FakeHttpClientFactory.BinaryFixture("mangaplus-viewer.pb") });

    [Fact]
    public async Task Search_matches_catalog_titles()
    {
        var results = await WithCatalog().SearchAsync("2.5 Dimensional Seduction");

        var hit = Assert.Single(results);
        Assert.Equal("100282", hit.SourceSeriesId);
        Assert.Equal("2.5 Dimensional Seduction", hit.Title);
        Assert.Equal("https://mangaplus.shueisha.co.jp/titles/100282", hit.Url);
        Assert.StartsWith("https://jumpg-assets.tokyo-cdn.com/secure/title/100282/", hit.CoverUrl);
    }

    [Fact]
    public async Task Search_ignores_the_other_language_editions_of_a_title()
    {
        // The catalog carries every language under one group; only language 0 (or an absent
        // field) is English. "Apple to Orange" ships an es edition, id 200108.
        var results = await WithCatalog().SearchAsync("Apple to Orange");

        Assert.Equal(["100237"], results.Select(r => r.SourceSeriesId));
    }

    [Fact]
    public async Task GetSeries_reads_the_title_block_and_overview()
    {
        var detail = await WithDetail().GetSeriesAsync("100020");

        Assert.Equal("One Piece", detail.Title);
        Assert.StartsWith("As a child, Monkey D. Luffy", detail.Description);
        Assert.StartsWith("https://jumpg-assets.tokyo-cdn.com/secure/title/100020/", detail.CoverUrl);
    }

    [Fact]
    public async Task ListChapters_reads_every_chapter_list_in_the_group()
    {
        var chapters = await WithDetail().ListChaptersAsync("100020");

        // The fixture holds the free head of the run and the paywalled tail, split across the
        // group's first/mid/last lists — all three have to be read or the newest chapter is missed.
        Assert.Contains(chapters, c => c.Number == 1);
        Assert.Contains(chapters, c => c.Number == 1188);
        Assert.All(chapters, c => Assert.Equal("en", c.Language));
    }

    [Fact]
    public async Task ListChapters_maps_the_hash_prefixed_name_and_subtitle()
    {
        var chapters = await WithDetail().ListChaptersAsync("100020");

        var first = chapters.Single(c => c.Number == 1);
        Assert.Equal("001", first.NumberRaw);
        Assert.Equal("Chapter 1: Romance Dawn", first.Title);
        Assert.Equal("1000486", first.SourceChapterId);
        Assert.Equal("https://mangaplus.shueisha.co.jp/viewer/1000486", first.Url);
    }

    [Fact]
    public async Task GetPages_carries_the_per_page_xor_key_and_skips_ad_slots()
    {
        var chapter = new Maki.Core.Sources.SourceChapter(
            "mangaplus", "100020", "1000486", "1", 1, null, null, "en", null, null);

        var pages = await WithViewer().GetPagesAsync(chapter);

        // The fixture's fourth page is a banner slot with no mangaPage and must not become a page.
        Assert.Equal(3, pages.Pages.Count);
        Assert.EndsWith("/manga_page/high/1.jpg", new Uri(pages.Pages[0].Url).AbsolutePath);
        Assert.All(pages.Pages, p => Assert.Equal(128, p.XorKeyHex!.Length));
    }

    [Fact]
    public async Task An_error_response_throws_the_english_popup_subject()
    {
        var source = SourceFor(new()
        {
            ["title_list/allV2"] = FakeHttpClientFactory.BinaryFixture("mangaplus-banned.pb")
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => source.SearchAsync("one piece"));
        Assert.Equal("Account Banned", error.Message);
    }
}
