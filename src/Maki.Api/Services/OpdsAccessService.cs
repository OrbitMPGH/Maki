using System.Security.Cryptography;
using System.Text;
using Maki.Core.Configuration;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// The OPDS catalogue's three settings, read together.
/// <para>
/// A value object rather than three <c>IAppSettings</c> lookups because every page image a
/// streaming reader fetches needs all of them, and <see cref="SettingsService"/> opens a fresh DI
/// scope and DbContext per key — three round trips per page, hundreds per chapter once a reader
/// starts prefetching.
/// </para>
/// </summary>
public record OpdsAccess(bool Enabled, string? Token, bool TrackProgress)
{
    /// <summary>
    /// Whether <paramref name="provided"/> is the configured token of an enabled catalogue.
    /// <para>
    /// Compared in fixed time. The usual argument against bothering — "an attacker only gets one
    /// guess per human typing it" — doesn't hold here: this token sits in a URL that automated
    /// clients replay, so it is exactly the kind of secret worth comparing carefully.
    /// </para>
    /// </summary>
    public bool Allows(string? provided) =>
        Enabled &&
        !string.IsNullOrEmpty(Token) &&
        !string.IsNullOrEmpty(provided) &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(Token));
}

/// <summary>Reads <see cref="OpdsAccess"/> straight off the config table, in one query.</summary>
public class OpdsAccessService(MakiDbContext db)
{
    public async Task<OpdsAccess> ReadAsync(CancellationToken ct)
    {
        var rows = await db.AppConfig
            .AsNoTracking()
            .Where(c => c.Key == SettingKeys.OpdsEnabled
                || c.Key == SettingKeys.OpdsToken
                || c.Key == SettingKeys.OpdsTrackProgress)
            .ToDictionaryAsync(c => c.Key, c => c.Value, ct);

        return new OpdsAccess(
            rows.GetValueOrDefault(SettingKeys.OpdsEnabled) == "true",
            rows.GetValueOrDefault(SettingKeys.OpdsToken),
            // Absent means on: progress tracking is the default, and only an explicit "false" is
            // the user having turned it off.
            rows.GetValueOrDefault(SettingKeys.OpdsTrackProgress) != "false");
    }
}
