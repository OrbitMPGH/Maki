using System.Text.Json;
using Maki.Core.Configuration;
using Maki.Core.Http;
using Maki.Core.Sources.Custom;

namespace Maki.Sources.Custom;

/// <summary>The only reader and writer of <see cref="SettingKeys.CustomRepositories"/>: one lock around read-modify-write.</summary>
public sealed class CustomRepositoryStore(IAppSettings settings)
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<List<CustomRepository>> ReadAsync(CancellationToken ct = default) =>
        Parse(await settings.GetAsync(SettingKeys.CustomRepositories, ct));

    public async Task<T> UpdateAsync<T>(Func<List<CustomRepository>, (bool Changed, T Result)> mutate, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var repositories = Parse(await settings.GetAsync(SettingKeys.CustomRepositories, ct), forWrite: true);
            var (changed, result) = mutate(repositories);
            if (changed)
            {
                await settings.SetAsync(SettingKeys.CustomRepositories,
                    repositories.Count == 0 ? null : JsonSerializer.Serialize(repositories, CustomRepository.Json), ct);
            }

            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    public static bool SameUrl(string a, string b) =>
        string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Unreadable reads as none, but is never written over, which would drop every repository.</summary>
    private static List<CustomRepository> Parse(string? json, bool forWrite = false)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<CustomRepository>>(json, CustomRepository.Json) ?? [];
        }
        catch (JsonException) when (!forWrite)
        {
            return [];
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"The stored {SettingKeys.CustomRepositories} value doesn't parse; refusing to overwrite it", ex);
        }
    }
}

/// <summary>
/// Reads repository indexes and definition files through the guarded <c>custom-sources</c> client,
/// which only connects to public addresses, up to a size cap.
/// </summary>
public sealed class CustomRepositoryClient(IHttpClientFactory http)
{
    public sealed class FetchFailed(string key, Dictionary<string, string> args, Exception? inner = null) : Exception(key, inner)
    {
        public string Key { get; } = key;
        public Dictionary<string, string> Args { get; } = args;
    }

    /// <summary>An http(s) URL on a host name, or null.</summary>
    public static Uri? RepositoryUri(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && string.IsNullOrEmpty(uri.UserInfo)
        && PublicAddressGuard.IsAllowedUrl(uri)
            ? uri
            : null;

    public async Task<byte[]> GetAsync(Uri url, int maxBytes, CancellationToken ct)
    {
        var args = new Dictionary<string, string> { ["url"] = url.AbsoluteUri };
        try
        {
            using var response = await http.CreateClient(CustomSourceFetcher.RepositoryHttpClientName)
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > maxBytes)
            {
                throw new FetchFailed("error.customRepository.tooLarge", new(args) { ["max"] = (maxBytes / 1024).ToString() });
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > maxBytes)
                {
                    throw new FetchFailed("error.customRepository.tooLarge", new(args) { ["max"] = (maxBytes / 1024).ToString() });
                }
            }

            return buffer.ToArray();
        }
        catch (Exception ex) when (ex is not FetchFailed && (ex is not OperationCanceledException || !ct.IsCancellationRequested))
        {
            throw new FetchFailed("error.customRepository.fetchFailed", new(args) { ["reason"] = ex.Message }, ex);
        }
    }

    public async Task<CustomRepositoryIndex> IndexAsync(Uri url, CancellationToken ct)
    {
        var bytes = await GetAsync(url, CustomRepositoryIndex.MaxBytes, ct);
        try
        {
            var index = JsonSerializer.Deserialize<CustomRepositoryIndex>(bytes, CustomRepository.Json)
                ?? throw new JsonException("empty");
            if (index.Definitions.Any(d => string.IsNullOrWhiteSpace(d.Id) || string.IsNullOrWhiteSpace(d.Url)))
            {
                throw new JsonException("every definition needs an id and a url");
            }

            return index;
        }
        catch (JsonException ex)
        {
            throw new FetchFailed("error.customRepository.indexInvalid", new() { ["url"] = url.AbsoluteUri, ["reason"] = ex.Message });
        }
    }
}
