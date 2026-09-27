using Maki.Core.Entities;
using Maki.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Auth;

/// <summary>
/// One-time rewrite of every <c>oidc</c>-provider <c>AspNetUserLogins</c> row from the pre-#15
/// bare-<c>sub</c> key into the issuer-scoped form (<see cref="OidcClaimMapper.ScopedProviderKey"/>).
/// Runs at startup, after <see cref="OidcRuntimeOptions"/> has loaded from <c>AppConfig</c> (a
/// migration runs too early to know the authority), gated by <see cref="MarkerKey"/> so it fires
/// exactly once, against whatever authority is configured at that point. A real subject claim can
/// itself contain <c>|</c> (Auth0, Google), so every row for the provider is rewritten unconditionally
/// rather than only ones that look unscoped.
/// </summary>
public class OidcLoginIssuerRepairService(MakiDbContext db, OidcRuntimeOptions oidc, ILogger<OidcLoginIssuerRepairService> logger)
{
    public const string MarkerKey = "auth.oidcLoginIssuerRepairDone";

    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        if (await db.AppConfig.AnyAsync(c => c.Key == MarkerKey, ct))
        {
            return;
        }

        if (oidc.Authority.Length == 0)
        {
            logger.LogInformation("OIDC login issuer repair postponed: no authority is configured yet");
            return;
        }

        var logins = await db.UserLogins
            .Where(l => l.LoginProvider == AuthSchemes.Oidc)
            .ToListAsync(ct);

        // ProviderKey is half of AspNetUserLogins' composite key, so EF refuses to mark it modified
        // in place ("part of a key and so cannot be modified"). Replace the row instead: remove the
        // old key, add the new one back with the same UserId/ProviderDisplayName.
        foreach (var login in logins)
        {
            db.UserLogins.Remove(login);
            db.UserLogins.Add(new IdentityUserLogin<int>
            {
                LoginProvider = login.LoginProvider,
                ProviderKey = OidcClaimMapper.ScopedProviderKey(oidc, login.ProviderKey),
                ProviderDisplayName = login.ProviderDisplayName,
                UserId = login.UserId
            });
        }

        db.AppConfig.Add(new AppConfigEntry { Key = MarkerKey, Value = DateTime.UtcNow.ToString("O") });
        await db.SaveChangesAsync(ct);

        if (logins.Count > 0)
        {
            logger.LogInformation(
                "Scoped {Count} existing single sign-on login(s) to the configured authority", logins.Count);
        }
    }
}
