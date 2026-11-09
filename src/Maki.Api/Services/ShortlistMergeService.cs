using Maki.Data;
using Maki.Metadata.MangaBaka;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Moves shortlist entries stored under a MangaBaka id that has since been merged onto the canonical
/// work. Runs after each dump install and once at startup; the GETs never write, they only hydrate a
/// merged entry from its canonical row in memory until this catches up.
/// </summary>
public class ShortlistMergeService(
    IServiceScopeFactory scopeFactory, MangaBakaLocalStore store, ILogger<ShortlistMergeService> logger)
{
    /// <returns>How many canonical groups were rewritten.</returns>
    public async Task<int> RekeyAsync(CancellationToken ct = default)
    {
        if (!await store.IsAvailableAsync(ct))
        {
            return 0;
        }

        List<int> keys;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
            keys = await db.ShortlistEntries.IgnoreQueryFilters().AsNoTracking()
                .Select(x => x.MangaBakaId).Distinct().ToListAsync(ct);
        }

        if (keys.Count == 0)
        {
            return 0;
        }

        var canonical = await store.ResolveCanonicalIdsAsync(keys.Select(k => (long)k).ToList(), ct);
        var groups = canonical
            .Where(r => r.Value != r.Key && r.Value is > 0 and <= int.MaxValue)
            .GroupBy(r => (int)r.Value, r => (int)r.Key)
            .ToList();

        var rewritten = 0;
        foreach (var group in groups)
        {
            try
            {
                await RekeyGroupAsync(group.Key, group.ToList(), ct);
                rewritten++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not move shortlist entries onto merged MangaBaka id {Canonical}", group.Key);
            }
        }

        if (rewritten > 0)
        {
            logger.LogInformation("Moved shortlist entries for {Count} merged MangaBaka id(s)", rewritten);
        }

        return rewritten;
    }

    private async Task RekeyGroupAsync(int canonical, IReadOnlyList<int> mergedKeys, CancellationToken ct)
    {
        // The add's lock: a save landing between the scan above and this point is seen by the re-query.
        using var _ = await SeriesLocks.ProviderIdAsync(canonical, ct);
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        var wanted = mergedKeys.Append(canonical).ToList();
        var entries = await db.ShortlistEntries.IgnoreQueryFilters()
            .Where(x => wanted.Contains(x.MangaBakaId))
            .ToListAsync(ct);

        foreach (var perUser in entries.GroupBy(x => x.UserId))
        {
            var ordered = perUser.OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).ToList();
            var keep = ordered[0];
            db.ShortlistEntries.RemoveRange(ordered.Skip(1));
            if (keep.MangaBakaId != canonical)
            {
                keep.MangaBakaId = canonical;
                keep.UpdatedAtUtc = DateTime.UtcNow;
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
