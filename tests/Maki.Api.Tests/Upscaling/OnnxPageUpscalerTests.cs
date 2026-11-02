using Maki.Api.Configuration;
using Maki.Api.Services.Upscaling;
using Maki.Core.Configuration;
using Maki.Core.Images;
using Maki.Core.Images.Upscaling;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests.Upscaling;

/// <summary>
/// The identity fixture is a nearest-neighbour 2× with no learned weights, installed under the SPAN-S
/// file name: SPAN-S is the catalogue's plain 2× entry, and the upscaler takes the tensor type from
/// the model file rather than the catalogue, so the float32 and float16 fixtures both run there.
/// </summary>
public class OnnxPageUpscalerTests : IDisposable
{
    private readonly string _configDir = Path.Combine(Path.GetTempPath(), "maki-upscaler-" + Guid.NewGuid().ToString("N"));
    private readonly FakeAppSettings _settings = new FakeAppSettings().Set(SettingKeys.UpscalingThreads, "2");
    private readonly UpscaleModelCatalog _catalog = new();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_configDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private OnnxPageUpscaler NewUpscaler(string fixture = "identity-2x.onnx", UpscaleModelId? installAs = null)
    {
        var models = Path.Combine(_configDir, "models", "upscale");
        Directory.CreateDirectory(models);
        var info = _catalog.Get(installAs ?? UpscaleModelId.SpanS);
        File.Copy(Path.Combine(Path.GetDirectoryName(UpscaleFixture.IdentityModelPath)!, fixture),
            Path.Combine(models, info.FileName), overwrite: true);
        var store = new UpscaleModelStore(new NoHttp(), new AppPaths(_configDir), _settings, _catalog,
            NullLogger<UpscaleModelStore>.Instance);
        return new OnnxPageUpscaler(_catalog, store, _settings, NullLogger<OnnxPageUpscaler>.Instance);
    }

    private static Raster Pattern(int width, int height, bool grey = false)
    {
        var image = new Raster(width, height, bands: 3);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var v = (byte)((x * 7 + y * 13 + (x * y) % 17) % 256);
            image[x, y] = grey ? new Rgba(v, v, v) : new Rgba(v, (byte)(x % 256), (byte)((y * 3) % 256));
        }

        return image;
    }

    private static void AssertNearest2x(Raster source, OnnxPageUpscaler.StitchedPage page)
    {
        Assert.Equal((source.Width * 2, source.Height * 2), (page.Width, page.Height));
        for (var y = 0; y < page.Height; y++)
        for (var x = 0; x < page.Width; x++)
        {
            var expected = source[x / 2, y / 2];
            var o = (y * page.Width + x) * 3;
            if (page.Rgb[o] != expected.R || page.Rgb[o + 1] != expected.G || page.Rgb[o + 2] != expected.B)
            {
                Assert.Fail($"Pixel ({x}, {y}) is ({page.Rgb[o]}, {page.Rgb[o + 1]}, {page.Rgb[o + 2]}), expected {expected}.");
            }
        }
    }

    [Theory]
    [InlineData("identity-2x.onnx")]
    [InlineData("identity-2x-fp16.onnx")]
    public void A_tiled_run_equals_a_whole_page_nearest_2x_with_no_seams(string fixture)
    {
        using var upscaler = NewUpscaler(fixture);
        var source = Pattern(900, 1300);
        var tiles = 0;
        upscaler.TileCompleted = _ => tiles++;

        var page = upscaler.Run(source, _catalog.Get(UpscaleModelId.SpanS), threads: 2, CancellationToken.None);

        Assert.Equal(12, tiles);
        AssertNearest2x(source, page);
    }

    [Fact]
    public void A_4x_model_is_halved_per_tile_like_halving_the_whole_page()
    {
        using var upscaler = NewUpscaler("identity-4x.onnx", UpscaleModelId.RealEsrganAv3);
        var source = Pattern(900, 1300);
        var whole = new byte[source.Width * 4 * source.Height * 4 * 3];
        for (var y = 0; y < source.Height * 4; y++)
        for (var x = 0; x < source.Width * 4; x++)
        {
            var p = source[x / 4, y / 4];
            var o = (y * source.Width * 4 + x) * 3;
            (whole[o], whole[o + 1], whole[o + 2]) = (p.R, p.G, p.B);
        }

        var (expected, width, height) = ImageCodec.ResizeRgb(whole, source.Width * 4, source.Height * 4, 0.5);

        var page = upscaler.Run(source, _catalog.Get(UpscaleModelId.RealEsrganAv3), threads: 2, CancellationToken.None);

        Assert.Equal((width, height), (page.Width, page.Height));
        var edge = 0;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        for (var c = 0; c < 3; c++)
        {
            var o = (y * width + x) * 3 + c;
            var diff = Math.Abs(page.Rgb[o] - expected[o]);
            var nearBorder = x < 4 || y < 4 || x >= width - 4 || y >= height - 4;
            if (nearBorder)
            {
                edge = Math.Max(edge, diff);
            }
            else if (diff != 0)
            {
                Assert.Fail($"Pixel ({x}, {y}) channel {c} is {page.Rgb[o]}, whole-page halving gives {expected[o]}.");
            }
        }

        // A tile's context is reflected at the page edge where vips extends the edge pixel instead.
        Assert.True(edge <= 16, $"Border pixels differ by up to {edge}.");
    }

    [Fact]
    public async Task A_colour_page_comes_back_as_a_three_band_webp_at_twice_the_size()
    {
        using var upscaler = NewUpscaler();

        var result = await upscaler.UpscaleAsync(Pattern(400, 500).EncodePng(), "span-s", CancellationToken.None);

        Assert.Equal(UpscaleOutcome.Ready, result.Outcome);
        Assert.Equal(("webp", 800, 1000), (result.Extension, result.Width, result.Height));
        using var decoded = NetVips.Image.NewFromBuffer(result.Bytes!);
        Assert.Equal((3, 800, 1000), (decoded.Bands, decoded.Width, decoded.Height));
    }

    [Fact]
    public async Task A_grey_page_is_encoded_from_a_single_channel()
    {
        using var upscaler = NewUpscaler();
        var source = Pattern(300, 420, grey: true);

        var grey = await upscaler.UpscaleAsync(source.EncodePng(), "span-s", CancellationToken.None);
        var tall = await upscaler.UpscaleAsync(Pattern(30, 8200, grey: true).EncodePng(), "span-s", CancellationToken.None);

        Assert.Equal((UpscaleOutcome.Ready, "webp"), (grey.Outcome, grey.Extension));
        Assert.Equal(ImageFormat.Webp, ImageCodec.DetectFormat(grey.Bytes!));
        // WebP has no greyscale mode: a one-band input decodes as three identical bands.
        using (var decoded = NetVips.Image.NewFromBuffer(grey.Bytes!))
        {
            Assert.Equal(3, decoded.Bands);
            Assert.True(UpscalePixels.IsGreyscale(decoded.WriteToMemory<byte>()));
        }

        Assert.Equal("jpg", tall.Extension);
        using var jpeg = NetVips.Image.NewFromBuffer(tall.Bytes!);
        Assert.Equal(1, jpeg.Bands);
    }

    [Fact]
    public async Task An_output_taller_than_webp_allows_is_a_jpeg()
    {
        using var upscaler = NewUpscaler();

        var result = await upscaler.UpscaleAsync(Pattern(40, 8200).EncodePng(), "span-s", CancellationToken.None);

        Assert.Equal(UpscaleOutcome.Ready, result.Outcome);
        Assert.Equal(("jpg", 80, 16400), (result.Extension, result.Width, result.Height));
        Assert.Equal(ImageFormat.Jpeg, ImageCodec.DetectFormat(result.Bytes!));
    }

    [Fact]
    public async Task A_corrupt_page_is_skipped()
    {
        using var upscaler = NewUpscaler();
        var jpeg = Pattern(200, 200).EncodeJpeg(90);

        var garbage = await upscaler.UpscaleAsync("not an image"u8.ToArray(), "span-s", CancellationToken.None);
        var truncated = await upscaler.UpscaleAsync(jpeg[..(jpeg.Length / 2)], "span-s", CancellationToken.None);

        Assert.Equal((UpscaleOutcome.Skipped, UpscaleReasons.Unreadable), (garbage.Outcome, garbage.Reason));
        Assert.Equal((UpscaleOutcome.Skipped, UpscaleReasons.Unreadable), (truncated.Outcome, truncated.Reason));
        Assert.Null(garbage.Bytes);
    }

    [Fact]
    public async Task Cancelling_stops_between_tiles_with_an_operation_cancelled_exception()
    {
        using var upscaler = NewUpscaler();
        using var cts = new CancellationTokenSource();
        var tiles = 0;
        upscaler.TileCompleted = _ =>
        {
            tiles++;
            cts.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            upscaler.UpscaleAsync(Pattern(900, 1300).EncodePng(), "span-s", cts.Token));

        Assert.Equal(1, tiles);
    }

    [Fact]
    public async Task Unknown_and_missing_models_fail_without_throwing()
    {
        using var upscaler = NewUpscaler();
        var png = Pattern(20, 20).EncodePng();

        var unknown = await upscaler.UpscaleAsync(png, "nope", CancellationToken.None);
        var missing = await upscaler.UpscaleAsync(png, "realesrgan-av3", CancellationToken.None);

        Assert.Equal((UpscaleOutcome.Failed, UpscaleReasons.UnknownModel), (unknown.Outcome, unknown.Reason));
        Assert.Equal((UpscaleOutcome.Failed, UpscaleReasons.ModelUnavailable), (missing.Outcome, missing.Reason));
    }

    [Fact]
    public async Task A_model_whose_output_is_not_the_adapter_scale_fails_the_page()
    {
        using var upscaler = NewUpscaler(installAs: UpscaleModelId.RealEsrganAv3);

        var result = await upscaler.UpscaleAsync(Pattern(50, 60).EncodePng(), "realesrgan-av3", CancellationToken.None);

        Assert.Equal((UpscaleOutcome.Failed, UpscaleReasons.InferenceFailed), (result.Outcome, result.Reason));
    }

    [Fact]
    public async Task Sessions_unload_when_idle_and_when_their_model_is_released()
    {
        using var upscaler = NewUpscaler();
        var png = Pattern(20, 20).EncodePng();

        await upscaler.UpscaleAsync(png, "span-s", CancellationToken.None);
        Assert.True(upscaler.IsLoaded(UpscaleModelId.SpanS));
        Assert.Equal(0, upscaler.ReleaseIfIdle(TimeSpan.FromMinutes(10)));
        Assert.Equal(1, upscaler.ReleaseIfIdle(TimeSpan.Zero));
        Assert.False(upscaler.IsLoaded(UpscaleModelId.SpanS));

        var again = await upscaler.UpscaleAsync(png, "span-s", CancellationToken.None);
        Assert.Equal(UpscaleOutcome.Ready, again.Outcome);
        upscaler.ReleaseModel(UpscaleModelId.SpanS);
        Assert.False(upscaler.IsLoaded(UpscaleModelId.SpanS));
        File.Delete(Path.Combine(_configDir, "models", "upscale", "span-s-2x.onnx"));
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new NotSupportedException();
    }
}
