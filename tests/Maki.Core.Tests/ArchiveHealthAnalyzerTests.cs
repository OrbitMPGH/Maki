using System.IO.Compression;
using Maki.Core.Reading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Formats.Png;
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
    /// <param name="shade">Shifts every pixel, producing a page that looks alike but is not identical.</param>
    private static byte[] Png(bool blank = false, PngCompressionLevel level = PngCompressionLevel.DefaultCompression, byte shade = 0)
    {
        using var image = new Image<Rgba32>(32, 48);
        for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++) image[x,y] = blank ? new Rgba32(255,255,255) : new Rgba32((byte)(x*7+shade), (byte)(y*5), (byte)((x+y)*3));
        using var bytes = new MemoryStream(); image.Save(bytes, new PngEncoder { CompressionLevel = level }); return bytes.ToArray();
    }
    [Fact] public async Task Detects_empty_and_missing_files()
    {
        var path = Path.Combine(root,"empty.cbz"); await File.WriteAllBytesAsync(path, []);
        var empty = await ArchiveHealthAnalyzer.AnalyzeAsync(path);
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
        var result = await ArchiveHealthAnalyzer.AnalyzeAsync(Archive(("z.png",Png(level:PngCompressionLevel.Level1)),("a.png",Png(level:PngCompressionLevel.Level9))));
        Assert.Equal("complete",result.Status); Assert.Empty(result.Problems);
        Assert.Equal("a.png",result.Pages[0].Name);
        Assert.Equal(result.Pages[0].PixelHash,result.Pages[1].PixelHash);
        Assert.Equal("exact",Assert.Single(result.Groups).Kind);
    }
    [Fact] public async Task Blank_pages_are_separate_from_content_repetition()
    {
        var result = await ArchiveHealthAnalyzer.AnalyzeAsync(Archive(("1.png",Png(true)),("2.png",Png(true))));
        Assert.Equal("blank",Assert.Single(result.Groups).Kind);
    }
    [Fact] public async Task Blank_pages_are_one_group_however_many_there_are()
    {
        var result = await ArchiveHealthAnalyzer.AnalyzeAsync(Archive(
            ("1.png",Png(true)),("2.png",Png(true)),("3.png",Png(true)),("4.png",Png(true))));
        var blank = Assert.Single(result.Groups);
        Assert.Equal("blank",blank.Kind);
        // Four blank pages are one fact about four pages, not the six pairs they combine into.
        Assert.Equal(new[]{0,1,2,3},blank.Pages);
    }
    [Fact] public async Task Pages_that_merely_look_alike_are_not_repetition()
    {
        // A volume's chapter dividers differ by a printed number and nothing else, which is the
        // same picture to a perceptual hash. Only provably identical pages count.
        var result = await ArchiveHealthAnalyzer.AnalyzeAsync(Archive(
            ("1.png",Png(shade:200)),("2.png",Png(shade:201)),("3.png",Png(shade:202))));
        Assert.Empty(result.Groups);
    }
    [Fact] public async Task Repeated_content_pages_collapse_into_one_set()
    {
        var page = Png();
        var result = await ArchiveHealthAnalyzer.AnalyzeAsync(Archive(("1.png",page),("2.png",page),("3.png",page)));
        var group = Assert.Single(result.Groups);
        Assert.Equal("exact",group.Kind);
        Assert.Equal(new[]{0,1,2},group.Pages);
    }
    [Fact] public async Task Unsupported_avif_is_partial_not_corrupt()
    {
        var result = await ArchiveHealthAnalyzer.AnalyzeAsync(Archive(("1.avif",new byte[256])));
        Assert.Equal("partial",result.Status); Assert.NotNull(result.Hash);
        Assert.DoesNotContain(result.Problems,p=>p.Severity=="error");
    }
    [Fact] public async Task Damaged_image_is_reported()
    {
        var result = await ArchiveHealthAnalyzer.AnalyzeAsync(Archive(("1.png","broken"u8.ToArray())));
        Assert.Contains(result.Problems,p=>p.Kind=="damagedImage");
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
