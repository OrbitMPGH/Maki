using Maki.Core.Sources;
using Maki.Sources.Manhwa18Net;

namespace Maki.Sources.Tests;

public class Manhwa18NetSourceTests
{
    /// <summary>
    /// A minimal Inertia page whose single chapter has no "chap"/"chapter" prefix and no volume
    /// marker, so ChapterNumberParser can't read a number out of it — the case that has to fall
    /// back to Title = the raw name rather than collapsing into every other unparsed chapter.
    /// </summary>
    private const string UnparseableChapterHtml =
        """
        <html><body><div id="app" data-page='{"component":"Manga","props":{"manga":{"id":1,"name":"Test Series","slug":"test-series","genres":[]},"chapters":[{"id":1,"name":"Extra","slug":"extra","order":1,"created_at":"2024-01-01T00:00:00.000000Z"}]}}'></div></body></html>
        """;

    /// <summary>A chapter page whose Inertia props carry no images at all (locked or not yet public).</summary>
    private const string NoPagesChapterHtml =
        """
        <html><body><div id="app" data-page='{"component":"Chapter","props":{"mangaSlug":"secret-class","chapterName":"Chapter 999","chapterId":1,"chapterImages":[],"chapterContent":""}}'></div></body></html>
        """;

    [Fact]
    public async Task Search_drops_raw_series_and_parses_the_rest()
    {
        var source = new Manhwa18NetSource(new FakeHtmlFetcher(new()
        {
            ["/tim-kiem"] = FakeHttpClientFactory.Fixture("manhwa18net-search.html")
        }));

        var results = await source.SearchAsync("secret class");

        // The fixture carries 3 hits; "Secret class raw" must not survive the filter.
        Assert.Equal(2, results.Count);
        Assert.DoesNotContain(results, r => r.SourceSeriesId.EndsWith("-raw", StringComparison.Ordinal));

        var first = results[0];
        Assert.Equal("secret-class", first.SourceSeriesId);
        Assert.Equal("Secret class", first.Title);
        Assert.Equal("https://manhwa18.net/manga/secret-class", first.Url);
        Assert.StartsWith("https://manhwa18.net/storage/images/", first.CoverUrl);
        Assert.Contains("cheating on her husband", first.Description);
    }

    [Fact]
    public async Task GetSeries_parses_detail()
    {
        var source = new Manhwa18NetSource(new FakeHtmlFetcher(new()
        {
            ["manga/secret-class"] = FakeHttpClientFactory.Fixture("manhwa18net-series.html")
        }));

        var detail = await source.GetSeriesAsync("secret-class");

        Assert.Equal("secret-class", detail.SourceSeriesId);
        Assert.Equal("Secret class", detail.Title);
        Assert.Equal("https://manhwa18.net/manga/secret-class", detail.Url);
        Assert.StartsWith("https://manhwa18.net/storage/images/", detail.CoverUrl);
        Assert.Contains("cheating on her husband", detail.Description);
    }

    [Fact]
    public async Task ListChapters_parses_numbers_dedupes_and_orders_ascending()
    {
        var source = new Manhwa18NetSource(new FakeHtmlFetcher(new()
        {
            ["manga/secret-class"] = FakeHttpClientFactory.Fixture("manhwa18net-series.html")
        }));

        var chapters = await source.ListChaptersAsync("secret-class");

        // 326 rows in the fixture, one duplicate pair at 316 ("Chapter 316" / "Chapter 316
        // Uncensored") collapsing into one.
        Assert.Equal(325, chapters.Count);
        Assert.All(chapters, c => Assert.Equal("en", c.Language));
        Assert.Equal(1m, chapters[0].Number);
        Assert.Equal(318m, chapters[^1].Number);

        // "Chap 01" is outside ChapterNumberParser's own pattern; the source-local prefix regex
        // has to catch it before the number is lost.
        var first = chapters[0];
        Assert.Equal("chap-01-361", first.SourceChapterId);
        Assert.Equal("Chap 01", first.NumberRaw);

        // The uncensored copy wins the 316 duplicate.
        var dup = Assert.Single(chapters, c => c.Number == 316m);
        Assert.Equal("Chapter 316 Uncensored", dup.NumberRaw);

        // "chap 10 ver2" / "chap 09 fixed" still parse via the prefix regex despite the trailing
        // words, and are not swallowed as duplicates of "Chapter 10" / "Chapter 9".
        Assert.Contains(chapters, c => c.NumberRaw == "chap 10 ver2" && c.Number == 10m);
    }

    [Fact]
    public async Task ListChapters_tags_a_raw_series_ko()
    {
        var source = new Manhwa18NetSource(new FakeHtmlFetcher(new()
        {
            ["secret-class-raw"] = FakeHttpClientFactory.Fixture("manhwa18net-series-raw.html")
        }));

        var chapters = await source.ListChaptersAsync("secret-class-raw");

        Assert.NotEmpty(chapters);
        Assert.All(chapters, c => Assert.Equal("ko", c.Language));
    }

    [Fact]
    public async Task ListChapters_sets_title_to_the_raw_name_when_the_number_is_unparseable()
    {
        var source = new Manhwa18NetSource(new FakeHtmlFetcher(new()
        {
            ["test-series"] = UnparseableChapterHtml
        }));

        var chapters = await source.ListChaptersAsync("test-series");

        var chapter = Assert.Single(chapters);
        Assert.Null(chapter.Number);
        Assert.Equal("Extra", chapter.Title);
        Assert.Equal("en", chapter.Language);
    }

    [Fact]
    public async Task GetPages_reads_the_new_cdn_via_chapterImages()
    {
        var source = new Manhwa18NetSource(new FakeHtmlFetcher(new()
        {
            ["chapter-318"] = FakeHttpClientFactory.Fixture("manhwa18net-chapter.html")
        }));

        var pages = await source.GetPagesAsync(new SourceChapter(
            "manhwa18net", "secret-class", "chapter-318", "Chapter 318", 318, null, null, "en", null));

        Assert.Equal(6, pages.Pages.Count);
        Assert.All(pages.Pages, p =>
        {
            Assert.StartsWith("https://min.manhwa18.net/", p.Url);
            Assert.Equal("https://manhwa18.net/", p.Headers!["Referer"]);
        });
    }

    [Fact]
    public async Task GetPages_reads_the_old_cdn()
    {
        var source = new Manhwa18NetSource(new FakeHtmlFetcher(new()
        {
            ["chap-01-361"] = FakeHttpClientFactory.Fixture("manhwa18net-chapter-old.html")
        }));

        var pages = await source.GetPagesAsync(new SourceChapter(
            "manhwa18net", "secret-class", "chap-01-361", "Chap 01", 1, null, null, "en", null));

        Assert.Equal(16, pages.Pages.Count);
        Assert.All(pages.Pages, p => Assert.StartsWith("https://cdn.pornwa.us/", p.Url));
    }

    [Fact]
    public async Task GetPages_throws_chapter_locked_when_no_pages_are_found()
    {
        var source = new Manhwa18NetSource(new FakeHtmlFetcher(new()
        {
            ["chapter-999"] = NoPagesChapterHtml
        }));

        await Assert.ThrowsAsync<ChapterLockedException>(() => source.GetPagesAsync(new SourceChapter(
            "manhwa18net", "secret-class", "chapter-999", "Chapter 999", 999, null, null, "en", null)));
    }
}
