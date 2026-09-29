using System.IO.Compression;
using System.Text.Json;
using Maki.Api.Configuration;
using Maki.Api.Localization;
using Maki.Core.Configuration;
using Maki.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

public record BackupManifest(string AppVersion, DateTime CreatedUtc, string? LastMigration, string Kind);

public record BackupInfo(string Name, long SizeBytes, BackupManifest Manifest);

/// <summary>A backup refused at staging. <see cref="Key"/> is the catalogue key the API answers with.</summary>
public sealed class BackupRestoreException(string key, object? args, string message)
    : InvalidOperationException(message)
{
    public string Key { get; } = key;
    public object? Args { get; } = args;
}

/// <summary>
/// A backup that failed to write. <see cref="Key"/> distinguishes a name collision with a backup
/// already in flight (recoverable by retrying) from every other IO failure (disk full, permission
/// denied), which is not.
/// </summary>
public sealed class BackupCreateException(string key) : InvalidOperationException(key)
{
    public string Key { get; } = key;
}

/// <summary>
/// Backup/restore for <c>{ConfigDir}</c>. A backup is a zip holding a consistent snapshot of
/// <c>maki.db</c> plus <c>config.json</c> (credential material) and a manifest. Big/regenerable
/// state (mangabaka.db, embeddings.db, cache, logs, models, MediaCover) is deliberately excluded.
///
/// The set of files is an explicit allowlist, not an exclude list, and one omission from it is
/// load-bearing: <c>dataprotection-keys</c> is <b>never</b> backed up. Those keys sign session
/// cookies, so a backup containing them would let anyone holding the zip mint a valid session for any
/// user — turning a stale backup on a NAS into a permanent authentication bypass. The cost is that
/// restoring onto a different machine signs everyone out once, which is the right trade.
///
/// Restore is staged into <see cref="AppPaths.RestorePendingDir"/> and applied on next boot by
/// <see cref="RestoreBootstrap"/> — live-swapping the DB under an open WAL connection is unsafe.
/// </summary>
public class BackupService(
    AppPaths paths,
    MakiDbContext db,
    IAppSettings settings,
    ILocalizer localizer,
    ILogger<BackupService> logger)
{
    private const string DbEntry = "maki.db";
    private const string ConfigEntry = "config.json";
    private const string ManifestEntry = "manifest.json";
    private const int DefaultRetention = 5;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<BackupInfo> CreateAsync(string kind, CancellationToken ct)
    {
        var createdUtc = DateTime.UtcNow;
        var lastMigration = (await db.Database.GetAppliedMigrationsAsync(ct)).LastOrDefault();
        var manifest = new BackupManifest(VersionInfo.Version, createdUtc, lastMigration, kind);

        var name = $"maki-{createdUtc:yyyyMMdd-HHmmss-fff}-{kind}.zip";
        var zipPath = Path.Combine(paths.BackupDir, name);
        var snapshotPath = Path.Combine(paths.BackupDir, $".{Guid.NewGuid():N}.db.tmp");

        try
        {
            SnapshotDatabase(snapshotPath);

            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(snapshotPath, DbEntry);
                if (File.Exists(paths.ConfigFile))
                    zip.CreateEntryFromFile(paths.ConfigFile, ConfigEntry);

                var manifestEntry = zip.CreateEntry(ManifestEntry);
                await using var writer = new StreamWriter(manifestEntry.Open());
                await writer.WriteAsync(JsonSerializer.Serialize(manifest, JsonOptions));
            }
        }
        catch (IOException ex)
        {
            logger.LogError(ex, "Failed to create {Kind} backup at {Path}", kind, zipPath);

            // ZipFile.Open(..., Create) opens the target with FileMode.CreateNew, so an IOException
            // whose target already exists (or whose HResult says so - ERROR_FILE_EXISTS) means a
            // backup for this exact name is already in flight. Anything else - disk full, permission
            // denied, a locked volume - is a real failure and must not be reported as a race.
            var inProgress = File.Exists(zipPath) || ex.HResult == unchecked((int)0x80070050);
            throw new BackupCreateException(inProgress ? "error.system.backupInProgress" : "error.system.backupFailed");
        }
        finally
        {
            TryDelete(snapshotPath);
        }

        logger.LogInformation("Created {Kind} backup {Name}", kind, name);
        await PruneAsync(ct);

        return new BackupInfo(name, new FileInfo(zipPath).Length, manifest);
    }

    /// <summary>Consistent snapshot via SQLite's online-backup API — safe against the live WAL
    /// connection; a plain File.Copy of an active WAL database can be torn.</summary>
    private void SnapshotDatabase(string destPath)
    {
        // Pooling=False so the connections release their OS file handles on dispose — otherwise
        // Microsoft.Data.Sqlite keeps the snapshot file locked and the subsequent zip read fails.
        using (var src = new SqliteConnection($"Data Source={paths.DatabasePath};Pooling=False"))
        using (var dst = new SqliteConnection($"Data Source={destPath};Pooling=False"))
        {
            src.Open();
            dst.Open();
            src.BackupDatabase(dst);
        }
    }

    public IReadOnlyList<BackupInfo> List()
    {
        if (!Directory.Exists(paths.BackupDir))
            return [];

        var list = new List<BackupInfo>();
        foreach (var file in Directory.EnumerateFiles(paths.BackupDir, "*.zip"))
        {
            var info = new FileInfo(file);
            list.Add(new BackupInfo(info.Name, info.Length, ReadManifest(file) ?? FallbackManifest(info)));
        }

        return list.OrderByDescending(b => b.Manifest.CreatedUtc).ToList();
    }

    /// <summary>Resolves a client-supplied backup name to a path inside <see cref="AppPaths.BackupDir"/>,
    /// rejecting traversal.</summary>
    public string PathFor(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || !name.EndsWith(".zip"))
            throw new ArgumentException($"Invalid backup name '{name}'");

        var path = Path.Combine(paths.BackupDir, name);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Backup '{name}' not found");

        return path;
    }

    public void Delete(string name)
    {
        File.Delete(PathFor(name));
        logger.LogInformation("Deleted backup {Name}", name);
    }

    /// <summary>Keeps the newest N backups per kind (parsed from the filename suffix); deletes the
    /// rest. Files that don't match our naming are left alone.</summary>
    public async Task PruneAsync(CancellationToken ct)
    {
        var retention = int.TryParse(await settings.GetAsync(SettingKeys.BackupRetention, ct), out var n) && n >= 1
            ? n
            : DefaultRetention;

        if (!Directory.Exists(paths.BackupDir))
            return;

        var byKind = Directory.EnumerateFiles(paths.BackupDir, "maki-*-*.zip")
            .Select(f => new FileInfo(f))
            .GroupBy(f => KindFromName(f.Name));

        foreach (var group in byKind)
        {
            var stale = group.OrderByDescending(f => f.Name).Skip(retention);
            foreach (var file in stale)
            {
                TryDelete(file.FullName);
                logger.LogInformation("Pruned old backup {Name}", file.Name);
            }
        }
    }

    public Task StagePendingRestoreFromFileAsync(string name, CancellationToken ct)
    {
        using var stream = File.OpenRead(PathFor(name));
        return StageAsync(stream, ct);
    }

    public async Task StagePendingRestoreFromUploadAsync(Stream zip, CancellationToken ct)
    {
        // ZipArchive needs a seekable stream; an upload body usually isn't. Spool to a temp file.
        var temp = Path.Combine(paths.BackupDir, $".upload-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var fs = File.Create(temp))
                await zip.CopyToAsync(fs, ct);

            await using var reread = File.OpenRead(temp);
            await StageAsync(reread, ct);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private async Task StageAsync(Stream zipStream, CancellationToken ct)
    {
        var parent = Path.GetDirectoryName(paths.RestorePendingDir)!;
        var tempDir = Path.Combine(parent, $".restore-staging-{Guid.NewGuid():N}");
        try
        {
            ExtractAndValidate(zipStream, tempDir);
            SwapIntoPending(tempDir, parent);
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }

        logger.LogWarning("Staged restore, will apply on next startup and then exit");
        await Task.CompletedTask;
    }

    private void ExtractAndValidate(Stream zipStream, string tempDir)
    {
        var known = db.Database.GetMigrations().ToList();
        var stagedDb = Path.Combine(tempDir, DbEntry);

        try
        {
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

            var dbEntry = archive.GetEntry(DbEntry) ?? throw Reject("error.system.backupMissingDb");

            // Downgrade guard: refuse a backup whose schema is newer than this binary knows. Migrations
            // are forward-only, so restoring a newer DB into an older build would leave it unmigratable.
            var manifest = ReadManifestFromArchive(archive);
            if (manifest?.LastMigration is { } last && !known.Contains(last))
                throw Reject("error.system.backupTooNew", new { migration = last });

            Directory.CreateDirectory(tempDir);
            dbEntry.ExtractToFile(stagedDb, overwrite: true);
            archive.GetEntry(ConfigEntry)?.ExtractToFile(Path.Combine(tempDir, ConfigEntry), overwrite: true);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or NotSupportedException)
        {
            logger.LogWarning("Rejected restore: unreadable backup archive: {Error}", ex.Message);
            throw Reject("error.system.backupUnreadable");
        }

        ValidateDatabase(stagedDb, known);
    }

    private void ValidateDatabase(string dbPath, IReadOnlyList<string> known)
    {
        string? lastApplied;
        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
            conn.Open();

            using (var check = conn.CreateCommand())
            {
                check.CommandText = "PRAGMA integrity_check";
                var result = check.ExecuteScalar() as string;
                if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning("Rejected restore: integrity_check reported {Result}", result);
                    throw Reject("error.system.backupInvalidDb");
                }
            }

            using (var exists = conn.CreateCommand())
            {
                exists.CommandText =
                    "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '__EFMigrationsHistory'";
                if (Convert.ToInt64(exists.ExecuteScalar()) == 0)
                {
                    logger.LogWarning("Rejected restore: no __EFMigrationsHistory table");
                    throw Reject("error.system.backupInvalidDb");
                }
            }

            using var history = conn.CreateCommand();
            history.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1";
            lastApplied = history.ExecuteScalar() as string;
        }
        catch (SqliteException ex)
        {
            logger.LogWarning("Rejected restore: staged database cannot be read: {Error}", ex.Message);
            throw Reject("error.system.backupInvalidDb");
        }

        if (lastApplied is null)
        {
            logger.LogWarning("Rejected restore: __EFMigrationsHistory is empty");
            throw Reject("error.system.backupInvalidDb");
        }

        if (known.Contains(lastApplied) || known.Count == 0)
            return;

        // No unknown migration id is accepted: this build has no squash handling, so a history
        // predating everything it ships (as well as one running ahead of it) would migrate forward
        // starting from Initial onto tables that already exist and crash-loop the restored instance.
        // The manifest check above already refuses any unknown id as too new; match that here too.
        if (string.CompareOrdinal(lastApplied, known[0]) < 0 || string.CompareOrdinal(lastApplied, known[^1]) > 0)
            throw Reject("error.system.backupTooNew", new { migration = lastApplied });

        logger.LogWarning("Rejected restore: unknown migration {Migration} in history", lastApplied);
        throw Reject("error.system.backupInvalidDb");
    }

    /// <summary>Moves a validated staging dir into <see cref="AppPaths.RestorePendingDir"/>. An
    /// existing pending restore is only removed once the new one is in place.</summary>
    private void SwapIntoPending(string tempDir, string parent)
    {
        string? previous = null;
        if (Directory.Exists(paths.RestorePendingDir))
        {
            previous = Path.Combine(parent, $".restore-previous-{Guid.NewGuid():N}");
            Directory.Move(paths.RestorePendingDir, previous);
        }

        try
        {
            Directory.Move(tempDir, paths.RestorePendingDir);
        }
        catch
        {
            if (previous is not null)
                Directory.Move(previous, paths.RestorePendingDir);
            throw;
        }

        if (previous is not null)
            TryDeleteDirectory(previous);
    }

    private BackupRestoreException Reject(string key, object? args = null) =>
        new(key, args, localizer.Get(key, args));

    private static string KindFromName(string name)
    {
        // maki-{timestamp}-{kind}.zip
        var stem = Path.GetFileNameWithoutExtension(name);
        var dash = stem.LastIndexOf('-');
        return dash >= 0 ? stem[(dash + 1)..] : "unknown";
    }

    private static BackupManifest? ReadManifest(string zipPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            return ReadManifestFromArchive(archive);
        }
        catch
        {
            return null;
        }
    }

    private static BackupManifest? ReadManifestFromArchive(ZipArchive archive)
    {
        var entry = archive.GetEntry(ManifestEntry);
        if (entry is null)
            return null;

        using var reader = new StreamReader(entry.Open());
        return JsonSerializer.Deserialize<BackupManifest>(reader.ReadToEnd());
    }

    private static BackupManifest FallbackManifest(FileInfo file) =>
        new("unknown", file.LastWriteTimeUtc, null, KindFromName(file.Name));

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
