using Maki.Core.Security;
using Maki.Data;

namespace Maki.Api.Services;

/// <summary>
/// Repeats the user-owned query filter as a plain predicate, for hot reads over large per-user tables.
/// <para>
/// The global filter is <c>Unrestricted || UserId == scope.UserId</c>, and EF sends
/// <c>Unrestricted</c> as a parameter, so SQLite sees <c>@p OR "UserId" = @q</c>. Its OR optimisation
/// needs every branch to be an indexable column term, so that WHERE cannot seek an index that leads
/// with <c>UserId</c> and the query scans the whole table instead. An extra <c>AND UserId = @q</c>
/// gives the planner a term it can use. Security still comes from the filter; this only adds the same
/// narrowing in a shape the planner understands, and adds nothing for an unrestricted scope.
/// </para>
/// </summary>
public static class UserScopedQuery
{
    public static IQueryable<T> OwnedByScopeUser<T>(this IQueryable<T> query, MakiDbContext db)
        where T : class, IUserOwned
    {
        if (db.Scope.Unrestricted)
        {
            return query;
        }

        var userId = db.Scope.UserId;
        return query.Where(x => x.UserId == userId);
    }
}
