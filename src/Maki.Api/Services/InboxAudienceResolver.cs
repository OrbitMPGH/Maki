using System.Linq.Expressions;
using Maki.Core.Inbox;
using Maki.Core.Security;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Turns an <see cref="InboxAudience"/> into the user ids that should receive a notification.
/// <para>
/// Every query here runs with <c>IgnoreQueryFilters</c> and names its user explicitly. The callers
/// are background jobs and singletons whose <see cref="DataScope"/> is unrestricted only by
/// convention, and "resolve who else should see this" is the one question a per-user filter can
/// never answer correctly — see <c>OpdsAccessService</c> for the same reasoning.
/// </para>
/// </summary>
public class InboxAudienceResolver(IServiceScopeFactory scopeFactory)
{
    /// <summary>
    /// Accounts that can be signed into. Kept as an expression so it composes into a query rather
    /// than filtering in memory, and so the three branches below cannot drift apart on it.
    /// </summary>
    private static Expression<Func<MakiUser, bool>> Usable => u => !u.Disabled && !u.PendingSetup;

    public async Task<IReadOnlyList<int>> ResolveAsync(InboxAudience audience, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();

        return audience.Kind switch
        {
            InboxAudienceKind.User => await UserAsync(db, audience.UserId, ct),
            InboxAudienceKind.Admins => await AdminsAsync(db, rootFolderId: null, ct),
            InboxAudienceKind.SeriesTrackers =>
                await SeriesTrackersAsync(db, audience.SeriesId, audience.RootFolderId, ct),
            _ => []
        };
    }

    private static async Task<IReadOnlyList<int>> UserAsync(MakiDbContext db, int userId, CancellationToken ct)
    {
        if (userId <= 0)
        {
            return [];
        }

        return await db.Users
            .IgnoreQueryFilters()
            .Where(Usable)
            .Where(u => u.Id == userId)
            .Select(u => u.Id)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Every usable admin, optionally narrowed to those who can see a root folder. Tests the
    /// <c>Admin</c> bit directly rather than through <c>MakiPermissions.Grants</c> because this has
    /// to translate to SQL; <c>Admin</c> is the one flag where the bare bit test and the grant
    /// semantics agree, since <c>Grants</c> only widens <em>from</em> Admin.
    /// </summary>
    private static async Task<IReadOnlyList<int>> AdminsAsync(MakiDbContext db, int? rootFolderId, CancellationToken ct)
    {
        var query = db.Users
            .IgnoreQueryFilters()
            .Where(Usable)
            .Where(u => (u.Permissions & MakiPermission.Admin) != 0);

        if (rootFolderId is { } folder)
        {
            query = query.Where(u =>
                u.AllRootFolders ||
                db.UserRootFolders.Any(g => g.UserId == u.Id && g.RootFolderId == folder));
        }

        return await query.Select(u => u.Id).ToListAsync(ct);
    }

    /// <summary>
    /// Whoever has a stake in a series: they are reading it, have read it, or asked for it. Narrowed
    /// to accounts that can see the series' root folder, because a grant revoked after somebody read
    /// a chapter must stop the notifications too.
    /// <para>
    /// Falls back to the admins who can see that folder when nobody tracks it. A series added an hour
    /// ago that nobody has opened yet is exactly the case where "your download finished" is worth
    /// sending, and silently dropping it would make the feature look broken on a fresh install.
    /// </para>
    /// <para>
    /// "Whoever added it" is deliberately not part of the rule: <c>Series</c> records no creator, and
    /// the request branch already covers the case where somebody asked for it by name.
    /// </para>
    /// </summary>
    private static async Task<IReadOnlyList<int>> SeriesTrackersAsync(
        MakiDbContext db, int seriesId, int rootFolderId, CancellationToken ct)
    {
        var readers = db.ChapterProgress.IgnoreQueryFilters()
            .Where(p => p.SeriesId == seriesId)
            .Select(p => p.UserId);

        var states = db.ReadingStates.IgnoreQueryFilters()
            .Where(s => s.SeriesId == seriesId)
            .Select(s => s.UserId);

        var requesters = db.SeriesRequests.IgnoreQueryFilters()
            .Where(r => r.SeriesId == seriesId)
            .Select(r => r.UserId);

        var trackers = readers.Union(states).Union(requesters);

        var ids = await db.Users
            .IgnoreQueryFilters()
            .Where(Usable)
            .Where(u => trackers.Contains(u.Id))
            .Where(u =>
                u.AllRootFolders ||
                db.UserRootFolders.Any(g => g.UserId == u.Id && g.RootFolderId == rootFolderId))
            .Select(u => u.Id)
            .ToListAsync(ct);

        return ids.Count > 0 ? ids : await AdminsAsync(db, rootFolderId, ct);
    }
}
