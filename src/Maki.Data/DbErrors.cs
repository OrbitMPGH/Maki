using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Maki.Data;

public static class DbErrors
{
    /// <summary>
    /// Whether a SQLite unique or primary-key constraint failed (2067 / 1555). Matched on the
    /// extended code, never the primary 19, which also covers foreign-key, NOT NULL and CHECK
    /// failures that no retry can fix. Accepts the base <see cref="Exception"/> because
    /// <c>SaveChangesAsync</c> wraps the provider error in a <see cref="DbUpdateException"/> while
    /// <c>ExecuteUpdateAsync</c> lets it through unwrapped.
    /// </summary>
    public static bool IsUniqueViolation(Exception e) => e switch
    {
        DbUpdateException { InnerException: SqliteException { SqliteExtendedErrorCode: 2067 or 1555 } } => true,
        SqliteException { SqliteExtendedErrorCode: 2067 or 1555 } => true,
        _ => false,
    };

    /// <summary>Whether a SQLite foreign-key constraint failed (787), such as a row whose parent was just deleted.</summary>
    public static bool IsForeignKeyViolation(Exception e) => e switch
    {
        DbUpdateException { InnerException: SqliteException { SqliteExtendedErrorCode: 787 } } => true,
        SqliteException { SqliteExtendedErrorCode: 787 } => true,
        _ => false,
    };
}
