using Maki.Core.Sources;
using Maki.Core.Sources.Custom;
using Maki.Sources.Custom;

namespace Maki.Sources.Tests;

/// <summary><c>chapters.locked</c> and <c>chapters.group</c> on a definition, read from a premium site's list.</summary>
public class CustomSourceLockedTests
{
    private const string SeriesId = "ember-choir";

    private static CustomSourceDefinition Lanternleaf() =>
        CustomSourceDefinition.Parse(FakeHttpClientFactory.Fixture("premiumtoon-definition.json"));

    private static CustomSource Source(CustomSourceDefinition? definition = null) => new(
        definition ?? Lanternleaf(),
        new FakeHtmlFetcher(new() { ["/series/ember-choir/"] = FakeHttpClientFactory.Fixture("premiumtoon-series.html") }),
        new FakeHttpClientFactory([]),
        new SpacingGate(TimeSpan.Zero));

    [Fact]
    public void The_definition_validates()
    {
        Assert.Empty(CustomSourceValidator.Validate(Lanternleaf()));
    }

    [Fact]
    public async Task A_coin_or_lock_icon_lists_the_chapter_as_locked()
    {
        var chapters = await Source().ListChaptersAsync(SeriesId);

        Assert.Equal([9m, 10m, 11m, 12m], chapters.Select(c => c.Number));
        Assert.Equal([false, false, true, true], chapters.Select(c => c.Locked));
    }

    [Fact]
    public async Task The_group_extractor_fills_the_chapter_group()
    {
        var chapters = await Source().ListChaptersAsync(SeriesId);

        Assert.Equal([null, "Lanternleaf Scans, Quiet Harbor", "Lanternleaf Scans", "Lanternleaf Scans"],
            chapters.Select(c => c.Group));
    }

    [Fact]
    public void A_locked_extractor_makes_the_source_say_it_lists_locked_chapters()
    {
        Assert.True(Source().Capabilities.HasFlag(SourceCapabilities.ListsLockedChapters));

        var plain = Lanternleaf() with { Chapters = Lanternleaf().Chapters with { Locked = null } };
        Assert.False(Source(plain).Capabilities.HasFlag(SourceCapabilities.ListsLockedChapters));
    }

    [Fact]
    public async Task Without_the_new_fields_nothing_is_locked_and_no_group_is_set()
    {
        var plain = Lanternleaf() with { Chapters = Lanternleaf().Chapters with { Locked = null, Group = null } };

        var chapters = await Source(plain).ListChaptersAsync(SeriesId);

        Assert.All(chapters, c => Assert.False(c.Locked));
        Assert.All(chapters, c => Assert.Null(c.Group));
    }

    [Theory]
    [InlineData("""{ "value": "true" }""")]
    [InlineData("""{}""")]
    public void A_locked_extractor_that_would_lock_every_chapter_is_refused(string locked)
    {
        var json = FakeHttpClientFactory.Fixture("premiumtoon-definition.json")
            .Replace("""{ "css": "img[src*='coin'], i.icon-lock" }""", locked);

        var problems = CustomSourceValidator.Validate(CustomSourceDefinition.Parse(json));

        Assert.Contains(problems, p => p.ToString().Contains("chapters.locked"));
    }

    [Theory]
    [InlineData("""<li><a href="/read/x/c1/">1</a><span data-paid="true"></span></li>""", true)]
    [InlineData("""<li><a href="/read/x/c1/">1</a><span data-paid="false"></span></li>""", false)]
    [InlineData("""<li><a href="/read/x/c1/">1</a><span data-paid></span></li>""", true)]
    [InlineData("""<li><a href="/read/x/c1/">1</a><span></span></li>""", false)]
    public void An_attribute_flag_counts_unless_it_says_false(string row, bool locked)
    {
        var page = new CustomPage($"<ul>{row}</ul>", new Uri("https://lanternleaf.example/"));
        var item = CustomExtractor.List(new ListExtractor { Css = "li" }, page)[0];

        Assert.Equal(locked, CustomExtractor.Matches(new FieldExtractor { Css = "span", Attr = "data-paid" }, item));
    }

    [Fact]
    public void A_regex_flag_matches_the_row_text()
    {
        var page = new CustomPage("""<ul><li>Chapter 5 🔒</li><li>Chapter 4</li></ul>""", new Uri("https://lanternleaf.example/"));
        var items = CustomExtractor.List(new ListExtractor { Css = "li" }, page);
        var field = new FieldExtractor { Regex = "🔒" };

        Assert.True(CustomExtractor.Matches(field, items[0]));
        Assert.False(CustomExtractor.Matches(field, items[1]));
    }
}
