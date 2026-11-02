using System.Collections.Concurrent;

namespace Maki.Api.Services;

/// <summary>
/// The write time of each series' cover file, read once and then kept. Cover URLs are built for
/// every series on every list, home, stats and inbox response, and a file stat per series adds up
/// when the config directory is on a network share. <see cref="CoverService"/> updates it whenever
/// it rewrites a cover and removes it when the cover is deleted. Keyed by the cover's path, so two
/// config directories in one process (the tests) cannot read each other's entries.
/// </summary>
internal static class CoverVersionCache
{
    private static readonly ConcurrentDictionary<string, long> Ticks = new();

    /// <summary>UTC ticks of the cover's write time, or null when the file cannot be read (not cached).</summary>
    public static long? Get(string coverPath)
    {
        if (Ticks.TryGetValue(coverPath, out var cached))
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
            Ticks[coverPath] = ticks;
            return ticks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public static void Set(string coverPath, long ticks) => Ticks[coverPath] = ticks;

    public static void Remove(string coverPath) => Ticks.TryRemove(coverPath, out _);
}
