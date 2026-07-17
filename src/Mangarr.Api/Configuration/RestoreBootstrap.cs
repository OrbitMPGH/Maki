namespace Mangarr.Api.Configuration;

/// <summary>
/// Applies a staged restore at process start, before anything reads <c>config.json</c> or opens the
/// database. A restore is staged into <see cref="AppPaths.RestorePendingDir"/> by
/// <c>BackupService</c>; the app then exits, and on the next boot this swaps the staged files into
/// place. Must run before <c>ConfigFileProvider</c> (which reads config.json immediately) — so it
/// runs before Serilog is configured and logs to the console.
/// </summary>
public static class RestoreBootstrap
{
    public static void ApplyPendingRestore(AppPaths paths)
    {
        var stagedDb = Path.Combine(paths.RestorePendingDir, "mangarr.db");
        if (!File.Exists(stagedDb))
            return;

        Console.WriteLine($"[restore] Applying staged restore from {paths.RestorePendingDir}");

        // Drop the live DB and its WAL sidecars so the restored file is authoritative.
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            TryDelete(paths.DatabasePath + suffix);

        File.Move(stagedDb, paths.DatabasePath, overwrite: true);

        var stagedConfig = Path.Combine(paths.RestorePendingDir, "config.json");
        if (File.Exists(stagedConfig))
            File.Copy(stagedConfig, paths.ConfigFile, overwrite: true);

        Directory.Delete(paths.RestorePendingDir, recursive: true);
        Console.WriteLine("[restore] Restore complete");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[restore] Could not delete {path}: {ex.Message}");
        }
    }
}
