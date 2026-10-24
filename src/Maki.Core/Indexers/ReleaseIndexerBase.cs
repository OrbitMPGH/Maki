namespace Maki.Core.Indexers;

/// <summary>What every backend shares: its named HTTP client and the download link fetch.</summary>
public abstract class ReleaseIndexerBase(IHttpClientFactory httpClientFactory, string httpClientName) : IReleaseIndexer
{
    private const int MaxTorrentFileBytes = 20 * 1024 * 1024;

    public abstract IndexerBackend Backend { get; }

    public abstract Task<IReadOnlyList<IndexerRelease>> SearchAsync(IndexerSettings s, string query, CancellationToken ct = default);

    public abstract Task<IReadOnlyList<IndexerInfo>> GetIndexersAsync(IndexerSettings s, CancellationToken ct = default);

    public abstract Task<bool> PingAsync(IndexerSettings s, CancellationToken ct = default);

    protected HttpClient CreateHttpClient() => httpClientFactory.CreateClient(httpClientName);

    /// <summary>
    /// Fetches a release's download link (the backend's own proxy URL, which carries its API key). A
    /// magnet-only indexer answers with a redirect to the magnet, which the handler does not follow.
    /// </summary>
    public virtual async Task<TorrentLink> FetchTorrentAsync(string downloadUrl, CancellationToken ct = default)
    {
        var client = CreateHttpClient();
        using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.Headers.Location is { } location &&
            location.OriginalString.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            return new TorrentLink(null, location.OriginalString);
        }

        response.EnsureSuccessStatusCode();
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

        return new TorrentLink(buffer.ToArray(), null);
    }
}
