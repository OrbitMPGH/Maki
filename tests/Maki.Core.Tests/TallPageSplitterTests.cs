using System.Diagnostics;
using System.IO.Compression;
using Maki.Core.Images;
using Maki.Core.Reading;
using Xunit.Abstractions;

namespace Maki.Core.Tests;

public class TallPageSplitterTests(ITestOutputHelper output)
{
    private static readonly PageConversionSettings WebpSplit =
        new(PageConversionFormat.Webp, PageConversionStop.High, false, SplitHeight: 5000);

    [Fact]
    public void A_cut_lands_in_the_gutter_near_the_height()
    {
        var cuts = TallPageSplitter.PlanCuts(TallStrips.Rows(12000, (4300, 4400), (8900, 9000)), 5000);

        Assert.Equal([4350, 8950], cuts);
    }

    [Fact]
    public void With_no_gutter_in_reach_the_cut_is_hard_at_the_height()
    {
        Assert.Equal([5000, 10000], TallPageSplitter.PlanCuts(TallStrips.Rows(12000), 5000));
        // A gutter further up than the search window reaches is not used.
        Assert.Equal([5000], TallPageSplitter.PlanCuts(TallStrips.Rows(9000, (1000, 1200)), 5000));
    }

    [Fact]
    public void A_short_remainder_stays_on_the_piece_before_it()
    {
        Assert.Equal([5000], TallPageSplitter.PlanCuts(TallStrips.Rows(10500), 5000));
        Assert.Empty(TallPageSplitter.PlanCuts(TallStrips.Rows(5400), 5000));
        Assert.Empty(TallPageSplitter.PlanCuts(TallStrips.Rows(5000), 5000));
    }

    [Fact]
    public void A_real_gutter_wins_over_the_space_between_two_lines_of_text()
    {
        // Ten flat rows just under the height (between two lines of narration) against a 200-row gutter further up.
        Assert.Equal([4100], TallPageSplitter.PlanCuts(TallStrips.Rows(8000, (4000, 4200), (4800, 4810)), 5000));
        // Flat runs under the minimum are never cut in.
        Assert.Equal([5000], TallPageSplitter.PlanCuts(TallStrips.Rows(8000, (4900, 4903)), 5000));
    }

    [Fact]
    public void Of_two_good_gutters_the_one_nearer_the_height_wins()
    {
        Assert.Equal([4650], TallPageSplitter.PlanCuts(TallStrips.Rows(8000, (3200, 3400), (4600, 4700)), 5000));
    }

    [Fact]
    public void A_gutter_running_past_the_height_is_cut_at_the_height()
    {
        Assert.Equal([5000], TallPageSplitter.PlanCuts(TallStrips.Rows(8000, (4980, 5300)), 5000));
    }

    [Theory]
    [InlineData(800, 12000, 5000, true)]
    [InlineData(800, 5000, 5000, false)]
    [InlineData(800, 4000, 5000, false)]
    [InlineData(3000, 6000, 5000, false)]
    [InlineData(2400, 6000, 5000, true)]
    public void Only_strips_over_the_height_are_tall(int width, int height, int max, bool tall) =>
        Assert.Equal(tall, TallPageSplitter.IsTall(width, height, max));

    [Fact]
    public void A_jpeg_strip_converted_to_webp_comes_back_as_webp_pieces_cut_in_its_gutters()
    {
        var page = TallStrips.Jpeg(800, 12000, [(4300, 4400), (8900, 9000)]);

        var pieces = TallPageSplitter.Split(page, ".jpg", WebpSplit)!;

        Assert.Equal([4350, 4600, 3050], pieces.Select(p => ImageCodec.Identify(p.Bytes).Height));
        Assert.All(pieces, p =>
        {
            Assert.Equal(".webp", p.Extension);
            Assert.Equal("jpg", p.SourceFormat);
            Assert.True(p.Converted);
            Assert.Equal(800, ImageCodec.Identify(p.Bytes).Width);
        });
    }

    [Fact]
    public void Pieces_keep_the_pages_own_format_when_the_plan_keeps_it()
    {
        var strip = TallStrips.Strip(600, 11000, [(4500, 4600)]);

        var lossless = TallPageSplitter.Split(strip.EncodeJpeg(90), ".jpeg",
            WebpSplit with { Stop = PageConversionStop.Lossless })!;
        Assert.All(lossless, p => Assert.Equal((".jpeg", ImageFormat.Jpeg), (p.Extension, ImageCodec.Identify(p.Bytes).Format)));

        var webp = TallPageSplitter.Split(strip.EncodeWebp(80), ".webp", WebpSplit with { SplitOnly = true })!;
        Assert.Equal(3, webp.Count);
        Assert.All(webp, p => Assert.Equal((".webp", ImageFormat.Webp), (p.Extension, ImageCodec.Identify(p.Bytes).Format)));

        var losslessWebp = TallPageSplitter.Split(strip.EncodeWebpLossless(), ".webp", WebpSplit with { SplitOnly = true })!;
        Assert.All(losslessWebp, p => Assert.Equal("VP8L"u8.ToArray(), p.Bytes[12..16]));

        var png = TallPageSplitter.Split(strip.EncodePng(), ".png", WebpSplit with { SplitOnly = true })!;
        Assert.All(png, p => Assert.Equal(ImageFormat.Png, ImageCodec.Identify(p.Bytes).Format));
    }

    [Fact]
    public void Ordinary_pages_and_a_plan_that_does_not_split_leave_the_page_whole()
    {
        Assert.Null(TallPageSplitter.Split(TallStrips.Jpeg(800, 4000, []), ".jpg", WebpSplit));
        Assert.Null(TallPageSplitter.Split(TallStrips.Jpeg(2800, 6000, []), ".jpg", WebpSplit));
        Assert.Null(TallPageSplitter.Split(TallStrips.Jpeg(800, 12000, []), ".jpg", WebpSplit with { SplitHeight = null }));
        Assert.Null(TallPageSplitter.Split(new Raster(400, 9000, bands: 3).EncodeGif(), ".gif", WebpSplit));
    }

    [Fact]
    public void A_split_only_page_that_is_not_tall_is_kept_as_it_is()
    {
        var page = TallStrips.Jpeg(800, 1200, []);

        var result = Assert.Single(PageConversion.ConvertPage(page, ".jpg", WebpSplit with { SplitOnly = true }));

        Assert.Same(page, result.Bytes);
        Assert.Equal(PageSkip.SplitOnly, result.Skipped);
    }

    [Fact]
    public void The_preview_reports_the_size_and_the_cuts()
    {
        var page = TallStrips.Jpeg(800, 12000, [(4300, 4400), (8900, 9000)]);

        var preview = TallPageSplitter.Preview(page, 5000)!.Value;

        Assert.Equal((800, 12000), (preview.Width, preview.Height));
        Assert.Equal([4350, 8950], preview.Cuts);
        Assert.Empty(TallPageSplitter.Preview(page, 12000)!.Value.Cuts);
        Assert.Null(TallPageSplitter.Preview([1, 2, 3], 5000));
    }

    [Fact]
    public async Task A_converted_archive_names_the_pieces_after_their_page_and_updates_ComicInfo()
    {
        var dir = Directory.CreateTempSubdirectory("maki-split-");
        try
        {
            var source = Path.Combine(dir.FullName, "in.cbz");
            var target = Path.Combine(dir.FullName, "out.cbz");
            using (var zip = ZipFile.Open(source, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("ComicInfo.xml").Open()))
                {
                    writer.Write("<?xml version=\"1.0\"?><ComicInfo><Series>S</Series><PageCount>3</PageCount><Pages><Page Image=\"0\" Type=\"FrontCover\" /><Page Image=\"1\" /><Page Image=\"2\" /></Pages></ComicInfo>");
                }

                foreach (var (name, bytes) in new[]
                         {
                             ("c001 - p001.jpg", TallStrips.Jpeg(800, 1200, [])),
                             ("c001 - p002.jpg", TallStrips.Jpeg(800, 12000, [(4300, 4400), (8900, 9000)])),
                             ("c001 - p003.webp", TallStrips.Strip(800, 9000, []).EncodeWebp(80))
                         })
                {
                    using var stream = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                    stream.Write(bytes);
                }
            }

            var result = await ArchivePageConverter.ConvertAsync(source, target, WebpSplit, CancellationToken.None);

            Assert.Equal(ArchiveConversionOutcome.Converted, result.Outcome);
            Assert.Equal("1:3,2:2", result.Split!.ToString());
            Assert.Equal(["c001 - p001.jpg", "c001 - p002.jpg", "c001 - p003.webp"], result.PagesBefore);
            using var written = ZipFile.OpenRead(target);
            Assert.Equal(
                ["c001 - p001.webp", "c001 - p002-01.webp", "c001 - p002-02.webp", "c001 - p002-03.webp",
                    "c001 - p003-01.webp", "c001 - p003-02.webp"],
                CbzReader.PageNames(written));
            using var info = new StreamReader(written.GetEntry("ComicInfo.xml")!.Open());
            var xml = info.ReadToEnd();
            Assert.Contains("<PageCount>6</PageCount>", xml);
            Assert.Equal(["0", "1", "2", "3", "4", "5"],
                System.Xml.Linq.XDocument.Parse(xml).Root!.Element("Pages")!.Elements("Page").Select(e => (string)e.Attribute("Image")!));
            Assert.Contains("Type=\"FrontCover\"", xml);
            Assert.Contains("<Series>S</Series>", xml);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task The_probe_finds_a_tall_page_from_its_header()
    {
        var dir = Directory.CreateTempSubdirectory("maki-split-");
        try
        {
            var path = Path.Combine(dir.FullName, "in.cbz");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                foreach (var (name, bytes) in new[]
                         {
                             ("001.webp", TallStrips.Strip(800, 1200, []).EncodeWebp(80)),
                             ("002.webp", TallStrips.Strip(800, 9000, []).EncodeWebp(80))
                         })
                {
                    using var stream = zip.CreateEntry(name).Open();
                    stream.Write(bytes);
                }
            }

            Assert.True(await ArchivePageConverter.ProbeAsync(path, WebpSplit with { SplitOnly = true }, CancellationToken.None));
            Assert.False(await ArchivePageConverter.ProbeAsync(path,
                WebpSplit with { SplitOnly = true, SplitHeight = 10000 }, CancellationToken.None));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_30000_px_strip_splits_in_reasonable_time()
    {
        var gutters = Enumerable.Range(1, 14).Select(i => (i * 2000 - 60, i * 2000)).ToList();
        var page = TallStrips.Jpeg(800, 30000, gutters);

        var timer = Stopwatch.StartNew();
        var preview = TallPageSplitter.Preview(page, 5000)!.Value;
        var previewTime = timer.Elapsed;
        timer.Restart();
        var webp = TallPageSplitter.Split(page, ".jpg", WebpSplit)!;
        var webpTime = timer.Elapsed;
        timer.Restart();
        var avif = TallPageSplitter.Split(page, ".jpg", WebpSplit with { Format = PageConversionFormat.Avif })!;
        var avifTime = timer.Elapsed;
        timer.Restart();
        var jpeg = TallPageSplitter.Split(page, ".jpg", WebpSplit with { Stop = PageConversionStop.Lossless })!;
        var jpegTime = timer.Elapsed;

        output.WriteLine($"800x30000 JPEG ({page.Length / 1024} KB): cuts {string.Join(",", preview.Cuts)}");
        output.WriteLine($"cuts only {previewTime.TotalMilliseconds:0} ms, WebP pieces {webpTime.TotalMilliseconds:0} ms, " +
                         $"AVIF pieces {avifTime.TotalMilliseconds:0} ms, JPEG pieces {jpegTime.TotalMilliseconds:0} ms");
        Assert.Equal(webp.Count, preview.Cuts.Count + 1);
        Assert.Equal(webp.Count, avif.Count);
        Assert.Equal(webp.Count, jpeg.Count);
        Assert.All(preview.Cuts, cut => Assert.Contains(gutters, g => cut >= g.Item1 && cut < g.Item2));
        Assert.Equal(30000, webp.Sum(p => ImageCodec.Identify(p.Bytes).Height));
    }
}
