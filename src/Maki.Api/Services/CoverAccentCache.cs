using System.Collections.Concurrent;
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

    /// <returns>A <c>#rrggbb</c> colour, or null when there is no cover or it has no hue to lend.</returns>
    public string? AccentFor(int seriesId)
    {
        var path = covers.CoverPathFor(seriesId);
        var version = CoverVersionCache.Get(seriesId, path);
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
