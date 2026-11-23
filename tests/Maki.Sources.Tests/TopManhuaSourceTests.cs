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

    [Fact]
    public async Task A_search_card_without_a_link_is_skipped_and_a_relative_one_resolves_against_the_site()
    {
        const string search = """
            <div class="c-tabs-item">
              <div><div class="post-title"><h3><a>No link</a></h3></div></div>
              <div><div class="post-title"><h3><a href="/manhua/relative">Relative</a></h3></div></div>
              <div><div class="post-title"><h3><a href="https://www.topmanhua.fan/manhua/anjo">Anjo</a></h3></div></div>
            </div>
            """;
        var source = new TopManhuaSource(new FakeHttpClientFactory(new() { ["?s="] = search }), null!);

        var results = await source.SearchAsync("anjo");

        Assert.Equal(["relative", "anjo"], results.Select(r => r.SourceSeriesId));
        Assert.All(results, r => Assert.StartsWith("https://www.topmanhua.fan/manhua/", r.Url));
    }

    [Fact]
    public async Task A_row_without_a_link_target_is_skipped_and_an_english_date_parses()
    {
        const string page = """
            <div class="chapter-list">
              <div><a>Chapter 3</a></div>
              <div><a href="https://www.topmanhua.fan/manhua/anjo/chapter-2">Chapter 2</a><span class="chapter-release-date">May 3, 2024</span></div>
            </div>
            """;
        var source = new TopManhuaSource(new FakeHttpClientFactory(new() { ["manhua/anjo"] = page }), null!);

        var chapter = Assert.Single(await source.ListChaptersAsync("anjo"));

        Assert.Equal(new DateTime(2024, 5, 3, 0, 0, 0, DateTimeKind.Utc), chapter.ReleaseDate);
    }

    [Fact]
    public async Task BuildStagedPages_reads_staged_files_lazily_handles_duplicates_and_cleans_up_on_dispose()
    {
        var dir = Path.Combine(Path.GetTempPath(), "maki-topmanhua-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var p1 = Path.Combine(dir, "0001.bin");
        var p2 = Path.Combine(dir, "0002.bin");
        var pUnused = Path.Combine(dir, "0003.bin");
        await File.WriteAllBytesAsync(p1, [1, 2, 3]);
        await File.WriteAllBytesAsync(p2, [4, 5, 6, 7]);
        await File.WriteAllBytesAsync(pUnused, [8, 9]);

        var staged = new TopManhuaImageBrowser.StagedImages(dir, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["https://cdn.example/1.jpg"] = p1,
            ["https://cdn.example/dup.jpg"] = p2,
            ["https://cdn.example/unused.jpg"] = pUnused,
        });

        var urls = new[]
        {
            "https://cdn.example/1.jpg",
            "https://cdn.example/dup.jpg",
            "https://cdn.example/dup.jpg",
            "https://cdn.example/missing.jpg",
            "https://cdn.example/unused.jpg",
        };
        var headers = new Dictionary<string, string> { ["Referer"] = "https://www.topmanhua.fan/" };

        using (var pages = TopManhuaSource.BuildStagedPages(urls, headers, staged))
        {
            Assert.All(pages.Pages, p => Assert.Null(p.Data));
            Assert.NotNull(pages.Pages[0].Fetch);
            Assert.Null(pages.Pages[3].Fetch);

            // Unique staged file is deleted as soon as it is read.
            Assert.Equal([1, 2, 3], await pages.Pages[0].Fetch!(CancellationToken.None));
            Assert.False(File.Exists(p1));

            // Duplicate URL stays on disk until its last occurrence is read.
            Assert.Equal([4, 5, 6, 7], await pages.Pages[1].Fetch!(CancellationToken.None));
            Assert.True(File.Exists(p2));
            Assert.Equal([4, 5, 6, 7], await pages.Pages[2].Fetch!(CancellationToken.None));
            Assert.False(File.Exists(p2));

            // Unread staged file still exists until ChapterPages is disposed.
            Assert.True(File.Exists(pUnused));
        }

        Assert.False(Directory.Exists(dir));
    }
}
