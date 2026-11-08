using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Maki.Data;

/// <summary>
/// EF Core holds a row in <c>__EFMigrationsLock</c> for the length of <c>Migrate()</c> and leaves it
/// behind when the process dies inside it. Every later start then waits on that row forever.
/// </summary>
public enum MigrationLockState
{
    None,
    Cleared,
    /// <summary>A row newer than this process: someone starting alongside may hold it.</summary>
    KeptNewer,
}

public static class MigrationLock
{
    private const string Table = "__EFMigrationsLock";

    /// <summary>
    /// Deletes a lock row taken before <paramref name="startedAt"/>, which a process starting at the
    /// same moment cannot have written. Call it only when no other process had the database open.
    /// </summary>
    public static MigrationLockState ClearAbandoned(DatabaseFacade database, DateTimeOffset startedAt)
    {
        var exists = database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS \"Value\" FROM \"sqlite_master\" WHERE \"type\" = 'table' AND \"name\" = '" + Table + "'")
            .AsEnumerable()
            .Single() > 0;
        if (!exists)
        {
            return MigrationLockState.None;
        }

        var rows = database.SqlQueryRaw<string>("SELECT \"Timestamp\" AS \"Value\" FROM \"" + Table + "\"").AsEnumerable().ToList();
        var stale = rows
            .Where(t => DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) && at < startedAt)
            .ToList();
        var cleared = 0;
        foreach (var timestamp in stale)
        {
            cleared += database.ExecuteSqlRaw("DELETE FROM \"" + Table + "\" WHERE \"Timestamp\" = {0}", timestamp);
        }

        return cleared > 0 ? MigrationLockState.Cleared
            : rows.Count > 0 ? MigrationLockState.KeptNewer
            : MigrationLockState.None;
    }
}
