using Maki.Core.Download;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace Maki.Core.Tests;

/// <summary>
/// The gate between a downloaded page and the CBZ it gets packaged into. Worth testing directly:
/// a false negative fails a whole chapter over one file, and a false positive ships a corrupt
/// archive that only surfaces when somebody opens it months later.
/// </summary>
public class ImageValidatorTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("maki-imgvalidator").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string WritePng(string name, int width, int height)
    {
        var path = Path.Combine(dir, name);
        using var image = new Image<Rgba32>(width, height);
        image.Save(path, new PngEncoder());
        return path;
    }

    /// <summary>
    /// The regression this class exists for. Some sources pad a chapter with separator pages that
    /// encode to well under the old 128-byte floor; rejecting them on length alone failed the whole
    /// download over a file that decodes perfectly well.
    /// </summary>
    [Fact]
    public async Task Accepts_a_tiny_but_decodable_image()
    {
        var path = WritePng("separator.png", 1, 1);

        Assert.True(new FileInfo(path).Length < 128);
        Assert.True(await ImageValidator.IsValidImageAsync(path));
    }

    [Fact]
    public async Task Accepts_an_ordinary_page()
    {
        Assert.True(await ImageValidator.IsValidImageAsync(WritePng("page.png", 800, 1200)));
    }

    /// <summary>
    /// Dropping the length floor must not soften what "invalid" means: these are the shapes a
    /// failed download actually takes, and each one has to keep failing.
    /// </summary>
    [Fact]
    public async Task Rejects_an_empty_file()
    {
        Assert.False(await ImageValidator.IsValidImageAsync(Write("empty.png", [])));
    }

    [Fact]
    public async Task Rejects_a_file_too_short_to_carry_magic_bytes()
    {
        Assert.False(await ImageValidator.IsValidImageAsync(Write("stub.png", [0x89, 0x50])));
    }

    [Fact]
    public async Task Rejects_an_html_error_page_served_with_an_image_name()
    {
        var body = "<!doctype html><html><body>404 not found</body></html>"u8.ToArray();

        Assert.False(await ImageValidator.IsValidImageAsync(Write("page.jpg", body)));
    }

    /// <summary>Right magic, no decodable image behind it — a truncated or interrupted download.</summary>
    [Fact]
    public async Task Rejects_a_truncated_png()
    {
        var full = await File.ReadAllBytesAsync(WritePng("full.png", 200, 200));

        Assert.False(await ImageValidator.IsValidImageAsync(Write("cut.png", full[..40])));
    }

    [Fact]
    public async Task Rejects_a_missing_file()
    {
        Assert.False(await ImageValidator.IsValidImageAsync(Path.Combine(dir, "absent.png")));
    }

    /// <summary>
    /// AVIF is trusted on container magic alone (ImageSharp cannot identify it), so the length floor
    /// still applies there — it is the only thing standing in for a decode.
    /// </summary>
    [Fact]
    public async Task Rejects_a_short_avif_header_with_nothing_behind_it()
    {
        byte[] header = [0, 0, 0, 0x20, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'a', (byte)'v', (byte)'i', (byte)'f'];

        Assert.False(await ImageValidator.IsValidImageAsync(Write("short.avif", header)));
    }

    [Fact]
    public async Task Accepts_an_avif_container_of_plausible_length()
    {
        byte[] header = [0, 0, 0, 0x20, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'a', (byte)'v', (byte)'i', (byte)'f'];
        var file = new byte[256];
        header.CopyTo(file, 0);

        Assert.True(await ImageValidator.IsValidImageAsync(Write("ok.avif", file)));
    }
}
