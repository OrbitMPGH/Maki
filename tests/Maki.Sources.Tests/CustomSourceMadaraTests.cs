using Maki.Core.Http;
using Maki.Core.Sources.Custom;
using Maki.Sources.Custom;

namespace Maki.Sources.Tests;

/// <summary>The <c>madara</c> engine against Toonily's recorded pages, which the built-in Toonily source parses too.</summary>
public class CustomSourceMadaraTests
{
    private const string SeriesId = "secret-class-38c3e37a";

    private static CustomSourceDefinition Toonily(string chapterList = "inline") => new()
    {
        Id = "toonily-copy",
        Name = "Toonily copy",
        BaseUrl = "https://toonily.com",
        Engine = CustomEngines.Madara,
        EngineOptions = new CustomEngineOptions { SeriesPath = "serie", ChapterList = chapterList },
        Sample = new CustomSample
        {
            SeriesUrl = $"https://toonily.com/serie/{SeriesId}/",
            ChapterUrl = $"https://toonily.com/serie/{SeriesId}/chapter-242/",
            Query = "Secret Class"
        }
    };

    private static CustomSource Source(CustomSourceDefinition definition, IHtmlFetcher fetcher) =>
        new(definition, fetcher, new FakeHttpClientFactory([]), new SpacingGate(TimeSpan.Zero));

    [Fact]
    public void Definition_validates_without_any_sections()
    {
        Assert.Empty(CustomSourceValidator.Validate(Toonily()));
    }

    [Fact]
    public void Sections_next_to_an_engine_are_refused()
    {
        var problems = CustomSourceValidator.Validate(Toonily() with { Pages = new CustomPages { List = new ListExtractor { Css = "img" } } });

        Assert.Contains(problems, p => p.Key == "error.customSource.engineSections");
    }

    [Theory]
    [InlineData("wordpress", "inline", "engine")]
    [InlineData("madara", "lazy", "engineOptions.chapterList")]
    public void Unknown_engines_and_options_are_refused(string engine, string chapterList, string field)
    {
        var problems = CustomSourceValidator.Validate(Toonily(chapterList) with { Engine = engine });

        Assert.Contains(problems, p => p.Key == "error.customSource.fieldInvalid" && p.ToString().Contains(field));
    }

    [Fact]
    public async Task Search_reads_the_theme_search_page()
    {
        var fetcher = new FakeHtmlFetcher(new() { ["?s="] = FakeHttpClientFactory.Fixture("toonily-search-get.html") });

        var results = await Source(Toonily(), fetcher).SearchAsync("secret class");

        Assert.Contains(results, r => r.SourceSeriesId == SeriesId && r.Url == $"https://toonily.com/serie/{SeriesId}/");
        Assert.Equal("https://toonily.com/?s=secret%20class&post_type=wp-manga", Assert.Single(fetcher.Requested));
    }

    [Fact]
    public async Task Series_and_inline_chapters_match_the_built_in_parser()
    {
        var fetcher = new FakeHtmlFetcher(new() { ["/serie/"] = FakeHttpClientFactory.Fixture("toonily-series.html") });
        var source = Source(Toonily(), fetcher);

        var series = await source.GetSeriesAsync(SeriesId);
        var chapters = await source.ListChaptersAsync(SeriesId);
        var builtIn = await new Toonily.ToonilySource(fetcher).ListChaptersAsync(SeriesId);

        Assert.Equal("Secret Class", series.Title);
        Assert.Equal("Ongoing", series.Status);
        Assert.Equal(builtIn.Select(c => (c.Number, c.SourceChapterId, c.Url)), chapters.Select(c => (c.Number, c.SourceChapterId, c.Url)));
        Assert.Equal(new DateTime(2024, 12, 9), chapters[^1].ReleaseDate);
        Assert.False(source.ReleaseDatesAreReliable);
    }

    [Fact]
    public async Task Ajax_list_posts_to_the_series_endpoint()
    {
        var fetcher = new FakeHtmlFetcher(new() { ["ajax/chapters/"] = FakeHttpClientFactory.Fixture("toonily-series.html") });

        var chapters = await Source(Toonily("ajax"), fetcher).ListChaptersAsync(SeriesId);

        Assert.True(chapters.Count >= 240);
        var sent = Assert.Single(fetcher.Requests);
        Assert.Equal($"https://toonily.com/serie/{SeriesId}/ajax/chapters/", sent.Url);
        Assert.Equal("", sent.FormBody);
    }

    [Fact]
    public async Task Ajax_list_falls_back_to_admin_ajax_with_the_post_id()
    {
        var fetcher = new RefusingFetcher("ajax/chapters/", new FakeHtmlFetcher(new()
        {
            ["admin-ajax.php"] = FakeHttpClientFactory.Fixture("toonily-series.html"),
            ["/serie/"] = """<html><body class="wp-manga"><div id="manga-chapters-holder" data-id="4721"></div></body></html>"""
        }));

        var chapters = await Source(Toonily("ajax"), fetcher).ListChaptersAsync(SeriesId);

        Assert.True(chapters.Count >= 240);
        var post = fetcher.Inner.Requests[^1];
        Assert.Equal("https://toonily.com/wp-admin/admin-ajax.php", post.Url);
        Assert.Equal("action=manga_get_chapters&manga=4721", post.FormBody);
    }

    [Fact]
    public async Task Pages_come_from_the_reading_content()
    {
        var fetcher = new FakeHtmlFetcher(new() { ["/chapter-242"] = FakeHttpClientFactory.Fixture("toonily-chapter.html") });
        var source = Source(Toonily(), fetcher);

        var pages = await source.GetPagesAsync(new Core.Sources.SourceChapter(
            source.Name, SeriesId, "chapter-242", null, 242m, null, null, "en", null, null));

        Assert.True(pages.Pages.Count >= 35);
        Assert.All(pages.Pages, p => Assert.NotNull(p.Fetch));
        Assert.Equal($"https://toonily.com/serie/{SeriesId}/chapter-242/", Assert.Single(fetcher.Requested));
    }

    [Fact]
    public void Series_urls_resolve_through_the_implied_template()
    {
        var source = Source(Toonily(), new FakeHtmlFetcher([]));

        Assert.Equal(SeriesId, source.ResolveSeriesIdFromUrl(new Uri($"https://toonily.com/serie/{SeriesId}/")));
        Assert.Null(source.ResolveSeriesIdFromUrl(new Uri($"https://toonily.com/manga/{SeriesId}/")));
    }

    private sealed class RefusingFetcher(string refused, FakeHtmlFetcher inner) : IHtmlFetcher
    {
        public FakeHtmlFetcher Inner => inner;

        public Task<string> GetHtmlAsync(string url, CancellationToken ct = default) => FetchAsync(new HtmlFetchRequest(url), ct);

        public Task<string> FetchAsync(HtmlFetchRequest request, CancellationToken ct = default) =>
            request.Url.Contains(refused, StringComparison.Ordinal)
                ? throw new HttpRequestException("404")
                : inner.FetchAsync(request, ct);
    }
}
