using System.IO.Compression;
using Maki.Core.Parsing;
using Maki.Core.Reading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Maki.Core.Tests;

public class PdfReaderTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("maki-pdf-reader-").FullName;
    public void Dispose() => Directory.Delete(root, true);

    private string Pdf(int pages = 3) => PdfFixture.Write(Path.Combine(root, Guid.NewGuid() + ".pdf"), pages);

    private string Garbage()
    {
        var path = Path.Combine(root, Guid.NewGuid() + ".pdf");
        File.WriteAllText(path, "not a pdf at all");
        return path;
    }

    [Fact]
    public void Counts_pages_and_never_throws_on_rubbish()
    {
        Assert.Equal(3, PdfReader.PageCount(Pdf()));
        Assert.Equal(0, PdfReader.PageCount(Garbage()));
        Assert.Equal(0, PdfReader.PageCount(Path.Combine(root, "absent.pdf")));
    }

    [Fact]
    public void Renders_a_page_as_a_decodable_jpeg_keeping_its_aspect()
    {
        using var stream = PdfReader.RenderPage(Pdf(), 1);
        var info = Image.Identify(stream);
        Assert.Equal("JPEG", info.Metadata.DecodedImageFormat?.Name);
        Assert.True(Math.Max(info.Width, info.Height) <= PdfReader.MaxEdge);
        Assert.Equal(300d / 450d, (double)info.Width / info.Height, 2);

        // PDFium leaves unpainted area transparent; unflattened it would encode as black.
        stream.Position = 0;
        using var image = Image.Load<Rgb24>(stream);
        Assert.Equal(new Rgb24(255, 255, 255), image[image.Width / 2, image.Height / 2]);
    }

    [Fact]
    public void Rejects_an_index_outside_the_document()
    {
        var path = Pdf();
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfReader.RenderPage(path, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfReader.RenderPage(path, -1));
    }

    [Fact]
    public void Honours_max_edge()
    {
        using var stream = PdfReader.RenderPage(Pdf(), 0, 800);
        Assert.Equal(0, stream.Position);
        var info = Image.Identify(stream);
        Assert.Equal(800, Math.Max(info.Width, info.Height));
        Assert.Equal(300d / 450d, (double)info.Width / info.Height, 2);
    }

    [Fact]
    public async Task Renders_concurrently_without_tripping_over_pdfium()
    {
        var path = Pdf();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            await using var stream = await PdfReader.RenderPageAsync(path, i % 3);
            return Image.Identify(stream).Metadata.DecodedImageFormat?.Name;
        })));
        Assert.All(results, name => Assert.Equal("JPEG", name));
    }

    [Fact]
    public void Page_names_round_trip()
    {
        Assert.Equal("0001.jpg", PdfReader.PageName(0, 3));
        Assert.Equal("0003.jpg", PdfReader.PageName(2, 3));
        Assert.Equal("00010.jpg", PdfReader.PageName(9, 12000));

        Assert.True(PdfReader.TryParsePageIndex("0002.jpg", out var index));
        Assert.Equal(1, index);
        Assert.False(PdfReader.TryParsePageIndex("0000.jpg", out _));
        Assert.False(PdfReader.TryParsePageIndex("cover.jpg", out _));
        Assert.False(PdfReader.TryParsePageIndex("0002.png", out _));
        Assert.False(PdfReader.TryParsePageIndex("../secret.jpg", out _));

        for (var i = 0; i < 12; i++)
        {
            Assert.True(PdfReader.TryParsePageIndex(PdfReader.PageName(i, 12), out var parsed));
            Assert.Equal(i, parsed);
        }
    }

    [Fact]
    public void Reader_serves_pdf_pages_through_the_cbz_entry_points()
    {
        var path = Pdf();
        Assert.Equal(["0001.jpg", "0002.jpg", "0003.jpg"], CbzReader.PageNames(path));
        Assert.Equal("image/jpeg", CbzReader.ContentType("0002.jpg"));

        using var page = CbzReader.OpenPage(path, "0002.jpg");
        Assert.NotNull(page);
        Assert.Equal("JPEG", Image.Identify(page).Metadata.DecodedImageFormat?.Name);
        Assert.Null(CbzReader.OpenPage(path, "nope.jpg"));
        Assert.Empty(CbzReader.PageNames(Garbage()));
    }

    [Fact]
    public void Open_page_is_null_rather_than_throwing_for_any_unreadable_pdf_page()
    {
        Assert.Null(CbzReader.OpenPage(Pdf(), "0004.jpg"));
        Assert.Null(CbzReader.OpenPage(Garbage(), "0001.jpg"));
        Assert.Null(CbzReader.OpenPage(Path.Combine(root, "absent.pdf"), "0001.jpg"));
    }

    [Fact]
    public async Task Open_page_async_round_trips_both_formats()
    {
        await using var pdfPage = await CbzReader.OpenPageAsync(Pdf(), "0002.jpg");
        Assert.NotNull(pdfPage);
        Assert.Equal("JPEG", Image.Identify(pdfPage).Metadata.DecodedImageFormat?.Name);

        Assert.Null(await CbzReader.OpenPageAsync(Pdf(), "0009.jpg"));
        Assert.Null(await CbzReader.OpenPageAsync(Garbage(), "0001.jpg"));

        var cbz = Path.Combine(root, "z.cbz");
        using (var zip = ZipFile.Open(cbz, ZipArchiveMode.Create))
        using (var entry = zip.CreateEntry("001.jpg").Open())
            entry.Write([1, 2, 3]);

        await using var cbzPage = await CbzReader.OpenPageAsync(cbz, "001.jpg");
        Assert.NotNull(cbzPage);
        Assert.Null(await CbzReader.OpenPageAsync(cbz, "002.jpg"));
    }

    [Fact]
    public void Chapter_scanner_finds_no_markers_in_a_pdf()
    {
        var path = Pdf();
        Assert.Empty(VolumeChapterScanner.ScanCbz(path));
        var (total, boundaries) = VolumeChapterScanner.ScanCbzBoundaries(path);
        Assert.Equal(3, total);
        Assert.Empty(boundaries);
    }

    [Fact]
    public void Comic_file_classifies_by_extension()
    {
        Assert.True(ComicFile.IsComic("a/b/c.CBZ"));
        Assert.True(ComicFile.IsComic("a/b/c.pdf"));
        Assert.False(ComicFile.IsComic("a/b/c.cbr"));
        Assert.True(ComicFile.IsPdf("x.PDF"));
        Assert.False(ComicFile.IsPdf("x.cbz"));
        Assert.True(ComicFile.IsCbz("x.cbz"));
    }

    [Fact]
    public async Task Health_analysis_reads_a_pdf_in_both_layers()
    {
        var path = Pdf();
        var index = await ArchiveHealthAnalyzer.AnalyzeAsync(path);
        Assert.Equal("complete", index.Status);
        Assert.Empty(index.Problems);
        Assert.Equal(3, index.Pages.Count);
        Assert.Equal("0001.jpg", index.Pages[0].Name);
        Assert.Null(index.Hash);
        Assert.All(index.Pages, p => Assert.Null(p.RawHash));

        var verified = await ArchiveHealthAnalyzer.AnalyzeAsync(path, default, null, null, verify: true);
        Assert.Equal("complete", verified.Status);
        Assert.Empty(verified.Problems);
        Assert.True(verified.Verified);
        Assert.NotNull(verified.Hash);
        Assert.Equal(3, verified.Pages.Count);
        Assert.All(verified.Pages, p => Assert.NotNull(p.RawHash));
        Assert.All(verified.Pages, p => Assert.Equal(PdfReader.FingerprintEdge, Math.Max(p.Width, p.Height)));
    }

    [Fact]
    public async Task Health_analysis_reports_a_broken_pdf()
    {
        Assert.Contains((await ArchiveHealthAnalyzer.AnalyzeAsync(Garbage())).Problems, p => p.Kind == "corrupt");

        var empty = Path.Combine(root, "empty.pdf");
        await File.WriteAllBytesAsync(empty, []);
        Assert.Contains((await ArchiveHealthAnalyzer.AnalyzeAsync(empty)).Problems, p => p.Kind == "empty");

        Assert.Contains((await ArchiveHealthAnalyzer.AnalyzeAsync(Path.Combine(root, "gone.pdf"))).Problems,
            p => p.Kind == "missing");
    }
}
