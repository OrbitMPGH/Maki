using System.IO.Compression;
using Maki.Core.Entities;
using Maki.Core.Images;
using Maki.Core.Quality;
using Maki.Core.Reading;
using NetVips;

namespace Maki.Core.Tests;

public class PageConversionJpegXlTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("maki-jxl-conversion-").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    private static readonly PageConversionSettings JxlHigh = new(PageConversionFormat.Jxl, PageConversionStop.High, false);

    /// <summary>A colour page: a hue sweep with grain, well past the colour check's chroma floor.</summary>
    private static Raster Painted(int width, int height, int seed)
    {
        var image = new Raster(width, height, bands: 3);
        var random = new Random(seed);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var grain = random.Next(24);
            image[x, y] = new Rgba((byte)Math.Clamp(x * 220 / width + grain, 0, 255), (byte)(40 + grain),
                (byte)Math.Clamp(y * 220 / height + grain, 0, 255));
        }

        return image;
    }

    private static byte[] ColourJxl(int width = 600, int height = 900) =>
        PageConversion.Convert(Painted(width, height, 1).EncodeJpeg(95), ".jpg", JxlHigh).Bytes;

    private static PlanCandidate File(string format) =>
        new(PageConversionOverride.Default, "manga", [], QualityTier.Aggregator, "mangafire", format, 7_500_000, 20, 1000, 1500);

    [Fact]
    public void Jpeg_xl_is_stored_as_two_after_webp_and_avif()
    {
        Assert.Equal(0, (int)PageConversionFormat.Webp);
        Assert.Equal(1, (int)PageConversionFormat.Avif);
        Assert.Equal(2, (int)PageConversionFormat.Jxl);
    }

    [Theory]
    [InlineData("jxl:balanced")]
    [InlineData("jxl:smallest+png")]
    [InlineData("webp:high/jxl:high")]
    [InlineData("jxl:compact/avif:balanced+png+split5000")]
    public void A_jpeg_xl_target_rides_in_the_name_and_parses_back(string name) =>
        Assert.Equal(name, PageConversionSettings.Parse(name)!.Name());

    [Fact]
    public void Jxl_is_a_format_name_and_lossless_stays_lossless_webp()
    {
        Assert.Equal(PageConversionFormat.Jxl, PageConversionSettings.ParseFormat(" JXL "));
        Assert.Equal("jxl", PageConversionSettings.FormatName(PageConversionFormat.Jxl));
        Assert.Equal(new PageTarget(PageConversionFormat.Webp, PageConversionStop.Lossless),
            new PageTarget(PageConversionFormat.Jxl, PageConversionStop.Lossless).Normalised());
        Assert.Null(PageTarget.Parse("jxl:lossless"));
        Assert.True(new PageConversionSettings(PageConversionFormat.Webp, PageConversionStop.High, false,
            Colour: new PageTarget(PageConversionFormat.Jxl, PageConversionStop.Balanced)).UsesFormat(PageConversionFormat.Jxl));
    }

    [Fact]
    public void Each_lossy_stop_has_a_distance_and_no_quality()
    {
        var stops = new[] { PageConversionStop.Smallest, PageConversionStop.Compact, PageConversionStop.Balanced, PageConversionStop.High };
        var distances = stops.Select(PageConversionSettings.Distance).ToArray();

        Assert.Equal(distances.OrderByDescending(d => d), distances);
        Assert.All(distances, d => Assert.InRange(d, 0.5f, 6f));
        Assert.Throws<ArgumentOutOfRangeException>(() => PageConversionSettings.Distance(PageConversionStop.Lossless));
        Assert.Throws<ArgumentOutOfRangeException>(() => PageConversionSettings.Quality(PageConversionFormat.Jxl, PageConversionStop.High));
    }

    [Fact]
    public void A_file_stored_as_jpeg_xl_counts_as_already_compact()
    {
        var plan = PageConversionPlan.For(new PageConversionSettings(PageConversionFormat.Webp, PageConversionStop.High, false));

        Assert.True(PageConversionPlan.IsAlreadyCompact("JXL"));
        Assert.Equal(new PlanAction.Skip("alreadycompact"), plan.Resolve(File("jxl")));
        Assert.Equal("split5000", Assert.IsType<PlanAction.Convert>(
            (plan with { SplitHeight = 5000 }).Resolve(File("jxl"))).Settings.Name());
    }

    [Fact]
    public void A_jpeg_xl_page_is_kept_without_a_decode()
    {
        // A signature and noise: anything that tried to decode it would throw.
        var page = new byte[PageConversion.MinPageBytes * 2];
        new Random(5).NextBytes(page);
        page[0] = 0xFF;
        page[1] = 0x0A;

        var kept = PageConversion.Convert(page, ".jxl", JxlHigh);

        Assert.Equal(("jxl", PageSkip.AlreadyCompact), (kept.SourceFormat, kept.Skipped));
        Assert.Same(page, kept.Bytes);
        Assert.Null(PageConversion.EncodingFor(page, ".jxl", JxlHigh, colour: false));
    }

    [Fact]
    public void A_broken_jpeg_xl_page_reads_as_undecodable()
    {
        byte[] page = [0xFF, 0x0A, 1, 2, 3, 4, 5, 6, 7, 8];

        Assert.Null(ImageCodec.TryIdentify(page));
        Assert.Null(ImageCodec.TryShape(page));
        Assert.Null(ImageCodec.IsColour(page));
        Assert.Throws<VipsException>(() => ImageCodec.Identify(page));
        Assert.Equal(ImageFormat.Jxl, ImageCodec.DetectFormat(page));
    }

    [Fact]
    public void A_jpeg_xl_encode_weighs_like_lossless_webp_on_one_thread()
    {
        var weight = ImageWorkGate.WeighConversion(1115, 1600, 3, PageEncoding.Jxl);

        Assert.Equal(ImageWorkGate.WeighConversion(1115, 1600, 3, PageEncoding.LosslessWebp), weight);
        Assert.False(weight.Avif);
    }

    [Fact]
    public void Jxl_pages_are_listed_and_served_as_image_jxl()
    {
        Assert.True(CbzReader.IsImage("012.jxl"));
        Assert.Equal("image/jxl", CbzReader.ContentType("012.JXL"));
        Assert.Equal(1.5, MeasuredQuality.FormatEfficiency("jxl"));
    }

    [JpegXlFact]
    public void A_jpeg_becomes_a_smaller_jpeg_xl_of_the_same_size()
    {
        var jpeg = Painted(600, 900, 1).EncodeJpeg(95);

        var page = PageConversion.Convert(jpeg, ".jpg", JxlHigh);

        Assert.True(page.Converted, page.Skipped?.ToString());
        Assert.Equal((".jxl", "jpg"), (page.Extension, page.SourceFormat));
        Assert.True(JpegXl.HasSignature(page.Bytes));
        Assert.True(page.Bytes.Length < jpeg.Length);
        Assert.Equal(new ImageHeader(600, 900, ImageFormat.Jxl), ImageCodec.Identify(page.Bytes));
        Assert.Equal(PageEncoding.Jxl, PageConversion.EncodingFor(jpeg, ".jpg", JxlHigh, colour: true));
        Assert.Equal("jxl", PageConversion.FormatOf(page.Bytes, ".jxl"));
    }

    [JpegXlFact]
    public void A_lower_stop_writes_fewer_bytes()
    {
        var jpeg = Painted(600, 900, 2).EncodeJpeg(95);
        var sizes = new[] { PageConversionStop.High, PageConversionStop.Balanced, PageConversionStop.Compact, PageConversionStop.Smallest }
            .Select(stop => PageConversion.Convert(jpeg, ".jpg", JxlHigh with { Stop = stop }).Bytes.Length)
            .ToArray();

        Assert.Equal(sizes.OrderByDescending(s => s), sizes);
    }

    [JpegXlFact]
    public void Colour_pages_take_jpeg_xl_while_grey_pages_stay_webp()
    {
        var settings = new PageConversionSettings(PageConversionFormat.Webp, PageConversionStop.High, false,
            Colour: new PageTarget(PageConversionFormat.Jxl, PageConversionStop.High));

        var colour = PageConversion.Convert(Painted(600, 900, 3).EncodeJpeg(95), ".jpg", settings);
        var grey = PageConversion.Convert(ArchivePageConverterTests.Scan(600, 900, 4).EncodeJpeg(95), ".jpg", settings);

        Assert.Equal((".jxl", true), (colour.Extension, colour.Colour));
        Assert.Equal((".webp", false), (grey.Extension, grey.Colour));
    }

    [JpegXlFact]
    public void A_one_band_page_stays_one_band_and_transparency_lands_on_white()
    {
        using var ramp = Image.Xyz(700, 1000).ExtractBand(0).Linear([255.0 / 699], [0.0]).Cast(Enums.BandFormat.Uchar);
        using var noise = Image.Gaussnoise(700, 1000, sigma: 20, mean: 0);
        using var grained = (ramp + noise).Cast(Enums.BandFormat.Uchar);
        var grey = grained.Copy(interpretation: Enums.Interpretation.Bw).JpegsaveBuffer(q: 95);
        var page = PageConversion.Convert(grey, ".jpg", JxlHigh);
        Assert.True(page.Converted, page.Skipped?.ToString());
        Assert.Equal(1, JpegXl.ReadHeader(page.Bytes).Bands);
        Assert.False(ImageCodec.IsColour(page.Bytes));

        using var clear = (Image.Black(400, 600, bands: 3) + 20).Cast(Enums.BandFormat.Uchar).Bandjoin(0)
            .Copy(interpretation: Enums.Interpretation.Srgb);
        var encoded = ImageCodec.EncodeJpegXl(clear, 1f);
        var (pixels, _, _, bands) = JpegXl.Decode(encoded, 1_000_000);
        Assert.Equal(3, bands);
        Assert.True(pixels.Average(p => (double)p) > 240, "alpha 0 should come out white");
    }

    [JpegXlFact]
    public void Every_page_reader_decodes_a_jpeg_xl_page()
    {
        var jxl = ColourJxl();

        Assert.Equal((600, 900, 3), ImageCodec.TryShape(jxl));
        Assert.True(ImageCodec.IsColour(jxl));
        Assert.Equal(ImageFormat.Jxl, ImageCodec.DetectFormat(jxl));
        Assert.Equal(ImageWorkGate.Weigh(600, 900), ImageWorkGate.Weigh(jxl));

        var thumb = ImageCodec.JpegThumbnail(jxl, 200, 80, enlarge: false);
        Assert.Equal(new ImageHeader(200, 300, ImageFormat.Jpeg), ImageCodec.Identify(thumb));
        var transcoded = ImageCodec.Transcode(jxl, TranscodeFormat.Png);
        Assert.Equal(new ImageHeader(600, 900, ImageFormat.Png), ImageCodec.Identify(transcoded));

        var raster = Raster.Decode(jxl, bands: 3);
        Assert.Equal((600, 900), (raster.Width, raster.Height));
        var (upscale, reason) = ImageCodec.DecodeForUpscale(jxl);
        Assert.Null(reason);
        Assert.Equal((600, 900, 3), (upscale!.Width, upscale.Height, upscale.Bands));

        Assert.Equal(RecapDetector.Width, RecapDetector.Shrink(jxl).Pixels.Length / RecapDetector.Shrink(jxl).Height);
        Assert.NotNull(CreditDetector.Shrink(jxl));

        var path = Path.Combine(_dir, "page.jxl");
        System.IO.File.WriteAllBytes(path, jxl);
        Assert.Equal(new ImageHeader(600, 900, ImageFormat.Jxl), ImageCodec.IdentifyFile(path));
        Assert.Equal(PerceptualHash.OfFile(path), PerceptualHash.OfFile(path));

        var webp = ImageCodec.TranscodeJpegXl(jxl, ArchiveHealthAnalyzer.MaxPixels);
        Assert.Equal(("webp", 600, 900), (webp.Extension, webp.Width, webp.Height));
    }

    [JpegXlFact]
    public void Upscaling_refuses_a_jpeg_xl_page_over_its_limit_from_the_header()
    {
        var rgb = new byte[16_400 * 8 * 3];
        var wide = JpegXl.Encode(rgb, 16_400, 8, 3, 1f, 1);

        Assert.Equal((null, Maki.Core.Images.Upscaling.UpscaleReasons.TooLarge), ImageCodec.DecodeForUpscale(wide));
    }

    [JpegXlFact]
    public void A_tall_strip_is_split_into_jpeg_xl_pieces()
    {
        var strip = Painted(800, 12_000, 6).EncodeJpeg(92);

        var pieces = TallPageSplitter.Split(strip, ".jpg", JxlHigh with { SplitHeight = 5000 })!;

        Assert.True(pieces.Count >= 2);
        Assert.All(pieces, p => Assert.Equal(".jxl", p.Extension));
        Assert.Equal(12_000, pieces.Sum(p => ImageCodec.Identify(p.Bytes).Height));
    }

    [JpegXlFact]
    public void A_jpeg_xl_strip_is_split_into_jpeg_xl_pieces_when_only_splitting()
    {
        var strip = PageConversion.Convert(Painted(800, 12_000, 7).EncodeJpeg(92), ".jpg", JxlHigh).Bytes;
        var splitOnly = PageConversionPlan.For(JxlHigh with { SplitHeight = 5000 }).SplitOnlySettings;

        var pieces = TallPageSplitter.Split(strip, ".jxl", splitOnly)!;

        Assert.True(pieces.Count >= 2);
        Assert.All(pieces, p => Assert.True(JpegXl.HasSignature(p.Bytes)));
        Assert.All(pieces, p => Assert.Equal(".jxl", p.Extension));
        Assert.Equal(PageEncoding.Jxl, TallPageSplitter.WeightEncoding(strip, ".jxl", splitOnly, colour: true));
    }

    [JpegXlFact]
    public async Task A_converted_archive_reads_back_as_jpeg_xl()
    {
        var source = Path.Combine(_dir, "ch1.cbz");
        using (var zip = ZipFile.Open(source, ZipArchiveMode.Create))
        {
            foreach (var (name, seed) in new[] { ("001.jpg", 1), ("002.jpg", 2) })
            {
                using var stream = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                stream.Write(Painted(600, 900, seed).EncodeJpeg(95));
            }
        }

        var target = Path.Combine(_dir, "out.cbz");
        var result = await ArchivePageConverter.ConvertAsync(source, target, JxlHigh, CancellationToken.None);

        Assert.Equal(ArchiveConversionOutcome.Converted, result.Outcome);
        Assert.Equal(("jpg", "jxl", 2), (result.SourceFormat, result.ResultFormat, result.PagesConverted));
        using (var zip = ZipFile.OpenRead(target))
        {
            Assert.Equal(["001.jxl", "002.jxl"], CbzReader.PageNames(zip));
        }

        var measured = ChapterFileMeasurer.MeasureArchive(target, 0, CancellationToken.None);
        Assert.Equal(("jxl", 600, 900), (measured.ImageFormat, measured.MedianWidth, measured.MedianHeight));
        var health = await ArchiveHealthAnalyzer.AnalyzeAsync(target, default, null, null, verify: true);
        Assert.Empty(health.Problems);
        Assert.All(health.Pages, p => Assert.Equal((600, 900), (p.Width, p.Height)));
    }
}
