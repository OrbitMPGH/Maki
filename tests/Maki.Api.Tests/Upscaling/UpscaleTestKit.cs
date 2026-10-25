using System.IO.Compression;
using Maki.Api.Configuration;
using Maki.Api.Hubs;
using Maki.Api.Services;
using Maki.Api.Services.Upscaling;
using Maki.Core.Configuration;
using Maki.Core.Images;
using Maki.Core.Images.Upscaling;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests.Upscaling;

internal static class UpscaleTestKit
{
    /// <summary>A CBZ of <paramref name="pages"/> small PNG pages named 001.png onwards.</summary>
    public static void WriteCbz(string path, int pages, int width = 48, int height = 64)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        for (var i = 0; i < pages; i++)
        {
            var page = new Raster(width, height, new Rgba((byte)(40 + i * 20), 90, 160), bands: 3);
            using var entry = zip.CreateEntry($"{i + 1:000}.png").Open();
            entry.Write(page.EncodePng());
        }
    }

    public static ReaderService.PageSlice Slice(int chapterId = 1, int fileId = 1, string path = "x.cbz", int pages = 20) =>
        new(chapterId, fileId, path, 1000, Enumerable.Range(1, pages).Select(i => $"{i:000}.png").ToList(), 0, pages, 5);

    public static UpscaleRequestItem Item(int page, int chapterId = 1, int fileId = 1, string model = "span-s") =>
        new(new UpscaleWorkKey(fileId, "1000-5", page, model), new UpscaleTarget(chapterId, page), Slice(chapterId, fileId));

    /// <summary>A store with the identity fixture installed under SPAN-S, which makes SPAN-S ready.</summary>
    public static UpscaleModelStore StoreWithSpanS(AppPaths paths, IAppSettings settings, UpscaleModelCatalog catalog)
    {
        var models = Path.Combine(paths.ModelsDir, "upscale");
        Directory.CreateDirectory(models);
        File.Copy(UpscaleFixture.IdentityModelPath, Path.Combine(models, catalog.Get(UpscaleModelId.SpanS).FileName), overwrite: true);
        return new UpscaleModelStore(new NoHttp(), paths, settings, catalog, NullLogger<UpscaleModelStore>.Instance);
    }

    public static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new NotSupportedException();
    }
}

internal sealed class RecordingBroadcaster() : EventBroadcaster(null!, null!)
{
    private readonly Lock _gate = new();
    private readonly List<(int UserId, int ChapterId, int Page, string Model, string State)> _sent = [];

    public IReadOnlyList<(int UserId, int ChapterId, int Page, string Model, string State)> Sent
    {
        get
        {
            lock (_gate)
            {
                return _sent.ToList();
            }
        }
    }

    public override Task PageEnhanced(int userId, int chapterId, int page, string model, string state)
    {
        lock (_gate)
        {
            _sent.Add((userId, chapterId, page, model, state));
        }

        return Task.CompletedTask;
    }
}

/// <summary>Answers every page with fixed bytes, or throws for the pages listed in <see cref="ThrowFor"/>.</summary>
internal sealed class FakeUpscaler : IPageUpscaler
{
    public HashSet<int> ThrowFor { get; } = [];

    public int Calls;

    public Task<UpscaleResult> UpscaleAsync(byte[] page, string modelId, CancellationToken ct)
    {
        var call = Interlocked.Increment(ref Calls);
        if (ThrowFor.Contains(call))
        {
            throw new InvalidOperationException("boom");
        }

        return Task.FromResult(UpscaleResult.Ready(new UpscaledImage([1, 2, 3, 4], "webp", 2, 2, 1)));
    }
}
