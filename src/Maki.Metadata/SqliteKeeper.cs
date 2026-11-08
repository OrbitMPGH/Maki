using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Maki.Metadata;

/// <summary>
/// Holds one idle connection to a WAL database while Maki runs, so its <c>-shm</c> is created once
/// per run and not deleted and recreated until <see cref="Release"/> at shutdown or a
/// <see cref="Suspend"/> around replacing the file.
/// <para>
/// SQLite creates the <c>-shm</c> when the first connection in a process attaches and deletes it
/// when the last one closes. On Docker Desktop's virtiofs bind mounts a <c>-shm</c> created again
/// later in the same run can map short and kill the process with SIGBUS on the next write, and one
/// left behind by a read-only connection gets truncated under the next opener's mapping. Every
/// other connection attaching to the keeper's <c>-shm</c> avoids both. Linux only, and only for the
/// paths a starting host passed to <see cref="Allow"/>: tools and tests that use a store directly
/// keep SQLite's own behaviour, where the last close checkpoints into the file.
/// </para>
/// </summary>
public sealed class SqliteKeeper
{
    private static readonly ConcurrentDictionary<string, SqliteKeeper> All = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, bool> Allowed = new(StringComparer.Ordinal);

    private readonly string _path;
    private readonly bool _enabled;
    private readonly object _gate = new();
    private SqliteConnection? _connection;
    private int _suspended;
    private bool _attached;
    private bool _released;

    internal SqliteKeeper(string path, bool enabled)
    {
        _path = path;
        _enabled = enabled;
    }

    /// <summary>Where open failures are logged. Set at startup.</summary>
    public static ILogger? Logger { get; set; }

    /// <summary>
    /// The keeper for <paramref name="databasePath"/>. One that never opens unless the path was passed
    /// to <see cref="Allow"/>, which also means after <see cref="Release"/>, so a late opener at
    /// shutdown cannot recreate the <c>-shm</c>.
    /// </summary>
    public static SqliteKeeper For(string databasePath)
    {
        var path = Path.GetFullPath(databasePath);
        return Allowed.ContainsKey(path)
            ? All.GetOrAdd(path, p => new SqliteKeeper(p, OperatingSystem.IsLinux()))
            : new SqliteKeeper(path, enabled: false);
    }

    /// <summary>Lets <paramref name="databasePath"/> have a keeper, when a host starts.</summary>
    public static void Allow(string databasePath) => Allowed[Path.GetFullPath(databasePath)] = true;

    /// <summary>
    /// Closes and forgets the keeper for <paramref name="databasePath"/>, at shutdown. Closing it
    /// last checkpoints the database and removes its <c>-wal</c> and <c>-shm</c>, so clear the
    /// connection pools first.
    /// </summary>
    public static void Release(string databasePath)
    {
        var path = Path.GetFullPath(databasePath);
        Allowed.TryRemove(path, out _);
        if (All.TryRemove(path, out var keeper))
        {
            lock (keeper._gate)
            {
                keeper._released = true;
                keeper.CloseLocked();
            }
        }
    }

    internal bool IsOpen
    {
        get { lock (_gate) return _connection is not null; }
    }

    /// <summary>
    /// Opens the keeper if it is not open and reads through it, which attaches it once the database
    /// is in WAL mode. Call it before opening a connection of your own, and again right after
    /// switching a database to WAL while that connection is still open. A missing file is skipped.
    /// </summary>
    public void Attach()
    {
        if (!_enabled || Volatile.Read(ref _attached))
        {
            return;
        }

        lock (_gate)
        {
            if (_attached || _released || _suspended > 0 || !File.Exists(_path))
            {
                return;
            }

            if (_connection is null)
            {
                try
                {
                    _connection = new SqliteConnection($"Data Source={_path};Mode=ReadWrite;Pooling=False");
                    _connection.Open();
                }
                catch (SqliteException ex)
                {
                    Logger?.LogWarning("Could not keep {Path} open: {Error}", _path, ex.Message);
                    CloseLocked();
                    return;
                }
            }

            try
            {
                using var read = _connection.CreateCommand();
                read.CommandText = "SELECT count(*) FROM sqlite_master";
                read.CommandTimeout = 1;
                read.ExecuteScalar();

                using var mode = _connection.CreateCommand();
                mode.CommandText = "PRAGMA journal_mode";
                if (string.Equals(mode.ExecuteScalar() as string, "wal", StringComparison.OrdinalIgnoreCase))
                {
                    Volatile.Write(ref _attached, true);
                }
            }
            catch (SqliteException ex) when ((ex.SqliteErrorCode & 0xFF) is 5 or 6)
            {
                // Busy or locked: a writer holds the file. The next call tries the read again.
            }
            catch (SqliteException ex)
            {
                Logger?.LogWarning("Could not keep {Path} open: {Error}", _path, ex.Message);
                CloseLocked();
            }
        }
    }

    /// <summary>
    /// Closes the keeper and keeps it closed until the returned scope is disposed, for replacing
    /// the database file. The next <see cref="Attach"/> after that opens the new file.
    /// </summary>
    public IDisposable Suspend()
    {
        lock (_gate)
        {
            _suspended++;
            CloseLocked();
        }

        return new Resumer(this);
    }

    private void CloseLocked()
    {
        _connection?.Dispose();
        _connection = null;
        Volatile.Write(ref _attached, false);
    }

    private sealed class Resumer(SqliteKeeper keeper) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                lock (keeper._gate)
                {
                    keeper._suspended--;
                }
            }
        }
    }
}
