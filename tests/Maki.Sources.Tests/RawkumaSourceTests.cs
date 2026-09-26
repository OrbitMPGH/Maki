using Maki.Core.Sources;
using Maki.Sources.Rawkuma;

namespace Maki.Sources.Tests;

public class RawkumaSourceTests
{
    [Fact]
    public async Task Search_parses_slug_title_cover_and_mangaupdates_id()
    {
        var source = new RawkumaSource(new FakeHtmlFetcher(new()
        {
            ["/wp-json/wp/v2/manga?search="] = FakeHttpClientFactory.Fixture("rawkuma-search.json")
        }));

        var results = await source.SearchAsync("One Piece");

        var first = Assert.Single(results, r => r.SourceSeriesId == "one-piece");
        Assert.Equal("One Piece", first.Title);
        Assert.Equal("https://rawkuma.net/manga/one-piece/", first.Url);
        Assert.Equal("https://rawkuma.net/wp-content/uploads/2025/09/i491609.jpg", first.CoverUrl);
        Assert.NotNull(first.ExternalIds);
        Assert.Equal("pb8uwds", first.ExternalIds![ExternalIdService.MangaUpdates]);

        // Entity-encoded titles ("&#8211;") decode to their real characters.
        var decoded = Assert.Single(results, r => r.SourceSeriesId == "asagiro-asagi-ookami");
        Assert.Equal("Asagiro – Asagi Ookami", decoded.Title);
    }

    [Fact]
    public async Task Search_skips_novels()
    {
        var source = new RawkumaSource(new FakeHtmlFetcher(new()
        {
            ["/wp-json/wp/v2/manga?search="] = FakeHttpClientFactory.Fixture("rawkuma-search.json")
        }));

        var results = await source.SearchAsync("One Piece");

        Assert.DoesNotContain(results, r => r.SourceSeriesId.EndsWith("-novel", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Search_through_a_pre_wrapped_response_parses_identically()
    {
        var raw = new RawkumaSource(new FakeHtmlFetcher(new()
        {
            ["/wp-json/wp/v2/manga?search="] = FakeHttpClientFactory.Fixture("rawkuma-search.json")
        }));
        var wrapped = new RawkumaSource(new FakeHtmlFetcher(new()
        {
            ["/wp-json/wp/v2/manga?search="] = FakeHttpClientFactory.Fixture("rawkuma-search-pre.html")
        }));

        var rawResults = await raw.SearchAsync("One Piece");
        var wrappedResults = await wrapped.SearchAsync("One Piece");

        Assert.Equal(
            rawResults.Select(r => (r.SourceSeriesId, r.Title, r.Url, r.CoverUrl)),
            wrappedResults.Select(r => (r.SourceSeriesId, r.Title, r.Url, r.CoverUrl)));
    }

    [Theory]
    [InlineData(55099564912, "pb8uwds")]
    [InlineData(1, "1")]
    [InlineData(35, "z")]
    [InlineData(36, "10")]
    public void ToBase36_matches_mangaupdates_slug_form(long value, string expected) =>
        Assert.Equal(expected, RawkumaSource.ToBase36(value));

    [Fact]
    public async Task GetSeries_parses_detail()
    {
        var source = new RawkumaSource(new FakeHtmlFetcher(new()
        {
            ["/wp-json/wp/v2/manga?slug="] = FakeHttpClientFactory.Fixture("rawkuma-manga.json")
        }));

        var detail = await source.GetSeriesAsync("one-piece");

        Assert.Equal("one-piece", detail.SourceSeriesId);
        Assert.Equal("One Piece", detail.Title);
        Assert.Equal("https://rawkuma.net/manga/one-piece/", detail.Url);
        Assert.Equal("https://rawkuma.net/wp-content/uploads/2025/09/i491609.jpg", detail.CoverUrl);
        Assert.Equal("Ongoing", detail.Status);
        Assert.Contains("Monkey D. Luffy", detail.Description);
    }

    [Fact]
    public async Task GetSeries_throws_for_an_unknown_slug()
    {
        var source = new RawkumaSource(new FakeHtmlFetcher(new()
        {
            ["/wp-json/wp/v2/manga?slug="] = "[]"
        }));

        await Assert.ThrowsAsync<KeyNotFoundException>(() => source.GetSeriesAsync("no-such-series"));
    }

    [Fact]
    public async Task ListChapters_parses_numbers_dates_and_language_normalized_ascending()
    {
        var source = new RawkumaSource(new FakeHtmlFetcher(new()
        {
            ["/wp-json/wp/v2/manga?slug="] = FakeHttpClientFactory.Fixture("rawkuma-manga.json"),
            ["action=chapter_list"] = FakeHttpClientFactory.Fixture("rawkuma-chapters.html")
        }));

        var chapters = await source.ListChaptersAsync("one-piece");

        Assert.Equal(15, chapters.Count);
        Assert.All(chapters, c => Assert.Equal("ja", c.Language));

        // The site lists newest first (1193 down to 1179); Normalize must flip it ascending.
        Assert.Equal(1179m, chapters[0].Number);
        Assert.Equal(1193m, chapters[^1].Number);
        Assert.True(chapters.Zip(chapters.Skip(1)).All(pair => pair.First.Number <= pair.Second.Number));

        var newest = chapters[^1];
        Assert.Equal("chapter-1193.407873", newest.SourceChapterId);
        Assert.Equal("https://rawkuma.net/manga/one-piece/chapter-1193.407873/", newest.Url);
        Assert.NotNull(newest.ReleaseDate);
        Assert.Equal(DateTimeKind.Utc, newest.ReleaseDate!.Value.Kind);
    }

    [Fact]
    public async Task ListChapters_requests_the_numeric_manga_id_from_the_series_lookup()
    {
        var fetcher = new FakeHtmlFetcher(new()
        {
            ["/wp-json/wp/v2/manga?slug="] = FakeHttpClientFactory.Fixture("rawkuma-manga.json"),
            ["action=chapter_list"] = FakeHttpClientFactory.Fixture("rawkuma-chapters.html")
        });

        await new RawkumaSource(fetcher).ListChaptersAsync("one-piece");

        Assert.Contains(fetcher.Requested, url => url.Contains("manga_id=7", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetPages_returns_every_image_with_the_referer()
    {
        var source = new RawkumaSource(new FakeHtmlFetcher(new()
        {
            ["/manga/one-piece/chapter-1193.407873/"] = FakeHttpClientFactory.Fixture("rawkuma-chapter.html")
        }));

        var chapter = new SourceChapter(
            "rawkuma", "one-piece", "chapter-1193.407873", "1193", 1193m, null, null, "ja", null,
            "https://rawkuma.net/manga/one-piece/chapter-1193.407873/");

        var pages = await source.GetPagesAsync(chapter);

        Assert.Equal(17, pages.Pages.Count);
        Assert.All(pages.Pages, p =>
        {
            Assert.StartsWith("https://kuma.kyut.dev/", p.Url);
            Assert.Equal("https://rawkuma.net/", p.Headers!["Referer"]);
        });
    }

    [Theory]
    [InlineData(null, "https://rawkuma.net")]
    [InlineData("https://mirror.example", "https://mirror.example")]
    public async Task Search_rebases_canonical_urls_onto_the_configured_base_url(string? baseUrlOverride, string expectedHost)
    {
        var source = new RawkumaSource(new FakeHtmlFetcher(new()
        {
            ["/wp-json/wp/v2/manga?search="] = FakeHttpClientFactory.Fixture("rawkuma-search.json")
        }), baseUrlOverride);

        var results = await source.SearchAsync("One Piece");

        var first = Assert.Single(results, r => r.SourceSeriesId == "one-piece");
        Assert.StartsWith($"{expectedHost}/", first.Url);
        Assert.StartsWith($"{expectedHost}/", first.CoverUrl);
    }

    [Fact]
    public async Task ListChapters_rebases_the_chapter_url_but_GetPages_leaves_the_image_host_alone()
    {
        var source = new RawkumaSource(new FakeHtmlFetcher(new()
        {
            ["/wp-json/wp/v2/manga?slug="] = FakeHttpClientFactory.Fixture("rawkuma-manga.json"),
            ["action=chapter_list"] = FakeHttpClientFactory.Fixture("rawkuma-chapters.html"),
            ["/manga/one-piece/chapter-1193.407873/"] = FakeHttpClientFactory.Fixture("rawkuma-chapter.html")
        }), "https://mirror.example");

        var chapters = await source.ListChaptersAsync("one-piece");
        var newest = chapters[^1];
        Assert.StartsWith("https://mirror.example/", newest.Url);

        var pages = await source.GetPagesAsync(newest);

        Assert.Equal(17, pages.Pages.Count);
        Assert.All(pages.Pages, p => Assert.StartsWith("https://kuma.kyut.dev/", p.Url));
    }

    [Fact]
    public async Task GetPages_throws_locked_when_no_images_are_present()
    {
        var source = new RawkumaSource(new FakeHtmlFetcher(new()
        {
            ["/manga/one-piece/chapter-9999.0/"] = "<html><body><main></main></body></html>"
        }));

        var chapter = new SourceChapter(
            "rawkuma", "one-piece", "chapter-9999.0", "9999", 9999m, null, null, "ja", null,
            "https://rawkuma.net/manga/one-piece/chapter-9999.0/");

        await Assert.ThrowsAsync<ChapterLockedException>(() => source.GetPagesAsync(chapter));
    }
}
