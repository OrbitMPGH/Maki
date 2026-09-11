using Microsoft.Extensions.Logging;

namespace Maki.Api.Configuration;

/// <summary>
/// Applies a staged restore at process start, before anything reads <c>config.json</c> or opens the
/// database. A restore is staged into <see cref="AppPaths.RestorePendingDir"/> by
/// <c>BackupService</c>; the app then exits, and on the next boot this swaps the staged files into
/// place. Must run before <c>ConfigFileProvider</c> (which reads config.json immediately), so the
/// logger it is handed is the bootstrap one, running on defaults rather than on configured levels.
/// </summary>
public static class RestoreBootstrap
{
    public static void ApplyPendingRestore(AppPaths paths, ILogger logger)
    {
        var stagedDb = Path.Combine(paths.RestorePendingDir, "maki.db");
        if (!File.Exists(stagedDb))
            return;

        logger.LogInformation("Applying staged restore from {Directory}", paths.RestorePendingDir);

        // Drop the live DB and its WAL sidecars so the restored file is authoritative.
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            TryDelete(paths.DatabasePath + suffix, logger);

        File.Move(stagedDb, paths.DatabasePath, overwrite: true);

        var stagedConfig = Path.Combine(paths.RestorePendingDir, "config.json");
        if (File.Exists(stagedConfig))
            File.Copy(stagedConfig, paths.ConfigFile, overwrite: true);

        Directory.Delete(paths.RestorePendingDir, recursive: true);
        logger.LogInformation("Restore complete");
    }

    private static void TryDelete(string path, ILogger logger)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not delete {Path}: {Error}", path, ex.Message);
        }
    }
}
