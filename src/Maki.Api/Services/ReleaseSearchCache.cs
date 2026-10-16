using Maki.Core.Indexers;
using Microsoft.Extensions.Caching.Memory;

namespace Maki.Api.Services;

/// <summary>
/// Releases an indexer search returned, per series and backend, so a grab can only send the download client a link the
/// server itself was handed. Taking the link from the request let any account with DownloadChapters
/// make the download client fetch an arbitrary URL from its own network position. Switching backend
/// strands what the old one returned, since its links go through the old backend's proxy. Entries are
/// keyed by the guid as clients see it, which has any API key query parameter removed.
/// </summary>
public class ReleaseSearchCache(IMemoryCache cache)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    public void Remember(int seriesId, IndexerBackend backend, IEnumerable<ReleaseDto> releases)
    {
        foreach (var release in releases)
        {
            cache.Set(Key(seriesId, backend, release.Guid), release with { Parsed = null }, Lifetime);
        }
    }

    public ReleaseDto? Find(int seriesId, IndexerBackend backend, string guid) =>
        cache.TryGetValue(Key(seriesId, backend, guid), out ReleaseDto? release) ? release : null;

    private static (string, int, IndexerBackend, string) Key(int seriesId, IndexerBackend backend, string guid) =>
        ("release-search", seriesId, backend, IndexerLinks.WithoutApiKey(guid)!);
}
