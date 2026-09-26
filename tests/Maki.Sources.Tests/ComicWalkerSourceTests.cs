using Maki.Core.Sources;
using Maki.Sources.ComicWalker;

namespace Maki.Sources.Tests;

public class ComicWalkerSourceTests
{
    private static ComicWalkerSource SourceFor(Dictionary<string, string> responses) =>
        new(new FakeHttpClientFactory(responses));

    private static ComicWalkerSource WithSearch() =>
        SourceFor(new() { ["search/keywords"] = FakeHttpClientFactory.Fixture("comicwalker-search.json") });

    private static ComicWalkerSource WithWork() =>
        SourceFor(new() { ["details/work"] = FakeHttpClientFactory.Fixture("comicwalker-work.json") });

    [Fact]
    public async Task Search_maps_code_title_and_cover()
    {
        var results = await WithSearch().SearchAsync("リゼロ");

        Assert.Equal(6, results.Count);
        var first = results[0];
        Assert.Equal("KC_002386_S", first.SourceSeriesId);
        Assert.Equal("Ｒｅ：ゼロから始める異世界生活 第四章 聖域と強欲の魔女", first.Title);
        Assert.Equal("https://comic-walker.com/detail/KC_002386_S", first.Url);
        Assert.Equal(
            "https://cdn.comic-walker.com/integration/bibliodb/cover-image/prd/image/bw/coverImage_3688754.jpg",
            first.CoverUrl);
    }

    [Fact]
    public async Task GetSeries_maps_title_status_and_cover()
    {
        var detail = await WithWork().GetSeriesAsync("KC_002386_S");

        Assert.Equal("KC_002386_S", detail.SourceSeriesId);
        Assert.Equal("Ｒｅ：ゼロから始める異世界生活 第四章 聖域と強欲の魔女", detail.Title);
        Assert.Equal("https://comic-walker.com/detail/KC_002386_S", detail.Url);
        Assert.Equal("Ongoing", detail.Status);
        Assert.NotNull(detail.CoverUrl);
        Assert.NotNull(detail.Description);
    }

    [Fact]
    public async Task ListChapters_drops_pr_and_inactive_episodes()
    {
        var chapters = await WithWork().ListChaptersAsync("KC_002386_S");

        // The fixture holds 18 episodes; only the 5 active "normal" ones survive.
        Assert.Equal(5, chapters.Count);
        Assert.All(chapters, c => Assert.Equal("ja", c.Language));
        Assert.DoesNotContain(chapters, c => c.Title!.Contains("投票"));

        // 第69話 is inactive in the fixture and must not appear.
        Assert.DoesNotContain(chapters, c => c.NumberRaw!.Contains("第69話"));
    }

    [Fact]
    public async Task ListChapters_appends_subtitle_when_present()
    {
        var chapters = await WithWork().ListChaptersAsync("KC_002386_S");

        var first = Assert.Single(chapters, c => c.Number == 1m);
        Assert.Equal("第1話　帰り着いた場所で - 外伝", first.Title);
        Assert.Equal("https://comic-walker.com/detail/KC_002386_S/episodes/KC_0023860000100011_E", first.Url);
    }

    [Fact]
    public async Task ListChapters_is_normalized()
    {
        var chapters = await WithWork().ListChaptersAsync("KC_002386_S");

        var numbers = chapters.Select(c => c.Number).ToList();
        Assert.Equal(numbers.Distinct().Count(), numbers.Count);
    }

    [Fact]
    public async Task ListChapters_throws_when_latestEpisodes_is_missing()
    {
        // A missing latestEpisodes must not read as "zero chapters": ChapterSyncService would
        // take an empty list as a successful sync and clear the mapping's chapter snapshot.
        var source = SourceFor(new()
        {
            ["details/work"] = "{\"work\":{\"code\":\"KC_002386_S\",\"title\":\"t\",\"language\":\"ja\"}}"
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => source.ListChaptersAsync("KC_002386_S"));
        Assert.Contains("details/work", ex.Message);
    }

    [Fact]
    public async Task ListChapters_throws_when_result_is_not_an_array()
    {
        var source = SourceFor(new()
        {
            ["details/work"] =
                "{\"work\":{\"code\":\"KC_002386_S\",\"title\":\"t\",\"language\":\"ja\"},\"latestEpisodes\":{\"result\":{}}}"
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.ListChaptersAsync("KC_002386_S"));
    }

    [Fact]
    public async Task ListChapters_returns_empty_for_a_genuinely_empty_result()
    {
        var source = SourceFor(new()
        {
            ["details/work"] =
                "{\"work\":{\"code\":\"KC_002386_S\",\"title\":\"t\",\"language\":\"ja\"},\"latestEpisodes\":{\"total\":0,\"result\":[]}}"
        });

        var chapters = await source.ListChaptersAsync("KC_002386_S");

        Assert.Empty(chapters);
    }

    [Theory]
    [InlineData("第73話②　あなたのその勇気", 73.2)]
    [InlineData("第73話①　あなたのその勇気", 73.1)]
    [InlineData("第70話 後編　空白の生", 70.3)]
    [InlineData("第70話 前編　空白の生", 70.1)]
    [InlineData("第69話➁　ド派手な初陣", 69.2)]
    [InlineData("第60話 後編②　エリオール大森林の永久凍土", 60.32)]
    [InlineData("第60話 後編①　エリオール大森林の永久凍土", 60.31)]
    [InlineData("第60話 前編②　エリオール大森林の永久凍土", 60.12)]
    [InlineData("第60話 前編①　エリオール大森林の永久凍土", 60.11)]
    [InlineData("第48話前編①　ガーフィールの結界", 48.11)]
    [InlineData("第48話前編②　ガーフィールの結界", 48.12)]
    [InlineData("第48話後編①　クウェインの石は一人じゃ上がらない", 48.31)]
    [InlineData("第48話後編②　クウェインの石は一人じゃ上がらない", 48.32)]
    [InlineData("第8話後編　試験結果", 8.3)]
    [InlineData("第8話　試験結果", 8)]
    [InlineData("第1話　帰り着いた場所で", 1)]
    public void ChapterNumber_parses_plan_samples(string title, decimal expected)
    {
        Assert.Equal(expected, ComicWalkerChapterNumber.Parse(title));
    }

    [Fact]
    public void ChapterNumber_falls_back_to_ChapterNumberParser()
    {
        Assert.Equal(12m, ComicWalkerChapterNumber.Parse("Chapter 12"));
    }

    [Fact]
    public void ChapterNumber_returns_null_for_unparseable_text()
    {
        Assert.Null(ComicWalkerChapterNumber.Parse("特別読み切り"));
    }

    [Fact]
    public async Task GetPages_sorts_by_page_and_carries_XorKeyHex_with_null_headers()
    {
        var source = SourceFor(new()
        {
            ["contents/viewer"] = FakeHttpClientFactory.Fixture("comicwalker-viewer.json")
        });

        var pages = await source.GetPagesAsync(new SourceChapter(
            "comicwalker", "KC_002386_S", "018d6d6c-d9ac-77f4-b984-b753dcb562cd", "第1話", 1m, null, null, "ja", null));

        Assert.Equal(5, pages.Pages.Count);
        Assert.Null(pages.Pages[0].Headers);
        Assert.Equal("f6424e5dccf22015", pages.Pages[0].XorKeyHex);
        Assert.Contains("/1_", pages.Pages[0].Url);
        Assert.Contains("/2_", pages.Pages[1].Url);
        Assert.Contains("/3_", pages.Pages[2].Url);
        Assert.Contains("/4_", pages.Pages[3].Url);
        Assert.Contains("/5_", pages.Pages[4].Url);
    }

    [Fact]
    public async Task GetPages_throws_ChapterLocked_for_empty_manuscripts()
    {
        var source = SourceFor(new()
        {
            ["contents/viewer"] = FakeHttpClientFactory.Fixture("comicwalker-viewer-locked.json")
        });

        await Assert.ThrowsAsync<ChapterLockedException>(() => source.GetPagesAsync(new SourceChapter(
            "comicwalker", "KC_002386_S", "018d6d6d-1243-7a74-9693-91c7aaf90359", "第8話", 8m, null, null, "ja", null)));
    }

    [Fact]
    public async Task GetPages_rejects_non_xor_drmMode()
    {
        var source = SourceFor(new()
        {
            ["contents/viewer"] = "{\"manuscripts\":[{\"drmMode\":\"aes\",\"drmHash\":\"abc\",\"drmImageUrl\":\"https://cdn.comic-walker.com/x.webp\",\"page\":1}]}"
        });

        await Assert.ThrowsAsync<NotSupportedException>(() => source.GetPagesAsync(new SourceChapter(
            "comicwalker", "KC_002386_S", "some-id", "第1話", 1m, null, null, "ja", null)));
    }

    [Fact]
    public async Task GetPages_throws_on_a_manuscript_missing_drmImageUrl_or_drmHash()
    {
        // A viewer response shape change here must not silently drop a page.
        var source = SourceFor(new()
        {
            ["contents/viewer"] = "{\"manuscripts\":[{\"drmMode\":\"xor\",\"page\":1}]}"
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => source.GetPagesAsync(new SourceChapter(
            "comicwalker", "KC_002386_S", "some-id", "第1話", 1m, null, null, "ja", null)));
        Assert.Contains("contents/viewer", ex.Message);
    }

    [Fact]
    public async Task Unexpected_search_shape_throws_InvalidOperationException_with_url_and_body()
    {
        var source = SourceFor(new() { ["search/keywords"] = "{\"oops\":true}" });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => source.SearchAsync("リゼロ"));
        Assert.Contains("search/keywords", ex.Message);
        Assert.Contains("oops", ex.Message);
    }
}
