using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Data;
using Maki.Metadata.MangaBaka;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>One health problem surfaced on the System status page and to notifications.</summary>
/// <param name="MessageKey">
/// A catalogue key. These checks run on a timer and land on a page read later by whoever opens it,
/// so nothing here is worded at the moment it is found.
/// </param>
/// <param name="Params">An anonymous object filling the message's placeholders, or null.</param>
/// <param name="Key">
/// Stable identity for the issue, so the same problem found twice is the same row. Also what
/// <see cref="HealthState"/> diffs on, which the rendered message used to do badly: two series with
/// the same problem produced two different sentences and so read as two unrelated issues.
/// </param>
public record HealthIssue(
    string Type, string Severity, string MessageKey, object? Params = null,
    int? SeriesId = null, string? Key = null)
{
    /// <summary>What makes this issue the same issue across two checks.</summary>
    public string Identity => Key ?? $"{Type}:{SeriesId}:{MessageKey}";
}

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

        var failingMappings = await db.SourceMappings
            .Where(m => m.Enabled && m.LastError != null && !disabledSources.Contains(m.SourceName))
            .Include(m => m.Series)
            .ToListAsync(ct);
        foreach (var mapping in failingMappings)
        {
            // {detail} is the source's own error text and is not translated.
            issues.Add(new HealthIssue("sourceMapping", "warning", "health.issue.mappingFailing",
                new { series = mapping.Series?.Title ?? "", source = mapping.SourceName, detail = mapping.LastError ?? "" },
                mapping.SeriesId, $"mapping:{mapping.Id}"));
        }

        foreach (var folder in await db.RootFolders.ToListAsync(ct))
        {
            if (!Directory.Exists(folder.Path))
            {
                issues.Add(new HealthIssue("rootFolder", "error", "health.issue.rootInaccessible",
                    new { path = folder.Path }, Key: $"root:{folder.Id}"));
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
            issues.Add(new HealthIssue("series", "warning", "health.issue.noSourceMappings",
                new { series = series.Title }, series.Id));
        }

        if (await settings.GetAsync(SettingKeys.MangaBakaUseLocalDb, ct) != "false")
        {
            var dump = await mangaBakaDump.GetStatusAsync(ct);
            var uptime = DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
            if (!dump.Present && uptime > TimeSpan.FromHours(1))
            {
                issues.Add(new HealthIssue("mangaBakaDump", "warning", "health.issue.dumpMissing"));
            }
            else if (dump.Present && dump.RefreshedAt < DateTime.UtcNow.AddHours(-72))
            {
                issues.Add(new HealthIssue("mangaBakaDump", "warning", "health.issue.dumpStale",
                    new { at = dump.RefreshedAt }));
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
        var currentKeys = current.Select(i => i.Identity).ToHashSet();
        var fresh = current.Where(i => !_lastSeen.Contains(i.Identity)).ToList();
        _lastSeen = currentKeys;
        return fresh;
    }
}
