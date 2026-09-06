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
    private static byte[] Png(PngCompressionLevel level = PngCompressionLevel.DefaultCompression)
    {
        using var image = new Image<Rgba32>(32, 48);
        for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++) image[x,y] = new Rgba32((byte)(x*7), (byte)(y*5), (byte)((x+y)*3));
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
        var result = await ArchiveHealthAnalyzer.AnalyzeAsync(Archive(("z.png",Png(level:PngCompressionLevel.Level1)),("a.png",Png(level:PngCompressionLevel.Level9))), default, null, null, deep: true);
        Assert.Equal("complete",result.Status); Assert.Empty(result.Problems);
        Assert.Equal("a.png",result.Pages[0].Name);
        Assert.Equal(2,result.Pages.Count);
    }
    [Fact] public async Task Verify_reads_headers_without_decoding_pages()
    {
        var path = Archive(("1.png",Png()),("2.png",Png()));
        var verify = await ArchiveHealthAnalyzer.AnalyzeAsync(path);
        Assert.False(verify.Deep);
        Assert.Equal("complete",verify.Status);
        // Dimensions come from the header, so they are known either way; pixels are not.
        Assert.Equal(2,verify.Pages.Count);
        Assert.All(verify.Pages,p=>Assert.Equal(32,p.Width));
        var deep = await ArchiveHealthAnalyzer.AnalyzeAsync(path, default, null, null, deep: true);
        Assert.True(deep.Deep);
        Assert.Equal("complete",deep.Status);
    }
    [Fact] public async Task Only_a_deep_analysis_sees_damage_behind_a_valid_header()
    {
        // One flipped byte inside the compressed image data. The header still reads 32x48, so
        // verify has nothing to complain about; the pixels behind it will not come out. Truncating
        // instead would not test this - ImageSharp reads far enough that even Identify fails.
        var damaged = Png().Select((b,i) => i == 60 ? (byte)(b ^ 0xFF) : b).ToArray();
        var path = Archive(("1.png",damaged));
        Assert.DoesNotContain((await ArchiveHealthAnalyzer.AnalyzeAsync(path)).Problems,p=>p.Kind=="damagedImage");
        Assert.Contains((await ArchiveHealthAnalyzer.AnalyzeAsync(path, default, null, null, deep: true)).Problems,p=>p.Kind=="damagedImage");
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
