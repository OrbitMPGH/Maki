using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Maki.Data;

/// <summary>
/// Connection-level pragmas. SQLite keeps these per native handle, and the pool hands the same handle
/// back across many logical opens (EF opens and closes around every query), so they run once per
/// handle: the first open that sees it.
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

    // Keyed weakly on the native handle wrapper, which lives exactly as long as the pooled connection.
    private static readonly ConditionalWeakTable<object, object> Configured = new();
    private static readonly object Marker = new();

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        if (!NeedsPragmas(connection))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        command.ExecuteNonQuery();
        MarkConfigured(connection);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (!NeedsPragmas(connection))
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        await command.ExecuteNonQueryAsync(cancellationToken);
        MarkConfigured(connection);
    }

    private static bool NeedsPragmas(DbConnection connection) =>
        connection is not SqliteConnection { Handle: { } handle } || !Configured.TryGetValue(handle, out _);

    private static void MarkConfigured(DbConnection connection)
    {
        if (connection is SqliteConnection { Handle: { } handle })
        {
            Configured.AddOrUpdate(handle, Marker);
        }
    }
}
