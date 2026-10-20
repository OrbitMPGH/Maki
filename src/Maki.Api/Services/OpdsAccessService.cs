using System.Collections.Concurrent;
using Maki.Api.Auth;
using Maki.Core.Configuration;
using Maki.Core.Security;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;

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
public class OpdsAccessService(MakiDbContext db, TimeProvider clock, IMemoryCache? cache = null)
{
    /// <summary>
    /// How long a resolved token is reused. A streaming reader resolves once per page, so this turns
    /// a chapter's worth of lookups into one. Only successes are kept, so enabling the catalogue or
    /// minting a key works at once. Rotating or revoking a key and editing or disabling its owner call
    /// <see cref="EvictUser"/>; disabling the catalogue takes up to this long.
    /// </summary>
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    private static readonly ConcurrentDictionary<int, CancellationTokenSource> UserEntries = new();
    private static readonly Lock Gate = new();
    private static long _evictions;

    /// <summary>Drops every cached resolution for this user's tokens.</summary>
    public static void EvictUser(int userId)
    {
        CancellationTokenSource? entries;
        lock (Gate)
        {
            _evictions++;
            UserEntries.TryRemove(userId, out entries);
        }

        entries?.Cancel();
    }

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
        var cacheKey = (typeof(OpdsAccess), hash);
        if (cache?.TryGetValue(cacheKey, out OpdsAccess? cached) == true)
        {
            return cached;
        }

        long evictionsBefore;
        lock (Gate)
        {
            evictionsBefore = _evictions;
        }

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

        if (await TrackProgressAsync(match.Id, ct) is not { } trackProgress)
        {
            return null;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        if (match.LastUsedAt is null || now - match.LastUsedAt > LastUsedGranularity)
        {
            await db.UserApiKeys
                .Where(k => k.Id == match.KeyId)
                .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now), ct);
        }

        var access = new OpdsAccess(trackProgress, match.Id, match.AllRootFolders);

        // An eviction while this was reading may have been about this key, so the answer is not kept.
        // Checked and stored under the lock EvictUser takes, so one cannot land in between.
        if (cache is not null)
        {
            lock (Gate)
            {
                if (_evictions == evictionsBefore)
                {
                    var entries = UserEntries.GetOrAdd(match.Id, _ => new CancellationTokenSource());
                    cache.Set(cacheKey, access, new MemoryCacheEntryOptions()
                        .SetAbsoluteExpiration(CacheFor)
                        .AddExpirationToken(new CancellationChangeToken(entries.Token)));
                }
            }
        }

        return access;
    }

    /// <summary>
    /// The access a user has right now, for work queued under an earlier request, such as a KOReader
    /// push waiting for the document index. Null on the same conditions as <see cref="ResolveAsync"/>,
    /// apart from the token, which was checked when the work was queued.
    /// </summary>
    public async Task<OpdsAccess?> ForUserAsync(int userId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.Disabled, u.PendingSetup, u.Permissions, u.AllRootFolders })
            .FirstOrDefaultAsync(ct);
        if (user is null || user.Disabled || user.PendingSetup || !user.Permissions.Grants(MakiPermission.UseOpds))
        {
            return null;
        }

        return await TrackProgressAsync(userId, ct) is { } trackProgress
            ? new OpdsAccess(trackProgress, userId, user.AllRootFolders)
            : null;
    }

    /// <summary>
    /// The owner's progress-tracking switch, or null when their catalogue is off. The two switches are
    /// the owner's, not the instance's: one reader can turn their catalogue off, or turn tracking off
    /// for a prefetching app, without touching anybody else's.
    /// </summary>
    private async Task<bool?> TrackProgressAsync(int userId, CancellationToken ct)
    {
        // IgnoreQueryFilters, and it is load-bearing. An OPDS request carries no cookie and no API-key
        // header, so CurrentUserMiddleware has already narrowed the scope to *nobody*. Without this the
        // user-owned filter ANDs with the predicate below, finds no rows, and every valid feed URL
        // answers 404. The predicate names the user instead.
        var settings = await db.UserSettings
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x => x.UserId == userId &&
                        (x.Key == SettingKeys.OpdsEnabled || x.Key == SettingKeys.OpdsTrackProgress))
            .ToDictionaryAsync(x => x.Key, x => x.Value, ct);

        if (settings.GetValueOrDefault(SettingKeys.OpdsEnabled) != "true")
        {
            return null;
        }

        // Absent means on: only an explicit "false" is the user having turned it off.
        return settings.GetValueOrDefault(SettingKeys.OpdsTrackProgress) != "false";
    }
}
