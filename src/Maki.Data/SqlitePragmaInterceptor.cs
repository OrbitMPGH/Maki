using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Maki.Data;

/// <summary>
/// Connection-level pragmas. SQLite keeps these per native handle and the pool can hand out a new
/// handle on any open, so they run on every open rather than once at startup. Each is a plain
/// setter with no I/O.
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    public static readonly SqlitePragmaInterceptor Instance = new();

    // synchronous=NORMAL is still corruption-safe under WAL and syncs at checkpoints instead of on
    // every commit. busy_timeout hands a contended write to SQLite's own backoff, where
    // Microsoft.Data.Sqlite alone sleeps 150 ms between retries. The page cache is per pooled
    // handle and the pool is unbounded (see HousekeepingJob), so it only doubles the 2 MB default.
    internal const string Pragmas =
        "PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-4000;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
