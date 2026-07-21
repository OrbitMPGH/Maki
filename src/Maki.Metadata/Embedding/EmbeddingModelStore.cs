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

    private const long MinModelBytes = 20_000_000; // quantized ~110 MB (bge-base); fp32 ~440 MB
    private const long MinVocabBytes = 100_000;     // real vocab is ~231 KB

    private readonly SemaphoreSlim _lock = new(1, 1);

    public bool IsPresent() =>
        FileAtLeast(options.ModelPath, MinModelBytes) && FileAtLeast(options.VocabPath, MinVocabBytes);

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
            if (!FileAtLeast(options.ModelPath, MinModelBytes))
            {
                await DownloadAsync(client, options.ModelUrl, options.ModelPath, MinModelBytes, "embedding model (~110 MB)", ct);
            }

            if (!FileAtLeast(options.VocabPath, MinVocabBytes))
            {
                await DownloadAsync(client, options.VocabUrl, options.VocabPath, MinVocabBytes, "tokenizer vocab", ct);
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
