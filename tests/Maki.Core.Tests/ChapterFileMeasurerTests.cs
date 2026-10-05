using System.IO.Compression;
using Maki.Core.Quality;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace Maki.Core.Tests;

public class ChapterFileMeasurerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("maki-chapter-measurer-").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    private static byte[] Png(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height);
        using var stream = new MemoryStream();
        image.Save(stream, new PngEncoder());
        return stream.ToArray();
    }

    private static byte[] Jpeg(int width, int height)
    {
        using var image = new Image<Rgb24>(width, height);
        using var stream = new MemoryStream();
        image.Save(stream, new JpegEncoder());
        return stream.ToArray();
    }

    // Not a decodable image, just enough of an ISO base media "ftyp" box to sniff as AVIF:
    // 4 filler bytes, "ftyp", then the "avif" brand, then more filler.
    private static byte[] AvifStub() => "....ftypavif...."u8.ToArray();

    private string WriteZip(string name, params (string Entry, byte[] Bytes)[] entries)
    {
        var path = Path.Combine(_root, name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, bytes) in entries)
        {
            using var stream = archive.CreateEntry(entry).Open();
            stream.Write(bytes);
        }

        return path;
    }

    [Fact]
    public void Measures_pages_already_in_memory()
    {
        var pages = new List<(string Name, byte[] Bytes)>
        {
            ("001.png", Png(100, 200)),
            ("002.png", Png(120, 220))
        };

        var result = ChapterFileMeasurer.Measure(pages);

        Assert.Equal(2, result.PageCount);
        Assert.Equal(110, result.MedianWidth);
        Assert.Equal(210, result.MedianHeight);
        Assert.Equal("png", result.ImageFormat);
    }

    [Fact]
    public void Mixed_formats_report_mixed()
    {
        var pages = new List<(string Name, byte[] Bytes)>
        {
            ("001.png", Png(100, 200)),
            ("002.jpg", Jpeg(100, 200))
        };

        var result = ChapterFileMeasurer.Measure(pages);

        Assert.Equal("mixed", result.ImageFormat);
    }

    [Fact]
    public void A_corrupt_page_is_skipped_rather_than_thrown()
    {
        var pages = new List<(string Name, byte[] Bytes)>
        {
            ("001.png", Png(100, 200)),
            ("002.png", "not an image at all"u8.ToArray())
        };

        var result = ChapterFileMeasurer.Measure(pages);

        Assert.Equal(2, result.PageCount);
        Assert.Equal(100, result.MedianWidth);
        Assert.Equal(200, result.MedianHeight);
        Assert.Equal("png", result.ImageFormat);
    }

    [Fact]
    public void An_avif_page_reports_its_format_with_no_dimensions()
    {
        var pages = new List<(string Name, byte[] Bytes)> { ("001.avif", AvifStub()) };

        var result = ChapterFileMeasurer.Measure(pages);

        Assert.Equal(1, result.PageCount);
        Assert.Null(result.MedianWidth);
        Assert.Null(result.MedianHeight);
        Assert.Equal("avif", result.ImageFormat);
    }

    [Fact]
    public void Samples_evenly_across_a_larger_archive_but_reports_the_full_page_count()
    {
        // Five pages of five different widths; a sample of 2 must not read all of them, so a
        // median over every page (120) would disagree with a median over just the two sampled.
        var path = WriteZip("five.cbz",
            ("001.png", Png(100, 100)),
            ("002.png", Png(110, 100)),
            ("003.png", Png(120, 100)),
            ("004.png", Png(130, 100)),
            ("005.png", Png(140, 100)));

        var result = ChapterFileMeasurer.MeasureArchive(path, sampleSize: 2, CancellationToken.None);

        Assert.Equal(5, result.PageCount);
        // Evenly spread indices for 5 pages sampled at 2 land on 001 and 003: widths 100 and 120.
        Assert.Equal(110, result.MedianWidth);
        Assert.Equal("png", result.ImageFormat);
    }

    [Fact]
    public void Reads_every_page_when_sample_size_is_not_positive()
    {
        var path = WriteZip("three.cbz",
            ("001.png", Png(100, 100)),
            ("002.png", Png(200, 100)),
            ("003.png", Png(300, 100)));

        var result = ChapterFileMeasurer.MeasureArchive(path, sampleSize: 0, CancellationToken.None);

        Assert.Equal(3, result.PageCount);
        Assert.Equal(200, result.MedianWidth);
    }

    [Fact]
    public void A_pdf_reports_its_page_count_and_no_dimensions()
    {
        var path = PdfFixture.Write(Path.Combine(_root, "book.pdf"), 4);

        var result = ChapterFileMeasurer.MeasureArchive(path, sampleSize: 0, CancellationToken.None);

        Assert.Equal(4, result.PageCount);
        Assert.Null(result.MedianWidth);
        Assert.Null(result.MedianHeight);
        Assert.Equal("pdf", result.ImageFormat);
    }

    [Fact]
    public void An_unrecognised_extension_is_reported_rather_than_thrown()
    {
        var path = Path.Combine(_root, "notes.txt");
        File.WriteAllText(path, "not a comic");

        var result = ChapterFileMeasurer.MeasureArchive(path, sampleSize: 0, CancellationToken.None);

        Assert.Equal(0, result.PageCount);
        Assert.Equal("unknown", result.ImageFormat);
    }

    [Fact]
    public void An_unreadable_archive_is_reported_rather_than_thrown()
    {
        var path = Path.Combine(_root, "corrupt.cbz");
        File.WriteAllText(path, "not a zip at all");

        var result = ChapterFileMeasurer.MeasureArchive(path, sampleSize: 0, CancellationToken.None);

        Assert.Equal(0, result.PageCount);
        Assert.Equal("unknown", result.ImageFormat);
    }

    [Fact]
    public void A_locked_archive_throws_rather_than_reading_as_corrupt()
    {
        var path = WriteZip("locked.cbz", ("001.png", Png(50, 50)));

        using var holder = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.ThrowsAny<IOException>(() => ChapterFileMeasurer.MeasureArchive(path, 0, CancellationToken.None));
    }

    [Fact]
    public void A_zip_of_undecodable_pages_reports_its_page_count_and_unknown_format()
    {
        var path = WriteZip("garbage.cbz",
            ("001.png", "not an image"u8.ToArray()),
            ("002.png", "nor this one"u8.ToArray()));

        var result = ChapterFileMeasurer.MeasureArchive(path, 0, CancellationToken.None);

        Assert.Equal(2, result.PageCount);
        Assert.Null(result.MedianWidth);
        Assert.Equal("unknown", result.ImageFormat);
    }

    [Theory]
    [InlineData("png")]
    [InlineData("jpg")]
    [InlineData("webp")]
    public void A_page_larger_than_the_header_budget_is_still_measured(string format)
    {
        // Noise does not compress, so each page runs well past the bytes MeasureArchive reads.
        using var image = new Image<Rgba32>(900, 1300);
        var random = new Random(7);
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                foreach (ref var pixel in rows.GetRowSpan(y))
                {
                    pixel = new Rgba32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
                }
            }
        });
        using var encoded = new MemoryStream();
        IImageEncoder encoder = format switch
        {
            "png" => new PngEncoder(),
            "jpg" => new JpegEncoder { Quality = 95 },
            _ => new WebpEncoder { FileFormat = WebpFileFormatType.Lossless }
        };
        image.Save(encoded, encoder);
        Assert.True(encoded.Length > ChapterFileMeasurer.HeaderBytes);
        var path = WriteZip("large.cbz", ($"001.{format}", encoded.ToArray()));

        var result = ChapterFileMeasurer.MeasureArchive(path, 0, CancellationToken.None);

        Assert.Equal(900, result.MedianWidth);
        Assert.Equal(1300, result.MedianHeight);
        Assert.Equal(format, result.ImageFormat);
    }

    [Fact]
    public void Measuring_an_archive_does_not_hold_the_file_open()
    {
        var path = WriteZip("movable.cbz", ("001.png", Png(50, 50)));

        ChapterFileMeasurer.MeasureArchive(path, sampleSize: 0, CancellationToken.None);

        var moved = Path.Combine(_root, "moved.cbz");
        File.Move(path, moved);
        Assert.True(File.Exists(moved));
    }
}
