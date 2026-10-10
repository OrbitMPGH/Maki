using Microsoft.Extensions.Caching.Memory;

namespace Maki.Api.Services;

/// <summary>
/// Releases a Prowlarr search returned, per series, so a grab can only send qBittorrent a link the
/// server itself was handed. Taking the link from the request let any account with DownloadChapters
/// make qBittorrent fetch an arbitrary URL from its own network position.
/// </summary>
public class ReleaseSearchCache(IMemoryCache cache)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    public void Remember(int seriesId, IEnumerable<ReleaseDto> releases)
    {
        foreach (var release in releases)
        {
            cache.Set(Key(seriesId, release.Guid), release with { Parsed = null }, Lifetime);
        }
    }

    public ReleaseDto? Find(int seriesId, string guid) =>
        cache.TryGetValue(Key(seriesId, guid), out ReleaseDto? release) ? release : null;

    private static (string, int, string) Key(int seriesId, string guid) => ("release-search", seriesId, guid);
}
