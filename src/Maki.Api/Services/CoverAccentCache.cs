using System.Collections.Concurrent;
using System.Threading.Channels;
using Maki.Core.Images;

namespace Maki.Api.Services;

/// <summary>
/// The colour each cover lends to the chrome around it (<see cref="CoverAccent"/>), computed once
/// per cover version and kept in memory. Keyed on the poster's write time, the same version the
/// cover URL's cache-buster follows, so a replaced cover gets a fresh colour and an unchanged one
/// never decodes twice. Not persisted: the whole library recomputes in well under a second.
/// </summary>
public class CoverAccentCache(CoverService covers, ILogger<CoverAccentCache> logger)
{
    private readonly ConcurrentDictionary<int, (long Version, string? Hex)> cache = new();
    private readonly ConcurrentDictionary<int, byte> queued = new();
    private readonly Channel<int> pending = Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleReader = true });
    private int workerStarted;

    /// <summary>
    /// The colour if it is already known, without decoding anything. A cover not seen yet (or
    /// replaced since) is queued for one background worker and reads as its last known colour, or
    /// null, until then. For the library list, which would otherwise decode every cover on disk
    /// inside the first request after a restart; the next fetch picks the colours up.
    /// </summary>
    public string? CachedAccentFor(int seriesId)
    {
        var version = CoverVersionCache.Get(covers.CoverPathFor(seriesId));
        if (version is null)
        {
            return null;
        }

        var known = cache.TryGetValue(seriesId, out var hit);
        if (known && hit.Version == version)
        {
            return hit.Hex;
        }

        if (queued.TryAdd(seriesId, 0))
        {
            pending.Writer.TryWrite(seriesId);
            if (Interlocked.Exchange(ref workerStarted, 1) == 0)
            {
                _ = Task.Run(DrainAsync);
            }
        }

        return known ? hit.Hex : null;
    }

    private async Task DrainAsync()
    {
        await foreach (var seriesId in pending.Reader.ReadAllAsync())
        {
            try
            {
                AccentFor(seriesId);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogDebug(ex, "Could not work out the cover colour of series {SeriesId}", seriesId);
            }
            finally
            {
                queued.TryRemove(seriesId, out _);
            }
        }
    }

    /// <returns>A <c>#rrggbb</c> colour, or null when there is no cover or it has no hue to lend.</returns>
    public string? AccentFor(int seriesId)
    {
        var path = covers.CoverPathFor(seriesId);
        var version = CoverVersionCache.Get(path);
        if (version is null)
        {
            return null;
        }

        if (cache.TryGetValue(seriesId, out var hit) && hit.Version == version)
        {
            return hit.Hex;
        }

        string? hex = null;
        try
        {
            hex = CoverAccent.FromImage(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A cover that will not decode is a cosmetic loss here; the cover endpoint reports it.
            logger.LogDebug(ex, "Could not read a colour from the cover of series {SeriesId}", seriesId);
        }

        cache[seriesId] = (version.Value, hex);
        return hex;
    }
}
