using Maki.Core.Sources;
using Maki.Sources.MangaLivre;

namespace Maki.Sources.Tests;

public class MangaLivreSourceTests
{
    private static MangaLivreSource SourceFor(Dictionary<string, string> responses) =>
        new(new FakeHttpClientFactory(responses));

    private const string SeriesPage = """
        <div class="chapter-box"><a href="https://mangalivre.to/manga/frieren/capitulo-12">Capitulo 12</a><span class="chapter-date">22 de agosto de 2026</span></div>
        <div class="chapter-box"><a href="https://mangalivre.to/manga/frieren/capitulo-11_5">Capitulo 11.5</a><span class="chapter-date">1 de março de 2026</span></div>
        <div class="chapter-box"><a href="https://mangalivre.to/manga/frieren/extra-especial">Especial</a></div>
        <div class="chapter-box"><a href="https://other.example/manga/frieren/capitulo-9">Capitulo 9</a></div>
        <div class="chapter-box"><a>No link</a></div>
        """;

    [Fact]
    public async Task Search_maps_the_ajax_payload_and_dedupes_by_slug()
    {
        var source = SourceFor(new()
        {
            ["admin-ajax.php"] = """
                {"data":[
                    {"title":"Frieren","url":"https://mangalivre.to/manga/frieren/"},
                    {"title":"Frieren again","url":"https://mangalivre.to/manga/frieren/"},
                    {"title":"","url":"https://mangalivre.to/manga/blank/"}
                ]}
                """
        });

        var result = Assert.Single(await source.SearchAsync("frieren"));

        Assert.Equal("frieren", result.SourceSeriesId);
    }

    [Fact]
    public async Task ListChapters_reads_numbers_off_the_slug_and_dates_off_the_portuguese_text()
    {
        var source = SourceFor(new() { ["manga/frieren/"] = SeriesPage });

        var chapters = await source.ListChaptersAsync("frieren");

        var numbered = chapters.Where(c => c.Number is not null).ToList();
        Assert.Equal([11.5m, 12m], numbered.Select(c => c.Number!.Value));
        Assert.Equal("capitulo-11_5", numbered[0].SourceChapterId);
        Assert.Equal(new DateTime(2026, 8, 22, 0, 0, 0, DateTimeKind.Utc), numbered[1].ReleaseDate);
        Assert.Equal(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), numbered[0].ReleaseDate);
        Assert.All(chapters, c => Assert.Equal("pt-BR", c.Language));
    }

    [Fact]
    public async Task ListChapters_keeps_a_non_numeric_slug_as_an_unnumbered_chapter_titled_by_its_label()
    {
        var source = SourceFor(new() { ["manga/frieren/"] = SeriesPage });

        var chapters = await source.ListChaptersAsync("frieren");

        var special = Assert.Single(chapters, c => c.Number is null);
        Assert.Equal("extra-especial", special.SourceChapterId);
        Assert.Equal("Especial", special.Title);
    }

    [Fact]
    public async Task GetPages_reads_the_chapter_images_in_order()
    {
        var source = SourceFor(new()
        {
            ["manga/frieren/capitulo-12/"] = """
                <img class="wp-manga-chapter-img" src=" https://mangalivre.to/wp-content/1.jpg ">
                <img class="wp-manga-chapter-img" src="https://mangalivre.to/wp-content/2.jpg">
                <img class="logo" src="https://mangalivre.to/logo.png">
                """
        });
        var chapter = new SourceChapter("mangalivre", "frieren", "capitulo-12", "12", 12m, null, null, "pt-BR", null);

        var pages = await source.GetPagesAsync(chapter);

        Assert.Equal(
            ["https://mangalivre.to/wp-content/1.jpg", "https://mangalivre.to/wp-content/2.jpg"],
            pages.Pages.Select(p => p.Url));
    }
}
