using System.Net;
using Maki.Core.Http;
using Maki.Core.Sources;
using Maki.Sources.Webtoons;

namespace Maki.Sources.Tests;

public class WebtoonsSourceTests
{
    private static WebtoonsSource SourceFor(Dictionary<string, string> responses) =>
        new(new FakeHttpClientFactory(responses));

    private static (WebtoonsSource Source, FakeHttpClientFactory Factory) SourceWithFactory(
        Dictionary<string, string> responses)
    {
        var factory = new FakeHttpClientFactory(responses);
        return (new WebtoonsSource(factory), factory);
    }

    /// <summary>
    /// Routes through the same <see cref="RateLimitDetectingHandler"/> the real named
    /// client uses, so a 429 for one locale surfaces as a genuine <see cref="RateLimitException"/>
    /// rather than something invented for the test.
    /// </summary>
    private sealed class RateLimitedLocaleFactory(
        string rateLimitedUrlSubstring, Dictionary<string, string> responses) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new RateLimitDetectingHandler { InnerHandler = new RoutingHandler(rateLimitedUrlSubstring, responses) })
            {
                BaseAddress = new Uri("https://fixture.test/")
            };

        private sealed class RoutingHandler(string rateLimitedUrlSubstring, Dictionary<string, string> responses)
            : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var url = request.RequestUri!.ToString();
                if (url.Contains(rateLimitedUrlSubstring, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new HttpResponseMessage((HttpStatusCode)429));
                }

                foreach (var (substring, body) in responses)
                {
                    if (url.Contains(substring, StringComparison.OrdinalIgnoreCase))
                    {
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(body)
                        });
                    }
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        }
    }

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
    public async Task Search_merges_locales_english_first_and_survives_a_failing_locale()
    {
        // Only en and es are mapped; id, th, fr, zh-hant and de 404 against the fake
        // factory and must be dropped rather than failing the whole search.
        var source = SourceFor(new()
        {
            ["en/search"] = FakeHttpClientFactory.Fixture("webtoons-search.html"),
            ["es/search"] = FakeHttpClientFactory.Fixture("webtoons-search-es.html")
        });

        var results = await source.SearchAsync("tower of god");

        var list = results.ToList();
        var englishIndex = list.FindIndex(r => r.SourceSeriesId == "fantasy/tower-of-god/95");
        var spanishIndex = list.FindIndex(r => r.SourceSeriesId == "es/fantasy/tower-of-god/1718");
        Assert.True(englishIndex >= 0);
        Assert.True(spanishIndex >= 0);
        Assert.True(englishIndex < spanishIndex);
    }

    [Fact]
    public async Task Search_tolerates_a_locale_that_is_rate_limited()
    {
        // "id" answers 429, which RateLimitDetectingHandler turns into a RateLimitException,
        // not an HttpRequestException; SearchLocaleAsync must drop that locale too instead of
        // letting the exception fail the other six.
        var factory = new RateLimitedLocaleFactory("id/search", new()
        {
            ["en/search"] = FakeHttpClientFactory.Fixture("webtoons-search.html")
        });
        var source = new WebtoonsSource(factory);

        var results = await source.SearchAsync("tower of god");

        Assert.Contains(results, r => r.SourceSeriesId == "fantasy/tower-of-god/95");
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
    public async Task GetSeries_reads_status_from_the_icon_class_on_the_es_fixture()
    {
        // The schedule text itself is localized ("TODOS LOS LUNES"), so status must come
        // from the txt_ico_up/txt_ico_completed class, not an English word match.
        var source = SourceFor(new() { ["title_no=1718"] = FakeHttpClientFactory.Fixture("webtoons-list-es.html") });

        var detail = await source.GetSeriesAsync("es/fantasy/tower-of-god/1718");

        Assert.Equal("Tower of God", detail.Title);
        Assert.Equal("Ongoing", detail.Status);
        Assert.Equal("https://www.webtoons.com/es/fantasy/tower-of-god/list?title_no=1718", detail.Url);
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
    public async Task ListChapters_uses_the_mobile_api_for_a_locale()
    {
        var source = SourceFor(new()
        {
            ["webtoon/1718/episodes"] = FakeHttpClientFactory.Fixture("webtoons-episodes-es.json")
        });

        var chapters = await source.ListChaptersAsync("es/fantasy/tower-of-god/1718");

        Assert.Equal(30, chapters.Count); // fixture trims the full 652-episode list to 30
        Assert.All(chapters, c => Assert.Equal("es", c.Language));
        Assert.Equal(1m, chapters.First().Number);
        Assert.True(chapters.First().Number < chapters.Last().Number);
    }

    [Fact]
    public async Task ListChapters_tags_zh_hant_language_from_the_series_id_locale()
    {
        // The API response carries no language of its own; the tag comes entirely from
        // which locale segment the series id was resolved under.
        var source = SourceFor(new()
        {
            ["webtoon/1718/episodes"] = FakeHttpClientFactory.Fixture("webtoons-episodes-es.json")
        });

        var chapters = await source.ListChaptersAsync("zh-hant/fantasy/tower-of-god/1718");

        Assert.NotEmpty(chapters);
        Assert.All(chapters, c => Assert.Equal("zh-Hant", c.Language));
    }

    [Fact]
    public async Task ListChapters_reads_canvas_locale_via_the_api_with_reading_language_code()
    {
        var (source, factory) = SourceWithFactory(new()
        {
            ["canvas/155834/episodes"] = FakeHttpClientFactory.Fixture("webtoons-episodes-canvas-th.json")
        });

        var chapters = await source.ListChaptersAsync("th/canvas/sky-tower-moon-tower-the-aureum-path/155834");

        Assert.Equal(11, chapters.Count);
        Assert.All(chapters, c => Assert.Equal("th", c.Language));
        Assert.Contains(factory.Requests, r => r.Contains("readingLanguageCode=th"));
    }

    [Fact]
    public async Task ListChapters_falls_back_to_html_when_the_api_reports_failure()
    {
        var source = SourceFor(new()
        {
            ["webtoon/95/episodes"] = FakeHttpClientFactory.Fixture("webtoons-episodes-missing.json"),
            ["page=1"] = FakeHttpClientFactory.Fixture("webtoons-list-page1.html"),
            ["page="] = FakeHttpClientFactory.Fixture("webtoons-list-last.html")
        });

        var chapters = await source.ListChaptersAsync("fantasy/tower-of-god/95");

        Assert.Equal(13, chapters.Count);
        Assert.All(chapters, c => Assert.Equal("en", c.Language));
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

    [Fact]
    public async Task GetPages_builds_the_locale_viewer_url()
    {
        var (source, factory) = SourceWithFactory(new()
        {
            ["viewer"] = FakeHttpClientFactory.Fixture("webtoons-viewer-es.html")
        });

        var pages = await source.GetPagesAsync(new SourceChapter(
            "webtoons", "es/fantasy/tower-of-god/1718", "1|t-1-ep-000", "1", 1, null, null, "es", null));

        Assert.NotEmpty(pages.Pages);
        Assert.DoesNotContain(pages.Pages, p => p.Url.Contains("decoy"));
        Assert.Contains(factory.Requests, r => r.Contains("es/fantasy/tower-of-god/t-1-ep-000/viewer"));
    }

    [Fact]
    public async Task GetPages_throws_chapter_locked_when_the_viewer_has_no_images()
    {
        var source = SourceFor(new() { ["viewer"] = "<html><body><div id=\"_imageList\"></div></body></html>" });

        await Assert.ThrowsAsync<ChapterLockedException>(() => source.GetPagesAsync(new SourceChapter(
            "webtoons", "fantasy/tower-of-god/95", "1|episode", "1", 1, null, null, "en", null)));
    }
}
