using Maki.Core.Sources;
using Maki.Sources.MangaDenizi;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Maki.Sources.Tests;

public class MangaDeniziSourceTests
{
    private static MangaDeniziSource SourceFor(Dictionary<string, string> responses) =>
        new(new FakeHttpClientFactory(responses));

    private static MangaDeniziSource WithSeries() =>
        SourceFor(new()
        {
            ["web/manga/solo-leveling"] = FakeHttpClientFactory.Fixture("mangadenizi-series.json")
        });

    [Fact]
    public async Task Search_maps_hits_to_series_results()
    {
        var source = SourceFor(new()
        {
            ["web/manga?"] = FakeHttpClientFactory.Fixture("mangadenizi-search.json")
        });

        var results = await source.SearchAsync("solo leveling");

        Assert.Single(results);
        Assert.Equal("solo-leveling", results[0].SourceSeriesId);
        Assert.Equal("Solo Leveling", results[0].Title);
        Assert.Equal("https://mangadenizi.net/manga/solo-leveling", results[0].Url);
        Assert.Equal("https://mangadenizi.net/storage/manga/solo-leveling/cover/cover.webp", results[0].CoverUrl);
        Assert.False(string.IsNullOrEmpty(results[0].Description));
    }

    [Fact]
    public async Task GetSeries_reads_title_status_cover_and_description()
    {
        var detail = await WithSeries().GetSeriesAsync("solo-leveling");

        Assert.Equal("Solo Leveling", detail.Title);
        Assert.Equal("completed", detail.Status);
        Assert.Equal("https://mangadenizi.net/manga/solo-leveling", detail.Url);
        Assert.Equal("https://mangadenizi.net/storage/manga/solo-leveling/cover/cover.webp", detail.CoverUrl);
        Assert.False(string.IsNullOrEmpty(detail.Description));
    }

    [Fact]
    public async Task ListChapters_normalizes_ascending_and_tags_turkish()
    {
        var chapters = await WithSeries().ListChaptersAsync("solo-leveling");

        Assert.Equal(180, chapters.Count);
        Assert.Equal(0m, chapters[0].Number);
        Assert.Equal(179m, chapters[^1].Number);
        Assert.All(chapters, c => Assert.Equal("tr", c.Language));
        Assert.All(chapters, c => Assert.Null(c.Volume));
        Assert.DoesNotContain(chapters, c => c.Number is null);
    }

    [Fact]
    public async Task ListChapters_builds_chapter_ids_and_urls_for_GetPagesAsync()
    {
        var chapters = await WithSeries().ListChaptersAsync("solo-leveling");

        var zero = chapters.Single(c => c.Number == 0m);
        Assert.Equal("solo-leveling/000", zero.SourceChapterId);
        Assert.Equal("https://mangadenizi.net/read/solo-leveling/000", zero.Url);
    }

    [Fact]
    public async Task ListChapters_falls_back_to_null_title_when_the_site_gives_none()
    {
        var chapters = await WithSeries().ListChaptersAsync("solo-leveling");

        // Chapter 0 has title:null on the site; never invent an English literal for it.
        Assert.Null(chapters.Single(c => c.Number == 0m).Title);
        // The final chapter carries "[SON]" ("[END]" in Turkish) as its label.
        Assert.Equal("[SON]", chapters.Single(c => c.Number == 179m).Title);
    }

    [Fact]
    public async Task GetPages_descrambles_every_page_and_sets_the_referer()
    {
        var source = new MangaDeniziSource(new FakeHttpClientFactory(
            new()
            {
                ["reader/solo-leveling/000"] = FakeHttpClientFactory.Fixture("mangadenizi-pages.json")
            },
            new() { ["reader-images/"] = FakeHttpClientFactory.BinaryFixture("mangadenizi-page1.bin") }));

        var chapter = new SourceChapter(
            "mangadenizi", "solo-leveling", "solo-leveling/000", "0", 0m, null, null, "tr", null);

        var pages = await source.GetPagesAsync(chapter);

        Assert.Equal(9, pages.Pages.Count);
        Assert.All(pages.Pages, p =>
        {
            Assert.NotNull(p.Data);
            Assert.NotEmpty(p.Data!);
            Assert.Equal("https://mangadenizi.net/", p.Headers!["Referer"]);
            // Every page must come back as a real, decodable JPEG - never the raw scrambled bytes.
            using var decoded = Image.Load(p.Data!);
            Assert.True(decoded.Width > 0 && decoded.Height > 0);
        });
    }

    [Fact]
    public async Task GetPages_throws_ChapterLocked_when_the_reader_has_no_pages()
    {
        var source = SourceFor(new()
        {
            ["reader/solo-leveling/999"] = "{\"pages\":[]}"
        });

        var chapter = new SourceChapter(
            "mangadenizi", "solo-leveling", "solo-leveling/999", "999", 999m, null, null, "tr", null);

        await Assert.ThrowsAsync<ChapterLockedException>(() => source.GetPagesAsync(chapter));
    }

    [Fact]
    public async Task GetPages_throws_InvalidOperation_on_an_unexpected_reader_shape()
    {
        var source = SourceFor(new()
        {
            ["reader/solo-leveling/000"] = "{\"unexpected\":true}"
        });

        var chapter = new SourceChapter(
            "mangadenizi", "solo-leveling", "solo-leveling/000", "0", 0m, null, null, "tr", null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => source.GetPagesAsync(chapter));
        Assert.Contains("reader/solo-leveling/000", ex.Message);
    }

    [Theory]
    [InlineData("https://mangadenizi.net/manga/solo-leveling", "solo-leveling")]
    [InlineData("https://mangadenizi.net/manga/solo-leveling/", "solo-leveling")]
    [InlineData("https://mangadenizi.net/read/solo-leveling/000", null)]
    [InlineData("https://mangadenizi.net/api/v1/web/manga/solo-leveling", null)]
    [InlineData("https://example.com/manga/solo-leveling", null)]
    public void ResolveSeriesIdFromUrl_accepts_series_pages_only(string url, string? expected)
    {
        ISource source = SourceFor(new());
        Assert.Equal(expected, source.ResolveSeriesIdFromUrl(new Uri(url)));
    }
}

public class MangaDeniziDescramblerTests
{
    private const uint Ta = 2463534242;
    private const uint Vo = 2654435769;
    private const uint Bo = 2246822507;

    /// <summary>Every pixel encodes its own coordinates, so any misplaced tile is detected.</summary>
    private static Image<Rgb24> CoordinateImage(int width, int height)
    {
        var image = new Image<Rgb24>(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                image[x, y] = new Rgb24((byte)(x % 256), (byte)(y % 256), (byte)((x / 256 * 16) + (y / 256)));
            }
        }

        return image;
    }

    private static uint Next(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }

    private static int[] Shuffle(int n, uint seed)
    {
        var count = Math.Max(1, n);
        var order = new int[count];
        for (var i = 0; i < count; i++)
        {
            order[i] = i;
        }

        var state = seed == 0 ? Ta : seed;
        for (var k = count - 1; k >= 1; k--)
        {
            var i = (int)(Next(ref state) % (uint)(k + 1));
            (order[k], order[i]) = (order[i], order[k]);
        }

        return order;
    }

    private static (int Offset, int Length)[] Slices(int total, int pieces)
    {
        var r = Math.Max(1, Math.Min(pieces, total));
        var result = new (int, int)[r];
        for (var i = 0; i < r; i++)
        {
            var offset = i * total / r;
            var length = Math.Max(1, (i + 1) * total / r - offset);
            result[i] = (offset, length);
        }

        return result;
    }

    private static (int Offset, int Length)[] MapSlices((int Offset, int Length)[] slices, int[] order)
    {
        var result = new (int, int)[order.Length];
        var t = 0;
        for (var i = 0; i < order.Length; i++)
        {
            var length = slices[order[i]].Length;
            result[i] = (t, length);
            t += length;
        }

        return result;
    }

    /// <summary>
    /// Independent re-derivation of the site's scramble (the inverse of MangaDeniziDescrambler),
    /// built straight from the plan's pseudocode rather than by calling into production code, so a
    /// bug shared between the two would not pass unnoticed.
    /// </summary>
    private static Image<Rgb24> Scramble(Image<Rgb24> original, int grid, uint seed)
    {
        var width = original.Width;
        var height = original.Height;
        var l = Math.Max(1, Math.Min(grid, Math.Min(width, height)));

        var u = Slices(width, l);
        var c = Slices(height, l);
        var m = Shuffle(l, seed ^ Bo);
        var f = Shuffle(l, seed ^ Vo);
        var p = MapSlices(u, m);
        var g = MapSlices(c, f);

        var scrambled = new Image<Rgb24>(width, height);
        scrambled.Mutate(ctx =>
        {
            for (var row = 0; row < l; row++)
            {
                for (var col = 0; col < l; col++)
                {
                    // The descrambler copies scrambled(p,g) -> original(u[m],c[f]); the scrambler
                    // is exactly that copy run backwards.
                    var sourceRect = new Rectangle(u[m[col]].Offset, c[f[row]].Offset, p[col].Length, g[row].Length);
                    var destination = new Point(p[col].Offset, g[row].Offset);
                    ctx.DrawImage(original, destination, sourceRect, 1f);
                }
            }
        });

        return scrambled;
    }

    private static void AssertPixelsEqual(Image<Rgb24> expected, Image<Rgb24> actual)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        for (var y = 0; y < expected.Height; y++)
        {
            for (var x = 0; x < expected.Width; x++)
            {
                Assert.True(expected[x, y] == actual[x, y], $"Pixel mismatch at ({x}, {y})");
            }
        }
    }

    [Theory]
    [InlineData(1100, 1463, 10, 316064641u)]  // page 1's real dimensions and seed
    [InlineData(720, 5000, 10, 888536135u)]   // page 2's real dimensions and seed
    [InlineData(97, 61, 10, 42u)]             // grid does not divide the dimensions evenly
    [InlineData(3, 3, 10, 7u)]                // grid larger than the image: l = min(grid, min(w,h))
    [InlineData(50, 50, 1, 0u)]               // seed 0 forces the TA fallback constant
    public void Descramble_reverses_the_synthetic_scramble(int width, int height, int grid, uint seed)
    {
        using var original = CoordinateImage(width, height);
        using var scrambled = Scramble(original, grid, seed);

        using var descrambled = MangaDeniziDescrambler.Descramble(scrambled, grid, seed);

        AssertPixelsEqual(original, descrambled);
    }

    [Fact]
    public void Descramble_of_the_recorded_page_matches_the_verified_output()
    {
        // solo-leveling/000 page 1, grid 10, seed 316064641 - verified once by eye against the
        // real site (a Python port produced the clean credits page; see PR description).
        var raw = FakeHttpClientFactory.BinaryFixture("mangadenizi-page1.bin");
        using var scrambled = Image.Load<Rgb24>(raw);
        using var expected = Image.Load<Rgb24>(
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "mangadenizi-page1-expected.jpg")));

        using var descrambled = MangaDeniziDescrambler.Descramble(scrambled, 10, 316064641u);

        // JPEG re-encoding (the descrambler's own output path, and the fixture's own capture)
        // is lossy, so pixels differ by a few units even for an identical image - compare the
        // mean absolute per-channel difference against a small threshold instead of exact equality.
        var matches = MeanAbsoluteDifference(expected, descrambled);
        Assert.True(matches < 8.0, $"Descrambled output differs from the known-good fixture by {matches}");

        // Negative control 1: the still-scrambled input must not itself pass the threshold - the
        // pieces are the same set of pixels, just in the wrong places, so a bug that no-ops the
        // descramble must be caught here.
        var scrambledDiff = MeanAbsoluteDifference(expected, scrambled);
        Assert.True(scrambledDiff > 30.0, $"Scrambled input unexpectedly close to the expected output ({scrambledDiff})");

        // Negative control 2: an off-by-one seed must not produce the same layout.
        using var wrongSeed = MangaDeniziDescrambler.Descramble(scrambled, 10, 316064642u);
        var wrongSeedDiff = MeanAbsoluteDifference(expected, wrongSeed);
        Assert.True(wrongSeedDiff > 30.0, $"Wrong seed unexpectedly close to the expected output ({wrongSeedDiff})");
    }

    private static double MeanAbsoluteDifference(Image<Rgb24> a, Image<Rgb24> b)
    {
        Assert.Equal(a.Width, b.Width);
        Assert.Equal(a.Height, b.Height);

        long total = 0;
        for (var y = 0; y < a.Height; y++)
        {
            for (var x = 0; x < a.Width; x++)
            {
                var pa = a[x, y];
                var pb = b[x, y];
                total += Math.Abs(pa.R - pb.R);
                total += Math.Abs(pa.G - pb.G);
                total += Math.Abs(pa.B - pb.B);
            }
        }

        return (double)total / (a.Width * a.Height * 3);
    }
}
