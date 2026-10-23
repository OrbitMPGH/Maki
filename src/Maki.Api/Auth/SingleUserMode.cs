using Maki.Core.Configuration;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Auth;

/// <summary>
/// Whether the SPA hides the surfaces that only make sense with several people on the instance.
/// On by default, and only ever on while a single account is enabled, so creating a second account
/// brings everything back without anyone touching the setting.
/// </summary>
public static class SingleUserMode
{
    public static Task<int> EnabledAccountsAsync(MakiDbContext db, CancellationToken ct) =>
        db.Users.CountAsync(u => !u.Disabled, ct);

    public static async Task<bool> IsOnAsync(MakiDbContext db, IAppSettings? settings, CancellationToken ct)
    {
        if (settings is not null && await settings.GetAsync(SettingKeys.SingleUserMode, ct) == "false")
        {
            return false;
        }
        return await EnabledAccountsAsync(db, ct) <= 1;
    }
}
