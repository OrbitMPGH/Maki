using Maki.Core.Sources;
using Maki.Sources.Shinigami;

namespace Maki.Sources.Tests;

public class ShinigamiSourceTests
{
    private const string SeriesId = "5c612573-fe38-42df-8618-dc3de1c9d04a";

    [Fact]
    public async Task Search_parses_ids_titles_and_covers()
    {
        var source = new ShinigamiSource(new FakeHttpClientFactory(new()
        {
            ["manga/list"] = FakeHttpClientFactory.Fixture("shinigami-search.json")
        }));

        var results = await source.SearchAsync("solo leveling");

        Assert.Equal(29, results.Count);
        Assert.Equal(SeriesId, results[0].SourceSeriesId);
        Assert.Equal("Solo Leveling", results[0].Title);
        Assert.Equal("https://assets.shngm.id/thumbnail/image/09919a8b39cb.jpeg", results[0].CoverUrl);
        Assert.Equal($"https://11.shinigami.asia/series/{SeriesId}", results[0].Url);
    }

    [Fact]
    public async Task Search_falls_back_to_cover_image_when_portrait_is_empty()
    {
        var source = new ShinigamiSource(new FakeHttpClientFactory(new()
        {
            ["manga/list"] = FakeHttpClientFactory.Fixture("shinigami-search.json")
        }));

        var results = await source.SearchAsync("solo leveling");

        var sideStory = Assert.Single(results, r => r.Title == "Solo Leveling Side Story");
        Assert.Equal("https://assets.shngm.id/thumbnail/cover/1ee5e6608288.jpeg", sideStory.CoverUrl);
        Assert.All(results, r => Assert.False(string.IsNullOrWhiteSpace(r.CoverUrl)));
    }

    [Fact]
    public async Task Search_sends_the_q_parameter_not_search()
    {
        var factory = new FakeHttpClientFactory(new()
        {
            ["manga/list"] = FakeHttpClientFactory.Fixture("shinigami-search.json")
        });

        await new ShinigamiSource(factory).SearchAsync("solo leveling");

        // Uri.ToString() (what Requests captures) decodes %20 back to a space for display,
        // so this checks the decoded form rather than the wire encoding.
        var request = Assert.Single(factory.Requests);
        Assert.Contains("q=solo leveling", request);
        Assert.DoesNotContain("search=", request);
    }

    [Fact]
    public async Task Series_detail_parses_title_cover_and_status()
    {
        var source = new ShinigamiSource(new FakeHttpClientFactory(new()
        {
            ["manga/detail"] = FakeHttpClientFactory.Fixture("shinigami-series.json")
        }));

        var detail = await source.GetSeriesAsync(SeriesId);

        Assert.Equal("Solo Leveling", detail.Title);
        Assert.Equal("https://assets.shngm.id/thumbnail/image/09919a8b39cb.jpeg", detail.CoverUrl);
        Assert.Equal("Ongoing", detail.Status);
        Assert.Equal($"https://11.shinigami.asia/series/{SeriesId}", detail.Url);
    }

    [Fact]
    public async Task Chapter_list_is_normalized_and_keeps_chapter_zero()
    {
        var source = new ShinigamiSource(new FakeHttpClientFactory(new()
        {
            ["chapter"] = FakeHttpClientFactory.Fixture("shinigami-chapters.json")
        }));

        var chapters = await source.ListChaptersAsync(SeriesId);

        Assert.Equal(180, chapters.Count);
        Assert.Equal(0, chapters[0].Number);
        Assert.Equal(179, chapters[^1].Number);
        Assert.All(chapters, c => Assert.Equal("id", c.Language));
        Assert.All(chapters, c => Assert.Null(c.Volume));
    }

    [Fact]
    public async Task An_untitled_unparseable_chapter_gets_a_synthetic_title_but_a_numbered_one_stays_null()
    {
        var source = new ShinigamiSource(new FakeHttpClientFactory(new()
        {
            ["chapter"] = """
                {"retcode":0,"message":"success","meta":{"page":1,"total_page":1},"data":[
                    {"chapter_id":"a","manga_id":"m","chapter_title":"","chapter_number":"TBA","release_date":null},
                    {"chapter_id":"b","manga_id":"m","chapter_title":"","chapter_number":5,"release_date":null}
                ]}
                """
        }));

        var chapters = await source.ListChaptersAsync(SeriesId);

        var untitled = Assert.Single(chapters, c => c.SourceChapterId == "a");
        Assert.Null(untitled.Number);
        Assert.Equal("Chapter TBA", untitled.Title);

        var numbered = Assert.Single(chapters, c => c.SourceChapterId == "b");
        Assert.Equal(5, numbered.Number);
        Assert.Null(numbered.Title);
    }

    [Fact]
    public async Task Chapter_list_asks_for_a_large_page_size_and_stops_when_meta_says_one_page()
    {
        var factory = new FakeHttpClientFactory(new()
        {
            ["chapter"] = FakeHttpClientFactory.Fixture("shinigami-chapters.json")
        });

        await new ShinigamiSource(factory).ListChaptersAsync(SeriesId);

        var request = Assert.Single(factory.Requests);
        Assert.Contains("page_size=3000", request);
    }

    [Fact]
    public async Task Pages_join_base_url_and_path_with_no_double_slash_and_carry_a_referer()
    {
        var source = new ShinigamiSource(new FakeHttpClientFactory(new()
        {
            ["chapter/detail"] = FakeHttpClientFactory.Fixture("shinigami-pages.json")
        }));

        var chapter = new SourceChapter(
            "shinigami", SeriesId, "d3016bcc-ad3e-43c5-b621-4b9cdd17df3f", "1", 1, null, null, "id", null);

        var pages = await source.GetPagesAsync(chapter);

        Assert.Equal(7, pages.Pages.Count);
        Assert.Equal(
            "https://assets.shngm.id/chapter/manga_5c612573-fe38-42df-8618-dc3de1c9d04a/chapter_d3016bcc-ad3e-43c5-b621-4b9cdd17df3f/1-0a33ddd6707e.jpg",
            pages.Pages[0].Url);
        Assert.DoesNotContain("//1-", pages.Pages[0].Url.Replace("https://", string.Empty));
        Assert.Equal("https://11.shinigami.asia/", pages.Pages[0].Headers!["Referer"]);
    }

    [Fact]
    public async Task Zero_pages_throws_ChapterLockedException()
    {
        var source = new ShinigamiSource(new FakeHttpClientFactory(new()
        {
            ["chapter/detail"] = """{"retcode":0,"message":"success","meta":{},"data":{"chapter_id":"x","manga_id":"y","base_url":"https://assets.shngm.id","chapter":{"path":"/chapter/manga_y/chapter_x/","data":[]}}}"""
        }));

        var chapter = new SourceChapter("shinigami", SeriesId, "x", "1", 1, null, null, "id", null);

        await Assert.ThrowsAsync<ChapterLockedException>(() => source.GetPagesAsync(chapter));
    }
}

[Collection(BaseUrlOverrideCollection.Name)]
public class ShinigamiBaseUrlOverrideTests
{
    private const string SeriesId = "5c612573-fe38-42df-8618-dc3de1c9d04a";

    [Theory]
    [InlineData("https://shinigami.example/series/" + SeriesId, SeriesId)]
    [InlineData("https://www.shinigami.example/series/" + SeriesId, SeriesId)]
    [InlineData("https://12.shinigami.asia/series/" + SeriesId, SeriesId)]
    [InlineData("https://other.example/series/" + SeriesId, null)]
    public void ResolveSeriesIdFromUrl_AcceptsTheOverriddenHost(string url, string? expected)
    {
        using var _ = new BaseUrlOverride("MAKI_SOURCE_SHINIGAMI_BASEURL", "https://shinigami.example");
        ISource source = new ShinigamiSource(new FakeHttpClientFactory([]));

        Assert.Equal(expected, source.ResolveSeriesIdFromUrl(new Uri(url)));
    }
}
