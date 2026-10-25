using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Maki.Api.Configuration;
using Maki.Core.Configuration;

namespace Maki.Api.Services.Upscaling;

public enum UpscaleModelState
{
    Missing,
    Downloading,
    Ready,
    Failed
}

/// <param name="ErrorKey">A server message catalogue key when <paramref name="State"/> is Failed, else null.</param>
/// <param name="Bytes">The file size when ready, else the size the manifest announced, else null.</param>
public sealed record UpscaleModelStatus(UpscaleModelId Id, UpscaleModelState State, string? ErrorKey, long? Bytes);

public sealed record UpscaleManifest
{
    [JsonPropertyName("version")] public int Version { get; init; }

    [JsonPropertyName("models")] public List<UpscaleManifestModel>? Models { get; init; }
}

public sealed record UpscaleManifestModel
{
    [JsonPropertyName("id")] public string? Id { get; init; }

    [JsonPropertyName("file")] public string? File { get; init; }

    [JsonPropertyName("sha256")] public string? Sha256 { get; init; }

    [JsonPropertyName("bytes")] public long Bytes { get; init; }
}

/// <summary>
/// Downloads the upscale models to <c>{ConfigDir}/models/upscale</c> and answers which are usable.
/// Failure reasons are catalogue keys, not text: this is a singleton and the request language is
/// scoped, so the caller renders the key with its own <c>ILocalizer</c>.
/// </summary>
public sealed class UpscaleModelStore
{
    public const string HttpClientName = "upscale-models";

    public const string DefaultManifestUrl =
        "https://github.com/OrbitMPGH/Maki/releases/download/upscale-models-v1/manifest.json";

    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAppSettings _settings;
    private readonly UpscaleModelCatalog _catalog;
    private readonly ILogger<UpscaleModelStore> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<UpscaleModelId, Entry> _entries = [];

    public UpscaleModelStore(
        IHttpClientFactory httpClientFactory, AppPaths paths, IAppSettings settings,
        UpscaleModelCatalog catalog, ILogger<UpscaleModelStore> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings;
        _catalog = catalog;
        _logger = logger;
        ModelsDirectory = Path.Combine(paths.ModelsDir, "upscale");

        foreach (var info in catalog.All)
        {
            var entry = new Entry();
            if (File.Exists(Path.Combine(ModelsDirectory, info.FileName)))
            {
                entry.State = UpscaleModelState.Ready;
            }

            _entries[info.Id] = entry;
        }

        RemovePartials();
    }

    public string ModelsDirectory { get; }

    public string PathOf(UpscaleModelId id) => Path.Combine(ModelsDirectory, _catalog.Get(id).FileName);

    /// <summary>True when the model finished downloading and its file is still on disk. Never hashes.</summary>
    public bool IsReady(UpscaleModelId id)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry) || entry.State != UpscaleModelState.Ready)
            {
                return false;
            }
        }

        return File.Exists(PathOf(id));
    }

    public UpscaleModelStatus GetStatus(UpscaleModelId id)
    {
        UpscaleModelState state;
        string? errorKey;
        long? announced;
        lock (_gate)
        {
            var entry = _entries[id];
            state = entry.State;
            errorKey = entry.ErrorKey;
            announced = entry.ManifestBytes;
        }

        if (state == UpscaleModelState.Ready)
        {
            var path = PathOf(id);
            if (!File.Exists(path))
            {
                lock (_gate)
                {
                    _entries[id].State = UpscaleModelState.Missing;
                }

                return new UpscaleModelStatus(id, UpscaleModelState.Missing, null, announced);
            }

            return new UpscaleModelStatus(id, state, null, new FileInfo(path).Length);
        }

        return new UpscaleModelStatus(id, state, state == UpscaleModelState.Failed ? errorKey : null, announced);
    }

    public IReadOnlyList<UpscaleModelStatus> GetStatuses() =>
        _catalog.All.Select(info => GetStatus(info.Id)).ToList();

    /// <summary>
    /// Starts the download and waits for it. A second call while one is running joins it. The download
    /// itself is not tied to <paramref name="ct"/>, which only stops this call from waiting.
    /// </summary>
    public async Task<UpscaleModelStatus> DownloadAsync(UpscaleModelId id, CancellationToken ct = default)
    {
        Task running;
        lock (_gate)
        {
            var entry = _entries[id];
            if (entry.State == UpscaleModelState.Ready && File.Exists(PathOf(id)))
            {
                return GetStatus(id);
            }

            if (entry.Running is null)
            {
                entry.State = UpscaleModelState.Downloading;
                entry.ErrorKey = null;
                var cancel = new CancellationTokenSource();
                entry.Cancel = cancel;
                entry.Running = Task.Run(() => RunAsync(id, entry, cancel.Token), CancellationToken.None);
            }

            running = entry.Running;
        }

        await running.WaitAsync(ct);
        return GetStatus(id);
    }

    /// <summary>Stops a download in flight and deletes the file. Throws if the file is still held open.</summary>
    public async Task<UpscaleModelStatus> DeleteAsync(UpscaleModelId id)
    {
        Task? running;
        lock (_gate)
        {
            var entry = _entries[id];
            entry.Cancel?.Cancel();
            running = entry.Running;
        }

        if (running is not null)
        {
            await running;
        }

        var path = PathOf(id);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        lock (_gate)
        {
            var entry = _entries[id];
            entry.State = UpscaleModelState.Missing;
            entry.ErrorKey = null;
        }

        return GetStatus(id);
    }

    private async Task RunAsync(UpscaleModelId id, Entry entry, CancellationToken ct)
    {
        string? errorKey = null;
        try
        {
            errorKey = await DownloadCoreAsync(id, entry, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogDebug("Download of upscale model {Model} was cancelled", id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Download of upscale model {Model} failed", id);
            errorKey = "error.upscaling.downloadFailed";
        }
        finally
        {
            lock (_gate)
            {
                entry.State = errorKey is not null
                    ? UpscaleModelState.Failed
                    : File.Exists(PathOf(id)) ? UpscaleModelState.Ready : UpscaleModelState.Missing;
                entry.ErrorKey = errorKey;
                entry.Running = null;
                entry.Cancel?.Dispose();
                entry.Cancel = null;
            }
        }
    }

    private async Task<string?> DownloadCoreAsync(UpscaleModelId id, Entry entry, CancellationToken ct)
    {
        var info = _catalog.Get(id);
        var location = ManifestLocation.Parse(await _settings.GetAsync(SettingKeys.UpscalingManifestUrl, ct));

        var manifest = await ReadManifestAsync(location, ct);
        var model = manifest?.Models?.FirstOrDefault(m => string.Equals(m.Id, id.Value, StringComparison.OrdinalIgnoreCase));
        if (manifest is not { Version: 1 } || model is null || !IsPlainFileName(model.File))
        {
            _logger.LogWarning("The upscale model manifest at {Location} does not list {Model}", location.Display, id);
            return "error.upscaling.downloadFailed";
        }

        lock (_gate)
        {
            entry.ManifestBytes = model.Bytes > 0 ? model.Bytes : null;
        }

        if (string.IsNullOrWhiteSpace(model.Sha256))
        {
            return "error.upscaling.checksumMismatch";
        }

        Directory.CreateDirectory(ModelsDirectory);
        var target = Path.Combine(ModelsDirectory, info.FileName);
        var partial = target + ".partial";
        _logger.LogInformation("Downloading upscale model {Model} from {Location}", id, location.Display);

        try
        {
            string actual;
            long length;
            await using (var source = await location.OpenAsync(_httpClientFactory, model.File!, ct))
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                (actual, length) = await CopyAndHashAsync(source, output, ct);
            }

            if (!string.Equals(actual, model.Sha256.Trim(), StringComparison.OrdinalIgnoreCase)
                || (model.Bytes > 0 && length != model.Bytes))
            {
                _logger.LogWarning(
                    "Upscale model {Model} failed verification: expected {Expected}, got {Actual}", id, model.Sha256, actual);
                return "error.upscaling.checksumMismatch";
            }

            File.Move(partial, target, overwrite: true);
            _logger.LogInformation("Upscale model {Model} is ready ({Bytes:N0} bytes)", id, length);
            return null;
        }
        finally
        {
            TryDelete(partial);
        }
    }

    private async Task<UpscaleManifest?> ReadManifestAsync(ManifestLocation location, CancellationToken ct)
    {
        try
        {
            // Read as a string: a manifest written by a Windows tool can carry a UTF-8 BOM, which
            // byte-level JSON parsing rejects.
            var json = await location.ReadManifestAsync(_httpClientFactory, ct);
            return JsonSerializer.Deserialize<UpscaleManifest>(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException
                                       or UnauthorizedAccessException or UriFormatException)
        {
            _logger.LogDebug(ex, "Upscale model manifest unavailable at {Location}", location.Display);
            return null;
        }
    }

    private static async Task<(string Sha256, long Length)> CopyAndHashAsync(Stream source, Stream output, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stall.CancelAfter(StallTimeout);
            int read;
            try
            {
                read = await source.ReadAsync(buffer, stall.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new IOException("The model download stalled.");
            }

            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            total += read;
        }

        return (Convert.ToHexStringLower(hash.GetHashAndReset()), total);
    }

    private static bool IsPlainFileName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name == Path.GetFileName(name)
        && !name.Contains('/') && !name.Contains('\\') && !name.Contains(':');

    private void RemovePartials()
    {
        if (!Directory.Exists(ModelsDirectory))
        {
            return;
        }

        foreach (var partial in Directory.EnumerateFiles(ModelsDirectory, "*.partial"))
        {
            TryDelete(partial);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class Entry
    {
        public UpscaleModelState State;
        public string? ErrorKey;
        public long? ManifestBytes;
        public Task? Running;
        public CancellationTokenSource? Cancel;
    }

    /// <summary>Where the manifest lives and how to open the files next to it.</summary>
    private sealed class ManifestLocation
    {
        private readonly Uri? _http;
        private readonly string? _path;

        private ManifestLocation(Uri? http, string? path)
        {
            _http = http;
            _path = path;
        }

        public string Display => _http?.ToString() ?? _path!;

        public static ManifestLocation Parse(string? setting)
        {
            var value = setting?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                return new ManifestLocation(new Uri(DefaultManifestUrl), null);
            }

            if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                if (uri.Scheme is "http" or "https")
                {
                    return new ManifestLocation(uri, null);
                }

                if (uri.IsFile && value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                {
                    return new ManifestLocation(null, uri.LocalPath);
                }
            }

            return new ManifestLocation(null, Path.GetFullPath(value));
        }

        public async Task<string> ReadManifestAsync(IHttpClientFactory factory, CancellationToken ct) =>
            _http is not null
                ? await factory.CreateClient(HttpClientName).GetStringAsync(_http, ct)
                : await File.ReadAllTextAsync(_path!, ct);

        public async Task<Stream> OpenAsync(IHttpClientFactory factory, string file, CancellationToken ct)
        {
            if (_http is null)
            {
                return new FileStream(
                    Path.Combine(Path.GetDirectoryName(_path!)!, file), FileMode.Open, FileAccess.Read, FileShare.Read);
            }

            var response = await factory.CreateClient(HttpClientName)
                .GetAsync(new Uri(_http, file), HttpCompletionOption.ResponseHeadersRead, ct);
            try
            {
                response.EnsureSuccessStatusCode();
                return new ResponseStream(response, await response.Content.ReadAsStreamAsync(ct));
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
    }

    private sealed class ResponseStream(HttpResponseMessage response, Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
