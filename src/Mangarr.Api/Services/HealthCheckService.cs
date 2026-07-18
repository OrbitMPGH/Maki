using Mangarr.Core.Configuration;
using Mangarr.Core.Entities;
using Mangarr.Data;
using Mangarr.Metadata.MangaBaka;
using Microsoft.EntityFrameworkCore;

namespace Mangarr.Api.Services;

/// <summary>One health problem surfaced on the System status page and to notifications.</summary>
public record HealthIssue(string Type, string Severity, string Message);

/// <summary>
/// Computes the current set of health problems. Shared by <c>SystemController</c> (on-demand)
/// and <c>HealthCheckJob</c> (periodic, diffed for notifications).
/// </summary>
public class HealthCheckService(
    MangarrDbContext db,
    IAppSettings settings,
    MangaBakaDumpService mangaBakaDump)
{
    public async Task<List<HealthIssue>> GetIssuesAsync(CancellationToken ct = default)
    {
        var issues = new List<HealthIssue>();

        var failingMappings = await db.SourceMappings
            .Where(m => m.Enabled && m.LastError != null)
            .Include(m => m.Series)
            .ToListAsync(ct);
        foreach (var mapping in failingMappings)
        {
            issues.Add(new HealthIssue("sourceMapping", "warning",
                $"{mapping.Series?.Title}: {mapping.SourceName} refresh failing — {mapping.LastError}"));
        }

        foreach (var folder in await db.RootFolders.ToListAsync(ct))
        {
            if (!Directory.Exists(folder.Path))
            {
                issues.Add(new HealthIssue("rootFolder", "error", $"Root folder inaccessible: {folder.Path}"));
            }
        }

        // Only worth flagging when the series is actually monitored — a "Monitor: none" series
        // with no source isn't waiting on anything.
        var noMappings = await db.Series
            .Where(s => s.MonitorNewItems != NewChapterMonitorMode.None &&
                        !s.SourceMappings.Any(m => m.Enabled))
            .Select(s => s.Title)
            .ToListAsync(ct);
        foreach (var title in noMappings)
        {
            issues.Add(new HealthIssue("series", "warning",
                $"{title} is monitored but has no enabled source mappings"));
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
