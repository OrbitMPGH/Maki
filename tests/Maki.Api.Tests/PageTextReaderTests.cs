using Maki.Api.Services.Ocr;
using Maki.Core.Images;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// The bundled OCR models on two invented pages. Text is compared loosely: libvips and Pillow (which
/// the reference pipeline used) resample a little differently.
/// </summary>
public sealed class PageTextReaderTests : IDisposable
{
    private readonly PageTextReader _reader = new(
        NullLogger<PageTextReader>.Instance, Path.Combine(AppContext.BaseDirectory, PageTextReader.FolderName));

    public void Dispose() => _reader.Dispose();

    private static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ocr", name));

    [Fact]
    public async Task Reads_a_credit_page_including_its_slanted_line()
    {
        var text = await _reader.ReadAsync(Fixture("ocr-credit-page.png"), 1, default);

        Assert.NotNull(text);
        Assert.Contains("discord", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("recruiting", text, StringComparison.OrdinalIgnoreCase);
        Assert.True(CreditText.IsScanlator(text, colour: true));
    }

    [Fact]
    public async Task Reads_a_colophon_as_a_publishers_page()
    {
        var text = await _reader.ReadAsync(Fixture("ocr-colophon.png"), 1, default);

        Assert.NotNull(text);
        Assert.Contains("isbn", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("all rights reserved", text, StringComparison.OrdinalIgnoreCase);
        Assert.False(CreditText.IsScanlator(text, colour: false));
    }

    [Fact]
    public async Task Reads_only_the_share_of_the_page_it_is_given()
    {
        var text = await _reader.ReadAsync(Fixture("ocr-credit-page.png"), 0.2, default);

        Assert.NotNull(text);
        Assert.Contains("moonlit", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("discord", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_page_that_is_not_an_image_reads_as_nothing()
    {
        Assert.Null(await _reader.ReadAsync([1, 2, 3, 4], 1, default));
        Assert.True(_reader.Available);
    }

    [Fact]
    public async Task Without_the_models_the_reader_is_not_available()
    {
        using var missing = new PageTextReader(
            NullLogger<PageTextReader>.Instance, Path.Combine(Path.GetTempPath(), "maki-no-ocr-" + Guid.NewGuid().ToString("N")));

        Assert.Null(await missing.ReadAsync(Fixture("ocr-credit-page.png"), 1, default));
        Assert.False(missing.Available);
    }

    [Fact]
    public async Task Idle_models_are_released_and_load_again_on_the_next_read()
    {
        await _reader.ReadAsync(Fixture("ocr-credit-page.png"), 0.2, default);

        Assert.True(_reader.ReleaseIfIdle(TimeSpan.Zero));
        Assert.False(_reader.ReleaseIfIdle(TimeSpan.Zero));
        Assert.Contains("moonlit", await _reader.ReadAsync(Fixture("ocr-credit-page.png"), 0.2, default) ?? "", StringComparison.OrdinalIgnoreCase);
    }
}
