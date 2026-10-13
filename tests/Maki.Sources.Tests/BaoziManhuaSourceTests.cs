using Maki.Sources.BaoziManhua;

namespace Maki.Sources.Tests;

public class BaoziManhuaSourceTests
{
    private static BaoziManhuaSource SourceFor(Dictionary<string, string> responses) =>
        new(new FakeHttpClientFactory(responses));

    [Fact]
    public async Task ListChapters_parses_the_number_from_the_label_not_chapter_slot()
    {
        var source = SourceFor(new()
        {
            ["comic/"] = FakeHttpClientFactory.Fixture("baozimanhua-chapters.html")
        });

        var chapters = await source.ListChaptersAsync("test-comic_a1b2c");

        // chapter_slot for 第1话 is 0 (zero-based); the site's own displayed number is 1.
        var first = chapters.Single(c => c.Number == 1m);
        Assert.Equal("第1话", first.NumberRaw);
        Assert.Equal("0_0", first.SourceChapterId);
        Assert.Null(first.Title);
    }

    [Fact]
    public async Task ListChapters_keeps_the_label_as_title_when_it_has_no_number()
    {
        var source = SourceFor(new()
        {
            ["comic/"] = FakeHttpClientFactory.Fixture("baozimanhua-chapters.html")
        });

        var chapters = await source.ListChaptersAsync("test-comic_a1b2c");

        var special = chapters.Single(c => c.Number is null);
        Assert.Equal("番外篇", special.Title);
        Assert.Equal("番外篇", special.NumberRaw);
        Assert.Equal("0_1", special.SourceChapterId);
    }

    [Fact]
    public async Task Search_throws_when_the_catalog_page_lists_no_series()
    {
        var source = SourceFor(new() { ["classify"] = "<html><body>Just a moment...</body></html>" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.SearchAsync("test"));
    }
}
