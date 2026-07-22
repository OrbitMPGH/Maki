#:project ../src/Maki.Metadata/Maki.Metadata.csproj

// Builds a local embeddings.db for one model from scratch, reusing Maki's own services so the
// result is byte-for-byte what a client would produce. Driven by publish-embeddings.ps1; run
// directly with:
//   dotnet run distribution/build-embeddings.cs -- <base|large> <artifactsDir>
//
// Three steps, each of which no-ops when its output is already current, so the first run is slow
// (multi-GB dump download + a full embedding pass) and every run after it only refreshes what
// actually changed:
//   1. MangaBakaDumpService.RefreshAsync — download the *full* dump (the one that carries the
//      MangaUpdates description the indexer prefers) into <artifactsDir>, skipped when the
//      published SHA1 matches what's already there.
//   2. EmbeddingModelStore.EnsureAsync — download this model's ONNX + vocab into
//      <artifactsDir>/models/<folder>, skipped when both files are already present.
//   3. SeriesEmbeddingIndexer.RunAsync — embed every recommendable series into
//      <artifactsDir>/embeddings-<model>.db, re-embedding only rows whose text/tags/model changed.
//
// Everything persists under <artifactsDir> (git-ignored) and is shared across models where it can
// be: both models embed the same dump, so the ~4.6 GB download happens once for the pair.
//
// stdout is the log; a non-zero exit means a step failed and nothing downstream should run.

using System.Globalization;
using System.Text.Json;
using Maki.Core.Configuration;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Microsoft.Extensions.Logging;

if (args.Length < 2)
{
    Console.WriteLine("usage: build-embeddings.cs <base|large> <artifactsDir>");
    return 2;
}

var modelArg = args[0].Trim().ToLowerInvariant();
if (modelArg is not ("base" or "large"))
{
    Console.WriteLine($"error: unknown model '{args[0]}' (expected 'base' or 'large')");
    return 2;
}

var profile = modelArg == "large" ? EmbeddingModelProfile.Large : EmbeddingModelProfile.Base;
var artifactsDir = Path.GetFullPath(args[1]);
Directory.CreateDirectory(artifactsDir);

// Shared across models: one dump, one staging dir, one models root, one settings file.
var dumpPath = Path.Combine(artifactsDir, "mangabaka.full.db");
var stagingDir = Path.Combine(artifactsDir, "staging");
var modelsRoot = Path.Combine(artifactsDir, "models");
var settingsPath = Path.Combine(artifactsDir, "settings.json");

// Per-model: its own embeddings DB, so building 'large' never disturbs the 'base' index.
var embeddingsDb = Path.Combine(artifactsDir, $"embeddings-{modelArg}.db");

Console.WriteLine($"model       : {modelArg} ({profile.Version}, {profile.Dimensions} dims)");
Console.WriteLine($"artifacts   : {artifactsDir}");
Console.WriteLine($"dump        : {dumpPath}");
Console.WriteLine($"embeddings  : {embeddingsDb}");
Console.WriteLine();

var http = new SimpleHttpClientFactory();
var settings = new FileAppSettings(settingsPath);
// The prebuilt index is only worth publishing when it's built from the richer full dump, so force
// it on regardless of what a previous run left in the settings file.
await settings.SetAsync(SettingKeys.MangaBakaUseFullDump, "true");

var dumpOptions = new MangaBakaDumpOptions(dumpPath, stagingDir);
var embeddingOptions = new EmbeddingOptions(modelsRoot, embeddingsDb, stagingDir, profile) { Enabled = true };

var dumpService = new MangaBakaDumpService(http, dumpOptions, settings, Log<MangaBakaDumpService>());
var modelStore = new EmbeddingModelStore(http, embeddingOptions, Log<EmbeddingModelStore>());
var embedder = new TextEmbedder(embeddingOptions, modelStore, Log<TextEmbedder>());
var store = new EmbeddingStore(embeddingOptions);
var status = new EmbeddingIndexStatus();
var indexer = new SeriesEmbeddingIndexer(dumpOptions, embeddingOptions, store, embedder, status, Log<SeriesEmbeddingIndexer>());

try
{
    Console.WriteLine("== 1/3 MangaBaka full dump ==");
    var installed = await dumpService.RefreshAsync();
    Console.WriteLine(installed ? "dump refreshed." : "dump already current.");
    Console.WriteLine();

    Console.WriteLine("== 2/3 embedding model ==");
    await modelStore.EnsureAsync();
    Console.WriteLine("model ready.");
    Console.WriteLine();

    Console.WriteLine("== 3/3 embedding pass ==");
    var result = await indexer.RunAsync();
    Console.WriteLine($"indexed: scanned {result.Scanned}, embedded {result.Embedded}, unchanged {result.Skipped}.");
    Console.WriteLine();
    Console.WriteLine($"done -> {embeddingsDb}");
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine($"error: {ex.Message}");
    Console.WriteLine(ex);
    return 1;
}

static ILogger<T> Log<T>() => new ConsoleLogger<T>();

/// <summary>Hands out an HttpClient per named client, matching the base address + timeout the API host uses.</summary>
file sealed class SimpleHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name)
    {
        // 60 min covers the ~4.6 GB dump on a slow line; model downloads are far smaller.
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Maki/1.0 (+https://github.com/OrbitMPGH/Maki)");
        // The dump service GETs relative paths (v1/database/...); the model store uses absolute URLs.
        if (name == MangaBakaDumpService.HttpClientName)
        {
            client.BaseAddress = new Uri("https://api.mangabaka.org/");
        }

        return client;
    }
}

/// <summary>
/// File-backed settings so the dump SHA1 marker (and the full-dump flag) survive between runs —
/// that's what lets a second run skip the multi-GB download instead of fetching it again.
/// </summary>
file sealed class FileAppSettings : IAppSettings
{
    private readonly string _path;
    private readonly Dictionary<string, string?> _values;

    public FileAppSettings(string path)
    {
        _path = path;
        _values = File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(path)) ?? new()
            : new();
    }

    public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(_values.GetValueOrDefault(key));

    public Task SetAsync(string key, string? value, CancellationToken ct = default)
    {
        _values[key] = value;
        File.WriteAllText(_path, JsonSerializer.Serialize(_values, new JsonSerializerOptions { WriteIndented = true }));
        return Task.CompletedTask;
    }
}

/// <summary>Minimal ILogger that prints Information and above to stdout — enough to watch the long pass.</summary>
file sealed class ConsoleLogger<T> : ILogger<T>
{
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var now = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        Console.WriteLine($"  [{now} {typeof(T).Name}] {formatter(state, exception)}");
        if (exception is not null)
        {
            Console.WriteLine(exception);
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
