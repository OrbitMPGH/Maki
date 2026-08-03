using System.Security.Claims;
using System.Text.Encodings.Web;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Maki.Api.Auth;

/// <summary>
/// Authenticates a request by the per-user API key in the <c>X-Api-Key</c> header.
/// <para>
/// Deliberately does <b>not</b> accept the key as a query parameter, which the instance-wide key it
/// replaces did: a query string is written to browser history, sent onward in <c>Referer</c>, and
/// recorded by every reverse proxy in front of Maki. The two places that needed a credential in a
/// URL — reader page images in an <c>&lt;img&gt;</c> tag and the SignalR handshake — are same-origin
/// and carry the session cookie instead.
/// </para>
/// </summary>
public class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    MakiDbContext db,
    TimeProvider clock)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string HeaderName = "X-Api-Key";

    /// <summary>
    /// How stale <c>LastUsedAt</c> is allowed to get. Writing it on every request would turn one
    /// prefetching OPDS reader into hundreds of writes per chapter, for a column nothing but the
    /// account UI reads.
    /// </summary>
    private static readonly TimeSpan LastUsedGranularity = TimeSpan.FromMinutes(5);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var presented = Request.Headers[HeaderName].FirstOrDefault();
        if (string.IsNullOrEmpty(presented))
        {
            // No opinion — let the next scheme (or the fallback policy) decide.
            return AuthenticateResult.NoResult();
        }

        var hash = ApiKeyCrypto.Hash(presented);

        var match = await db.UserApiKeys
            .Where(k => k.KeyHash == hash && k.RevokedAt == null && k.Scope == UserApiKeyScope.Full)
            .Join(db.Users, k => k.UserId, u => u.Id, (k, u) => new
            {
                KeyId = k.Id,
                k.LastUsedAt,
                u.Id,
                u.UserName,
                u.Disabled,
                u.PendingSetup
            })
            .FirstOrDefaultAsync();

        // One generic failure for every reason: unknown key, revoked key, OPDS-scoped key used on
        // the management API, disabled account. Distinguishing them would tell an attacker which
        // guesses were closer.
        if (match is null || match.Disabled || match.PendingSetup)
        {
            return AuthenticateResult.Fail("Invalid API key");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        if (match.LastUsedAt is null || now - match.LastUsedAt > LastUsedGranularity)
        {
            await db.UserApiKeys
                .Where(k => k.Id == match.KeyId)
                .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now));
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, match.Id.ToString()),
                new Claim(ClaimTypes.Name, match.UserName ?? string.Empty)
            ],
            Scheme.Name);

        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
