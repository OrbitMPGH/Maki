using Maki.Core.Security;

namespace Maki.Api.Services;

/// <summary>What one user may see of another's name: a display name is public, a login name is not.</summary>
public static class UserNameVisibility
{
    /// <summary>
    /// The display name, else the login name when the reader is an admin or is that user, else null.
    /// Callers render null as a generic "another user" label.
    /// </summary>
    public static string? For(ICurrentUser reader, int userId, string? displayName, string? loginName) =>
        displayName ?? (reader.Has(MakiPermission.Admin) || userId == reader.UserId ? loginName : null);
}
