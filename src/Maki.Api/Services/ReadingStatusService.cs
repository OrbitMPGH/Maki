using Maki.Core.Entities;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Writes a reader's <see cref="ReadingStatus"/> for a set of series, by hand or from an auto rule.
/// Reads that move a series to Reading do not come through here; <c>MakiDbContext</c> does those.
/// <para>
/// Takes an explicit user and ignores query filters, because the auto rule runs as a job with no
/// user in scope. Callers acting for a signed-in user pass ids they already resolved through
/// <c>db.Series</c>, so the root-folder filter has had its say.
/// </para>
/// </summary>
public class ReadingStatusService(MakiDbContext db)
{
    /// <summary>
    /// Sets <paramref name="status"/> on every series in <paramref name="seriesIds"/> and returns the ids
    /// whose status actually changed, which are the ones worth telling the trackers about. Setting the
    /// status a rule already set marks it as the reader's own, so the drop rule leaves it alone.
    /// </summary>
    public async Task<List<int>> SetAsync(
        int userId, IReadOnlyCollection<int> seriesIds, ReadingStatus status, bool auto, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await SetCoreAsync(userId, seriesIds, status, auto, ct);
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                // A first read inserted the row between the read and the insert here; go again as an update.
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task<List<int>> SetCoreAsync(
        int userId, IReadOnlyCollection<int> seriesIds, ReadingStatus status, bool auto, CancellationToken ct)
    {
        var changed = new List<int>();
        if (seriesIds.Count == 0)
        {
            return changed;
        }

        var now = DateTime.UtcNow;
        var existing = await db.UserSeriesStates.IgnoreQueryFilters()
            .Where(s => s.UserId == userId && seriesIds.Contains(s.SeriesId))
            .ToListAsync(ct);
        foreach (var state in existing)
        {
            if (state.Status == status && state.StatusAuto == auto)
            {
                continue;
            }

            if (state.Status != status)
            {
                changed.Add(state.SeriesId);
            }

            state.Status = status;
            state.StatusAuto = auto;
            state.StatusChangedAt = now;
            state.UpdatedAt = now;
        }

        if (status != ReadingStatus.PlanToRead)
        {
            var missing = seriesIds.Except(existing.Select(s => s.SeriesId)).ToList();
            db.UserSeriesStates.AddRange(missing.Select(seriesId => new UserSeriesState
            {
                UserId = userId,
                SeriesId = seriesId,
                Status = status,
                StatusAuto = auto,
                StatusChangedAt = now,
                UpdatedAt = now,
            }));
            changed.AddRange(missing);
        }

        await db.SaveChangesAsync(ct);
        return changed;
    }
}
