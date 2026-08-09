using Maki.Core.Security;

namespace Maki.Api.Services;

/// <summary>
/// Decides which user a stats-shaped request is about. Absent or self is the ordinary case; naming
/// somebody else is the admin cross-user view and is refused otherwise.
/// <para>
/// Shared by <c>GamificationController</c> and <c>RewindController</c> rather than written once per
/// controller: it is the only gate on reading another account's reading history, and two copies of
/// a permission check drift. Every action that accepts a <c>userId</c> must funnel through here.
/// </para>
/// </summary>
public class UserViewResolver(ICurrentUser currentUser)
{
    /// <summary>
    /// Resolves <paramref name="requested"/> to a concrete user id. Returns false when the caller
    /// asked for somebody else without Admin — the controller answers 403 in that case.
    /// </summary>
    public bool TryResolve(int? requested, out int userId)
    {
        userId = requested ?? currentUser.UserId;
        return userId == currentUser.UserId || currentUser.Permissions.Grants(MakiPermission.Admin);
    }
}
