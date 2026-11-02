using Maki.Core.Sources.Custom;
using Maki.Sources.Custom;
using Maki.Sources.Custom.Inference;

namespace Maki.Sources.Tests;

/// <summary>
/// The wizard's guessers against the HTML-scraped built-in sources' recorded pages. Expected counts
/// are the ones each built-in source's own tests assert for the same file.
/// </summary>
public class CustomSourceGuessTests
{
    private static CustomPage Page(string fixture, string url) => new(FakeHttpClientFactory.Fixture(fixture), new Uri(url));

    [Theory]
    [InlineData("silentquill-series.html", "https://silentquill.net/series/thus-the-seasons-learn-of-blue/", "https://silentquill.net/thus-the-seasons-learn-of-blue-chapter-2/",
        "/{chapterId}/", 2, "thus-the-seasons-learn-of-blue-chapter-2", 2)]
    [InlineData("toonily-series.html", "https://toonily.com/serie/secret-class-38c3e37a/", "https://toonily.com/serie/secret-class-38c3e37a/chapter-242/",
        "/serie/{seriesId}/{chapterId}/", 243, "chapter-242", 242)]
    [InlineData("mangapill-series.html", "https://mangapill.com/manga/1/berserk", "https://mangapill.com/chapters/1-20385000/berserk-chapter-385",
        "/chapters/{chapterId}", 403, "1-20385000/berserk-chapter-385", 385)]
    [InlineData("mangaworld-series.html", "https://www.mangaworld.mx/manga/1708/one-piece/", null,
        "/manga/1708/{seriesId}/read/{chapterId}", 39, "6ab6b1f9c1f8362c329080c7", 1194)]
    [InlineData("teamx-series.html", "https://olympustaff.com/series/SL", null,
        "/series/{seriesId}/{chapterId}", 40, "200.5", 200.5)]
    [InlineData("manhuagui-series.html", "https://www.manhuagui.com/comic/1128/", null,
        "/comic/{seriesId}/{chapterId}", 516, "127258.html", 743)]
    public void Chapter_list_guess_finds_the_list(
        string fixture, string seriesUrl, string? chapterUrl, string template, int count, string firstId, double firstNumber)
    {
        var series = Page(fixture, seriesUrl);
        var seriesId = UrlShape.Series(series.Url)!.Value.SeriesId;

        var best = ChapterListGuesser.Run(series, seriesId, chapterUrl is null ? null : new Uri(chapterUrl))[0];

        Assert.Equal(template, best.Chapters.Url);
        Assert.Equal(count, best.Count);
        Assert.Equal(firstId, best.Rows[0].Id);
        Assert.Equal((decimal)firstNumber, best.Rows[0].Number);
    }

    [Fact]
    public void Chapter_list_guess_runs_the_same_through_the_engine()
    {
        var series = Page("silentquill-series.html", "https://silentquill.net/series/thus-the-seasons-learn-of-blue/");
        var guess = ChapterListGuesser.Run(series, "thus-the-seasons-learn-of-blue", new Uri("https://silentquill.net/thus-the-seasons-learn-of-blue-chapter-2/"))[0];
        var definition = CustomSourceDefinition.Parse(FakeHttpClientFactory.Fixture("silentquill-definition.json")) with { Chapters = guess.Chapters };
        Assert.Empty(CustomSourceValidator.Validate(definition));

        var source = new CustomSource(definition, new FakeHtmlFetcher(new() { ["/series/"] = series.Body }), new FakeHttpClientFactory([]), new SpacingGate(TimeSpan.Zero));
        var chapters = source.ListChaptersAsync("thus-the-seasons-learn-of-blue").Result;

        Assert.Equal([1m, 2m], chapters.Select(c => c.Number));
        Assert.Equal("https://silentquill.net/thus-the-seasons-learn-of-blue-chapter-2/", chapters[1].Url);
        Assert.NotNull(chapters[1].ReleaseDate);
    }

    [Theory]
    [InlineData("silentquill-chapter.html", "https://silentquill.net/thus-the-seasons-learn-of-blue-chapter-2/", "silentquill-series.html", 40)]
    [InlineData("toonily-chapter.html", "https://toonily.com/serie/secret-class-38c3e37a/chapter-242/", "toonily-series.html", 35)]
    [InlineData("rawkuma-chapter.html", "https://rawkuma.net/manga/one-piece/chapter-1193.407873/", null, 17)]
    [InlineData("teamx-chapter.html", "https://olympustaff.com/series/SL/1", "teamx-series.html", 13)]
    [InlineData("mangaworld-chapter.html", "https://www.mangaworld.mx/manga/1708/one-piece/read/x", "mangaworld-series.html", 12)]
    [InlineData("manhwa18net-chapter.html", "https://manhwa18.net/manga/secret-class/chapter-318", "manhwa18net-series.html", 6)]
    [InlineData("mangapill-chapter.html", "https://mangapill.com/chapters/1-20385000/berserk-chapter-385", "mangapill-series.html", 23)]
    public void Page_list_guess_finds_every_page(string fixture, string chapterUrl, string? seriesFixture, int count)
    {
        var series = seriesFixture is null ? null : Page(seriesFixture, "https://example.com/series/x/");

        var guess = PageListGuesser.Run(Page(fixture, chapterUrl), series);

        Assert.NotNull(guess);
        Assert.Equal(count, guess.Images.Count);
        Assert.Empty(CustomSourceValidator.Validate(CustomSourceDefinition.Parse(FakeHttpClientFactory.Fixture("silentquill-definition.json")) with { Pages = guess.Pages }));
    }

    [Fact]
    public void Madara_is_recognised_with_its_series_path_and_list_mode()
    {
        var match = EngineDetector.Run(Page("toonily-series.html", "https://toonily.com/serie/secret-class-38c3e37a/"));

        Assert.Equal(CustomEngines.Madara, match?.Engine);
        Assert.Equal("serie", match?.Options.SeriesPath);
        Assert.Equal(CustomEngineOptions.Inline, match?.Options.ChapterList);
        Assert.Null(EngineDetector.Run(Page("silentquill-series.html", "https://silentquill.net/series/thus-the-seasons-learn-of-blue/")));
    }

    [Fact]
    public async Task Search_guess_tries_the_common_shapes_until_one_lists_the_sample()
    {
        var requested = new List<string>();
        var guess = await SearchGuesser.RunAsync(
            new Uri("https://silentquill.net/"), "/series/{seriesId}/", "thus-the-seasons-learn-of-blue", "Thus, the Seasons Learn of Blue",
            (url, _) =>
            {
                requested.Add(url.PathAndQuery);
                return Task.FromResult<(CustomPage?, string?)>(url.AbsolutePath == "/search/"
                    ? (Page("silentquill-search.html", url.AbsoluteUri), null)
                    : (null, "404"));
            },
            CancellationToken.None);

        Assert.Equal("/search/?q={query}", guess.Search?.Url);
        Assert.Contains("Thus, the Seasons Learn of Blue", guess.Titles);
        Assert.Equal(["/", "/?s=Thus%2C%20the%20Seasons%20Learn%20of%20Blue", "/search?q=Thus%2C%20the%20Seasons%20Learn%20of%20Blue",
            "/search/?q=Thus%2C%20the%20Seasons%20Learn%20of%20Blue"], requested);

        var definition = CustomSourceDefinition.Parse(FakeHttpClientFactory.Fixture("silentquill-definition.json")) with { Search = guess.Search };
        Assert.Empty(CustomSourceValidator.Validate(definition));
        var results = await new CustomSource(definition, new FakeHtmlFetcher(new() { ["/search/"] = FakeHttpClientFactory.Fixture("silentquill-search.html") }),
            new FakeHttpClientFactory([]), new SpacingGate(TimeSpan.Zero)).SearchAsync("Thus, the Seasons Learn of Blue");
        Assert.Contains(results, r => r.SourceSeriesId == "thus-the-seasons-learn-of-blue" && r.Title == "Thus, the Seasons Learn of Blue");
    }

    [Fact]
    public async Task Search_guess_reports_no_search_when_nothing_lists_the_sample()
    {
        var guess = await SearchGuesser.RunAsync(
            new Uri("https://silentquill.net/"), "/series/{seriesId}/", "thus-the-seasons-learn-of-blue", "Thus",
            (_, _) => Task.FromResult<(CustomPage?, string?)>((null, "404")), CancellationToken.None);

        Assert.Null(guess.Search);
        Assert.Null(guess.Catalogue);
        Assert.Equal(1 + SearchGuesser.CommonShapes.Length + SearchGuesser.CatalogueShapes.Length, guess.Tried.Count);
    }

    [Fact]
    public void A_home_page_search_form_becomes_a_template_with_its_hidden_fields()
    {
        var home = new CustomPage("""
            <html><body><form method="get" action="/"><input type="hidden" name="post_type" value="wp-manga"><input type="text" name="s"></form></body></html>
            """, new Uri("https://example.com/"));

        Assert.Equal("/?s={query}&post_type=wp-manga", SearchGuesser.FormTemplate(home));
    }

    [Fact]
    public void Series_template_takes_the_last_segment_as_the_id()
    {
        Assert.Equal(("/series/{seriesId}/", "thus"), UrlShape.Series(new Uri("https://silentquill.net/series/thus/")));
        Assert.Equal(("/manga/1/{seriesId}", "berserk"), UrlShape.Series(new Uri("https://mangapill.com/manga/1/berserk")));
        Assert.Null(UrlShape.Series(new Uri("https://example.com/series.php?id=4")));
    }
}
