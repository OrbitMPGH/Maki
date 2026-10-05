using Maki.Sources.TopManhua;

namespace Maki.Sources.Tests;

public class TopManhuaSourceTests
{
    private const string SeriesPage = """
        <div class="chapter-list">
          <div><a href="https://www.topmanhua.fan/manhua/anjo/chapter-226">Chapter 226: Seto-kun Can't Help but Make Her Study</a></div>
          <div><a href="https://www.topmanhua.fan/manhua/anjo/chapter-225">Anjou Anna Will Not Take the Easy Way Anymore</a></div>
          <div><a href="https://www.topmanhua.fan/manhua/anjo/chapter-12-5">Side Story</a></div>
          <div><a href="https://www.topmanhua.fan/manhua/anjo/chapter-222">Chapter 222</a></div>
        </div>
        """;

    [Fact]
    public async Task A_row_labelled_only_by_its_title_takes_the_number_from_the_url()
    {
        var source = new TopManhuaSource(new FakeHttpClientFactory(new() { ["manhua/anjo"] = SeriesPage }), null!);

        var chapters = await source.ListChaptersAsync("anjo");

        Assert.Equal([12.5m, 222m, 225m, 226m], chapters.Select(c => c.Number!.Value));
        var titled = chapters.Single(c => c.Number == 225m);
        Assert.Equal("Anjou Anna Will Not Take the Easy Way Anymore", titled.Title);
        Assert.Equal("chapter-225", titled.SourceChapterId);
        Assert.Null(chapters.Single(c => c.Number == 222m).Title);
    }
}
