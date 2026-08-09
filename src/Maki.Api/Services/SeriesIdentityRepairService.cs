using Maki.Core.Entities;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// One-time repair of activity rows written before <see cref="StatsEvent.SeriesKey"/> existed.
/// <para>
/// Two passes. First fills the key in: rows whose series is still present get that series' identity,
/// orphans get the title key their re-added copy will also compute. Then it adopts — any orphan
/// whose key now matches a live series is re-pointed at it, which is what repairs a series removed
/// and added back before this feature landed.
/// </para>
/// <para>
/// Marker-gated in AppConfig like <see cref="StatsBackfillService"/>, and runs at startup before
/// Kestrel and Quartz so it cannot overlap a live write. It only ever fills in a severed
/// <c>SeriesId</c> and a null key: no count, timestamp or user is touched, so a wrong match costs a
/// merge that was already the intended behaviour, not lost history.
/// </para>
/// </summary>
public class SeriesIdentityRepairService(
    MakiDbContext db,
    SeriesIdentityService identity,
    ILogger<SeriesIdentityRepairService> logger)
{
    public const string MarkerKey = "stats.identityRepairDone";

    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        if (await db.AppConfig.AnyAsync(c => c.Key == MarkerKey, ct))
        {
            return;
        }

        var keyed = await BackfillKeysAsync(ct);
        var adopted = await AdoptAsync(ct);

        db.AppConfig.Add(new AppConfigEntry
        {
            Key = MarkerKey,
            Value = DateTime.UtcNow.ToString("O")
        });
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Series identity repair complete: keyed {Keyed} event(s), adopted {Events} event(s) and {States} reading state(s) into existing series",
            keyed, adopted.Events, adopted.States);
    }

    private async Task<int> BackfillKeysAsync(CancellationToken ct)
    {
        var series = await db.Series.AsNoTracking().IgnoreQueryFilters()
            .Select(s => new
            {
                s.Id, s.Title, s.MangaBakaId, s.MangaDexUuid, s.AniListId, s.MalId
            })
            .ToListAsync(ct);
        var keys = series.ToDictionary(
            s => s.Id,
            s => SeriesIdentity.For(new Series
            {
                Title = s.Title,
                MangaBakaId = s.MangaBakaId,
                MangaDexUuid = s.MangaDexUuid,
                AniListId = s.AniListId,
                MalId = s.MalId
            }));

        // Tracked, not ExecuteUpdate: the key is computed in C# (normalization is a regex SQLite
        // cannot express) and the value differs per row.
        var rows = await db.StatsEvents.IgnoreQueryFilters()
            .Where(e => e.SeriesKey == null)
            .ToListAsync(ct);

        foreach (var row in rows)
        {
            row.SeriesKey = row.SeriesId is int sid && keys.TryGetValue(sid, out var key)
                ? key
                : SeriesIdentity.ForTitle(row.SeriesTitle);
        }

        await db.SaveChangesAsync(ct);
        return rows.Count;
    }

    private async Task<(int Events, int States)> AdoptAsync(CancellationToken ct)
    {
        // Only series that actually have orphans waiting are worth a lookup, but the set of
        // orphan keys is small and the series list is the smaller table — walk the series.
        var orphanKeys = await db.StatsEvents.AsNoTracking().IgnoreQueryFilters()
            .Where(e => e.SeriesId == null && e.SeriesKey != null)
            .Select(e => e.SeriesKey!)
            .Distinct()
            .ToListAsync(ct);

        var tombstonedTitles = await db.ReadingStates.AsNoTracking().IgnoreQueryFilters()
            .Where(r => r.SeriesId == null && r.KavitaSeriesId == null)
            .Select(r => r.Title)
            .Distinct()
            .ToListAsync(ct);

        if (orphanKeys.Count == 0 && tombstonedTitles.Count == 0)
        {
            return (0, 0);
        }

        var candidateKeys = orphanKeys.ToHashSet(StringComparer.Ordinal);
        var candidateTitles = tombstonedTitles
            .Select(SeriesIdentity.ForTitle)
            .ToHashSet(StringComparer.Ordinal);

        var totals = (Events: 0, States: 0);
        foreach (var series in await db.Series.IgnoreQueryFilters().ToListAsync(ct))
        {
            var key = SeriesIdentity.For(series);
            var titleKey = SeriesIdentity.ForTitle(series.Title);
            if (!candidateKeys.Contains(key) && !candidateKeys.Contains(titleKey) &&
                !candidateTitles.Contains(titleKey))
            {
                continue;
            }

            var (events, states) = await identity.AdoptOrphansAsync(series, ct);
            totals.Events += events;
            totals.States += states;
        }

        return totals;
    }
}
