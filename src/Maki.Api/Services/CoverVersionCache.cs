using System.Collections.Concurrent;

namespace Maki.Api.Services;

/// <summary>
/// The write time of each series' cover file, read once and then kept. Cover URLs are built for
/// every series on every list, home, stats and inbox response, and a file stat per series adds up
/// when the config directory is on a network share. <see cref="CoverService"/> updates it whenever
/// it rewrites a cover and removes it when the cover is deleted.
/// </summary>
internal static class CoverVersionCache
{
    private static readonly ConcurrentDictionary<int, long> Ticks = new();

    /// <summary>UTC ticks of the cover's write time, or null when the file cannot be read (not cached).</summary>
    public static long? Get(int seriesId, string coverPath)
    {
        if (Ticks.TryGetValue(seriesId, out var cached))
        {
            return cached;
        }

        try
        {
            var info = new FileInfo(coverPath);
            if (!info.Exists)
            {
                return null;
            }

            var ticks = info.LastWriteTimeUtc.Ticks;
            Ticks[seriesId] = ticks;
            return ticks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public static void Set(int seriesId, long ticks) => Ticks[seriesId] = ticks;

    public static void Remove(int seriesId) => Ticks.TryRemove(seriesId, out _);

    internal static void Clear() => Ticks.Clear();
}
