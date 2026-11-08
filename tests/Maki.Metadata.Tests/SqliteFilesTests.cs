using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Maki.Core.Io;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Maki.Metadata.Tests;

public class SqliteFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "maki-sqlitefiles-" + Guid.NewGuid().ToString("N"));

    public SqliteFilesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void An_abandoned_shm_is_deleted_and_the_rows_in_the_wal_survive()
    {
        var db = Path.Combine(_dir, "maki.db");
        LeaveWalBehind(db, rows: 50);
        var walLength = new FileInfo(db + "-wal").Length;

        var deleted = SqliteFiles.DeleteStaleSharedMemory(_dir);

        Assert.Equal([db + "-shm"], deleted);
        Assert.False(File.Exists(db + "-shm"));
        Assert.Equal(walLength, new FileInfo(db + "-wal").Length);
        Assert.Equal(50L, Count(db));
    }

    [Fact]
    public void An_shm_another_owner_holds_the_attach_lock_on_is_kept()
    {
        var db = Path.Combine(_dir, "maki.db");
        LeaveWalBehind(db, rows: 1);
        var shm = db + "-shm";

        using var holder = new FileStream(shm, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        if (!HoldAttachLock(holder))
        {
            return;
        }

        Assert.False(SqliteFiles.TryDeleteStaleSharedMemory(shm));
        Assert.True(File.Exists(shm));
    }

    /// <summary>
    /// The real hazard: another process holding SQLite's classic record lock. Needs python3, which
    /// takes the lock with fcntl the way SQLite does; skipped off Linux or without it.
    /// </summary>
    [Fact]
    public void An_shm_another_process_holds_a_read_lock_on_is_kept()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var db = Path.Combine(_dir, "maki.db");
        LeaveWalBehind(db, rows: 1);
        var shm = db + "-shm";

        Process child;
        try
        {
            var start = new ProcessStartInfo("python3")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(
                "import fcntl, sys; f = open(sys.argv[1], 'r+b'); fcntl.lockf(f, fcntl.LOCK_SH, 1, 128); "
                + "print('locked', flush=True); sys.stdin.read()");
            start.ArgumentList.Add(shm);
            child = Process.Start(start)!;
        }
        catch (Win32Exception)
        {
            return;
        }

        using (child)
        {
            try
            {
                Assert.Equal("locked", child.StandardOutput.ReadLine());
                Assert.False(SqliteFiles.TryDeleteStaleSharedMemory(shm));
                Assert.True(File.Exists(shm));
            }
            finally
            {
                child.StandardInput.Close();
                if (!child.WaitForExit(5000))
                {
                    child.Kill();
                    child.WaitForExit();
                }
            }
        }

        Assert.True(SqliteFiles.TryDeleteStaleSharedMemory(shm));
    }

    [Fact]
    public void A_keeper_opens_only_between_allow_and_release()
    {
        var db = Path.Combine(_dir, "allowed.db");
        SqliteFiles.CreateIfMissing(db);

        var before = SqliteKeeper.For(db);
        before.Attach();
        Assert.False(before.IsOpen);
        Assert.NotSame(before, SqliteKeeper.For(db));

        SqliteKeeper.Allow(db);
        var kept = SqliteKeeper.For(db);
        Assert.Same(kept, SqliteKeeper.For(db));
        kept.Attach();
        Assert.Equal(OperatingSystem.IsLinux(), kept.IsOpen);

        SqliteKeeper.Release(db);
        kept.Attach();
        var late = SqliteKeeper.For(db);
        late.Attach();

        Assert.False(kept.IsOpen);
        Assert.False(late.IsOpen);
    }

    [Fact]
    public void IsUnattached_tells_a_database_nothing_has_open_from_one_held()
    {
        var db = Path.Combine(_dir, "maki.db");
        Assert.True(SqliteFiles.IsUnattached(db));
        LeaveWalBehind(db, rows: 1);
        Assert.Equal(!OperatingSystem.IsMacOS(), SqliteFiles.IsUnattached(db));

        using var holder = new FileStream(db + "-shm", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        if (!HoldAttachLock(holder))
        {
            return;
        }

        Assert.False(SqliteFiles.IsUnattached(db));
        Assert.True(File.Exists(db + "-shm"));
    }

    [Fact]
    public void A_missing_directory_or_file_deletes_nothing()
    {
        Assert.Empty(SqliteFiles.DeleteStaleSharedMemory(Path.Combine(_dir, "absent")));
        Assert.False(SqliteFiles.TryDeleteStaleSharedMemory(Path.Combine(_dir, "absent.db-shm")));
    }

    [Fact]
    public void CreateIfMissing_makes_an_empty_database_and_leaves_an_existing_one_alone()
    {
        var db = Path.Combine(_dir, "new.db");

        SqliteFiles.CreateIfMissing(db);

        Assert.Equal(0, new FileInfo(db).Length);
        using (var conn = new SqliteConnection($"Data Source={db};Pooling=False"))
        {
            conn.Open();
            Execute(conn, "CREATE TABLE t (x INTEGER); INSERT INTO t VALUES (1);");
        }

        var length = new FileInfo(db).Length;
        SqliteFiles.CreateIfMissing(db);

        Assert.Equal(length, new FileInfo(db).Length);
        Assert.Equal(1L, Count(db));
    }

    [Fact]
    public void The_keeper_holds_the_shm_until_it_is_suspended()
    {
        var db = Path.Combine(_dir, "embeddings.db");
        using (var setup = new SqliteConnection($"Data Source={db};Pooling=False"))
        {
            setup.Open();
            Execute(setup, "PRAGMA journal_mode=WAL; CREATE TABLE t (x INTEGER);");
        }

        var keeper = new SqliteKeeper(db, enabled: true);
        keeper.Attach();
        using (var reader = new SqliteConnection($"Data Source={db};Mode=ReadOnly;Pooling=False"))
        {
            reader.Open();
            Execute(reader, "SELECT COUNT(*) FROM t");
        }

        Assert.True(keeper.IsOpen);
        Assert.True(File.Exists(db + "-shm"));

        using (keeper.Suspend())
        {
            Assert.False(keeper.IsOpen);
            Assert.False(File.Exists(db + "-shm"));
            keeper.Attach();
            Assert.False(keeper.IsOpen);
        }

        keeper.Attach();
        Assert.True(keeper.IsOpen);
        keeper.Suspend();
    }

    [Fact]
    public void The_keeper_skips_a_missing_file_and_does_nothing_when_disabled()
    {
        var missing = new SqliteKeeper(Path.Combine(_dir, "absent.db"), enabled: true);
        missing.Attach();
        Assert.False(missing.IsOpen);
        Assert.False(File.Exists(Path.Combine(_dir, "absent.db")));

        var db = Path.Combine(_dir, "present.db");
        SqliteFiles.CreateIfMissing(db);
        var disabled = new SqliteKeeper(db, enabled: false);
        disabled.Attach();
        Assert.False(disabled.IsOpen);
    }

    /// <summary>
    /// What a killed process leaves: a WAL database with committed rows still in the -wal and the
    /// -shm on disk. A read-only connection that closes last cannot checkpoint, so it leaves both.
    /// </summary>
    private static void LeaveWalBehind(string db, int rows)
    {
        using (var writer = new SqliteConnection($"Data Source={db};Pooling=False"))
        {
            writer.Open();
            Execute(writer, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE t (x INTEGER);");
            for (var i = 0; i < rows; i++)
            {
                Execute(writer, $"INSERT INTO t VALUES ({i})");
            }

            using var reader = new SqliteConnection($"Data Source={db};Mode=ReadOnly;Pooling=False");
            reader.Open();
            Execute(reader, "SELECT COUNT(*) FROM t");
            writer.Close();
        }

        Assert.True(File.Exists(db + "-shm"));
        Assert.True(new FileInfo(db + "-wal").Length > 0);
    }

    /// <summary>
    /// Takes a read lock on SQLite's attach byte as an owner other than this handle's later opens.
    /// Windows locks belong to the handle. Linux record locks belong to the process, so this takes an
    /// open-file-description lock, which conflicts with this process's own record locks.
    /// </summary>
    private static bool HoldAttachLock(FileStream holder)
    {
        if (OperatingSystem.IsWindows())
        {
            holder.Lock(128, 1);
            return true;
        }

        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
        {
            return false;
        }

        var flock = new Flock { Type = 0, Whence = 0, Start = 128, Length = 1 };
        Assert.Equal(0, Fcntl((int)holder.SafeFileHandle.DangerousGetHandle(), 37, ref flock));
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Flock
    {
        public short Type;
        public short Whence;
        public long Start;
        public long Length;
        public int Pid;
    }

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int Fcntl(int fd, int cmd, ref Flock flock);

    private static long Count(string db)
    {
        using var conn = new SqliteConnection($"Data Source={db};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM t";
        return (long)cmd.ExecuteScalar()!;
    }

    private static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
