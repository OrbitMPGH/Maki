using Maki.Core.Entities;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Changes <see cref="Series.LockedFields"/> with one bitwise UPDATE instead of writing the value the
/// request read. Two edits racing on different fields each read the same flags and wrote back their
/// own union, so whichever saved last dropped the other's lock.
/// </summary>
public static class SeriesMetadataLocks
{
    /// <summary>
    /// Stops the next <c>SaveChanges</c> from writing the in-memory flags, so a caller that changed
    /// them (a reset clears a lock in memory so the refresh will touch the field) leaves the column to
    /// <see cref="ApplyAsync"/>.
    /// </summary>
    public static void LeaveToApply(MakiDbContext db, Series series) =>
        db.Entry(series).Property(s => s.LockedFields).IsModified = false;

    /// <summary>
    /// Re-reads the series after a slow step and says whether it still exists, so what a refresh
    /// applies is checked against what is stored now and not against what was loaded before the wait.
    /// </summary>
    public static async Task<bool> ReloadAsync(MakiDbContext db, Series series, CancellationToken ct)
    {
        await db.Entry(series).ReloadAsync(ct);
        return db.Entry(series).State != EntityState.Detached;
    }

    /// <summary>Sets and clears flags in the database, then makes the loaded series show the stored result.</summary>
    public static async Task ApplyAsync(
        MakiDbContext db, Series series, SeriesMetadataField set, SeriesMetadataField clear, CancellationToken ct)
    {
        var keep = ~clear;
        await db.Series.Where(s => s.Id == series.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.LockedFields, s => (s.LockedFields | set) & keep), ct);

        var stored = await db.Series.Where(s => s.Id == series.Id).Select(s => s.LockedFields).FirstAsync(ct);
        var property = db.Entry(series).Property(s => s.LockedFields);
        property.CurrentValue = stored;
        property.OriginalValue = stored;
        property.IsModified = false;
    }
}
