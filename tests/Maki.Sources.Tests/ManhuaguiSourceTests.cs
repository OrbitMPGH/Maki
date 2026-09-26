using Maki.Core.Sources;
using Maki.Sources.Manhuagui;

namespace Maki.Sources.Tests;

public class ManhuaguiSourceTests
{
    private static ManhuaguiSource SourceFor(Dictionary<string, string> responses) =>
        new(new FakeHttpClientFactory(responses));

    [Fact]
    public async Task Search_parses_id_title_and_cover()
    {
        var source = SourceFor(new() { ["/s/"] = FakeHttpClientFactory.Fixture("manhuagui-search.html") });

        var results = await source.SearchAsync("海贼王");

        Assert.NotEmpty(results);
        var first = results[0];
        Assert.Equal("1128", first.SourceSeriesId);
        Assert.Equal("ONE PIECE航海王", first.Title);
        Assert.Equal("https://cf.mhgui.com/cpic/b/1128.jpg", first.CoverUrl);
        Assert.Contains("/comic/1128/", first.Url);
    }

    [Fact]
    public async Task GetSeries_parses_title_status_and_cover()
    {
        var source = SourceFor(new() { ["/comic/1128/"] = FakeHttpClientFactory.Fixture("manhuagui-series.html") });

        var detail = await source.GetSeriesAsync("1128");

        Assert.Equal("ONE PIECE航海王", detail.Title);
        Assert.Equal("Ongoing", detail.Status);
        Assert.Equal("https://cf.mhgui.com/cpic/h/1128.jpg", detail.CoverUrl);
        Assert.False(string.IsNullOrWhiteSpace(detail.Description));
    }

    [Fact]
    public async Task ListChapters_splits_chapters_and_volumes_and_skips_extras()
    {
        var source = SourceFor(new() { ["/comic/1128/"] = FakeHttpClientFactory.Fixture("manhuagui-series.html") });

        var chapters = await source.ListChaptersAsync("1128");

        var numberedChapters = chapters.Where(c => c.Number is not null).ToList();
        var volumes = chapters.Where(c => c.Volume is not null && c.Number is null).ToList();

        Assert.Equal(516, numberedChapters.Count);
        Assert.Equal(112, volumes.Count);
        Assert.All(chapters, c => Assert.Equal("zh-Hans", c.Language));
        // 番外篇 entries are bare numbers ("食戟的山治06话"-style) that would collide with main
        // chapters, so the whole section is skipped; total stays at chapters + volumes.
        Assert.Equal(516 + 112, chapters.Count);

        // Every chapter (including null-number volumes) carries a non-null Title so Core's
        // ChapterIdentity dedupe-by-title for null-number chapters has something to key on.
        Assert.All(chapters, c => Assert.False(string.IsNullOrEmpty(c.Title)));

        Assert.Contains(chapters, c => c.SourceChapterId == "909042" && c.Number == 1193m);
    }

    [Fact]
    public async Task ListChapters_tags_latest_chapter_with_release_date()
    {
        var source = SourceFor(new() { ["/comic/1128/"] = FakeHttpClientFactory.Fixture("manhuagui-series.html") });

        var chapters = await source.ListChaptersAsync("1128");

        var latest = Assert.Single(chapters, c => c.SourceChapterId == "909042");
        Assert.Equal(new DateTime(2026, 9, 17), latest.ReleaseDate);
        Assert.All(chapters.Where(c => c.SourceChapterId != "909042"), c => Assert.Null(c.ReleaseDate));
    }

    [Fact]
    public async Task ListChapters_parses_audit_gated_viewstate_fallback()
    {
        var source = SourceFor(new() { ["/comic/99999/"] = FakeHttpClientFactory.Fixture("manhuagui-viewstate.html") });

        var chapters = await source.ListChaptersAsync("99999");

        var chapter = Assert.Single(chapters);
        Assert.Equal("555111", chapter.SourceChapterId);
        Assert.Equal(5m, chapter.Number);
    }

    [Fact]
    public void LzString_decompresses_the_series_fixture_word_list()
    {
        var html = FakeHttpClientFactory.Fixture("manhuagui-chapter.html");
        var match = System.Text.RegularExpressions.Regex.Match(
            html, @"\}\('(?<p>.*)',(?<a>\d+),(?<c>\d+),'(?<k>[A-Za-z0-9+/=]+)'\[",
            System.Text.RegularExpressions.RegexOptions.Singleline);

        Assert.True(match.Success);
        var words = LzString.DecompressFromBase64(match.Groups["k"].Value).Split('|');

        Assert.Equal(51, words.Length);
    }

    [Fact]
    public void PackedScript_unpacks_the_chapter_fixture()
    {
        var html = FakeHttpClientFactory.Fixture("manhuagui-chapter.html");

        var data = PackedScript.Unpack(html);

        Assert.Equal(1128, data.Bid);
        Assert.Equal(909042, data.Cid);
        Assert.Equal(16, data.Files.Count);
        Assert.Equal("0001.jpg.webp", data.Files[0]);
        Assert.Equal("/ps1/h/op/第1193话/", data.Path);
        Assert.Equal(1791463300, data.Sl.E);
        Assert.Equal("0jIfZ8oXiLbJkHmVEPieag", data.Sl.M);
    }

    [Fact]
    public async Task GetPages_builds_encoded_urls_with_referer()
    {
        var source = SourceFor(new() { ["/909042.html"] = FakeHttpClientFactory.Fixture("manhuagui-chapter.html") });

        var pages = await source.GetPagesAsync(new SourceChapter(
            "manhuagui", "1128", "909042", "第1193话", 1193, null, "第1193话", "zh-Hans", null));

        Assert.Equal(16, pages.Pages.Count);
        var first = pages.Pages[0];
        Assert.StartsWith("https://i.hamreus.com/ps1/h/op/%E7%AC%AC1193%E8%AF%9D/0001.jpg.webp?e=", first.Url);
        Assert.Equal("https://www.manhuagui.com/", first.Headers!["Referer"]);
    }
}
