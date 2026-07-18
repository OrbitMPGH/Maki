using System.IO.Compression;
using System.Text.Json;
using Mangarr.Api;
using Mangarr.Api.Configuration;
using Mangarr.Api.Services;
using Mangarr.Core.Configuration;
using Mangarr.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mangarr.Api.Tests;

/// <summary>
/// Exercises <see cref="BackupService"/> against a real temp config dir and a file-backed SQLite
/// DB: name validation, create/list roundtrip, per-kind retention pruning, and the restore
/// staging guards (missing db entry, downgrade rejection, happy-path staging).
/// </summary>
public class BackupServiceTests : IDisposable
{
    private readonly string _configDir;
    private readonly string? _priorEnv;
    private readonly AppPaths _paths;
    private readonly MangarrDbContext _db;
    private readonly FakeAppSettings _settings = new();

    public BackupServiceTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "mangarr-backup-tests", Guid.NewGuid().ToString("N"));
        _priorEnv = Environment.GetEnvironmentVariable("MANGARR_CONFIG_DIR");
        Environment.SetEnvironmentVariable("MANGARR_CONFIG_DIR", _configDir);

        _paths = new AppPaths();
        _db = new MangarrDbContext(new DbContextOptionsBuilder<MangarrDbContext>()
            .UseSqlite($"Data Source={_paths.DatabasePath}")
            .Options);
        _db.Database.EnsureCreated();
    }

    private BackupService Build() =>
        new(_paths, _db, _settings, NullLogger<BackupService>.Instance);

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        Environment.SetEnvironmentVariable("MANGARR_CONFIG_DIR", _priorEnv);
        try
        {
            Directory.Delete(_configDir, recursive: true);
        }
        catch
        {
            // best-effort temp cleanup
        }
    }

    [Theory]
    [InlineData("../escape.zip")]
    [InlineData("sub/dir.zip")]
    [InlineData("notazip.txt")]
    [InlineData("")]
    public void PathFor_rejects_bad_names(string name)
    {
        Assert.Throws<ArgumentException>(() => Build().PathFor(name));
    }

    [Fact]
    public void PathFor_rejects_a_missing_backup()
    {
        Assert.Throws<FileNotFoundException>(() => Build().PathFor("mangarr-20260101-000000-manual.zip"));
    }

    [Fact]
    public void List_is_empty_before_any_backup()
    {
        Assert.Empty(Build().List());
    }

    [Fact]
    public async Task Create_then_list_roundtrips_with_manifest()
    {
        var info = await Build().CreateAsync("manual", CancellationToken.None);

        Assert.EndsWith("-manual.zip", info.Name);
        Assert.True(info.SizeBytes > 0);
        Assert.Equal(VersionInfo.Version, info.Manifest.AppVersion);
        Assert.Equal("manual", info.Manifest.Kind);

        var listed = Assert.Single(Build().List());
        Assert.Equal(info.Name, listed.Name);
    }

    [Fact]
    public async Task Prune_keeps_the_newest_per_kind()
    {
        _settings.Set(SettingKeys.BackupRetention, "2");
        foreach (var stamp in new[] { "20260101-000000", "20260102-000000", "20260103-000000", "20260104-000000" })
        {
            await File.WriteAllTextAsync(Path.Combine(_paths.BackupDir, $"mangarr-{stamp}-auto.zip"), "x");
        }
        await File.WriteAllTextAsync(Path.Combine(_paths.BackupDir, "mangarr-20260101-000000-manual.zip"), "x");

        await Build().PruneAsync(CancellationToken.None);

        var remaining = Directory.GetFiles(_paths.BackupDir, "*.zip").Select(Path.GetFileName).ToList();
        Assert.Equal(3, remaining.Count);
        Assert.Contains("mangarr-20260104-000000-auto.zip", remaining);
        Assert.Contains("mangarr-20260103-000000-auto.zip", remaining);
        Assert.Contains("mangarr-20260101-000000-manual.zip", remaining);
        Assert.DoesNotContain("mangarr-20260101-000000-auto.zip", remaining);
    }

    [Fact]
    public async Task Restore_rejects_a_zip_without_the_database()
    {
        var zip = BuildBackupZip(includeDb: false, manifest: new BackupManifest("1.0.0", DateTime.UtcNow, null, "manual"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build().StagePendingRestoreFromUploadAsync(new MemoryStream(zip), CancellationToken.None));
        Assert.Contains("mangarr.db", ex.Message);
    }

    [Fact]
    public async Task Restore_rejects_a_backup_from_a_newer_schema()
    {
        var manifest = new BackupManifest("9.9.9", DateTime.UtcNow, "99999999999999_FromTheFuture", "manual");
        var zip = BuildBackupZip(includeDb: true, manifest: manifest);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build().StagePendingRestoreFromUploadAsync(new MemoryStream(zip), CancellationToken.None));
        Assert.Contains("newer version", ex.Message);
    }

    [Fact]
    public async Task Restore_stages_a_valid_backup()
    {
        var manifest = new BackupManifest("1.0.0", DateTime.UtcNow, null, "manual");
        var zip = BuildBackupZip(includeDb: true, includeConfig: true, manifest: manifest);

        await Build().StagePendingRestoreFromUploadAsync(new MemoryStream(zip), CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_paths.RestorePendingDir, "mangarr.db")));
        Assert.True(File.Exists(Path.Combine(_paths.RestorePendingDir, "config.json")));
    }

    private static byte[] BuildBackupZip(bool includeDb, BackupManifest manifest, bool includeConfig = false)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (includeDb)
            {
                WriteEntry(zip, "mangarr.db", "SQLite format 3\0");
            }

            if (includeConfig)
            {
                WriteEntry(zip, "config.json", "{}");
            }

            WriteEntry(zip, "manifest.json", JsonSerializer.Serialize(manifest));
        }

        return ms.ToArray();
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(content);
    }
}
