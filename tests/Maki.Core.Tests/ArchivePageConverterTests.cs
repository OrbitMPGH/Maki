using System.IO.Compression;
using Maki.Core.Images;
using Maki.Core.Quality;
using Maki.Core.Reading;

namespace Maki.Core.Tests;

public class ArchivePageConverterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("maki-page-conversion-").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    private static readonly PageConversionSettings Webp = new(PageConversionFormat.Webp, PageConversionStop.Balanced, false);
    private static readonly PageConversionSettings Avif = new(PageConversionFormat.Avif, PageConversionStop.Compact, false);

    /// <summary>Gradients with fine grain, which a JPEG at 95 stores at several bits per pixel.</summary>
    internal static Raster Scan(int width, int height, int seed)
    {
        var image = new Raster(width, height, bands: 3);
        var random = new Random(seed);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var grain = random.Next(24);
            var tone = (byte)Math.Clamp((x + y) * 200 / (width + height) + grain, 0, 255);
            image[x, y] = new Rgba(tone, tone, tone);
        }

        return image;
    }

    /// <summary>Flat panels and black strokes, the shape of a cleaned PNG page.</summary>
    internal static Raster LineArt(int width, int height, int seed)
    {
        var image = new Raster(width, height, new Rgba(255, 255, 255), bands: 3);
        var random = new Random(seed);
        for (var i = 0; i < 4000; i++)
        {
            var (x, y) = (random.Next(width - 40), random.Next(height - 4));
            var length = random.Next(4, 40);
            for (var dx = 0; dx < length; dx++)
            for (var dy = 0; dy < 2; dy++)
                image[x + dx, y + dy] = new Rgba(0, 0, 0);
        }

        return image;
    }

    private const string ComicInfo = "<?xml version=\"1.0\"?><ComicInfo><Series>Test</Series></ComicInfo>";

    private string Cbz(string name, params (string Name, byte[] Bytes)[] entries)
    {
        var path = Path.Combine(_dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entryName, bytes) in entries)
        {
            using var stream = zip.CreateEntry(entryName, CompressionLevel.NoCompression).Open();
            stream.Write(bytes);
        }

        return path;
    }

    private static byte[] Xml => System.Text.Encoding.UTF8.GetBytes(ComicInfo);

    [Fact]
    public async Task Jpeg_pages_become_webp_and_everything_else_is_copied_as_it_was()
    {
        var source = Cbz("ch1.cbz",
            ("ComicInfo.xml", Xml),
            ("001.jpg", Scan(600, 900, 1).EncodeJpeg(95)),
            ("002.jpg", Scan(600, 900, 2).EncodeJpeg(95)));
        var target = Path.Combine(_dir, "out.cbz");

        var result = await ArchivePageConverter.ConvertAsync(source, target, Webp, CancellationToken.None);

        Assert.Equal(ArchiveConversionOutcome.Converted, result.Outcome);
        Assert.Equal(("jpg", "webp", 2, 0), (result.SourceFormat, result.ResultFormat, result.PagesConverted, result.PagesKept));
        Assert.Equal(new FileInfo(source).Length - new FileInfo(target).Length, result.SavedBytes);
        Assert.True(result.ResultBytes < result.SourceBytes / 2, $"{result.ResultBytes} of {result.SourceBytes}");

        using var zip = ZipFile.OpenRead(target);
        Assert.Equal(["ComicInfo.xml", "001.webp", "002.webp"], zip.Entries.Select(e => e.FullName));
        using (var reader = new StreamReader(zip.GetEntry("ComicInfo.xml")!.Open()))
        {
            Assert.Equal(ComicInfo, reader.ReadToEnd());
        }

        Assert.Equal(["001.webp", "002.webp"], CbzReader.PageNames(zip));
        Assert.False(File.Exists(target + ".partial"));
        Assert.Equal("webp", ChapterFileMeasurer.MeasureArchive(target, 0, CancellationToken.None).ImageFormat);
    }

    [Fact]
    public async Task Avif_is_written_for_jpeg_pages_when_chosen()
    {
        var source = Cbz("ch1.cbz", ("001.jpg", Scan(600, 900, 3).EncodeJpeg(95)));
        var target = Path.Combine(_dir, "out.cbz");

        var result = await ArchivePageConverter.ConvertAsync(source, target, Avif, CancellationToken.None);

        Assert.Equal(ArchiveConversionOutcome.Converted, result.Outcome);
        using var zip = ZipFile.OpenRead(target);
        Assert.Equal("001.avif", Assert.Single(CbzReader.PageNames(zip)));
        Assert.Equal("avif", result.ResultFormat);
    }

    [Fact]
    public async Task A_png_page_kept_lossless_becomes_lossless_webp_with_the_same_pixels_even_when_avif_is_chosen()
    {
        var art = LineArt(900, 1300, 4);
        var png = art.EncodePng();
        Assert.True(png.Length > PageConversion.MinPageBytes, $"fixture is only {png.Length} bytes");
        var source = Cbz("ch1.cbz", ("001.png", png));
        var target = Path.Combine(_dir, "out.cbz");

        var result = await ArchivePageConverter.ConvertAsync(source, target, Avif with { PngLossless = true }, CancellationToken.None);

        Assert.Equal(ArchiveConversionOutcome.Converted, result.Outcome);
        using var zip = ZipFile.OpenRead(target);
        using var stream = zip.GetEntry("001.webp")!.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var decoded = Raster.Decode(buffer.ToArray());
        Assert.Equal(art.Pixels, decoded.Bands == 3 ? decoded.Pixels : WithoutAlpha(decoded));
    }

    [Theory]
    [InlineData(PageConversionFormat.Webp, ".webp")]
    [InlineData(PageConversionFormat.Avif, ".avif")]
    public void A_png_page_goes_lossy_in_the_chosen_format_at_a_lossy_stop(PageConversionFormat format, string extension)
    {
        var png = Scan(600, 900, 20).EncodePng();

        var page = PageConversion.Convert(png, ".png", new PageConversionSettings(format, PageConversionStop.High, false));

        Assert.True(page.Converted, page.Skipped?.ToString());
        Assert.Equal(extension, page.Extension);
        Assert.Equal("png", page.SourceFormat);
    }

    [Fact]
    public void At_lossless_a_png_goes_to_lossless_webp_and_a_jpeg_is_kept()
    {
        var lossless = new PageConversionSettings(PageConversionFormat.Avif, PageConversionStop.Lossless, false);
        var jpeg = Scan(600, 900, 21).EncodeJpeg(95);
        var art = LineArt(900, 1300, 22);

        var kept = PageConversion.Convert(jpeg, ".jpg", lossless);
        var png = PageConversion.Convert(art.EncodePng(), ".png", lossless);

        Assert.Equal(PageSkip.Lossless, kept.Skipped);
        Assert.Same(jpeg, kept.Bytes);
        Assert.Equal(".webp", png.Extension);
        var decoded = Raster.Decode(png.Bytes);
        Assert.Equal(art.Pixels, decoded.Bands == 3 ? decoded.Pixels : WithoutAlpha(decoded));
    }

    private static byte[] WithoutAlpha(Raster raster) =>
        [.. raster.Pixels.Where((_, i) => i % 4 != 3)];

    [Fact]
    public async Task Pages_already_compact_animated_or_tiny_are_left_alone_and_the_file_is_not_rewritten()
    {
        var webp = Scan(600, 900, 5).EncodeWebp(80);
        var tiny = new Raster(40, 40, bands: 3).EncodeJpeg(80);
        var apng = AnimatedPng(LineArt(900, 1300, 6).EncodePng());
        var source = Cbz("ch1.cbz", ("001.webp", webp), ("002.jpg", tiny), ("003.png", apng));
        var target = Path.Combine(_dir, "out.cbz");

        var result = await ArchivePageConverter.ConvertAsync(source, target, Webp, CancellationToken.None);

        Assert.Equal(ArchiveConversionOutcome.NotSmaller, result.Outcome);
        Assert.Equal(0, result.SavedBytes);
        Assert.Equal(1, result.Skipped[PageSkip.AlreadyCompact]);
        Assert.Equal(1, result.Skipped[PageSkip.TooSmall]);
        Assert.Equal(1, result.Skipped[PageSkip.Animated]);
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".partial"));
    }

    /// <summary>An acTL chunk right after IHDR is all it takes for a reader to treat the PNG as an animation.</summary>
    private static byte[] AnimatedPng(byte[] png)
    {
        byte[] acTl = [0, 0, 0, 8, (byte)'a', (byte)'c', (byte)'T', (byte)'L', 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0];
        const int afterIhdr = 8 + 25;
        return [.. png[..afterIhdr], .. acTl, .. png[afterIhdr..]];
    }

    [Fact]
    public void An_apng_is_recognised_and_a_plain_png_is_not()
    {
        var png = LineArt(200, 200, 7).EncodePng();
        Assert.False(PageConversion.IsAnimatedPng(png));
        Assert.True(PageConversion.IsAnimatedPng(AnimatedPng(png)));
    }

    [Fact]
    public async Task A_jpeg_that_comes_out_larger_is_kept_and_the_file_left_as_it_was()
    {
        var source = Cbz("ch1.cbz", ("001.jpg", Scan(600, 900, 8).EncodeJpeg(40)));
        var target = Path.Combine(_dir, "out.cbz");

        var result = await ArchivePageConverter.ConvertAsync(source, target,
            new PageConversionSettings(PageConversionFormat.Webp, PageConversionStop.High, false), CancellationToken.None);

        Assert.Equal(ArchiveConversionOutcome.NotSmaller, result.Outcome);
        Assert.Equal(1, result.Skipped[PageSkip.NotSmaller]);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task A_page_that_does_not_decode_leaves_the_file_for_the_health_scan()
    {
        var broken = Scan(600, 900, 9).EncodeJpeg(95)[..20_000];
        var source = Cbz("ch1.cbz", ("001.jpg", Scan(600, 900, 10).EncodeJpeg(95)), ("002.jpg", broken));
        var target = Path.Combine(_dir, "out.cbz");

        var result = await ArchivePageConverter.ConvertAsync(source, target, Webp, CancellationToken.None);

        Assert.Equal(ArchiveConversionOutcome.Unreadable, result.Outcome);
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".partial"));
    }

    [Fact]
    public async Task A_new_extension_that_collides_with_another_entry_is_refused()
    {
        var source = Cbz("ch1.cbz",
            ("001.jpg", Scan(600, 900, 11).EncodeJpeg(95)),
            ("001.webp", Scan(600, 900, 12).EncodeWebp(80)));
        var target = Path.Combine(_dir, "out.cbz");

        var result = await ArchivePageConverter.ConvertAsync(source, target, Webp, CancellationToken.None);

        Assert.Equal(ArchiveConversionOutcome.NamesConflict, result.Outcome);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task Volume_page_names_keep_their_chapter_markers_and_order()
    {
        var source = Cbz("v01.cbz",
            ("Series - c001 (v01) - p000.jpg", Scan(600, 900, 13).EncodeJpeg(95)),
            ("Series - c001 (v01) - p001.png", LineArt(900, 1300, 14).EncodePng()),
            ("Series - c002 (v01) - p002.jpg", Scan(600, 900, 15).EncodeJpeg(95)));
        var target = Path.Combine(_dir, "out.cbz");

        var result = await ArchivePageConverter.ConvertAsync(source, target, Webp with { PngLossless = true }, CancellationToken.None);

        Assert.Equal(ArchiveConversionOutcome.Converted, result.Outcome);
        Assert.Equal("mixed", result.SourceFormat);
        using var zip = ZipFile.OpenRead(target);
        Assert.Equal(
            ["Series - c001 (v01) - p000.webp", "Series - c001 (v01) - p001.webp", "Series - c002 (v01) - p002.webp"],
            CbzReader.PageNames(zip));
    }

    [Fact]
    public async Task An_entry_declaring_more_than_the_health_limit_is_refused_before_it_is_read()
    {
        var source = Cbz("ch1.cbz", ("001.jpg", Scan(600, 900, 16).EncodeJpeg(95)));
        var bytes = File.ReadAllBytes(source);
        // Past Array.MaxLength but under int.MaxValue: allocating it throws OutOfMemoryException anywhere.
        const uint declared = 0x7FFFFFF0;
        PatchUInt32(bytes, [0x50, 0x4B, 0x03, 0x04], 22, declared);
        PatchUInt32(bytes, [0x50, 0x4B, 0x01, 0x02], 24, declared);
        File.WriteAllBytes(source, bytes);
        var target = Path.Combine(_dir, "out.cbz");

        var result = await ArchivePageConverter.ConvertAsync(source, target, Webp, CancellationToken.None);

        Assert.Equal(ArchiveConversionOutcome.Unreadable, result.Outcome);
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".partial"));
    }

    private static void PatchUInt32(byte[] bytes, byte[] signature, int offset, uint value)
    {
        var at = bytes.AsSpan().IndexOf(signature);
        Assert.True(at >= 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + offset), value);
    }

    [Fact]
    public async Task A_page_over_the_pixel_limit_is_kept_without_being_decoded()
    {
        var huge = Scan(600, 900, 17).EncodeJpeg(95);
        var frame = huge.AsSpan().IndexOf(new byte[] { 0xFF, 0xC0 });
        if (frame < 0) frame = huge.AsSpan().IndexOf(new byte[] { 0xFF, 0xC2 });
        Assert.True(frame >= 0);
        // 8000 by 6000 is inside the per-side limit, but 48 million pixels is over the health scan's.
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(huge.AsSpan(frame + 5), 6000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(huge.AsSpan(frame + 7), 8000);

        var page = PageConversion.Convert(huge, ".jpg", Webp);

        Assert.Equal(PageSkip.TooLarge, page.Skipped);
        Assert.Same(huge, page.Bytes);

        var source = Cbz("ch1.cbz", ("001.jpg", Scan(600, 900, 18).EncodeJpeg(95)), ("002.jpg", huge));
        var result = await ArchivePageConverter.ConvertAsync(source, Path.Combine(_dir, "out.cbz"), Webp, CancellationToken.None);
        Assert.Equal(ArchiveConversionOutcome.Converted, result.Outcome);
        Assert.Equal(1, result.Skipped[PageSkip.TooLarge]);
    }
}
