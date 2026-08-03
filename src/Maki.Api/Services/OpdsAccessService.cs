using Maki.Api.Auth;
using Maki.Core.Configuration;
using Maki.Core.Security;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// A resolved OPDS request: the catalogue is enabled and the token in the path belongs to a usable
/// account. <see cref="UserId"/> is who the reading is attributed to.
/// </summary>
public record OpdsAccess(bool TrackProgress, int UserId, bool AllRootFolders);

/// <summary>
/// Resolves the token in an OPDS URL to the user it belongs to.
/// <para>
/// The token is a <see cref="UserApiKey"/> row scoped to <see cref="UserApiKeyScope.Opds"/>, looked up
/// by the SHA-256 digest of what the caller presented. That replaces the old single instance-wide
/// <c>opds.token</c> setting and, incidentally, removes the need for the fixed-time comparison that
/// used to live here: nothing is compared against a stored secret any more, because no stored secret
/// exists — only its digest, matched by an index.
/// </para>
/// <para>
/// Two queries rather than one, and worth knowing why: the catalogue switches live in
/// <c>AppConfig</c> and the credential lives in <c>UserApiKeys</c>, so there is no single statement
/// that reads both. Both are indexed point lookups and this runs on every page image a streaming
/// reader fetches, which is also why it does not go through <see cref="IAppSettings"/> — that opens a
/// fresh scope and DbContext per key, three round trips where this needs one.
/// </para>
/// </summary>
public class OpdsAccessService(MakiDbContext db, TimeProvider clock)
{
    /// <summary>
    /// How stale a key's <c>LastUsedAt</c> may get. A prefetching reader would otherwise turn one
    /// chapter into a write per page.
    /// </summary>
    private static readonly TimeSpan LastUsedGranularity = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The resolved access, or null when the catalogue is disabled, the token is unknown or revoked,
    /// or its owner can no longer use OPDS. Callers turn null into <b>404</b>, never 401 — a disabled
    /// catalogue must not confirm that it exists.
    /// </summary>
    public async Task<OpdsAccess?> ResolveAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        var hash = ApiKeyCrypto.Hash(token);
        var match = await db.UserApiKeys
            .Where(k => k.KeyHash == hash && k.RevokedAt == null && k.Scope == UserApiKeyScope.Opds)
            .Join(db.Users, k => k.UserId, u => u.Id, (k, u) => new
            {
                KeyId = k.Id,
                k.LastUsedAt,
                u.Id,
                u.Disabled,
                u.PendingSetup,
                u.Permissions,
                u.AllRootFolders
            })
            .FirstOrDefaultAsync(ct);

        if (match is null || match.Disabled || match.PendingSetup ||
            !match.Permissions.Grants(MakiPermission.UseOpds))
        {
            return null;
        }

        // The two switches are the *owner's*, not the instance's: one reader can turn their catalogue
        // off, or turn progress tracking off for a prefetching app, without touching anybody else's.
        // Read with an explicit user filter because this runs before the request scope is narrowed —
        // resolving the token is what decides who the caller is.
        // IgnoreQueryFilters, and it is load-bearing. An OPDS request carries no cookie and no API-key
        // header, so CurrentUserMiddleware has already narrowed the scope to *nobody* — resolving the
        // token is what decides who the caller is, and that has not happened yet at this point. Without
        // this the user-owned filter ANDs with the predicate below, finds no rows, and every valid feed
        // URL answers 404. The narrowing the filter would have done is done explicitly instead: the
        // predicate names the user the token resolved to, and nothing else.
        var settings = await db.UserSettings
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x => x.UserId == match.Id &&
                        (x.Key == SettingKeys.OpdsEnabled || x.Key == SettingKeys.OpdsTrackProgress))
            .ToDictionaryAsync(x => x.Key, x => x.Value, ct);

        if (settings.GetValueOrDefault(SettingKeys.OpdsEnabled) != "true")
        {
            return null;
        }

        // Absent means on: progress tracking is the default, and only an explicit "false" is the user
        // having turned it off.
        var trackProgress = settings.GetValueOrDefault(SettingKeys.OpdsTrackProgress) != "false";

        var now = clock.GetUtcNow().UtcDateTime;
        if (match.LastUsedAt is null || now - match.LastUsedAt > LastUsedGranularity)
        {
            await db.UserApiKeys
                .Where(k => k.Id == match.KeyId)
                .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now), ct);
        }

        return new OpdsAccess(trackProgress, match.Id, match.AllRootFolders);
    }
}
