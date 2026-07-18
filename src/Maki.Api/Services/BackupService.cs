using System.IO.Compression;
using System.Text.Json;
using Maki.Api.Configuration;
using Maki.Core.Configuration;
using Maki.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

public record BackupManifest(string AppVersion, DateTime CreatedUtc, string? LastMigration, string Kind);

public record BackupInfo(string Name, long SizeBytes, BackupManifest Manifest);

/// <summary>
/// Backup/restore for <c>{ConfigDir}</c>. A backup is a zip holding a consistent snapshot of
/// <c>maki.db</c> plus <c>config.json</c> (credential material) and a manifest. Big/regenerable
/// state (mangabaka.db, embeddings.db, cache, logs, models, MediaCover) is deliberately excluded.
///
/// Restore is staged into <see cref="AppPaths.RestorePendingDir"/> and applied on next boot by
/// <see cref="RestoreBootstrap"/> — live-swapping the DB under an open WAL connection is unsafe.
/// </summary>
public class BackupService(
    AppPaths paths,
    MakiDbContext db,
    IAppSettings settings,
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

        var name = $"maki-{createdUtc:yyyyMMdd-HHmmss}-{kind}.zip";
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
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

        var dbEntry = archive.GetEntry(DbEntry)
            ?? throw new InvalidOperationException("Backup is missing maki.db — not a Maki backup.");

        // Downgrade guard: refuse a backup whose schema is newer than this binary knows. Migrations
        // are forward-only, so restoring a newer DB into an older build would leave it unmigratable.
        var manifest = ReadManifestFromArchive(archive);
        if (manifest?.LastMigration is { } last)
        {
            var known = db.Database.GetMigrations().ToHashSet();
            if (!known.Contains(last))
                throw new InvalidOperationException(
                    $"Backup is from a newer version (migration '{last}' unknown to this build). " +
                    "Upgrade Maki before restoring it.");
        }

        if (Directory.Exists(paths.RestorePendingDir))
            Directory.Delete(paths.RestorePendingDir, recursive: true);
        Directory.CreateDirectory(paths.RestorePendingDir);

        dbEntry.ExtractToFile(Path.Combine(paths.RestorePendingDir, DbEntry), overwrite: true);
        archive.GetEntry(ConfigEntry)?.ExtractToFile(
            Path.Combine(paths.RestorePendingDir, ConfigEntry), overwrite: true);

        logger.LogWarning("Staged restore — will apply on next startup and then exit");
        await Task.CompletedTask;
    }

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
