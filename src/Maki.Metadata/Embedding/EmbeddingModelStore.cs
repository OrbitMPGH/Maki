using Microsoft.Extensions.Logging;

namespace Maki.Metadata.Embedding;

/// <summary>
/// Downloads the ONNX embedding model and its tokenizer vocab into the config dir on first
/// use (~110 MB one-time, like the MangaBaka dump). Files are streamed to a .partial staging
/// path, size-checked, then moved into place so a half-written file is never loaded.
/// </summary>
public class EmbeddingModelStore(
    IHttpClientFactory httpClientFactory,
    EmbeddingOptions options,
    ILogger<EmbeddingModelStore> logger)
{
    public const string HttpClientName = "embedding-model";

    private const long MinModelBytes = 20_000_000; // quantized base model is ~110 MB
    private const long MinVocabBytes = 100_000;     // real vocab is ~231 KB
    private const long MinMergesBytes = 100_000;    // byte-level BPE merges are ~1.6 MB

    /// <summary>
    /// The floor for the .onnx of an external-data model, where the weights live in the companion
    /// file and the graph on its own is small. EmbeddingGemma's is 480 KB, so applying the
    /// single-file floor to it rejects a perfectly good download as truncated.
    /// </summary>
    private const long MinGraphBytes = 100_000;

    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Size floor for the .onnx itself. Split from <see cref="MinModelBytes"/> because "the model
    /// file is at least 20 MB" is a single-file assumption: with external data, the size lives in
    /// the companion, which is checked separately.
    /// </summary>
    private long MinModelFileBytes => options.Model.ModelDataUrl is null ? MinModelBytes : MinGraphBytes;

    public bool IsPresent() =>
        FileAtLeast(options.ModelPath, MinModelFileBytes) &&
        FileAtLeast(options.VocabPath, MinVocabBytes) &&
        (options.Model.ModelDataUrl is null || FileAtLeast(options.ModelDataPath, MinModelBytes)) &&
        (options.Model.MergesUrl is null || FileAtLeast(options.MergesPath, MinMergesBytes));

    /// <summary>Ensures both files are present, downloading whichever is missing/truncated.</summary>
    public async Task EnsureAsync(CancellationToken ct = default)
    {
        if (IsPresent())
        {
            return;
        }

        await _lock.WaitAsync(ct);
        try
        {
            if (IsPresent())
            {
                return;
            }

            Directory.CreateDirectory(options.ModelDirectory);
            var client = httpClientFactory.CreateClient(HttpClientName);
            if (!FileAtLeast(options.ModelPath, MinModelFileBytes))
            {
                await DownloadAsync(
                    client, options.ModelUrl, options.ModelPath, MinModelFileBytes,
                    $"embedding model ({options.Model.FolderName}, {options.Precision})", ct);
            }

            // The weights half of an external-data graph. ONNX Runtime resolves it by the name baked
            // into the .onnx, relative to that file, so it must sit beside it under exactly that name
            // or the session fails to load with no useful message.
            if (options.Model.ModelDataUrlFor(options.Precision) is { } dataUrl &&
                !FileAtLeast(options.ModelDataPath, MinModelBytes))
            {
                await DownloadAsync(
                    client, dataUrl, options.ModelDataPath, MinModelBytes,
                    $"embedding model weights ({options.Model.FolderName})", ct);
            }

            if (!FileAtLeast(options.VocabPath, MinVocabBytes))
            {
                await DownloadAsync(client, options.VocabUrl, options.VocabPath, MinVocabBytes, "tokenizer vocab", ct);
            }

            if (options.Model.MergesUrl is { } mergesUrl && !FileAtLeast(options.MergesPath, MinMergesBytes))
            {
                await DownloadAsync(client, mergesUrl, options.MergesPath, MinMergesBytes, "tokenizer merges", ct);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task DownloadAsync(
        HttpClient client, string url, string destination, long minBytes, string label, CancellationToken ct)
    {
        logger.LogInformation("Downloading {Label}…", label);
        var staging = destination + ".partial";
        try
        {
            using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var output = File.Create(staging);
                await source.CopyToAsync(output, ct);
            }

            var size = new FileInfo(staging).Length;
            if (size < minBytes)
            {
                throw new InvalidOperationException(
                    $"Downloaded {label} is too small ({size} bytes) — expected at least {minBytes}");
            }

            File.Move(staging, destination, overwrite: true);
            logger.LogInformation("Installed {Label} at {Path} ({Size} bytes)", label, destination, size);
        }
        catch
        {
            TryDelete(staging);
            throw;
        }
    }

    private static bool FileAtLeast(string path, long bytes) =>
        File.Exists(path) && new FileInfo(path).Length >= bytes;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
