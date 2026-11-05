using Maki.Core.Configuration;
using Maki.Core.Security;

namespace Maki.Api.Services;

/// <summary>
/// What one user may see of another's name: a display name is public, a login name is not unless an
/// admin made login names public (<see cref="SettingKeys.LoginNamesVisible"/>).
/// </summary>
public static class UserNameVisibility
{
    /// <summary>
    /// The display name, else the login name when the reader is an admin, is that user, or the instance
    /// shows login names, else null. Callers render null as a generic "another user" label.
    /// </summary>
    public static string? For(
        ICurrentUser reader, int userId, string? displayName, string? loginName, bool loginNamesVisible = false) =>
        ShownName(displayName, loginName, loginNamesVisible || reader.Has(MakiPermission.Admin) || userId == reader.UserId);

    /// <summary>The display name unless it is blank, else the login name when <paramref name="loginNameAllowed"/>.</summary>
    public static string? ShownName(string? displayName, string? loginName, bool loginNameAllowed) =>
        !string.IsNullOrWhiteSpace(displayName) ? displayName : loginNameAllowed ? loginName : null;

    public static async Task<bool> LoginNamesVisibleAsync(IAppSettings? settings, CancellationToken ct) =>
        settings is not null && await settings.GetAsync(SettingKeys.LoginNamesVisible, ct) == "true";
}
