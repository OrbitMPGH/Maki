using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Maki.Data;

/// <summary>
/// EF Core holds a row in <c>__EFMigrationsLock</c> for the length of <c>Migrate()</c> and leaves it
/// behind when the process dies inside it. Every later start then waits on that row forever.
/// </summary>
public static class MigrationLock
{
    private const string Table = "__EFMigrationsLock";

    /// <summary>
    /// Deletes a lock row left by a run that stopped while migrating. Returns whether there was one.
    /// Call it only when no other process can be migrating the database.
    /// </summary>
    public static bool ClearAbandoned(DatabaseFacade database)
    {
        var exists = database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS \"Value\" FROM \"sqlite_master\" WHERE \"type\" = 'table' AND \"name\" = '" + Table + "'")
            .AsEnumerable()
            .Single() > 0;
        return exists && database.ExecuteSqlRaw("DELETE FROM \"" + Table + "\"") > 0;
    }
}
