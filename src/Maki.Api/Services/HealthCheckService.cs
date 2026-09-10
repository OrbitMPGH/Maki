using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Data;
using Maki.Metadata.MangaBaka;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>One health problem surfaced on the System status page and to notifications.</summary>
/// <param name="RolledUp">
/// A real problem that a broader issue in the same set already reports. <c>HealthMonitor</c> keeps
/// no check row for one, and clears any row it left behind without announcing a recovery: it was
/// superseded, not fixed, and saying otherwise is both wrong and one message per series.
/// </param>
public record HealthIssue(string Type, string Severity, string Message, int? SeriesId = null,
    string? Key = null, string? Url = null, bool RolledUp = false);

/// <summary>
/// Computes the current set of health problems. Shared by <c>SystemController</c> (on-demand)
/// and <c>HealthCheckJob</c> (periodic, diffed for notifications).
/// </summary>
public class HealthCheckService(
    MakiDbContext db,
    IAppSettings settings,
    SourceAvailability sourceAvailability,
    MangaBakaDumpService mangaBakaDump)
{
    public async Task<List<HealthIssue>> GetIssuesAsync(CancellationToken ct = default)
    {
        var issues = new List<HealthIssue>();

        // A globally switched-off source is not "failing" and cannot satisfy a monitored
        // series' need for a mapping, so it drops out of both checks below.
        var disabledSources = await sourceAvailability.DisabledAsync(ct);

        // Everything the refresh has actually tried against a live source. Mappings it has never
        // reached carry neither a refresh nor an error and cannot say anything about the source.
        var attemptedMappings = await db.SourceMappings
            .Where(m => m.Enabled && !disabledSources.Contains(m.SourceName))
            .Where(m => m.LastRefresh != null || m.LastError != null)
            .Include(m => m.Series)
            .ToListAsync(ct);

        var outages = SourceOutages.Detect(attemptedMappings);
        var outagedSources = outages.Select(o => o.SourceName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var outage in outages)
        {
            var scale = outage.Unavailable
                ? $"all {outage.Failing} series that have refreshed against it are failing"
                : $"{outage.Failing} of {outage.Attempted} series fail to refresh against it";
            issues.Add(new HealthIssue("source", outage.Unavailable ? "error" : "warning",
                $"{outage.SourceName} is {(outage.Unavailable ? "unavailable" : "unstable")}: {scale}. Last error: {outage.Sample}",
                Key: $"source:{outage.SourceName}", Url: "/settings?tab=downloads&s=sources"));
        }

        // A failure on a source that is out is rolled up into the outage above rather than repeated
        // once per series. It is still emitted, so a caller that wants the detail (and the monitor,
        // which has to retire the row it already wrote) can see it.
        foreach (var mapping in attemptedMappings.Where(m => m.LastError != null))
        {
            issues.Add(new HealthIssue("sourceMapping", "warning",
                $"{mapping.Series?.Title}: {mapping.SourceName} refresh failing — {mapping.LastError}",
                mapping.SeriesId, $"mapping:{mapping.Id}",
                RolledUp: outagedSources.Contains(mapping.SourceName)));
        }

        foreach (var folder in await db.RootFolders.ToListAsync(ct))
        {
            if (!Directory.Exists(folder.Path))
            {
                issues.Add(new HealthIssue("rootFolder", "error", $"Root folder inaccessible: {folder.Path}", Key: $"root:{folder.Id}"));
            }
        }

        // Only worth flagging when the series is actually monitored — a "Monitor: none" series
        // with no source isn't waiting on anything.
        var noMappings = await db.Series
            .Where(s => s.MonitorNewItems != NewChapterMonitorMode.None &&
                        !s.SourceMappings.Any(m => m.Enabled && !disabledSources.Contains(m.SourceName)))
            .Select(s => new { s.Id, s.Title })
            .ToListAsync(ct);
        foreach (var series in noMappings)
        {
            issues.Add(new HealthIssue("series", "warning",
                $"{series.Title} is monitored but has no enabled source mappings", series.Id));
        }

        if (await settings.GetAsync(SettingKeys.MangaBakaUseLocalDb, ct) != "false")
        {
            var dump = await mangaBakaDump.GetStatusAsync(ct);
            var uptime = DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
            if (!dump.Present && uptime > TimeSpan.FromHours(1))
            {
                issues.Add(new HealthIssue("mangaBakaDump", "warning",
                    "MangaBaka local database not downloaded yet — metadata requests use the rate-limited API"));
            }
            else if (dump.Present && dump.RefreshedAt < DateTime.UtcNow.AddHours(-72))
            {
                issues.Add(new HealthIssue("mangaBakaDump", "warning",
                    $"MangaBaka local database is stale (last refresh {dump.RefreshedAt:yyyy-MM-dd HH:mm} UTC) — dump refresh may be failing"));
            }
        }

        return issues;
    }
}

/// <summary>
/// The set of health-issue messages seen on the last check, so <c>HealthCheckJob</c> only
/// notifies on newly-appeared issues rather than every tick. In-memory: a restart re-notifies
/// on anything still wrong, which is acceptable.
/// </summary>
public class HealthState
{
    private HashSet<string> _lastSeen = [];

    /// <summary>Records the current issues and returns those not present on the previous check.</summary>
    public List<HealthIssue> Diff(IReadOnlyList<HealthIssue> current)
    {
        var currentKeys = current.Select(i => i.Message).ToHashSet();
        var fresh = current.Where(i => !_lastSeen.Contains(i.Message)).ToList();
        _lastSeen = currentKeys;
        return fresh;
    }
}
