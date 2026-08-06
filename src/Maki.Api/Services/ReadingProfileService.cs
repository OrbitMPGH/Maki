using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Reading;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>Which of the four layers answered "what does this series look like".</summary>
public enum ReaderPrefsSource
{
    /// <summary>The user's <c>reader.prefs</c>: nothing more specific matched.</summary>
    Global,

    /// <summary>A reading profile, either pinned to the series or claimed by its type.</summary>
    Profile,

    /// <summary>An ad-hoc override stored on the series itself.</summary>
    Series,
}

/// <param name="ProfileId">The profile in force, when <paramref name="Source"/> is Profile.</param>
/// <param name="PinnedProfileId">
/// The profile pinned to this series by hand, if any. Distinct from <paramref name="ProfileId"/>:
/// the reader's picker has to show "Auto" versus an explicit choice differently, and after an
/// auto-selection the two are equal.
/// </param>
/// <param name="AutoProfileId">
/// What the series' type would select, whether or not it won. The picker labels its Auto entry with
/// this so choosing it says which profile that actually means.
/// </param>
public record ResolvedReaderPrefs(
    ReaderPrefsSpec Prefs,
    ReaderPrefsSource Source,
    int? ProfileId,
    string? ProfileName,
    int? PinnedProfileId,
    int? AutoProfileId);

/// <summary>
/// Resolves and maintains this user's reading profiles. Everything here runs through the scoped
/// <see cref="MakiDbContext"/>, so the query filters do the per-user narrowing and no method has to
/// carry a user id.
/// </summary>
public class ReadingProfileService(MakiDbContext db, IUserSettings userSettings)
{
    public Task<List<ReadingProfile>> ListAsync(CancellationToken ct) =>
        db.ReadingProfiles.OrderBy(p => p.Name).ToListAsync(ct);

    /// <summary>
    /// The four-layer answer for one series: its own override, the profile pinned to it, the profile
    /// claiming its type, then the user's global defaults.
    /// <para>
    /// Two queries plus a settings read, and the profile list is filtered in memory: a user has a
    /// handful of profiles, and a CSV <c>LIKE</c> over <c>SeriesTypes</c> would match "manhua"
    /// inside "manhua,manhwa" correctly but "manga" inside neither, which is the kind of thing that
    /// works until somebody adds a type whose name contains another.
    /// </para>
    /// </summary>
    public async Task<ResolvedReaderPrefs> ResolveAsync(int seriesId, CancellationToken ct)
    {
        // Through the Series query filter, so a series in a root folder this user was never granted
        // resolves to their plain defaults rather than leaking that it exists.
        var type = await db.Series
            .Where(s => s.Id == seriesId)
            .Select(s => s.Type)
            .FirstOrDefaultAsync(ct);

        var state = await db.UserSeriesStates
            .Where(s => s.SeriesId == seriesId)
            .Select(s => new { s.ReaderPrefsJson, s.ReadingProfileId })
            .FirstOrDefaultAsync(ct);

        var profiles = await ListAsync(ct);
        var auto = type is null
            ? null
            : profiles.FirstOrDefault(p => p.Types().Contains(type, StringComparer.Ordinal));

        if (state?.ReaderPrefsJson is { Length: > 0 } own)
        {
            return new ResolvedReaderPrefs(
                ReaderPrefsSpec.Parse(own), ReaderPrefsSource.Series, null, null, null, auto?.Id);
        }

        // A pinned id that no longer resolves means the profile was deleted between the SetNull and
        // this read, or the row belongs to another user; either way fall through to auto.
        var pinned = state?.ReadingProfileId is int id
            ? profiles.FirstOrDefault(p => p.Id == id)
            : null;

        var effective = pinned ?? auto;
        if (effective is not null)
        {
            return new ResolvedReaderPrefs(
                ReaderPrefsSpec.Parse(effective.PrefsJson), ReaderPrefsSource.Profile,
                effective.Id, effective.Name, pinned?.Id, auto?.Id);
        }

        var global = ReaderPrefsSpec.Parse(await userSettings.GetAsync(SettingKeys.ReaderPrefs, ct));
        return new ResolvedReaderPrefs(global, ReaderPrefsSource.Global, null, null, null, auto?.Id);
    }

    /// <summary>
    /// The type already claimed by a profile other than <paramref name="excludeProfileId"/>, or null
    /// when the requested set is free. Refusing a second claimant is deliberate: with two profiles
    /// claiming "manhwa" one of them silently never applies, and nothing in the UI could say which.
    /// </summary>
    public async Task<(string Type, string ProfileName)?> ConflictingClaimAsync(
        IEnumerable<string> types, int? excludeProfileId, CancellationToken ct)
    {
        var wanted = types.ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0)
        {
            return null;
        }

        foreach (var profile in await ListAsync(ct))
        {
            if (profile.Id == excludeProfileId)
            {
                continue;
            }

            foreach (var claimed in profile.Types())
            {
                if (wanted.Contains(claimed))
                {
                    return (claimed, profile.Name);
                }
            }
        }

        return null;
    }

}

/// <summary>
/// Gives a brand-new account the built-in reading profiles. Existing accounts were seeded by the
/// <c>ReadingProfiles</c> migration instead.
/// <para>
/// Static, and taking the context and the id rather than being a method on
/// <see cref="ReadingProfileService"/>, because both callers are acting as somebody else: an admin
/// creating an account, or the OIDC callback provisioning one before anybody is signed in. Neither
/// has a per-user settings reader to hand and neither should have to build one to write three rows.
/// </para>
/// </summary>
public static class ReadingProfileSeeder
{
    public static async Task SeedAsync(MakiDbContext db, int userId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        foreach (var seed in ReadingProfileDefaults.All)
        {
            // UserId explicit: the SaveChanges hook only fills a zero, and here the ambient scope
            // is the admin's (or nobody's), which is exactly the wrong owner.
            db.ReadingProfiles.Add(new ReadingProfile
            {
                UserId = userId,
                Name = seed.Name,
                PrefsJson = ReaderPrefsSpec.Serialize(seed.Prefs),
                SeriesTypes = string.Join(',', seed.Types),
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        await db.SaveChangesAsync(ct);
    }
}
