using Maki.Api.Dtos;
using Maki.Core.Security;
using Maki.Data.Identity;

namespace Maki.Api.Auth;

/// <summary>Shared projection so <c>/auth/me</c> and the admin user list cannot drift apart.</summary>
public static class UserDtoMapper
{
    /// <summary>
    /// The permission flags as names, with <see cref="MakiPermission.Admin"/> expanded to the full
    /// set. The client would otherwise have to know that Admin implies everything, and one client
    /// forgetting that is a control greyed out for the one account that certainly may use it.
    /// </summary>
    public static IReadOnlyList<string> Names(MakiPermission permissions) =>
        Enum.GetValues<MakiPermission>()
            .Where(p => p != MakiPermission.None && permissions.Grants(p))
            .Select(p => p.ToString())
            .ToList();

    public static MeDto ToMe(MakiUser user, IReadOnlyList<int> rootFolderIds, bool oidcLinked) => new(
        user.Id,
        user.UserName ?? string.Empty,
        user.DisplayName,
        user.Permissions,
        Names(user.Permissions),
        user.Permissions.Grants(MakiPermission.Admin),
        user.MaxContentRating,
        user.AllRootFolders,
        rootFolderIds,
        user.TwoFactorEnabled,
        oidcLinked);

    public static UserSummaryDto ToSummary(MakiUser user, IReadOnlyList<int> rootFolderIds) => new(
        user.Id,
        user.UserName ?? string.Empty,
        user.DisplayName,
        user.Permissions,
        Names(user.Permissions),
        user.Permissions.Grants(MakiPermission.Admin),
        user.MaxContentRating,
        user.AllRootFolders,
        rootFolderIds,
        user.Disabled,
        user.PendingSetup,
        user.TwoFactorEnabled,
        user.CreatedAt,
        user.LastLoginAt);
}
