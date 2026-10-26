namespace Maki.Core.Indexers;

/// <summary>What every backend shares: its named HTTP client and the download link fetch.</summary>
public abstract class ReleaseIndexerBase(IHttpClientFactory httpClientFactory, string httpClientName) : IReleaseIndexer
{
    /// <summary>The client <see cref="FetchTorrentAsync"/> uses, built on <see cref="CreateDownloadLinkHandler"/>.</summary>
    public const string DownloadHttpClientName = "indexer-download";

    private const int MaxTorrentFileBytes = 20 * 1024 * 1024;
    private const int MaxRedirects = 5;

    public abstract IndexerBackend Backend { get; }

    public abstract Task<IReadOnlyList<IndexerRelease>> SearchAsync(IndexerSettings s, string query, CancellationToken ct = default);

    public abstract Task<IReadOnlyList<IndexerInfo>> GetIndexersAsync(IndexerSettings s, CancellationToken ct = default);

    public abstract Task<bool> PingAsync(IndexerSettings s, CancellationToken ct = default);

    protected HttpClient CreateHttpClient() => httpClientFactory.CreateClient(httpClientName);

    /// <summary>
    /// SocketsHttpHandler follows a redirect to <c>magnet:</c> as if it were a host and throws a
    /// <see cref="UriFormatException"/>, so the download link fetch follows redirects itself.
    /// </summary>
    public static SocketsHttpHandler CreateDownloadLinkHandler() => new() { AllowAutoRedirect = false };

    /// <summary>
    /// Fetches a release's download link (the backend's own proxy URL, which carries its API key). A
    /// magnet-only indexer answers with a redirect to the magnet, which is returned instead of followed;
    /// a redirect to another http link is followed to the torrent file.
    /// </summary>
    public virtual async Task<TorrentLink> FetchTorrentAsync(string downloadUrl, CancellationToken ct = default)
    {
        var client = httpClientFactory.CreateClient(DownloadHttpClientName);
        var url = new Uri(downloadUrl);
        for (var hop = 0; ; hop++)
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (RawLocation(response) is { } location)
            {
                if (location.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                {
                    return new TorrentLink(null, location);
                }

                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    if (hop >= MaxRedirects ||
                        !Uri.TryCreate(url, location, out var next) ||
                        next.Scheme != Uri.UriSchemeHttp && next.Scheme != Uri.UriSchemeHttps)
                    {
                        throw new InvalidDataException("The download link redirected somewhere Maki cannot follow");
                    }

                    url = next;
                    continue;
                }
            }

            response.EnsureSuccessStatusCode();
            return new TorrentLink(await ReadTorrentFileAsync(response, ct), null);
        }
    }

    /// <summary>The Location header as sent, since a magnet with unescaped characters fails Uri parsing.</summary>
    private static string? RawLocation(HttpResponseMessage response) =>
        response.Headers.NonValidated.TryGetValues("Location", out var values) && values.FirstOrDefault() is { Length: > 0 } value
            ? value.Trim()
            : null;

    private static async Task<byte[]> ReadTorrentFileAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > MaxTorrentFileBytes)
        {
            throw new InvalidDataException("The download link served more than a torrent file");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxTorrentFileBytes)
            {
                throw new InvalidDataException("The download link served more than a torrent file");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
