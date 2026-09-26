using Maki.Core.Sources;
using Maki.Sources.CuuTruyen;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Maki.Sources.Tests;

public class CuuTruyenSourceTests
{
    private static CuuTruyenSource NewSource(Dictionary<string, string> responses) =>
        new(new FakeHtmlFetcher(responses), new FakeHttpClientFactory([]));

    [Fact]
    public async Task SearchAsync_ParsesIdTitleAndRewrittenCover()
    {
        var source = NewSource(new()
        {
            ["/mangas/search"] = FakeHttpClientFactory.Fixture("cuutruyen-search.json")
        });

        var results = await source.SearchAsync("one piece");

        Assert.NotEmpty(results);
        var first = results[0];
        Assert.Equal("2637", first.SourceSeriesId);
        Assert.Equal("One Piece", first.Title);
        Assert.Equal("https://cuutruyen.net/mangas/2637", first.Url);
        Assert.NotNull(first.CoverUrl);
        Assert.Contains("storage-bravo.cuutruyen.net", first.CoverUrl);
        Assert.DoesNotContain("lrclib.net", first.CoverUrl);
    }

    [Fact]
    public async Task SearchAsync_UnwrapsFlareSolverrPreResponse()
    {
        var direct = NewSource(new()
        {
            ["/mangas/search"] = FakeHttpClientFactory.Fixture("cuutruyen-search.json")
        });
        var wrapped = NewSource(new()
        {
            ["/mangas/search"] = FakeHttpClientFactory.Fixture("cuutruyen-search-pre.html")
        });

        var directResults = await direct.SearchAsync("one piece");
        var wrappedResults = await wrapped.SearchAsync("one piece");

        Assert.Equal(directResults.Count, wrappedResults.Count);
        Assert.Equal(directResults[0].SourceSeriesId, wrappedResults[0].SourceSeriesId);
        Assert.Equal(directResults[0].Title, wrappedResults[0].Title);
    }

    [Fact]
    public async Task GetSeriesAsync_ParsesDetailAndDefaultsToOngoing()
    {
        var source = NewSource(new()
        {
            ["/api/v2/mangas/2637"] = FakeHttpClientFactory.Fixture("cuutruyen-manga.json")
        });

        var detail = await source.GetSeriesAsync("2637");

        Assert.Equal("2637", detail.SourceSeriesId);
        Assert.Equal("One Piece", detail.Title);
        Assert.Equal("https://cuutruyen.net/mangas/2637", detail.Url);
        Assert.Equal("Ongoing", detail.Status);
        Assert.NotNull(detail.CoverUrl);
        Assert.Contains("storage-bravo.cuutruyen.net", detail.CoverUrl);
        Assert.False(string.IsNullOrWhiteSpace(detail.Description));
        // full_description is HTML; the parsed description must not carry markup through.
        Assert.DoesNotContain("<", detail.Description);
    }

    [Fact]
    public async Task ListChaptersAsync_ParsesNumbersDatesLanguageAndNullSpecials()
    {
        var source = NewSource(new()
        {
            ["/chapters"] = FakeHttpClientFactory.Fixture("cuutruyen-chapters.json")
        });

        var chapters = await source.ListChaptersAsync("2637");

        Assert.NotEmpty(chapters);
        Assert.All(chapters, c => Assert.Equal("vi", c.Language));
        Assert.All(chapters, c => Assert.Equal("cuutruyen", c.SourceName));

        var numbered = chapters.Where(c => c.Number is not null).ToList();
        Assert.NotEmpty(numbered);
        Assert.Equal(numbered.OrderBy(c => c.Number), numbered);

        // "Movie"/"Crossover" rows parse to a null Number and must carry a non-null Title (the
        // rule that stops ChapterIdentity's by-title dedupe from losing them to a blank key).
        var specials = chapters.Where(c => c.Number is null).ToList();
        Assert.NotEmpty(specials);
        Assert.All(specials, c => Assert.False(string.IsNullOrEmpty(c.Title)));
    }

    [Fact]
    public async Task ListChaptersAsync_ParsesReleaseDateWithOffset()
    {
        var source = NewSource(new()
        {
            ["/chapters"] = FakeHttpClientFactory.Fixture("cuutruyen-chapters.json")
        });

        var chapters = await source.ListChaptersAsync("2637");
        var newest = chapters.MaxBy(c => c.Number);

        Assert.NotNull(newest);
        Assert.NotNull(newest!.ReleaseDate);
        // created_at "2026-04-27T19:04:16.589+07:00" converted to UTC is 12:04:16.
        Assert.Equal(new DateTime(2026, 4, 27, 12, 4, 16, DateTimeKind.Unspecified), newest.ReleaseDate!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task GetPagesAsync_UnscramblesDrmPagesAndKeepsDimensions()
    {
        var source = new CuuTruyenSource(
            new FakeHtmlFetcher(new()
            {
                ["/api/v2/chapters/87003"] = FakeHttpClientFactory.Fixture("cuutruyen-pages.json")
            }),
            new FakeHttpClientFactory(new(), new()
            {
                ["scrambled-cd5f27f9fbd274785ae0c1477927e400"] = FakeHttpClientFactory.BinaryFixture("cuutruyen-page.bin")
            }));

        var pages = await source.GetPagesAsync(new SourceChapter(
            "cuutruyen", "2637", "87003", "1181", 1181, null, "Thần Và Quỷ", "vi", null));

        Assert.NotEmpty(pages.Pages);
        var first = pages.Pages[0];
        Assert.NotNull(first.Data);
        Assert.Equal("Referer", first.Headers!.Keys.Single());

        using var image = Image.Load<Rgba32>(first.Data!);
        Assert.Equal(2048, image.Width);
        Assert.Equal(1152, image.Height);
    }

    [Fact]
    public async Task GetPagesAsync_PageWithoutDrmDataStaysAUrlRequest()
    {
        const string body = """
            { "data": { "pages": [
                { "id": 1, "order": 0, "width": 100, "height": 100, "status": "processed",
                  "image_url": "https://storage-ct.lrclib.net/file/x.jpg" }
            ] } }
            """;

        var source = new CuuTruyenSource(
            new FakeHtmlFetcher(new() { ["/api/v2/chapters/1"] = body }),
            new FakeHttpClientFactory([]));

        var pages = await source.GetPagesAsync(new SourceChapter(
            "cuutruyen", "2637", "1", "1", 1, null, null, "vi", null));

        var only = Assert.Single(pages.Pages);
        Assert.Null(only.Data);
        Assert.Equal("https://storage-bravo.cuutruyen.net/file/x.jpg", only.Url);
    }

    [Fact]
    public async Task GetPagesAsync_EmptyPagesThrowsChapterLocked()
    {
        const string body = """{ "data": { "pages": [] } }""";
        var source = new CuuTruyenSource(
            new FakeHtmlFetcher(new() { ["/api/v2/chapters/1"] = body }),
            new FakeHttpClientFactory([]));

        await Assert.ThrowsAsync<ChapterLockedException>(() => source.GetPagesAsync(
            new SourceChapter("cuutruyen", "2637", "1", "1", 1, null, null, "vi", null)));
    }

    [Fact]
    public async Task UnscrambleAsync_DecodesTheSampleDrmMap()
    {
        // The raw drm_data for chapter 87003 page 0; decoded per 11-cuutruyen.md it must read
        // "#v4|300-150|900-150|450-150|0-150|1050-102|150-150|750-150|600-150".
        const string drmData =
            "EEcATQYJAhsEBgVEAAcJHgIEBE0BDAIbBAYFRAkaCAYDTQUBAAkfBwUBSQkM\nBxQCBgFIBgAJHwcAA0kOCQcUAgYB\n";

        var bytes = FakeHttpClientFactory.BinaryFixture("cuutruyen-page.bin");
        var unscrambled = await CuuTruyenSource.UnscrambleAsync(bytes, drmData);

        using var image = Image.Load<Rgba32>(unscrambled);
        Assert.Equal(2048, image.Width);
        Assert.Equal(1152, image.Height);
    }

    [Fact]
    public async Task UnscrambleAsync_RejectsAnUnknownDrmVersion()
    {
        // XOR-encode "#v9|..." with the same key so the decoded prefix check is what fails.
        var key = "3141592653589793"u8.ToArray();
        var plain = System.Text.Encoding.UTF8.GetBytes("#v9|0-10");
        var encoded = new byte[plain.Length];
        for (var i = 0; i < plain.Length; i++)
        {
            encoded[i] = (byte)(plain[i] ^ key[i % key.Length]);
        }

        var drmData = Convert.ToBase64String(encoded);
        var bytes = FakeHttpClientFactory.BinaryFixture("cuutruyen-page.bin");

        await Assert.ThrowsAsync<InvalidDataException>(() => CuuTruyenSource.UnscrambleAsync(bytes, drmData));
    }
}
