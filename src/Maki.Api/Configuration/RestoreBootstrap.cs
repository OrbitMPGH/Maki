using System.Reflection;
using Maki.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging;

namespace Maki.Api.Configuration;

/// <summary>
/// Applies a staged restore at process start, before anything reads <c>config.json</c> or opens the
/// database. A restore is staged into <see cref="AppPaths.RestorePendingDir"/> by
/// <c>BackupService</c>; the app then exits, and on the next boot this swaps the staged files into
/// place. Must run before <c>ConfigFileProvider</c> (which reads config.json immediately), so the
/// logger it is handed is the bootstrap one, running on defaults rather than on configured levels.
/// <para>
/// The live database and its WAL sidecars are renamed to <c>*.pre-restore</c> rather than deleted,
/// and put back if the restored file cannot be opened, so a bad staged file never costs the live DB.
/// </para>
/// </summary>
public static class RestoreBootstrap
{
    public const string PreRestoreSuffix = ".pre-restore";

    private static readonly string[] Sidecars = ["", "-wal", "-shm"];

    public static void ApplyPendingRestore(AppPaths paths, ILogger logger)
    {
        var stagedDb = Path.Combine(paths.RestorePendingDir, "maki.db");
        if (!File.Exists(stagedDb))
            return;

        logger.LogInformation("Applying staged restore from {Directory}", paths.RestorePendingDir);

        var renamed = new List<string>();
        var movedIn = false;
        try
        {
            foreach (var suffix in Sidecars)
            {
                var live = paths.DatabasePath + suffix;
                if (!File.Exists(live))
                {
                    File.Delete(live + PreRestoreSuffix);
                    continue;
                }
                File.Move(live, live + PreRestoreSuffix, overwrite: true);
                // A rename keeps the old mtime, and the housekeeping purge ages the copy by it.
                File.SetLastWriteTimeUtc(live + PreRestoreSuffix, DateTime.UtcNow);
                renamed.Add(live);
            }

            File.Move(stagedDb, paths.DatabasePath, overwrite: true);
            movedIn = true;
            EnsureOpens(paths.DatabasePath);
        }
        catch (Exception ex)
        {
            logger.LogError("Staged restore could not be applied, keeping the current database: {Error}", ex.Message);
            RollBack(paths, renamed, movedIn, logger);
            TryDeleteDirectory(paths.RestorePendingDir, logger);
            return;
        }

        var stagedConfig = Path.Combine(paths.RestorePendingDir, "config.json");
        if (File.Exists(stagedConfig))
        {
            try
            {
                File.Copy(stagedConfig, paths.ConfigFile, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError("Restored database in place, but config.json could not be replaced: {Error}", ex.Message);
            }
        }

        TryDeleteDirectory(paths.RestorePendingDir, logger);
        logger.LogInformation("Restore complete");
    }

    /// <summary>
    /// Deletes the rollback copies a restore leaves behind once they are older than
    /// <paramref name="maxAge"/>. They hold the old database in full, secrets included, outside
    /// backup retention, so they cannot stay forever.
    /// </summary>
    public static void PurgeStalePreRestoreCopies(AppPaths paths, TimeSpan maxAge, ILogger logger)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        var main = paths.DatabasePath + PreRestoreSuffix;
        // The -wal and -shm copies go with the database copy, aged by it.
        var mainExpired = File.Exists(main) && File.GetLastWriteTimeUtc(main) < cutoff;
        foreach (var suffix in Sidecars)
        {
            var copy = paths.DatabasePath + suffix + PreRestoreSuffix;
            try
            {
                if (File.Exists(copy) && (mainExpired || File.GetLastWriteTimeUtc(copy) < cutoff))
                {
                    File.Delete(copy);
                    logger.LogInformation("Deleted old pre-restore database copy {Path}", copy);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not delete {Path}", copy);
            }
        }
    }

    /// <summary>Migration ids known to this build, read off the <see cref="MigrationAttribute"/> on
    /// every migration class in <c>Maki.Data</c>. This runs before the host is built (BackupService,
    /// which owns the real check via <c>db.Database.GetMigrations()</c>, is not available this
    /// early), so reflection over the assembly is the cheap alternative rather than standing up a
    /// <c>DbContext</c> just to ask it.</summary>
    private static readonly Lazy<HashSet<string>> KnownMigrations = new(() =>
        typeof(MakiDbContext).Assembly.GetTypes()
            .Select(t => t.GetCustomAttribute<MigrationAttribute>())
            .Where(a => a is not null)
            .Select(a => a!.Id)
            .ToHashSet());

    private static void EnsureOpens(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        conn.Open();

        using var history = conn.CreateCommand();
        history.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1";
        var lastApplied = history.ExecuteScalar() as string;

        if (lastApplied is not null && !KnownMigrations.Value.Contains(lastApplied))
        {
            throw new InvalidOperationException(
                $"Staged database's last migration '{lastApplied}' is not known to this build");
        }
    }

    private static void RollBack(AppPaths paths, List<string> renamed, bool movedIn, ILogger logger)
    {
        if (movedIn)
        {
            foreach (var suffix in Sidecars)
            {
                try
                {
                    File.Delete(paths.DatabasePath + suffix);
                }
                catch (Exception ex)
                {
                    logger.LogError("Could not remove rejected {Path}: {Error}", paths.DatabasePath + suffix, ex.Message);
                }
            }
        }

        foreach (var live in renamed)
        {
            try
            {
                File.Move(live + PreRestoreSuffix, live, overwrite: true);
            }
            catch (Exception ex)
            {
                logger.LogError("Could not put {Path} back: {Error}", live, ex.Message);
            }
        }
    }

    private static void TryDeleteDirectory(string path, ILogger logger)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not delete {Path}: {Error}", path, ex.Message);
        }
    }
}
