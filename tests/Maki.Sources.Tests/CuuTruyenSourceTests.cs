using Maki.Core.Sources;
using Maki.Sources.CuuTruyen;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
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
        var cuuTruyenSource = new CuuTruyenSource(
            new FakeHtmlFetcher(new()
            {
                ["/api/v2/chapters/87003"] = FakeHttpClientFactory.Fixture("cuutruyen-pages.json")
            }),
            new FakeHttpClientFactory(new(), new()
            {
                ["scrambled-cd5f27f9fbd274785ae0c1477927e400"] = FakeHttpClientFactory.BinaryFixture("cuutruyen-page.bin")
            }));

        var pages = await cuuTruyenSource.GetPagesAsync(new SourceChapter(
            "cuutruyen", "2637", "87003", "1181", 1181, null, "Thần Và Quỷ", "vi", null));

        Assert.NotEmpty(pages.Pages);
        var first = pages.Pages[0];
        Assert.NotNull(first.Data);
        Assert.Equal("Referer", first.Headers!.Keys.Single());

        using var source = Image.Load<Rgba32>(FakeHttpClientFactory.BinaryFixture("cuutruyen-page.bin"));
        using var result = Image.Load<Rgba32>(first.Data!);
        Assert.Equal(2048, result.Width);
        Assert.Equal(1152, result.Height);
        AssertStripMoved(source, result, sourceY: 0, destinationY: 300, height: 150);
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
        // "#v4|300-150|900-150|450-150|0-150|1050-102|150-150|750-150|600-150": eight source strips,
        // consecutive from the top, each moved to the dy the map names for it.
        const string drmData =
            "EEcATQYJAhsEBgVEAAcJHgIEBE0BDAIbBAYFRAkaCAYDTQUBAAkfBwUBSQkM\nBxQCBgFIBgAJHwcAA0kOCQcUAgYB\n";

        var bytes = FakeHttpClientFactory.BinaryFixture("cuutruyen-page.bin");
        using var source = Image.Load<Rgba32>(bytes);
        var unscrambled = await CuuTruyenSource.UnscrambleAsync(bytes, drmData);
        using var result = Image.Load<Rgba32>(unscrambled);

        Assert.Equal(2048, result.Width);
        Assert.Equal(1152, result.Height);

        // Two non-trivial strips from the map: source rows [0,150) move to y=300, and the 102-row
        // remainder strip, source rows [600,702), moves to y=1050.
        AssertStripMoved(source, result, sourceY: 0, destinationY: 300, height: 150);
        AssertStripMoved(source, result, sourceY: 600, destinationY: 1050, height: 102);

        // A no-op descrambler, or one with the strips reversed or left in place, would put unrelated
        // source content at destination y=0. Its true source is rows [450,600), not rows [0,150).
        AssertRowsDiffer(source, result, sourceY: 0, destinationY: 0);
    }

    /// <summary>
    /// Proves the descrambler actually moves pixels rather than merely producing an image of the
    /// right size: the mapped destination strip must closely match the source strip it came from.
    /// Comparison is by mean per-channel difference over the whole strip, not exact pixel equality,
    /// because <c>UnscrambleAsync</c> re-encodes the composited image to JPEG. That is a lossy round
    /// trip (the strip boundaries introduce block edges the original JPEG didn't have), which measured
    /// a mean difference of 3-5 per channel for a genuinely matching strip on this fixture, against
    /// 79 for an unrelated one (see <see cref="AssertRowsDiffer"/>), so a threshold well below that
    /// gap distinguishes "moved correctly" from "wrong source" without being thrown off by codec noise.
    /// </summary>
    private static void AssertStripMoved(Image<Rgba32> source, Image<Rgba32> result, int sourceY, int destinationY, int height)
    {
        var meanDiff = MeanAbsChannelDiff(source, result, sourceY, destinationY, height);
        Assert.True(meanDiff < 15,
            $"source rows [{sourceY},{sourceY + height}) should reappear at result rows [{destinationY},{destinationY + height}); mean channel diff was {meanDiff:F2}");
    }

    /// <summary>
    /// A no-op or wrongly-mapped descrambler would leave unrelated content sitting at
    /// <paramref name="destinationY"/> instead of what the map says belongs there. Real photo content
    /// differs by far more than JPEG re-encoding noise (measured ~79 mean channel difference on this
    /// fixture against ~3-5 for a true match), so this checks the gap from the other side.
    /// </summary>
    private static void AssertRowsDiffer(Image<Rgba32> source, Image<Rgba32> result, int sourceY, int destinationY, int height = 150)
    {
        var meanDiff = MeanAbsChannelDiff(source, result, sourceY, destinationY, height);
        Assert.True(meanDiff > 30,
            $"result rows [{destinationY},{destinationY + height}) should not just be the source's rows [{sourceY},{sourceY + height}) left in place; mean channel diff was only {meanDiff:F2}");
    }

    private static double MeanAbsChannelDiff(Image<Rgba32> a, Image<Rgba32> b, int aY, int bY, int height)
    {
        double total = 0;
        long samples = 0;
        for (var k = 0; k < height; k++)
        {
            var rowA = a.DangerousGetPixelRowMemory(aY + k).Span;
            var rowB = b.DangerousGetPixelRowMemory(bY + k).Span;
            for (var x = 0; x < rowA.Length; x += 4)
            {
                total += Math.Abs(rowA[x].R - rowB[x].R) + Math.Abs(rowA[x].G - rowB[x].G) + Math.Abs(rowA[x].B - rowB[x].B);
                samples++;
            }
        }

        return total / (samples * 3.0);
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

    [Fact]
    public async Task PreUnwrap_ThrowsOnHtmlWithNoPreElement()
    {
        const string body = "<html><body><div>Just a moment...</div></body></html>";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CuuTruyenPreUnwrap.UnwrapAsync(body, "https://cuutruyen.net/api/v2/mangas/2637"));

        Assert.Contains("https://cuutruyen.net/api/v2/mangas/2637", ex.Message);
    }
}
