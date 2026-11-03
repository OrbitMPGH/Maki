using Maki.Core.Sources;
using Maki.Core.Sources.Custom;
using Maki.Sources.Custom;
using Maki.Sources.Custom.Inference;

namespace Maki.Sources.Tests;

/// <summary>
/// The MangaThemesia, Keyoapp and HeanCMS engines against pages recorded on 2026-10-27 from Arena
/// Scans, Kewn Scans and Omega Scans' API.
/// </summary>
public class CustomSourceEngineTests
{
    private static CustomSourceDefinition Definition(string engine, string baseUrl, string seriesUrl, string chapterUrl, string query = "x") => new()
    {
        Id = engine,
        Name = engine,
        BaseUrl = baseUrl,
        Engine = engine,
        Sample = new CustomSample { SeriesUrl = seriesUrl, ChapterUrl = chapterUrl, Query = query }
    };

    private static CustomSource Source(CustomSourceDefinition definition, FakeHtmlFetcher fetcher) =>
        new(definition, fetcher, new FakeHttpClientFactory([]), new SpacingGate(TimeSpan.Zero));

    private static SourceChapter Chapter(CustomSource source, string seriesId, string chapterId) =>
        new(source.Name, seriesId, chapterId, null, null, null, null, "en", null, null);

    // ── MangaThemesia ─────────────────────────────────────────────────

    private static readonly CustomSourceDefinition Arena = Definition(CustomEngines.MangaThemesia, "https://arenascan.com",
        "https://arenascan.com/manga/acidity-of-regret/", "https://arenascan.com/acidity-of-regret-chapter-69/", "acidity");

    private static FakeHtmlFetcher ArenaPages() => new(new()
    {
        ["?s="] = FakeHttpClientFactory.Fixture("mangathemesia-search.html"),
        ["/manga/acidity-of-regret/"] = FakeHttpClientFactory.Fixture("mangathemesia-series.html"),
        ["/acidity-of-regret-chapter-69/"] = FakeHttpClientFactory.Fixture("mangathemesia-chapter.html")
    });

    [Fact]
    public async Task MangaThemesia_reads_search_series_chapters_and_the_ts_reader_pages()
    {
        Assert.Empty(CustomSourceValidator.Validate(Arena));
        var source = Source(Arena, ArenaPages());

        var results = await source.SearchAsync("acidity");
        var series = await source.GetSeriesAsync("acidity-of-regret");
        var chapters = await source.ListChaptersAsync("acidity-of-regret");
        var pages = await source.GetPagesAsync(Chapter(source, "acidity-of-regret", "acidity-of-regret-chapter-69"));

        Assert.Contains(results, r => r.SourceSeriesId == "acidity-of-regret" && r.Title == "Acidity of Regret");
        Assert.Equal("Acidity of Regret", series.Title);
        Assert.StartsWith("https://arenascan.com/wp-content/uploads/", series.CoverUrl);
        Assert.Equal(70, chapters.Count);
        Assert.Equal(70m, chapters[^1].Number);
        Assert.Equal("https://arenascan.com/acidity-of-regret-chapter-70/", chapters[^1].Url);
        Assert.Equal(new DateTime(2026, 10, 27), chapters[^1].ReleaseDate);
        Assert.Equal("Chapter 70", chapters[^1].NumberRaw);
        Assert.Equal(77, pages.Pages.Count);
        Assert.All(pages.Pages, p => Assert.StartsWith("https://cdn.arenascan.com/", p.Url));
    }

    // ── Keyoapp ───────────────────────────────────────────────────────

    private static readonly CustomSourceDefinition Kewn = Definition(CustomEngines.Keyoapp, "https://kewnscans.org",
        "https://kewnscans.org/series/bitter-side-cranberries/", "https://kewnscans.org/chapter/63e9f15b09e-63e9fd83480/", "Bitter Side Cranberries");

    private static FakeHtmlFetcher KewnPages() => new(new()
    {
        ["/series/bitter-side-cranberries/"] = FakeHttpClientFactory.Fixture("keyoapp-series.html"),
        ["/series/"] = FakeHttpClientFactory.Fixture("keyoapp-list.html"),
        ["/chapter/"] = FakeHttpClientFactory.Fixture("keyoapp-chapter.html")
    });

    [Fact]
    public async Task Keyoapp_matches_the_series_list_and_leaves_upcoming_chapters_out()
    {
        Assert.Empty(CustomSourceValidator.Validate(Kewn));
        var source = Source(Kewn, KewnPages());

        var results = await source.SearchAsync("Bitter Side Cranberries");
        var series = await source.GetSeriesAsync("bitter-side-cranberries");
        var chapters = await source.ListChaptersAsync("bitter-side-cranberries");
        var pages = await source.GetPagesAsync(Chapter(source, "bitter-side-cranberries", "63e9f15b09e-63e9fd83480"));

        Assert.Equal("bitter-side-cranberries", results[0].SourceSeriesId);
        Assert.Equal("Bitter Side Cranberries", series.Title);
        Assert.Equal("Ongoing", series.Status);
        Assert.Equal("https://cdn.meowing.org/uploads/U1Dd3mDINy4", series.CoverUrl);
        // 16 listed; 15 and 16 are upcoming.
        Assert.Equal(Enumerable.Range(1, 14).Select(n => (decimal?)n), chapters.Select(c => c.Number));
        Assert.Equal("https://kewnscans.org/chapter/63e9f15b09e-63e9fd83480/", chapters[0].Url);
        Assert.Equal(new DateTime(2025, 9, 12), chapters[0].ReleaseDate);
        Assert.Equal(80, pages.Pages.Count);
        Assert.StartsWith("https://cdn.meowing.org/uploads/", pages.Pages[0].Url);
    }

    // ── HeanCMS ───────────────────────────────────────────────────────

    private static readonly CustomSourceDefinition Omega = Definition(CustomEngines.HeanCms, "https://omegascans.org",
        "https://omegascans.org/series/love-quest", "https://omegascans.org/series/love-quest/chapter-1", "Love Quest");

    private static FakeHtmlFetcher OmegaApi() => new(new()
    {
        ["api.omegascans.org/query"] = FakeHttpClientFactory.Fixture("heancms-search.json"),
        ["api.omegascans.org/series/love-quest"] = FakeHttpClientFactory.Fixture("heancms-series.json"),
        ["chapter/query?page=1&"] = FakeHttpClientFactory.Fixture("heancms-chapters-1.json"),
        ["chapter/query?page=2&"] = FakeHttpClientFactory.Fixture("heancms-chapters-2.json"),
        ["api.omegascans.org/chapter/love-quest/chapter-1"] = FakeHttpClientFactory.Fixture("heancms-chapter.json")
    });

    [Fact]
    public async Task HeanCms_reads_everything_from_the_api_host()
    {
        Assert.Empty(CustomSourceValidator.Validate(Omega));
        var fetcher = OmegaApi();
        var source = Source(Omega, fetcher);

        var results = await source.SearchAsync("Love Quest");
        var series = await source.GetSeriesAsync("love-quest");
        var chapters = await source.ListChaptersAsync("love-quest");
        var pages = await source.GetPagesAsync(Chapter(source, "love-quest", "chapter-1"));

        Assert.Equal("love-quest", results[0].SourceSeriesId);
        Assert.Equal("Love Quest", series.Title);
        Assert.Equal("Ongoing", series.Status);
        Assert.Equal(40, chapters.Count);
        Assert.Contains(chapters, c => c.Number == 24.5m && c.SourceChapterId == "chapter-24-5");
        Assert.Equal("https://omegascans.org/series/love-quest/chapter-39", chapters[^1].Url);
        Assert.True(source.ReleaseDatesAreReliable);
        Assert.Equal(14, pages.Pages.Count);
        Assert.All(fetcher.Requested, url => Assert.StartsWith("https://api.omegascans.org/", url));
        Assert.Equal("love-quest", source.ResolveSeriesIdFromUrl(new Uri("https://omegascans.org/series/love-quest")));
    }

    [Theory]
    [InlineData("https://api.example.com")]
    [InlineData("http://192.168.1.10")]
    public void HeanCms_refuses_an_api_off_the_site_host(string apiUrl)
    {
        var problems = CustomSourceValidator.Validate(Omega with { EngineOptions = new CustomEngineOptions { ApiUrl = apiUrl } });

        Assert.Contains(problems, p => p.ToString().Contains("engineOptions.apiUrl"));
    }

    [Fact]
    public void An_api_url_only_belongs_to_heancms()
    {
        var problems = CustomSourceValidator.Validate(Arena with { EngineOptions = new CustomEngineOptions { ApiUrl = "https://api.arenascan.com" } });

        Assert.Contains(problems, p => p.ToString().Contains("engineOptions.apiUrl"));
    }

    // ── Detection ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("mangathemesia-series.html", "https://arenascan.com/manga/acidity-of-regret/", CustomEngines.MangaThemesia, "manga")]
    [InlineData("keyoapp-series.html", "https://kewnscans.org/series/bitter-side-cranberries/", CustomEngines.Keyoapp, "series")]
    public void Themes_are_recognised_from_the_series_page(string fixture, string url, string engine, string seriesPath)
    {
        var match = EngineDetector.Run(new CustomPage(FakeHttpClientFactory.Fixture(fixture), new Uri(url)));

        Assert.Equal(engine, match?.Engine);
        Assert.Equal(seriesPath, match?.Options.SeriesPath);
    }
}
