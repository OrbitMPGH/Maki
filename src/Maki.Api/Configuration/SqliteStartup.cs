using System.Collections.Concurrent;
using Maki.Core.Io;
using Maki.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Maki.Api.Configuration;

/// <summary>
/// Readies the config dir's SQLite files before anything in the process opens one; see
/// <see cref="SqliteFiles"/> and <see cref="SqliteKeeper"/> for why. The sweep runs once per
/// directory per process, since a test host booted a second time in one process already has
/// databases open there.
/// </summary>
public static class SqliteStartup
{
    private static readonly ConcurrentDictionary<string, bool> Swept = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, bool> Unattached = new(StringComparer.Ordinal);

    public static void Prepare(AppPaths paths, ILogger logger)
    {
        SqliteKeeper.Logger = logger;
        SqliteKeeper.Allow(paths.DatabasePath);
        SqliteKeeper.Allow(paths.EmbeddingsDbPath);
        var first = Swept.TryAdd(Path.GetFullPath(paths.ConfigDir), true);
        // Asked before this process opens the database, and only once per process for the same reason.
        Unattached[Path.GetFullPath(paths.DatabasePath)] = first && SqliteFiles.IsUnattached(paths.DatabasePath);
        if (OperatingSystem.IsLinux() && first)
        {
            foreach (var directory in new[] { paths.ConfigDir, paths.CacheDir })
            {
                foreach (var shm in SqliteFiles.DeleteStaleSharedMemory(directory))
                {
                    logger.LogDebug("Removed {File} left by the previous run", Path.GetFileName(shm));
                }
            }
        }

        SqliteFiles.CreateIfMissing(paths.DatabasePath);
        KeepAttached(paths);
    }

    /// <summary>
    /// Whether no other process had the database open when this one started, so nothing else can be
    /// migrating it. False when it could not be told.
    /// </summary>
    public static bool AloneAtStart(AppPaths paths) =>
        Unattached.TryGetValue(Path.GetFullPath(paths.DatabasePath), out var alone) && alone;

    /// <summary>Again once the database is in WAL mode: on a fresh install the keeper opened before it was.</summary>
    public static void KeepAttached(AppPaths paths) => SqliteKeeper.For(paths.DatabasePath).Attach();

    /// <summary>
    /// At shutdown: pooled connections close first, so each keeper closes last, checkpoints, and a
    /// clean stop leaves no <c>-wal</c> or <c>-shm</c> behind.
    /// </summary>
    public static void Release(AppPaths paths)
    {
        SqliteConnection.ClearAllPools();
        SqliteKeeper.Release(paths.DatabasePath);
        SqliteKeeper.Release(paths.EmbeddingsDbPath);
    }
}
