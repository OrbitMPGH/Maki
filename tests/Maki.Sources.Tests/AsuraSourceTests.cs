using Maki.Core.Sources;
using Maki.Sources.Asura;

namespace Maki.Sources.Tests;

public class AsuraSourceTests
{
    private static AsuraSource SourceFor(Dictionary<string, string> responses) =>
        new(new FakeHttpClientFactory(responses));

    private static SourceChapter ChapterOf(string number) =>
        new("asura", "nano-machine-1a2b", $"nano-machine-1a2b|{number}", number, null, null, null, "en", null);

    [Fact]
    public async Task Search_uses_the_public_url_slug_and_skips_rows_without_one()
    {
        var source = SourceFor(new()
        {
            ["api/series?search="] = """
                {"data":[
                    {"title":"Nano Machine","public_url":"/comics/nano-machine-1a2b/","cover":"https://c/1.webp"},
                    {"title":"Bare slug","slug":"bare-slug"},
                    {"title":"Nothing"}
                ]}
                """
        });

        var results = await source.SearchAsync("nano");

        Assert.Equal(["nano-machine-1a2b", "bare-slug"], results.Select(r => r.SourceSeriesId));
        Assert.Equal("https://asurascans.com/comics/nano-machine-1a2b", results[0].Url);
        Assert.Equal("https://c/1.webp", results[0].CoverUrl);
    }

    [Fact]
    public async Task ListChapters_keeps_premium_rows_and_packs_series_and_number_into_the_id()
    {
        var source = SourceFor(new()
        {
            ["chapters"] = """
                {"data":[
                    {"number":3,"title":"Locked","is_premium":true},
                    {"number":2.5,"title":""},
                    {"number":"1","title":"Start"},
                    {"title":"no number"}
                ]}
                """
        });

        var chapters = await source.ListChaptersAsync("nano-machine-1a2b");

        Assert.Equal([1m, 2.5m, 3m], chapters.Select(c => c.Number!.Value));
        Assert.Equal("nano-machine-1a2b|3", chapters[2].SourceChapterId);
        Assert.Equal("Start", chapters[0].Title);
        Assert.Null(chapters[1].Title);
    }

    [Fact]
    public async Task ListChapters_without_an_array_throws_instead_of_reading_as_empty()
    {
        var source = SourceFor(new() { ["chapters"] = """{"message":"nope"}""" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.ListChaptersAsync("nano-machine-1a2b"));
    }

    [Fact]
    public async Task GetPages_reads_string_and_object_pages_with_a_referer()
    {
        var source = SourceFor(new()
        {
            ["chapters/7"] = """
                {"data":{"chapter":{"pages":["https://i/1.webp",{"url":"https://i/2.webp"},{"url":""}]}}}
                """
        });

        var pages = await source.GetPagesAsync(ChapterOf("7"));

        Assert.Equal(["https://i/1.webp", "https://i/2.webp"], pages.Pages.Select(p => p.Url));
        Assert.All(pages.Pages, p => Assert.Equal("https://asurascans.com/", p.Headers!["Referer"]));
    }

    [Fact]
    public async Task GetPages_of_an_early_access_chapter_throws_locked_with_the_unlock_time()
    {
        var source = SourceFor(new()
        {
            ["chapters/7"] = """
                {"data":{"chapter":{"pages":[],"is_premium":true,"early_access_until":"2026-05-03T12:00:00Z"}}}
                """
        });

        var ex = await Assert.ThrowsAsync<ChapterLockedException>(() => source.GetPagesAsync(ChapterOf("7")));

        Assert.Equal(new DateTimeOffset(2026, 5, 3, 12, 0, 0, TimeSpan.Zero), ex.UnlockAt);
    }

    [Fact]
    public async Task GetPages_without_a_chapter_payload_throws()
    {
        var source = SourceFor(new() { ["chapters/7"] = """{"data":{}}""" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.GetPagesAsync(ChapterOf("7")));
    }
}
