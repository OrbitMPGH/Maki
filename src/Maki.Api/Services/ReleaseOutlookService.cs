using Maki.Api.Dtos;
using Maki.Api.Jobs;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Releases;
using Maki.Core.Sources;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Builds <see cref="ReleaseOutlookDto"/>s from stored dates: <see cref="ReleaseCadence"/> for the
/// window, the mappings for an announced date, <see cref="RefreshSchedule"/> for why the next
/// check is when it is. Reads only; nothing here touches a source.
/// </summary>
public class ReleaseOutlookService(
    MakiDbContext db,
    SourceRegistry sources,
    SourceAvailability availability,
    IAppSettings settings,
    TimeProvider time)
{
    public async Task<ReleaseOutlookDto?> ForSeriesAsync(int seriesId, CancellationToken ct)
    {
        var series = await db.Series.AsNoTracking().FirstOrDefaultAsync(s => s.Id == seriesId, ct);
        if (series is null)
        {
            return null;
        }

        var disabled = await availability.DisabledAsync(ct);
        var refreshable = await RefreshMonitoredSeriesJob.RefreshableSeriesIdsAsync(db, disabled, ct);
        var outlooks = await BuildAsync([series], refreshable.ToHashSet(), disabled, ct);
        return outlooks[seriesId];
    }

    /// <summary>
    /// Every refreshed series whose next chapter can be placed: an announced date, or a window that
    /// is open or recently closed. Irregular and stalled series are left out rather than guessed at.
    /// </summary>
    public async Task<List<(Series Series, ReleaseOutlookDto Outlook)>> UpcomingAsync(CancellationToken ct)
    {
        var disabled = await availability.DisabledAsync(ct);
        var ids = await RefreshMonitoredSeriesJob.RefreshableSeriesIdsAsync(db, disabled, ct);
        var series = await db.Series.AsNoTracking().Where(s => ids.Contains(s.Id)).ToListAsync(ct);
        var outlooks = await BuildAsync(series, ids.ToHashSet(), disabled, ct);

        return series
            .Select(s => (Series: s, Outlook: outlooks[s.Id]))
            .Where(x => x.Outlook.AnnouncedAt is not null ||
                        x.Outlook.State is CadenceState.Expected or CadenceState.Late)
            .OrderBy(x => x.Outlook.AnnouncedAt ?? x.Outlook.WindowStart)
            .ToList();
    }

    private async Task<Dictionary<int, ReleaseOutlookDto>> BuildAsync(
        List<Series> series, HashSet<int> refreshable, List<string> disabled, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var ids = series.Select(s => s.Id).ToList();
        var sightings = await ReleaseSightings.ForSeriesAsync(db, sources, ids, ct);
        var adaptive = await settings.GetAsync(SettingKeys.MonitoringAdaptiveRefresh, ct) != "false";

        // The soonest announcement still worth showing, per series, among usable mappings.
        var announcements = (await db.SourceMappings.AsNoTracking()
                .Where(m => ids.Contains(m.SeriesId) && m.Enabled && m.AnnouncedNextReleaseAt != null)
                .Select(m => new { m.SeriesId, m.SourceName, At = m.AnnouncedNextReleaseAt!.Value })
                .ToListAsync(ct))
            .Where(m => !disabled.Contains(m.SourceName, StringComparer.OrdinalIgnoreCase))
            .Where(m => m.At > now - RefreshSchedule.AnnouncedWatch)
            .GroupBy(m => m.SeriesId)
            .ToDictionary(g => g.Key, g => g.MinBy(m => m.At)!);

        var result = new Dictionary<int, ReleaseOutlookDto>();
        foreach (var s in series)
        {
            var seen = sightings.GetValueOrDefault(s.Id) ?? [];
            var cadence = ReleaseCadence.Estimate(ReleaseHistory.ReleaseTimes(seen, now), now);
            var lastNew = ReleaseHistory.LastNewChapter(seen, now);

            var announced = announcements.GetValueOrDefault(s.Id);
            // Already out: the chapter it announced has been seen, so the date is history.
            if (announced is not null && lastNew is { } latest && latest >= announced.At)
            {
                announced = null;
            }

            var plan = RefreshSchedule.Plan(s.LastCheckAt ?? now, lastNew, s.Status, announced?.At, adaptive);
            result[s.Id] = new ReleaseOutlookDto(
                cadence.State,
                cadence.ReleaseDays,
                cadence.Interval,
                cadence.Spread,
                cadence.LastRelease,
                cadence.WindowStart,
                cadence.WindowEnd,
                cadence.Confidence,
                cadence.Hits,
                cadence.Checked,
                announced?.At,
                announced is null ? null : sources.Find(announced.SourceName)?.DisplayName ?? announced.SourceName,
                refreshable.Contains(s.Id),
                plan.Tier,
                s.LastCheckAt,
                s.NextCheckAt);
        }

        return result;
    }
}
