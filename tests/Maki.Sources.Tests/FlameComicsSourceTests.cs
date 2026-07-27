using Maki.Core.Sources;
using Maki.Sources.FlameComics;

namespace Maki.Sources.Tests;

public class FlameComicsSourceTests
{
    private static FlameComicsSource SourceFor(Dictionary<string, string> responses) =>
        new(new FakeHttpClientFactory(responses));

    private static FlameComicsSource WithCatalog() =>
        SourceFor(new() { ["browse"] = FakeHttpClientFactory.Fixture("flamecomics-browse.html") });

    private static FlameComicsSource WithSeries() =>
        SourceFor(new() { ["series/2"] = FakeHttpClientFactory.Fixture("flamecomics-series.html") });

    [Fact]
    public async Task Search_ranks_the_catalog_by_title()
    {
        var results = await WithCatalog().SearchAsync("omniscient reader");

        var hit = Assert.Single(results);
        Assert.Equal("2", hit.SourceSeriesId);
        Assert.Equal("Omniscient Reader's Viewpoint", hit.Title);
        Assert.Equal("https://flamecomics.xyz/series/2", hit.Url);
        Assert.StartsWith("https://cdn.flamecomics.xyz/uploads/images/series/2/thumbnail", hit.CoverUrl);
    }

    [Fact]
    public async Task Search_skips_the_prose_novels_the_catalog_also_lists()
    {
        // /browse mixes in novels, which key on novel_id and have no page images. The fixture
        // holds the novel edition of "Omniscient Reader's Viewpoint" alongside the comic one.
        var results = await WithCatalog().SearchAsync("omniscient reader");

        Assert.Single(results);
        Assert.All(results, r => Assert.Matches("^[0-9]+$", r.SourceSeriesId));
    }

    [Fact]
    public async Task Search_strips_the_html_the_descriptions_are_stored_as()
    {
        var results = await WithCatalog().SearchAsync("omniscient reader");

        Assert.DoesNotContain("<p", results[0].Description);
        Assert.DoesNotContain("mantine", results[0].Description);
    }

    [Fact]
    public async Task GetSeries_reads_title_status_and_description()
    {
        var detail = await WithSeries().GetSeriesAsync("2");

        Assert.Equal("Omniscient Reader's Viewpoint", detail.Title);
        Assert.Equal("Hiatus", detail.Status);
        Assert.Equal("https://flamecomics.xyz/series/2", detail.Url);
        Assert.DoesNotContain("<p", detail.Description);
    }

    [Fact]
    public async Task ListChapters_parses_numbers_tokens_and_dates_ascending()
    {
        var chapters = await WithSeries().ListChaptersAsync("2");

        Assert.NotEmpty(chapters);
        Assert.All(chapters, c => Assert.Equal("en", c.Language));
        Assert.All(chapters, c => Assert.Null(c.Volume));
        Assert.True(chapters.First().Number < chapters.Last().Number);

        // A prologue is chapter 0, not a one-shot.
        var prologue = chapters.First();
        Assert.Equal(0m, prologue.Number);
        Assert.Equal("Prologue", prologue.Title);
        Assert.Equal("0c9db8012fbd1257", prologue.SourceChapterId);
        Assert.Equal(new DateTime(2021, 1, 28), prologue.ReleaseDate!.Value.Date);
        Assert.Equal("https://flamecomics.xyz/series/2/0c9db8012fbd1257", prologue.Url);

        Assert.Equal(311m, chapters.Last().Number);
    }

    [Fact]
    public async Task GetPages_orders_pages_numerically_not_as_text()
    {
        // "images" is an object keyed by index as a string: sorted as text, page 10 would land
        // between 1 and 2. The fixture chapter has 17 pages, so the bug would be visible.
        var source = SourceFor(new()
        {
            ["364db6fd6bef182e"] = FakeHttpClientFactory.Fixture("flamecomics-chapter.html")
        });

        var pages = await source.GetPagesAsync(new SourceChapter(
            "flamecomics", "2", "364db6fd6bef182e", "311.00", 311m, null, null, "en", null));

        Assert.Equal(17, pages.Pages.Count);
        Assert.All(pages.Pages, p =>
            Assert.StartsWith("https://cdn.flamecomics.xyz/uploads/images/series/2/364db6fd6bef182e/", p.Url));
        Assert.EndsWith("ORV-311-00.jpg?1779213574", pages.Pages[0].Url);
        Assert.EndsWith("ORV-311-01.jpg?1779213574", pages.Pages[1].Url);
        Assert.EndsWith("ORV-311-11.jpg?1779213574", pages.Pages[11].Url);
        Assert.EndsWith("ORV-311-16.jpg?1779213574", pages.Pages[^1].Url);
    }

    [Fact]
    public async Task Missing_next_data_is_an_error_rather_than_an_empty_result()
    {
        // Silently returning nothing here would read as "the series has no chapters" and let
        // a monitored series quietly stop updating.
        var source = SourceFor(new() { ["series/2"] = "<html><body>Just a Cloudflare page</body></html>" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.ListChaptersAsync("2"));
    }
}
