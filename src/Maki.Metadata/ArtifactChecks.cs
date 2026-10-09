using Microsoft.Data.Sqlite;

namespace Maki.Metadata;

/// <summary>Checks every downloaded artifact runs before it replaces the live file.</summary>
internal static class ArtifactChecks
{
    /// <summary>
    /// Refuses a file that holds per-user reading tables. The reading-list artifacts are derived from
    /// a working database with one row per user per series read, which sits in the same folder as the
    /// export on the machine that builds them. Refusing it does not undo a publish, but it stops every
    /// install that would otherwise download and keep a copy. Matched by prefix rather than by a list
    /// of names, so a working table nobody remembered to list is caught too.
    /// </summary>
    public static void RefusePerUserTables(SqliteConnection conn, string workingDatabaseOwner)
    {
        using var personal = conn.CreateCommand();
        personal.CommandText =
            """
            SELECT COUNT(*) FROM sqlite_master
            WHERE type = 'table' AND (name LIKE 'user\_%' ESCAPE '\' OR name = 'pending_user')
            """;
        if (personal.ExecuteScalar() is long found && found > 0)
        {
            throw new InvalidOperationException(
                $"downloaded file holds per-user reading tables; this is the {workingDatabaseOwner} working "
                + "database, not an export, and it must not be distributed");
        }
    }

    public static void RequireIntegrity(SqliteConnection conn, string what)
    {
        using var check = conn.CreateCommand();
        check.CommandText = "PRAGMA quick_check";
        check.CommandTimeout = 600;
        var result = check.ExecuteScalar()?.ToString();
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"downloaded {what} failed its integrity check ({result})");
        }
    }
}
