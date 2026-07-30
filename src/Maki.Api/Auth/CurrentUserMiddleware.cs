using System.Security.Claims;
using Maki.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Auth;

/// <summary>
/// Resolves the authenticated principal into a <see cref="CurrentUserContext"/> snapshot loaded from
/// the database, and refuses the request outright if the account is no longer usable.
/// <para>
/// Must run after <c>UseAuthentication</c> and before <c>UseAuthorization</c> — the permission
/// handler reads the snapshot this populates.
/// </para>
/// <para>
/// The disabled/unclaimed check here is a hard backstop, not the primary mechanism. Identity's
/// security stamp validator already invalidates a disabled user's cookie, but only when its
/// validation interval elapses; this closes that window on every request, which is what makes
/// "disable this account" mean *now* rather than *within a minute*.
/// </para>
/// </summary>
public class CurrentUserMiddleware(RequestDelegate next)
{
    /// <param name="scope">
    /// The request's data scope. This middleware is the <em>only</em> place a request narrows it, which
    /// is what lets every user-owned table carry a global query filter instead of asking each of the
    /// dozens of call sites to remember a <c>WHERE UserId = …</c>. An anonymous request is narrowed to
    /// nobody rather than left wide open, so an allow-anonymous endpoint cannot read library data even
    /// by accident.
    /// </param>
    public async Task InvokeAsync(
        HttpContext context, CurrentUserContext current, DataScope scope, MakiDbContext db)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            scope.SetNobody();
            await next(context);
            return;
        }

        var raw = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(raw, out var userId))
        {
            // Authenticated but carrying no usable subject. This must be a refusal, not a pass-through:
            // the authorization fallback policy only asks whether the *principal* is authenticated, so
            // falling through would satisfy it while ICurrentUser stayed anonymous — every endpoint
            // that relies on the fallback alone would then run with UserId 0. Nothing produces such a
            // principal today (both schemes write the integer key), but an external identity provider
            // whose subject is not an integer would, and the failure mode would be silent.
            await RejectAsync(context);
            return;
        }

        var row = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new
            {
                u.Id,
                u.UserName,
                u.Permissions,
                u.AllRootFolders,
                u.MaxContentRating,
                u.Disabled,
                u.PendingSetup
            })
            .FirstOrDefaultAsync(context.RequestAborted);

        if (row is null || row.Disabled || row.PendingSetup)
        {
            // Deleted, suspended, or never claimed.
            await RejectAsync(context);
            return;
        }

        // Two queries rather than one projection with a correlated collection: EF Core compiles a
        // collection inside a projection in ways SQLite does not always support, and this second
        // query does not run at all for the common "sees everything" case.
        IReadOnlySet<int> folders = row.AllRootFolders
            ? new HashSet<int>()
            : (await db.UserRootFolders
                .Where(g => g.UserId == userId)
                .Select(g => g.RootFolderId)
                .ToListAsync(context.RequestAborted))
                .ToHashSet();

        current.Set(
            row.Id,
            row.UserName ?? string.Empty,
            row.Permissions,
            row.AllRootFolders,
            folders,
            row.MaxContentRating);

        // Same facts, second consumer: CurrentUserContext answers "may this caller do X?" for the
        // authorization handlers, DataScope answers "which rows exist?" for the DbContext. Set from one
        // load so the two can never disagree.
        scope.SetUser(row.Id, row.AllRootFolders);

        await next(context);
    }

    /// <summary>
    /// Signs the cookie out so the browser stops presenting it, then answers 401 with a body that
    /// does not say which of the several reasons applied.
    /// </summary>
    private static async Task RejectAsync(HttpContext context)
    {
        await context.SignOutAsync(Microsoft.AspNetCore.Identity.IdentityConstants.ApplicationScheme);
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
    }
}
