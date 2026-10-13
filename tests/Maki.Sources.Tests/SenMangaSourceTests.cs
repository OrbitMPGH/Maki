using Maki.Core.Sources;
using Maki.Sources.SenManga;

namespace Maki.Sources.Tests;

public class SenMangaSourceTests
{
    private static SenMangaSource SourceFor(Dictionary<string, string> responses) =>
        new(new FakeHttpClientFactory(responses));

    [Fact]
    public async Task Search_skips_hits_with_a_null_slug_or_title()
    {
        var source = SourceFor(new()
        {
            ["api/search"] = """
                {"series":[
                    {"title":"One Piece","slug":"one-piece","cover":"https://c/op.jpg"},
                    {"title":"No slug","slug":null},
                    {"title":null,"slug":"no-title"},
                    {"title":"","slug":""}
                ]}
                """
        });

        var result = Assert.Single(await source.SearchAsync("one"));

        Assert.Equal("one-piece", result.SourceSeriesId);
        Assert.Equal("https://raw.senmanga.com/manga/one-piece", result.Url);
    }

    [Fact]
    public async Task ListChapters_uses_the_url_field_as_id_and_the_number_field_as_number()
    {
        var source = SourceFor(new()
        {
            ["api/manga/one-piece"] = """
                {"title":"One Piece","chapterList":[
                    {"title":"Chapter 1193","number":"1193","url":"1193.407873","full_url":"/one-piece/1193.407873","datetime":"2026-05-03T00:00:00Z"},
                    {"title":"Chapter 37.3","number":"37.3","url":"37.3.1","full_url":"/one-piece/37.3.1"},
                    {"title":"No url","number":"5","url":""}
                ]}
                """
        });

        var chapters = await source.ListChaptersAsync("one-piece");

        Assert.Equal([37.3m, 1193m], chapters.Select(c => c.Number!.Value));
        var latest = chapters[1];
        Assert.Equal("1193.407873", latest.SourceChapterId);
        Assert.Equal("https://raw.senmanga.com/one-piece/1193.407873", latest.Url);
        Assert.Equal(new DateTime(2026, 5, 3, 0, 0, 0, DateTimeKind.Utc), latest.ReleaseDate);
        Assert.All(chapters, c => Assert.Equal("ja", c.Language));
    }

    [Fact]
    public async Task ListChapters_without_a_chapter_list_throws()
    {
        var source = SourceFor(new() { ["api/manga/one-piece"] = """{"title":"One Piece"}""" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.ListChaptersAsync("one-piece"));
    }

    [Fact]
    public async Task GetPages_drops_blank_urls_and_throws_when_the_payload_has_no_pages()
    {
        var chapter = new SourceChapter("senmanga", "one-piece", "1193.407873", "1193", 1193m, null, null, "ja", null);

        var ok = SourceFor(new()
        {
            ["api/read/one-piece/1193.407873"] = """{"pages":["https://p/1.jpg","","https://p/2.jpg"]}"""
        });
        Assert.Equal(["https://p/1.jpg", "https://p/2.jpg"], (await ok.GetPagesAsync(chapter)).Pages.Select(p => p.Url));

        var missing = SourceFor(new() { ["api/read/one-piece/1193.407873"] = "{}" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => missing.GetPagesAsync(chapter));
    }
}
