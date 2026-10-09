using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Maki.Api.Configuration;
using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// Restore staging validates the backup before touching an existing staged restore, the API answers
/// a damaged upload with 400, and applying a staged restore never loses the live database.
/// </summary>
[Collection(ConfigDirCollection.Name)]
public class BackupRestoreTests : IDisposable
{
    private readonly string _configDir;
    private readonly string? _priorEnv;
    private readonly AppPaths _paths;
    private readonly MakiDbContext _db;
    private readonly FakeAppSettings _settings = new();

    public BackupRestoreTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "maki-restore-tests", Guid.NewGuid().ToString("N"));
        _priorEnv = Environment.GetEnvironmentVariable("MAKI_CONFIG_DIR");
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _configDir);

        _paths = new AppPaths();
        _db = new MakiDbContext(new DbContextOptionsBuilder<MakiDbContext>()
            .UseSqlite($"Data Source={_paths.DatabasePath}")
            .Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _priorEnv);
        try
        {
            Directory.Delete(_configDir, recursive: true);
        }
        catch
        {
            // best-effort temp cleanup
        }
    }

    private BackupService Build() =>
        new(_paths, _db, _settings, new TestLocalizer(), NullLogger<BackupService>.Instance);

    private SystemController Controller() =>
        new(_paths, Build(), null!, null!, null!, null!, null!, null!, null!,
            new TestLocalizer(), NullLogger<SystemController>.Instance);

    private string LastKnownMigration => _db.Database.GetMigrations().Last();

    [Fact]
    public async Task Stages_a_valid_wal_mode_backup()
    {
        var zip = Zip(ValidDatabase(_configDir, LastKnownMigration), Manifest());

        await Build().StagePendingRestoreFromUploadAsync(new MemoryStream(zip), CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_paths.RestorePendingDir, "maki.db")));
        Assert.Empty(Directory.GetDirectories(_configDir, ".restore-*"));
    }

    [Fact]
    public async Task Random_bytes_database_is_rejected_and_an_existing_staged_restore_survives()
    {
        var good = ValidDatabase(_configDir, LastKnownMigration);
        await Build().StagePendingRestoreFromUploadAsync(new MemoryStream(Zip(good, Manifest())), CancellationToken.None);
        var stagedPath = Path.Combine(_paths.RestorePendingDir, "maki.db");
        var before = await File.ReadAllBytesAsync(stagedPath);

        var garbage = new byte[8192];
        new Random(42).NextBytes(garbage);
        var ex = await Assert.ThrowsAsync<BackupRestoreException>(() =>
            Build().StagePendingRestoreFromUploadAsync(new MemoryStream(Zip(garbage, Manifest())), CancellationToken.None));

        Assert.Equal("error.system.backupInvalidDb", ex.Key);
        Assert.Equal(before, await File.ReadAllBytesAsync(stagedPath));
        Assert.Empty(Directory.GetDirectories(_configDir, ".restore-*"));
    }

    [Fact]
    public async Task Database_without_migration_history_is_rejected()
    {
        var bytes = SqliteFile(_configDir, conn => Exec(conn, "CREATE TABLE Foo (Id INTEGER)"));

        var ex = await Assert.ThrowsAsync<BackupRestoreException>(() =>
            Build().StagePendingRestoreFromUploadAsync(new MemoryStream(Zip(bytes, Manifest())), CancellationToken.None));

        Assert.Equal("error.system.backupInvalidDb", ex.Key);
        Assert.False(Directory.Exists(_paths.RestorePendingDir));
    }

    [Fact]
    public async Task Database_history_newer_than_this_build_is_rejected()
    {
        var zip = Zip(ValidDatabase(_configDir, "99999999999999_FromTheFuture"), Manifest());

        var ex = await Assert.ThrowsAsync<BackupRestoreException>(() =>
            Build().StagePendingRestoreFromUploadAsync(new MemoryStream(zip), CancellationToken.None));

        Assert.Equal("error.system.backupTooNew", ex.Key);
    }

    [Fact]
    public async Task Database_history_older_than_every_known_migration_is_rejected()
    {
        var zip = Zip(ValidDatabase(_configDir, "00000000000000_Ancient"), Manifest());

        var ex = await Assert.ThrowsAsync<BackupRestoreException>(() =>
            Build().StagePendingRestoreFromUploadAsync(new MemoryStream(zip), CancellationToken.None));

        Assert.Equal("error.system.backupTooOld", ex.Key);
        Assert.False(Directory.Exists(_paths.RestorePendingDir));
    }

    [Fact]
    public async Task Upload_with_a_corrupt_zip_is_a_400()
    {
        var result = await Controller().RestoreUpload(Upload(Encoding.UTF8.GetBytes("not a zip at all")), CancellationToken.None);

        AssertFail(result, "error.system.backupUnreadable");
    }

    [Fact]
    public async Task Upload_with_a_bad_manifest_is_a_400()
    {
        var zip = Zip(ValidDatabase(_configDir, LastKnownMigration), "{ this is not json");

        var result = await Controller().RestoreUpload(Upload(zip), CancellationToken.None);

        AssertFail(result, "error.system.backupUnreadable");
    }

    [Fact]
    public async Task Named_restore_with_a_corrupt_zip_is_a_400()
    {
        const string name = "maki-20260101-000000-manual.zip";
        await File.WriteAllTextAsync(Path.Combine(_paths.BackupDir, name), "not a zip at all");

        var result = await Controller().RestoreBackup(name, CancellationToken.None);

        AssertFail(result, "error.system.backupUnreadable");
    }

    [Fact]
    public async Task Named_restore_with_a_bad_manifest_is_a_400()
    {
        const string name = "maki-20260101-000000-manual.zip";
        await File.WriteAllBytesAsync(Path.Combine(_paths.BackupDir, name),
            Zip(ValidDatabase(_configDir, LastKnownMigration), "[1, 2"));

        var result = await Controller().RestoreBackup(name, CancellationToken.None);

        AssertFail(result, "error.system.backupUnreadable");
    }

    [Fact]
    public void Apply_with_an_invalid_staged_database_keeps_the_original()
    {
        ReleaseLiveDb();
        var original = File.ReadAllBytes(_paths.DatabasePath);

        Directory.CreateDirectory(_paths.RestorePendingDir);
        var garbage = new byte[8192];
        new Random(7).NextBytes(garbage);
        File.WriteAllBytes(Path.Combine(_paths.RestorePendingDir, "maki.db"), garbage);
        File.WriteAllText(Path.Combine(_paths.RestorePendingDir, "config.json"), "{\"restored\":true}");
        var configBefore = File.Exists(_paths.ConfigFile) ? File.ReadAllText(_paths.ConfigFile) : null;

        RestoreBootstrap.ApplyPendingRestore(_paths, NullLogger.Instance);

        Assert.Equal(original, File.ReadAllBytes(_paths.DatabasePath));
        Assert.False(File.Exists(_paths.DatabasePath + RestoreBootstrap.PreRestoreSuffix));
        Assert.False(Directory.Exists(_paths.RestorePendingDir));
        Assert.Equal(configBefore, File.Exists(_paths.ConfigFile) ? File.ReadAllText(_paths.ConfigFile) : null);
    }

    [Fact]
    public void Apply_with_a_valid_staged_database_keeps_the_old_one_as_pre_restore()
    {
        ReleaseLiveDb();
        var original = File.ReadAllBytes(_paths.DatabasePath);
        var staged = ValidDatabase(_configDir, LastKnownMigration);

        Directory.CreateDirectory(_paths.RestorePendingDir);
        File.WriteAllBytes(Path.Combine(_paths.RestorePendingDir, "maki.db"), staged);

        RestoreBootstrap.ApplyPendingRestore(_paths, NullLogger.Instance);

        Assert.Equal(original, File.ReadAllBytes(_paths.DatabasePath + RestoreBootstrap.PreRestoreSuffix));
        Assert.NotEqual(original, File.ReadAllBytes(_paths.DatabasePath));
        Assert.False(Directory.Exists(_paths.RestorePendingDir));
    }

    [Fact]
    public void Pre_restore_copies_are_purged_only_once_they_are_a_week_old()
    {
        var copy = _paths.DatabasePath + RestoreBootstrap.PreRestoreSuffix;
        File.WriteAllText(copy, "old");

        RestoreBootstrap.PurgeStalePreRestoreCopies(_paths, TimeSpan.FromDays(7), NullLogger.Instance);
        Assert.True(File.Exists(copy));

        File.SetLastWriteTimeUtc(copy, DateTime.UtcNow.AddDays(-8));
        RestoreBootstrap.PurgeStalePreRestoreCopies(_paths, TimeSpan.FromDays(7), NullLogger.Instance);
        Assert.False(File.Exists(copy));
    }

    [Fact]
    public void Sidecar_copies_are_purged_with_the_database_copy_and_the_rename_restarts_the_clock()
    {
        ReleaseLiveDb();
        File.SetLastWriteTimeUtc(_paths.DatabasePath, DateTime.UtcNow.AddDays(-30));
        Directory.CreateDirectory(_paths.RestorePendingDir);
        File.WriteAllBytes(Path.Combine(_paths.RestorePendingDir, "maki.db"), ValidDatabase(_configDir, LastKnownMigration));

        RestoreBootstrap.ApplyPendingRestore(_paths, NullLogger.Instance);

        var main = _paths.DatabasePath + RestoreBootstrap.PreRestoreSuffix;
        Assert.True(File.GetLastWriteTimeUtc(main) > DateTime.UtcNow.AddMinutes(-5));

        var wal = _paths.DatabasePath + "-wal" + RestoreBootstrap.PreRestoreSuffix;
        File.WriteAllText(wal, "wal");
        File.SetLastWriteTimeUtc(wal, DateTime.UtcNow);
        File.SetLastWriteTimeUtc(main, DateTime.UtcNow.AddDays(-8));

        RestoreBootstrap.PurgeStalePreRestoreCopies(_paths, TimeSpan.FromDays(7), NullLogger.Instance);

        Assert.False(File.Exists(main));
        Assert.False(File.Exists(wal));
    }

    [Fact]
    public async Task A_key_revoked_after_the_backup_stays_revoked_after_the_restore()
    {
        var backupTaken = DateTime.UtcNow.AddDays(-2);
        _db.Users.Add(new Maki.Data.Identity.MakiUser { Id = 5, UserName = "ada", NormalizedUserName = "ADA" });
        _db.UserApiKeys.AddRange(
            new Maki.Data.Identity.UserApiKey
            {
                UserId = 5, Name = "late", KeyHash = "hash-late", Prefix = "late",
                CreatedAt = backupTaken.AddDays(-30), RevokedAt = DateTime.UtcNow.AddDays(-1)
            },
            new Maki.Data.Identity.UserApiKey
            {
                UserId = 5, Name = "early", KeyHash = "hash-early", Prefix = "early",
                CreatedAt = backupTaken.AddDays(-30), RevokedAt = backupTaken.AddDays(-10)
            });
        await _db.SaveChangesAsync();

        var backupDb = SqliteFile(_configDir, conn =>
        {
            Exec(conn, "PRAGMA journal_mode=WAL");
            Exec(conn, "CREATE TABLE __EFMigrationsHistory (MigrationId TEXT PRIMARY KEY, ProductVersion TEXT NOT NULL)");
            Exec(conn, $"INSERT INTO __EFMigrationsHistory VALUES ('{LastKnownMigration}', '10.0.0')");
            Exec(conn, "CREATE TABLE UserApiKeys (KeyHash TEXT PRIMARY KEY, RevokedAt TEXT NULL)");
            Exec(conn, "INSERT INTO UserApiKeys VALUES ('hash-late', NULL), ('hash-early', NULL)");
        });
        var manifest = JsonSerializer.Serialize(new BackupManifest("1.0.0", backupTaken, null, "manual"));

        await Build().StagePendingRestoreFromUploadAsync(
            new MemoryStream(Zip(backupDb, manifest)), CancellationToken.None);

        using var staged = new SqliteConnection(
            $"Data Source={Path.Combine(_paths.RestorePendingDir, "maki.db")};Mode=ReadOnly;Pooling=False");
        staged.Open();
        using var query = staged.CreateCommand();
        query.CommandText = "SELECT KeyHash, RevokedAt IS NOT NULL FROM UserApiKeys ORDER BY KeyHash";
        using var reader = query.ExecuteReader();
        var rows = new Dictionary<string, bool>();
        while (reader.Read()) rows[reader.GetString(0)] = reader.GetBoolean(1);

        Assert.True(rows["hash-late"]);
        Assert.False(rows["hash-early"]);
    }

    private void ReleaseLiveDb()
    {
        _db.Database.CloseConnection();
        SqliteConnection.ClearAllPools();
    }

    private static void AssertFail(IActionResult result, string key)
    {
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var code = bad.Value!.GetType().GetProperty("code")!.GetValue(bad.Value);
        Assert.Equal(key, code);
    }

    private static IFormFile Upload(byte[] bytes) =>
        new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "backup.zip");

    private static string Manifest() =>
        JsonSerializer.Serialize(new BackupManifest("1.0.0", DateTime.UtcNow, null, "manual"));

    private static byte[] Zip(byte[] db, string manifestJson)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var entry = zip.CreateEntry("maki.db").Open())
                entry.Write(db);
            using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
            writer.Write(manifestJson);
        }

        return ms.ToArray();
    }

    /// <summary>A WAL-mode SQLite file carrying an EF migration history whose last row is
    /// <paramref name="lastMigration"/>, like a snapshot of the live database.</summary>
    internal static byte[] ValidDatabase(string scratchDir, string lastMigration) =>
        SqliteFile(scratchDir, conn =>
        {
            Exec(conn, "PRAGMA journal_mode=WAL");
            Exec(conn, "CREATE TABLE __EFMigrationsHistory (MigrationId TEXT PRIMARY KEY, ProductVersion TEXT NOT NULL)");
            Exec(conn, $"INSERT INTO __EFMigrationsHistory VALUES ('{lastMigration}', '10.0.0')");
        });

    private static byte[] SqliteFile(string scratchDir, Action<SqliteConnection> build)
    {
        Directory.CreateDirectory(scratchDir);
        var path = Path.Combine(scratchDir, $"fixture-{Guid.NewGuid():N}.db");
        using (var conn = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            conn.Open();
            build(conn);
        }

        var bytes = File.ReadAllBytes(path);
        File.Delete(path);
        return bytes;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
