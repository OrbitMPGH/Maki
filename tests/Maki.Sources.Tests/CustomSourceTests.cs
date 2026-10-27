using System.Text.Json;
using Maki.Core.Configuration;
using Maki.Core.Sources;
using Maki.Core.Sources.Custom;
using Maki.Sources.Common;
using Maki.Sources.Custom;
using Maki.Sources.Tests.Suwayomi;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Sources.Tests;

public class CustomSourceTests
{
    private const string SeriesId = "thus-the-seasons-learn-of-blue";

    private static CustomSourceDefinition SilentQuill() =>
        CustomSourceDefinition.Parse(FakeHttpClientFactory.Fixture("silentquill-definition.json"));

    private static FakeHtmlFetcher Fetcher() => new(new()
    {
        ["/search/?q="] = FakeHttpClientFactory.Fixture("silentquill-search.html"),
        ["/series/thus-the-seasons-learn-of-blue/"] = FakeHttpClientFactory.Fixture("silentquill-series.html"),
        ["/thus-the-seasons-learn-of-blue-chapter-2/"] = FakeHttpClientFactory.Fixture("silentquill-chapter.html")
    });

    private static CustomSource Source(CustomSourceDefinition? definition = null, FakeHtmlFetcher? fetcher = null) => new(
        definition ?? SilentQuill(),
        fetcher ?? Fetcher(),
        new FakeHttpClientFactory([]),
        new SpacingGate(TimeSpan.Zero));

    [Fact]
    public void SilentQuill_definition_validates()
    {
        Assert.Empty(CustomSourceValidator.Validate(SilentQuill()));
    }

    [Fact]
    public void Source_identity_comes_from_the_definition()
    {
        var source = Source();

        Assert.Equal("custom-silentquill", source.Name);
        Assert.Equal("SilentQuill", source.DisplayName);
        Assert.Equal("https://silentquill.net", source.BaseUrl);
        Assert.Equal(SourceKind.Scanlator, source.Kind);
        Assert.Equal(SourceContent.Manga, source.Content);
        Assert.Equal(["en"], source.SupportedLanguages);
        Assert.False(source.ReleaseDatesAreReliable);
        Assert.True(source.HasSearch);
    }

    [Fact]
    public async Task Search_finds_the_sample_series()
    {
        var fetcher = Fetcher();
        var results = await Source(fetcher: fetcher).SearchAsync("Thus, the Seasons Learn of Blue");

        var hit = Assert.Single(results, r => r.SourceSeriesId == SeriesId);
        Assert.Equal("Thus, the Seasons Learn of Blue", hit.Title);
        Assert.Equal("https://silentquill.net/series/thus-the-seasons-learn-of-blue/", hit.Url);
        Assert.StartsWith("https://silentquill.net/img/", hit.CoverUrl);
        Assert.Contains("q=Thus%2C%20the%20Seasons", fetcher.Requested[0]);
        Assert.Equal("https://silentquill.net/", fetcher.Requests[0].Headers!["Referer"]);
    }

    [Fact]
    public async Task GetSeries_reads_title_cover_and_description()
    {
        var series = await Source().GetSeriesAsync(SeriesId);

        Assert.Equal("Thus, the Seasons Learn of Blue", series.Title);
        Assert.StartsWith("https://silentquill.net/img/og/thus-the-seasons-learn-of-blue.jpg", series.CoverUrl);
        Assert.StartsWith("Natsuki Sano is a high school boy", series.Description);
    }

    [Fact]
    public async Task ListChapters_reads_both_chapters_with_root_relative_ids()
    {
        var chapters = await Source().ListChaptersAsync(SeriesId);

        Assert.Equal([1m, 2m], chapters.Select(c => c.Number));
        Assert.Equal(
            ["kakushite-kisetsu-wa-ao-wo-shiru-chapter-1", "thus-the-seasons-learn-of-blue-chapter-2"],
            chapters.Select(c => c.SourceChapterId));
        Assert.Equal("https://silentquill.net/thus-the-seasons-learn-of-blue-chapter-2/", chapters[1].Url);
        Assert.All(chapters, c => Assert.Equal("en", c.Language));
        Assert.All(chapters, c => Assert.Equal("custom-silentquill", c.SourceName));
        Assert.All(chapters, c => Assert.NotNull(c.ReleaseDate));
    }

    [Fact]
    public async Task Chapter_url_template_rebuilds_every_listed_link()
    {
        var entries = await Source().ListEntriesAsync(SeriesId);

        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal(e.ItemHref, e.Chapter.Url));
    }

    [Fact]
    public async Task GetPages_reads_every_page_out_of_the_script_payload_in_order()
    {
        var chapter = new SourceChapter("custom-silentquill", SeriesId, "thus-the-seasons-learn-of-blue-chapter-2",
            "Ch. 2", 2, null, null, "en", null);

        var pages = (await Source().GetPagesAsync(chapter)).Pages;

        Assert.Equal(40, pages.Count);
        Assert.Equal("https://silentquill.net/img/p/99763-eY7SByNctzaU8baW", pages[0].Url);
        Assert.Equal("https://silentquill.net/img/p/99764-EFe0lR4IricAdRK9", pages[1].Url);
        Assert.All(pages, p => Assert.NotNull(p.Fetch));
        Assert.Equal("https://silentquill.net/", pages[0].Headers!["Referer"]);
        Assert.Contains("Mozilla/5.0", pages[0].Headers!["User-Agent"]);
    }

    [Theory]
    [InlineData("https://silentquill.net/series/thus-the-seasons-learn-of-blue/", SeriesId)]
    [InlineData("https://www.silentquill.net/series/thus-the-seasons-learn-of-blue", SeriesId)]
    [InlineData("https://silentquill.net/thus-the-seasons-learn-of-blue-chapter-2/", null)]
    [InlineData("https://example.com/series/thus-the-seasons-learn-of-blue/", null)]
    public void ResolveSeriesIdFromUrl_inverts_the_series_template(string url, string? expected)
    {
        Assert.Equal(expected, Source().ResolveSeriesIdFromUrl(new Uri(url)));
    }

    [Fact]
    public async Task A_chapter_id_that_points_at_another_host_is_never_fetched()
    {
        var fetcher = Fetcher();
        var chapter = new SourceChapter("custom-silentquill", SeriesId, "/evil.example/x", null, null, null, null, "en", null);

        await Assert.ThrowsAsync<SourceErrorException>(() => Source(fetcher: fetcher).GetPagesAsync(chapter));
        Assert.Empty(fetcher.Requested);
    }

    [Fact]
    public async Task A_page_that_matches_no_chapters_throws_rather_than_reading_as_empty()
    {
        var fetcher = new FakeHtmlFetcher(new() { ["/series/"] = "<html><body><h1>Maintenance</h1></body></html>" });

        var ex = await Assert.ThrowsAsync<SourceErrorException>(() => Source(fetcher: fetcher).ListChaptersAsync(SeriesId));
        Assert.Contains("div.columns-1 a[href]", ex.Message);
    }

    [Fact]
    public async Task Json_extractors_read_an_embedded_blob()
    {
        const string html = """
            <html><body><script id="__NEXT_DATA__" type="application/json">
            {"props":{"chapters":[{"slug":"ch-1","no":"1"},{"slug":"ch-2","no":"2.5"}]}}
            </script></body></html>
            """;
        var definition = SilentQuill() with
        {
            Chapters = new CustomChapters
            {
                Item = new ListExtractor { Json = new JsonExtractor { Css = "script#__NEXT_DATA__", Path = "props.chapters[*]" } },
                Id = new FieldExtractor { Json = "slug" },
                Number = new FieldExtractor { Json = "no" },
                Url = "/{chapterId}/"
            }
        };
        Assert.Empty(CustomSourceValidator.Validate(definition));

        var chapters = await Source(definition, new FakeHtmlFetcher(new() { ["/series/"] = html })).ListChaptersAsync(SeriesId);

        Assert.Equal([1m, 2.5m], chapters.Select(c => c.Number));
        Assert.Equal(["ch-1", "ch-2"], chapters.Select(c => c.SourceChapterId));
    }

    [Fact]
    public async Task A_search_less_source_answers_empty_without_a_request()
    {
        var fetcher = Fetcher();
        var source = Source(SilentQuill() with { Search = null }, fetcher);

        Assert.False(source.HasSearch);
        Assert.Empty(await source.SearchAsync("anything"));
        Assert.Empty(fetcher.Requested);
    }

    [Theory]
    [InlineData("http://192.168.1.10")]
    [InlineData("https://localhost")]
    [InlineData("https://nas.local")]
    [InlineData("https://example.com/manga")]
    [InlineData("ftp://example.com")]
    [InlineData("https://user:pw@example.com")]
    public void Base_urls_that_are_not_a_public_origin_are_refused(string baseUrl)
    {
        var problems = CustomSourceValidator.Validate(SilentQuill() with { BaseUrl = baseUrl });

        Assert.Contains(problems, p => p.Key == "error.customSource.baseUrlInvalid");
    }

    [Fact]
    public void Validator_names_each_broken_part()
    {
        var definition = SilentQuill() with
        {
            Id = "Bad Id",
            Headers = new() { ["Cookie"] = "a=b" },
            Series = new CustomSeries { Url = "/series/" },
            Chapters = SilentQuill().Chapters with { Item = new ListExtractor { Css = "div[", Regex = "x" } },
            Pages = new CustomPages { List = new ListExtractor { Regex = "(" } }
        };

        var keys = CustomSourceValidator.Validate(definition).Select(p => p.Key).ToList();

        Assert.Contains("error.customSource.idInvalid", keys);
        Assert.Contains("error.customSource.headerRefused", keys);
        Assert.Contains("error.customSource.templatePlaceholder", keys);
        Assert.Contains("error.customSource.listExtractorShape", keys);
        Assert.Contains("error.customSource.regexInvalid", keys);
    }

    [Fact]
    public void A_template_on_another_host_is_refused()
    {
        var definition = SilentQuill() with { Series = new CustomSeries { Url = "https://evil.example/series/{seriesId}/" } };

        Assert.Contains(CustomSourceValidator.Validate(definition), p => p.Key == "error.customSource.templateHost");
    }

    [Fact]
    public void Unknown_fields_fail_to_parse()
    {
        Assert.Throws<JsonException>(() => CustomSourceDefinition.Parse("""{ "schema": 1, "id": "x", "serach": {} }"""));
    }

    [Fact]
    public async Task Provider_registers_an_unreadable_definition_as_not_working()
    {
        var settings = new InMemorySettings();
        var good = SilentQuill().ToJson();
        await settings.SetAsync(SettingKeys.CustomSources, $$"""[{{good}}, { "schema": 9, "id": "future", "name": "Future site" }]""");
        var store = new CustomSourceStore(settings);
        var provider = new CustomSourceProvider(
            store,
            new CustomSourceFactory(null!, new FakeHttpClientFactory([]), new CustomSourceThrottle()),
            NullLogger<CustomSourceProvider>.Instance);

        await provider.ReloadAsync();

        Assert.Equal(["custom-silentquill", "custom-future"], provider.Current.Select(s => s.Name));
        var invalid = Assert.IsType<InvalidCustomSource>(provider.Current[1]);
        Assert.Equal("Future site", invalid.DisplayName);
        await Assert.ThrowsAsync<SourceErrorException>(() => invalid.ListChaptersAsync("x"));
    }

    [Fact]
    public async Task Store_keeps_entries_it_cannot_read_when_another_is_saved()
    {
        var settings = new InMemorySettings();
        await settings.SetAsync(SettingKeys.CustomSources, """[{ "schema": 9, "id": "future", "extra": true }]""");
        var store = new CustomSourceStore(settings);

        await store.UpdateAsync(entries =>
        {
            entries.Add(CustomSourceStore.ToJson(SilentQuill()));
            return (true, 0);
        });

        var stored = await store.ReadAsync();
        Assert.Equal(["future", "silentquill"], stored.Select(s => s.Id));
        Assert.True(stored[0].Raw.ContainsKey("extra"));
    }

    [Theory]
    [InlineData("1 month", 30)]
    [InlineData("2 days ago", 2)]
    [InlineData("a week ago", 7)]
    public void Relative_dates_read_with_or_without_ago(string text, int days)
    {
        var now = new DateTime(2026, 10, 27, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(now.AddDays(-days), RelativeDate.Parse(text, now));
    }

    [Fact]
    public async Task Spacing_gate_holds_requests_apart()
    {
        var gate = new SpacingGate(TimeSpan.FromMilliseconds(120));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        await gate.WaitAsync(default);
        await gate.WaitAsync(default);
        await gate.WaitAsync(default);

        Assert.True(watch.ElapsedMilliseconds >= 220, $"took {watch.ElapsedMilliseconds} ms");
    }
}
