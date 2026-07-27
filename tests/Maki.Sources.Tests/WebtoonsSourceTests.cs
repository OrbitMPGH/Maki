using Maki.Core.Sources;
using Maki.Sources.Webtoons;

namespace Maki.Sources.Tests;

public class WebtoonsSourceTests
{
    private static WebtoonsSource SourceFor(Dictionary<string, string> responses) =>
        new(new FakeHttpClientFactory(responses));

    [Fact]
    public async Task Search_parses_originals_and_canvas()
    {
        var source = SourceFor(new() { ["en/search"] = FakeHttpClientFactory.Fixture("webtoons-search.html") });

        var results = await source.SearchAsync("tower of god");

        var original = Assert.Single(results, r => r.Title == "Tower of God");
        Assert.Equal("fantasy/tower-of-god/95", original.SourceSeriesId);
        Assert.Contains("title_no=95", original.Url);

        // CANVAS entries live under a fixed path segment and must keep it.
        Assert.Contains(results, r => r.SourceSeriesId == "canvas/tower-of-god-no-mans-tower/726081");
    }

    [Fact]
    public async Task Search_strips_the_image_transform_from_covers()
    {
        var source = SourceFor(new() { ["en/search"] = FakeHttpClientFactory.Fixture("webtoons-search.html") });

        var results = await source.SearchAsync("tower of god");

        Assert.All(results, r =>
        {
            Assert.StartsWith("https://", r.CoverUrl);
            Assert.DoesNotContain("?type=", r.CoverUrl);
        });
    }

    [Fact]
    public async Task GetSeries_reads_the_open_graph_block_and_schedule()
    {
        var source = SourceFor(new() { ["title_no=95"] = FakeHttpClientFactory.Fixture("webtoons-list-page1.html") });

        var detail = await source.GetSeriesAsync("fantasy/tower-of-god/95");

        Assert.Equal("Tower of God", detail.Title);
        Assert.Equal("Ongoing", detail.Status); // fixture reads "UP EVERY MONDAY"
        Assert.Contains("What do you desire?", detail.Description);
        Assert.DoesNotContain("?type=", detail.CoverUrl);
    }

    [Fact]
    public async Task ListChapters_walks_pages_until_one_adds_nothing()
    {
        // Out-of-range pages clamp to the last page rather than 404ing, so the "page="
        // fallback below stands in for every page past the tail — which is what stops
        // the walk. Insertion order matters: the fake matches substrings in order.
        var source = SourceFor(new()
        {
            ["page=1"] = FakeHttpClientFactory.Fixture("webtoons-list-page1.html"),
            ["page="] = FakeHttpClientFactory.Fixture("webtoons-list-last.html")
        });

        var chapters = await source.ListChaptersAsync("fantasy/tower-of-god/95");

        Assert.Equal(13, chapters.Count); // 9 newest + the 4 on the clamped tail page
        Assert.All(chapters, c => Assert.Equal("en", c.Language));
        Assert.True(chapters.First().Number < chapters.Last().Number);
        Assert.Equal(1m, chapters.First().Number);
        Assert.Equal(653m, chapters.Last().Number);
    }

    [Fact]
    public async Task ListChapters_keeps_the_episode_slug_and_metadata()
    {
        var source = SourceFor(new()
        {
            ["page=1"] = FakeHttpClientFactory.Fixture("webtoons-list-page1.html"),
            ["page="] = FakeHttpClientFactory.Fixture("webtoons-list-last.html")
        });

        var chapters = await source.ListChaptersAsync("fantasy/tower-of-god/95");

        var latest = chapters.Last();
        Assert.Equal("653|season-3-ep-235-season-3-finale", latest.SourceChapterId);
        Assert.Equal("[Season 3] Ep. 235 (Season 3 Finale)", latest.Title);
        Assert.Equal(new DateTime(2025, 2, 23), latest.ReleaseDate!.Value.Date);
    }

    [Fact]
    public async Task ListChapters_reads_canvas_rows()
    {
        // CANVAS items carry no "#N" sequence label and repeat data-episode-no on their
        // edit links; the episode number still has to come off the list item itself.
        var source = SourceFor(new()
        {
            ["page=1"] = FakeHttpClientFactory.Fixture("webtoons-canvas-list.html"),
            ["page="] = FakeHttpClientFactory.Fixture("webtoons-canvas-list.html")
        });

        var chapters = await source.ListChaptersAsync("canvas/tower-of-god-no-mans-tower/726081");

        Assert.Equal(10, chapters.Count);
        Assert.Equal(165m, chapters.Last().Number);
        Assert.Equal("The Ending Of No Man's Tower Completed", chapters.Last().Title);
    }

    [Fact]
    public async Task GetPages_returns_only_the_viewer_strip_with_a_referer()
    {
        var source = SourceFor(new() { ["viewer"] = FakeHttpClientFactory.Fixture("webtoons-viewer.html") });

        var pages = await source.GetPagesAsync(new SourceChapter(
            "webtoons", "fantasy/tower-of-god/95", "1|season-1-ep-0", "1", 1, null, null, "en", null));

        Assert.NotEmpty(pages.Pages);
        Assert.DoesNotContain(pages.Pages, p => p.Url.Contains("decoy"));
        Assert.All(pages.Pages, p =>
        {
            Assert.StartsWith("https://", p.Url);
            Assert.DoesNotContain("?type=", p.Url);
            Assert.Equal("https://www.webtoons.com/", p.Headers!["Referer"]);
        });
    }
}
