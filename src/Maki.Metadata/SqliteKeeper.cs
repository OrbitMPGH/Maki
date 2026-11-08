using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace Maki.Metadata;

/// <summary>
/// Holds one idle connection to a WAL database for the life of the process, so its <c>-shm</c> is
/// created once and never deleted and recreated while Maki runs.
/// <para>
/// SQLite creates the <c>-shm</c> when the first connection in a process attaches and deletes it
/// when the last one closes. On Docker Desktop's virtiofs bind mounts a <c>-shm</c> created again
/// later in the same run can map short and kill the process with SIGBUS on the next write, and one
/// left behind by a read-only connection gets truncated under the next opener's mapping. Every
/// other connection attaching to the keeper's <c>-shm</c> avoids both. Linux only: elsewhere this
/// does nothing.
/// </para>
/// </summary>
public sealed class SqliteKeeper
{
    private static readonly ConcurrentDictionary<string, SqliteKeeper> All = new(StringComparer.Ordinal);

    private readonly string _path;
    private readonly bool _enabled;
    private readonly object _gate = new();
    private SqliteConnection? _connection;
    private int _suspended;

    internal SqliteKeeper(string path, bool enabled)
    {
        _path = path;
        _enabled = enabled;
    }

    public static SqliteKeeper For(string databasePath) =>
        All.GetOrAdd(Path.GetFullPath(databasePath), path => new SqliteKeeper(path, OperatingSystem.IsLinux()));

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
        if (!_enabled)
        {
            return;
        }

        lock (_gate)
        {
            if (_suspended > 0 || !File.Exists(_path))
            {
                return;
            }

            try
            {
                if (_connection is null)
                {
                    _connection = new SqliteConnection($"Data Source={_path};Mode=ReadWrite;Pooling=False");
                    _connection.Open();
                }

                using var read = _connection.CreateCommand();
                read.CommandText = "SELECT count(*) FROM sqlite_master";
                read.CommandTimeout = 1;
                read.ExecuteScalar();
            }
            catch (SqliteException)
            {
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
