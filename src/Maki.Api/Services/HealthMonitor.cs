using Maki.Api.Localization;
using System.Text.Json;
using Maki.Api.Configuration;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Http;
using Maki.Core.Inbox;
using Maki.Core.Notifications;
using Maki.Core.Sources;
using Maki.Data;
using Microsoft.EntityFrameworkCore;
using Quartz;

namespace Maki.Api.Services;

/// <param name="ScanWorkers">Pages decoded at once during a scan; 0 sizes it to the machine.
/// Analysis is almost entirely image decoding, so this is directly how much CPU a background scan
/// is allowed to take, and the only performance knob worth exposing.</param>
public record HealthOptions(double WarningPercent = 10, double ErrorPercent = 2,
    double WarningGiB = 10, double ErrorGiB = 1, int BackupDays = 7,
    string? TimeZone = null, int ScanHour = 3, bool AutomaticScanning = true, int ScanWorkers = 0);

public class HealthMonitor(MakiDbContext db, HealthCheckService legacy, IAppSettings settings,
    AppPaths paths, SourceRegistry sources, SourceAvailability availability, IServiceProvider services,
    NotificationService notifications, InboxService inbox, ILocalizer localizer,
    IUserLocaleResolver locales, ISchedulerFactory schedulerFactory)
{
    private static readonly SemaphoreSlim Gate = new(1);
    public async Task RefreshAsync(CancellationToken ct)
    {
        if (!await Gate.WaitAsync(0, ct)) return;
        try
        {
            var options = System.Text.Json.JsonSerializer.Deserialize<HealthOptions>(await settings.GetAsync(SettingKeys.HealthOptions, ct) ?? "{}", HealthScanService.Json) ?? new();
            var checks = new List<(string Id, string Category, string Status, string MessageKey, string? ParamsJson, string? Url, bool Connectivity)>();
            // Key and values, never a sentence: a check runs on a timer with nobody attached, and
            // the page it lands on is read later by whoever opens it. The em dash strip this used to
            // do is gone with the English it was guarding.
            void Add(
                string id, string category, string status, string key, object? args = null,
                string? url = null, bool connection = false) =>
                checks.Add((id, category, status, key, args is null ? null : JsonSerializer.Serialize(args), url, connection));
            var folded = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (var issue in await legacy.GetIssuesAsync(ct))
                {
                    Add($"legacy:{issue.Key ?? $"{issue.Type}:{issue.SeriesId}"}", "library", issue.Severity,
                        issue.MessageKey, issue.Params,
                        issue.SeriesId is {} id ? $"/series/{id}" : issue.Covers is null ? "/settings" : null);
                    foreach (var mappingId in issue.Covers ?? []) folded.Add($"legacy:mapping:{mappingId}");
                }
            }
            catch { Add("library-check", "library", "unavailable", "health.check.libraryUnavailable"); }
            var roots = await db.RootFolders.ToListAsync(ct);
            var drives = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var (id, directory) in roots.Select(r => ($"root:{r.Id}", r.Path)).Append(("config", paths.ConfigDir)))
            {
                string? probe = null;
                try
                {
                    if (!Directory.Exists(directory)) throw new IOException();
                    HealthPaths.Resolve(directory, ".maki-health-check");
                    probe = Path.Combine(directory, $".maki-health-{Guid.NewGuid():N}.tmp");
                    await using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        await stream.WriteAsync(new byte[] { 1 }, ct);
                    File.Delete(probe);
                    Add(id, "storage", "healthy", "health.check.writable", new { path = directory });
                }
                catch { Add(id, "storage", "error", "health.check.notWritable", new { path = directory }, "/settings"); }
                finally { if (probe != null) { try { File.Delete(probe); } catch { } } }
                try
                {
                    var full = Path.GetFullPath(directory);
                    var drive = DriveInfo.GetDrives().Where(d => full.StartsWith(d.Name, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                        .OrderByDescending(d => d.Name.Length).FirstOrDefault();
                    if (drive == null || !drives.Add(drive.Name)) continue;
                    var gib = drive.AvailableFreeSpace / Math.Pow(1024, 3);
                    var percent = 100.0 * drive.AvailableFreeSpace / drive.TotalSize;
                    var status = gib < options.ErrorGiB || percent < options.ErrorPercent ? "error" : gib < options.WarningGiB || percent < options.WarningPercent ? "warning" : "healthy";
                    Add($"disk:{drive.Name}", "storage", status, "health.check.diskFree", new
                    {
                        drive = drive.Name,
                        gib = Math.Round(gib, 1),
                        percent = Math.Round(percent, 1),
                    });
                }
                catch { Add($"disk:{id}", "storage", "unavailable", "health.check.diskUnavailable"); }
            }
            var disabled = await availability.DisabledAsync(ct);
            var needsFlare = sources.All.Any(s => !disabled.Contains(s.Name) && s.Capabilities.HasFlag(SourceCapabilities.NeedsFlareSolverr));
            await Probe("FlareSolverr", SettingKeys.FlareSolverrUrl, null, needsFlare,
                (url, _, token) => services.GetRequiredService<FlareSolverrClient>().PingAsync(url, token));
            await Probe("Prowlarr", SettingKeys.ProwlarrUrl, SettingKeys.ProwlarrApiKey, false,
                (url, key, token) => services.GetRequiredService<Maki.Core.Indexers.ProwlarrClient>().PingAsync(url, key!, token));
            await Probe("Kavita", SettingKeys.KavitaUrl, SettingKeys.KavitaApiKey, false,
                (url, key, token) => services.GetRequiredService<Maki.Core.Kavita.KavitaClient>().PingAsync(url, key!, token));
            await Probe("qBittorrent", SettingKeys.QBittorrentUrl, null, false,
                async (url, _, token) => await services.GetRequiredService<Maki.Core.Download.QBittorrentClient>().PingAsync(url,
                    await settings.GetAsync(SettingKeys.QBittorrentUsername, token) ?? "", await settings.GetAsync(SettingKeys.QBittorrentPassword, token) ?? "", token));
            try
            {
                var latest = Directory.EnumerateFiles(paths.BackupDir, "*.zip").Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
                Add("backup", "system",
                    latest < DateTime.UtcNow.AddDays(-options.BackupDays) ? "warning" : "healthy",
                    latest == DateTime.MinValue ? "health.check.noBackup" : "health.check.lastBackup",
                    latest == DateTime.MinValue ? null : new { at = latest },
                    "/settings?tab=system&s=backups");
            }
            catch { Add("backup", "system", "unavailable", "health.check.backupUnreadable"); }
            var failed = await db.DownloadQueue.CountAsync(q => q.Status == QueueStatus.Failed, ct);
            foreach (var root in roots)
            {
                try
                {
                    var stage = HealthPaths.Resolve(root.Path, ".maki/health");
                    long size = 0;
                    if (Directory.Exists(stage))
                        foreach (var candidate in HealthPaths.Archives(stage)) size += new FileInfo(candidate).Length;
                    Add($"staging:{root.Id}", "storage", "healthy", "health.check.repairStaging",
                        new { root = root.Id, mib = Math.Round(size / 1048576.0, 1) }, "/health?tab=repairs");
                }
                catch
                {
                    Add($"staging:{root.Id}", "storage", "unavailable",
                        "health.check.repairStagingUnreadable", new { root = root.Id });
                }
            }
            Add("downloads", "downloads", failed > 0 ? "warning" : "healthy",
                "health.check.failedDownloads", new { count = failed }, "/activity");
            var queue = services.GetRequiredService<DownloadQueueService>();
            foreach (var source in sources.All)
                Add($"cooldown:{source.Name}", "downloads",
                    queue.CooldownRemaining(source.Name) > TimeSpan.Zero ? "warning" : "healthy",
                    queue.CooldownRemaining(source.Name) > TimeSpan.Zero
                        ? "health.check.coolingDown"
                        : "health.check.noCooldown",
                    new { source = source.Name }, "/activity");
            try
            {
                var scheduler = await schedulerFactory.GetScheduler(ct);
                var running = scheduler.IsStarted && !scheduler.InStandbyMode;
                Add("scheduler", "system", running ? "healthy" : "warning",
                    running ? "health.check.schedulerRunning" : "health.check.schedulerPaused");
                var pending = (await db.Database.GetPendingMigrationsAsync(ct)).Count();
                Add("database", "system", pending > 0 ? "warning" : "healthy",
                    pending > 0 ? "health.check.migrationsPending" : "health.check.databaseCurrent",
                    pending > 0 ? new { count = pending } : null);
                if (File.Exists(Path.Combine(paths.ConfigDir, "health-migration-error.txt")))
                {
                    Add("migration-history", "system", "warning", "health.check.migrationFailed");
                }
            }
            catch { Add("database", "system", "unavailable", "health.check.diagnosticsUnavailable"); }
            var old = await db.HealthChecks.Where(c => c.Category != "job").ToListAsync(ct);
            foreach (var check in checks)
            {
                var row = old.FirstOrDefault(r => r.Id == check.Id);
                if (row == null) { row = new() { Id = check.Id }; db.HealthChecks.Add(row); old.Add(row); }
                row.Category = check.Category;
                row.MessageKey = check.MessageKey;
                row.ParamsJson = check.ParamsJson;
                row.Message = string.Empty;
                row.Url = check.Url;
                if (HealthTransitions.Observe(row, check.Status, check.Connectivity, DateTime.UtcNow))
                    await NotifyAsync(row, !HealthTransitions.IsIssue(check.Status), ct);
            }
            if (!checks.Any(c => c.Id == "library-check"))
                foreach (var row in old.Where(r => r.Id.StartsWith("legacy:") && !checks.Any(c => c.Id == r.Id) && r.Status != "healthy"))
                {
                    // Still failing, now counted in its source's row. Announcing it as recovered
                    // would send one false all-clear per series the moment a site goes down.
                    if (folded.Contains(row.Id)) { db.HealthChecks.Remove(row); continue; }
                    row.Status = row.NotifiedStatus = "healthy";
                    row.ChangedAt = row.CheckedAt = DateTime.UtcNow;
                    await NotifyAsync(row, true, ct);
                }
            await db.SaveChangesAsync(ct);

            async Task Probe(string name, string urlKey, string? secretKey, bool required, Func<string, string?, CancellationToken, Task<bool>> ping)
            {
                var url = await settings.GetAsync(urlKey, ct);
                var secret = secretKey == null ? null : await settings.GetAsync(secretKey, ct);
                if (string.IsNullOrWhiteSpace(url))
                {
                    Add(name, "connections", required || !string.IsNullOrWhiteSpace(secret) ? "warning" : "disabled",
                        "health.check.notConfigured", new { service = name }, "/settings?tab=connections");
                    return;
                }
                if (secretKey != null && string.IsNullOrWhiteSpace(secret))
                {
                    Add(name, "connections", "warning", "health.check.incompleteConfig",
                        new { service = name }, "/settings?tab=connections");
                    return;
                }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var ok = false;
                try { ok = await ping(url, secret, timeout.Token).WaitAsync(timeout.Token); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } catch { }
                Add(name, "connections", ok ? "healthy" : "error",
                    ok ? "health.check.connected" : "health.check.connectionFailed",
                    new { service = name }, "/settings?tab=connections", true);
            }
        }
        finally { Gate.Release(); }
    }

    /// <summary>
    /// One check changed state. The inbox row stores the check's own key and values and is worded
    /// per reader; the Discord copy renders once, in the instance's language.
    /// </summary>
    private async Task NotifyAsync(HealthCheckRecord row, bool recovered, CancellationToken ct)
    {
        var level = row.Status == "error" ? NotificationLevel.Error
            : recovered ? NotificationLevel.Info
            : NotificationLevel.Warning;

        // A row written before the checks were keyed still has its English, and a check whose key
        // this build does not know still has an id worth naming.
        var detail = row.MessageKey is { Length: > 0 } key
            ? localizer.GetFor(await locales.DefaultAsync(ct), key, HealthParams(row.ParamsJson))
            : row.Message;

        var locale = await locales.DefaultAsync(ct);
        var title = localizer.GetFor(locale, recovered ? "notify.health.recovered.title" : "notify.health.issue.title");
        var body = recovered
            ? localizer.GetFor(locale, "notify.health.recovered.body", new { detail })
            : detail;

        db.HealthHistory.Add(new()
        {
            Kind = "transition",
            MessageKey = recovered ? "health.history.recovered" : "health.history.degraded",
            ParamsJson = JsonSerializer.Serialize(new { check = row.Id, status = row.Status }),
        });
        notifications.Dispatch(NotificationEventType.HealthIssue,
            new(NotificationEventType.HealthIssue, title, body, Level: level));
        inbox.Raise(
            InboxEventType.HealthIssue,
            new InboxMessage(
                Key: recovered ? "inbox.health.recovered" : "inbox.health.issue",
                Params: InboxDetailArgs(row),
                Level: level,
                Url: "/health"),
            InboxAudience.Admins);
    }

    /// <summary>
    /// The inbox row's parameters: the check's message key (or its English, on a row from before the
    /// checks were keyed) as <c>detail</c>, and the check's own values under a <c>detail.</c> prefix.
    /// <see cref="Localization.InboxRenderer"/> renders the detail per reader from those. Prefixed
    /// because the renderer owns <c>{series}</c>, and a check's own <c>{series}</c> would otherwise be
    /// replaced with the inbox's.
    /// </summary>
    private static Dictionary<string, object?> InboxDetailArgs(HealthCheckRecord row)
    {
        var args = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["detail"] = row.MessageKey ?? row.Message,
        };
        foreach (var (name, value) in HealthParams(row.ParamsJson))
        {
            args[$"detail.{name}"] = value;
        }

        return args;
    }

    /// <summary>
    /// Values for a keyed health message. Never throws: a row whose parameters cannot be read still
    /// has a message worth showing, with its placeholders unfilled.
    /// </summary>
    public static Dictionary<string, object?> HealthParams(string? json)
    {
        var into = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(json)) return into;

        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                into[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.Number => property.Value.TryGetInt64(out var l) ? l : property.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => property.Value.GetString(),
                };
            }
        }
        catch (JsonException)
        {
        }

        return into;
    }
}

