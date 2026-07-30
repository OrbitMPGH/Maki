using Maki.Core.Configuration;
using Maki.Core.Security;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Answers "whose reading is Kavita's reading?" — the one question every Kavita-facing path needs and
/// none of them can derive locally.
/// <para>
/// Kavita is a single external server reached with a single API key, so everything it reports belongs
/// to one person; there is no way to tell two Kavita users apart from this side. Binding it to one Maki
/// user is what keeps the whole adopt/merge/zero-delta chain in <c>ReadingProgressService</c> intact:
/// the recurring pass, the read-status import, the per-chapter external sync and the push-back all act
/// as the same user, so a chapter read in Maki and re-reported by Kavita still yields a delta of zero.
/// </para>
/// <para>
/// A singleton with a short cache, because it is consulted once per Kavita series per tick and the
/// answer changes about never.
/// </para>
/// </summary>
public class KavitaUserResolver(IServiceScopeFactory scopeFactory, SettingsService settings)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);
    private (DateTime At, int? UserId)? _cached;

    /// <summary>
    /// The bound user, or null when there is nobody to attribute Kavita's reading to — in which case
    /// every Kavita path skips rather than guessing. Falls back to the lowest-numbered enabled admin so
    /// that an upgraded single-user install needs no configuration at all.
    /// </summary>
    public async Task<int?> ResolveAsync(CancellationToken ct = default)
    {
        if (_cached is { } cached && DateTime.UtcNow - cached.At < CacheFor)
        {
            return cached.UserId;
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();

        int? resolved = null;
        if (int.TryParse(await settings.GetAsync(SettingKeys.KavitaUserId, ct), out var configured) &&
            configured > 0 &&
            await db.Users.AnyAsync(u => u.Id == configured && !u.Disabled && !u.PendingSetup, ct))
        {
            resolved = configured;
        }

        resolved ??= await db.Users
            .Where(u => !u.Disabled && !u.PendingSetup && (u.Permissions & MakiPermission.Admin) != 0)
            .OrderBy(u => u.Id)
            .Select(u => (int?)u.Id)
            .FirstOrDefaultAsync(ct);

        _cached = (DateTime.UtcNow, resolved);
        return resolved;
    }

    /// <summary>Drops the cache so a settings change takes effect without waiting for it to lapse.</summary>
    public void Invalidate() => _cached = null;
}
