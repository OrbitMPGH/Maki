using System.IO.Compression;
using Maki.Core.Images;
using Maki.Core.Reading;
namespace Maki.Core.Tests;
public class ArchiveHealthAnalyzerTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("maki-health-analyzer-").FullName;
    public void Dispose() => Directory.Delete(root, true);
    private string Archive(params (string Name, byte[] Bytes)[] entries)
    {
        var path = Path.Combine(root, Guid.NewGuid() + ".cbz");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, bytes) in entries) { using var stream = zip.CreateEntry(name).Open(); stream.Write(bytes); }
        return path;
    }
    private static byte[] Png(int compression = 6)
    {
        var image = new Raster(32, 48);
        for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++) image[x,y] = new Rgba((byte)(x*7), (byte)(y*5), (byte)((x+y)*3));
        return image.EncodePng(compression);
    }
    /// <summary>A 24-bit BMP written by hand: libvips has no BMP saver.</summary>
    private static byte[] Bmp(int width, int height)
    {
        var stride = (width * 3 + 3) & ~3;
        var bytes = new byte[54 + stride * height];
        bytes[0] = (byte)'B'; bytes[1] = (byte)'M';
        BitConverter.GetBytes(bytes.Length).CopyTo(bytes, 2);
        BitConverter.GetBytes(54).CopyTo(bytes, 10);
        BitConverter.GetBytes(40).CopyTo(bytes, 14);
        BitConverter.GetBytes(width).CopyTo(bytes, 18);
        BitConverter.GetBytes(height).CopyTo(bytes, 22);
        BitConverter.GetBytes((short)1).CopyTo(bytes, 26);
        BitConverter.GetBytes((short)24).CopyTo(bytes, 28);
        return bytes;
    }
    [Fact] public async Task Detects_empty_and_missing_files()
    {
        var path = Path.Combine(root,"empty.cbz"); await File.WriteAllBytesAsync(path, []);
        var empty = await ArchiveHealthAnalyzer.AnalyzeAsync(path, default, null, null, verify: true);
        Assert.Contains(empty.Problems,p=>p.Kind=="empty"); Assert.NotNull(empty.Hash);
        File.Delete(path); Assert.Contains((await ArchiveHealthAnalyzer.AnalyzeAsync(path)).Problems,p=>p.Kind=="missing");
    }
    [Fact] public async Task Detects_invalid_zip_and_empty_page_catalog()
    {
        var path = Path.Combine(root,"bad.cbz"); await File.WriteAllTextAsync(path,"broken");
        Assert.Contains((await ArchiveHealthAnalyzer.AnalyzeAsync(path)).Problems,p=>p.Kind=="corrupt");
        Assert.Contains((await ArchiveHealthAnalyzer.AnalyzeAsync(Archive(("ComicInfo.xml", "<ComicInfo/>"u8.ToArray())))).Problems,p=>p.Kind=="noPages");
    }
    [Fact] public async Task Reencoded_pages_match_decoded_hash_and_reader_order()
    {
        var result = await ArchiveHealthAnalyzer.AnalyzeAsync(Archive(("z.png",Png(compression:1)),("a.png",Png(compression:9))), default, null, null, verify: true);
        Assert.Equal("complete",result.Status); Assert.Empty(result.Problems);
        Assert.Equal("a.png",result.Pages[0].Name);
        Assert.Equal(2,result.Pages.Count);
    }
    // Regression: page order used to sort lexically, so an unpadded archive's 10.png reported
    // right after 1.png, disagreeing with what CbzReader/VolumeChapterScanner actually read.
    [Fact] public async Task Page_order_is_natural_not_lexical()
    {
        var path = Archive(("10.png",Png()),("1.png",Png()),("2.png",Png()));
        var index = await ArchiveHealthAnalyzer.AnalyzeAsync(path);
        Assert.Equal(["1.png","2.png","10.png"], index.Pages.Select(p=>p.Name));
        var verified = await ArchiveHealthAnalyzer.AnalyzeAsync(path, default, null, null, verify: true);
        Assert.Equal(["1.png","2.png","10.png"], verified.Pages.Select(p=>p.Name));
    }
    [Fact] public async Task Indexing_takes_the_archive_at_its_word()
    {
        var path = Archive(("1.png",Png()),("2.png",Png()));
        var index = await ArchiveHealthAnalyzer.AnalyzeAsync(path);
        Assert.False(index.Verified);
        Assert.Equal("complete",index.Status);
        // The directory names the pages and orders them; it says nothing about their contents.
        Assert.Equal(2,index.Pages.Count);
        Assert.Null(index.Hash);
        Assert.All(index.Pages,p=>Assert.Null(p.RawHash));
        Assert.All(index.Pages,p=>Assert.Equal(0,p.Width));

        var verified = await ArchiveHealthAnalyzer.AnalyzeAsync(path, default, null, null, verify: true);
        Assert.True(verified.Verified);
        Assert.NotNull(verified.Hash);
        Assert.All(verified.Pages,p=>Assert.NotNull(p.RawHash));
        Assert.All(verified.Pages,p=>Assert.Equal(32,p.Width));
    }
    [Fact] public async Task Indexing_still_catches_an_archive_that_is_not_one()
    {
        // The case that matters most and costs least: not a zip at all.
        var path = Path.Combine(root,"broken.cbz");
        await File.WriteAllTextAsync(path,"this is not a zip");
        Assert.Contains((await ArchiveHealthAnalyzer.AnalyzeAsync(path)).Problems,p=>p.Kind=="corrupt");
    }
    [Fact] public async Task Only_a_verify_catches_bytes_that_rotted_on_disk()
    {
        // Stored uncompressed, so the page sits verbatim in the file and one flipped byte leaves
        // the checksum the archive recorded for that entry pointing at content that is no longer
        // there. This is the whole reason verify reads anything: the directory still describes a
        // perfectly good archive.
        var page = Png();
        var path = Path.Combine(root, Guid.NewGuid() + ".cbz");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var stream = zip.CreateEntry("1.png", CompressionLevel.NoCompression).Open();
            stream.Write(page);
        }
        var bytes = await File.ReadAllBytesAsync(path);
        var at = Enumerable.Range(0, bytes.Length - page.Length)
            .First(i => bytes.Skip(i).Take(page.Length).SequenceEqual(page));
        bytes[at + 40] ^= 0xFF;
        await File.WriteAllBytesAsync(path, bytes);

        Assert.Empty((await ArchiveHealthAnalyzer.AnalyzeAsync(path)).Problems);
        Assert.Contains((await ArchiveHealthAnalyzer.AnalyzeAsync(path, default, null, null, verify: true)).Problems,p=>p.Kind=="corrupt");
    }
    [Fact] public async Task Unsupported_avif_is_partial_not_corrupt()
    {
        var result = await ArchiveHealthAnalyzer.AnalyzeAsync(Archive(("1.avif",new byte[256])), default, null, null, verify: true);
        Assert.Equal("partial",result.Status); Assert.NotNull(result.Hash);
        Assert.All(result.Problems,p=>Assert.Equal("info",p.Severity));
    }
    [Fact] public async Task Damaged_image_is_reported()
    {
        var archive = Archive(("1.png","broken"u8.ToArray()));
        // The index takes the name at its word; only reading the bytes shows it is not an image.
        Assert.Empty((await ArchiveHealthAnalyzer.AnalyzeAsync(archive)).Problems);
        var result = await ArchiveHealthAnalyzer.AnalyzeAsync(archive, default, null, null, verify: true);
        Assert.Contains(result.Problems,p=>p.Kind=="damagedImage");
    }
    [Fact] public async Task A_truncated_header_is_reported_not_thrown()
    {
        // Every cut of a BMP, JPEG and WebP header must come back as a page, damaged or not,
        // never as an exception that aborts the archive's analysis.
        var image = new Raster(40, 30);
        foreach (var (name, good) in new[] { ("1.bmp", Bmp(40, 30)), ("1.jpg", image.EncodeJpeg(75)), ("1.webp", image.EncodeWebp(75)) })
        {
            for (var cut = 4; cut < Math.Min(good.Length, 120); cut += 2)
            {
                var result = await ArchiveHealthAnalyzer.AnalyzeAsync(Archive((name, good[..cut])), default, null, null, verify: true);
                Assert.Single(result.Pages);
            }
        }
    }
    [Fact] public async Task Too_many_entries_are_incomplete_not_corrupt()
    {
        var path = Archive(Enumerable.Range(0,10001).Select(i=>($"{i}.txt",Array.Empty<byte>())).ToArray());
        var result = await ArchiveHealthAnalyzer.AnalyzeAsync(path);
        Assert.Equal("partial",result.Status); Assert.DoesNotContain(result.Problems,p=>p.Kind=="corrupt");
    }
    [Fact] public async Task Cancellation_propagates()
    {
        using var ct = new CancellationTokenSource(); ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>ArchiveHealthAnalyzer.AnalyzeAsync(Archive(("1.png",Png())),ct.Token));
    }
}
