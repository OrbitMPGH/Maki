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
    NotificationService notifications, InboxService inbox, ISchedulerFactory schedulerFactory)
{
    private static readonly SemaphoreSlim Gate = new(1);
    public async Task RefreshAsync(CancellationToken ct)
    {
        if (!await Gate.WaitAsync(0, ct)) return;
        try
        {
            var options = System.Text.Json.JsonSerializer.Deserialize<HealthOptions>(await settings.GetAsync("health.options", ct) ?? "{}", HealthScanService.Json) ?? new();
            var checks = new List<(string Id, string Category, string Status, string Message, string? Url, bool Connectivity)>();
            void Add(string id, string category, string status, string message, string? url = null, bool connection = false) => checks.Add((id, category, status, message.Replace('\u2014', '-'), url, connection));
            try
            {
                foreach (var issue in await legacy.GetIssuesAsync(ct))
                    Add($"legacy:{issue.Key ?? $"{issue.Type}:{issue.SeriesId}"}", "library", issue.Severity, issue.Message, issue.SeriesId is {} id ? $"/series/{id}" : "/settings");
            }
            catch { Add("library-check", "library", "unavailable", "Library health checks could not complete"); }
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
                    Add(id, "storage", "healthy", $"{directory} is writable");
                }
                catch { Add(id, "storage", "error", $"{directory} is missing or not writable", "/settings"); }
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
                    Add($"disk:{drive.Name}", "storage", status, $"{drive.Name}: {gib:F1} GiB free ({percent:F1}%)");
                }
                catch { Add($"disk:{id}", "storage", "unavailable", "Disk capacity is unavailable"); }
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
                Add("backup", "system", latest < DateTime.UtcNow.AddDays(-options.BackupDays) ? "warning" : "healthy", latest == DateTime.MinValue ? "No backup available" : $"Last backup: {latest:u}", "/settings?tab=system&s=backups");
            }
            catch { Add("backup", "system", "unavailable", "Backup directory cannot be inspected"); }
            var failed = await db.DownloadQueue.CountAsync(q => q.Status == QueueStatus.Failed, ct);
            foreach (var root in roots)
            {
                try
                {
                    var stage = HealthPaths.Resolve(root.Path, ".maki/health");
                    long size = 0;
                    if (Directory.Exists(stage))
                        foreach (var candidate in HealthPaths.Archives(stage)) size += new FileInfo(candidate).Length;
                    Add($"staging:{root.Id}", "storage", "healthy", $"Root {root.Id} repair staging: {size / 1048576.0:F1} MiB", "/health?tab=repairs");
                }
                catch { Add($"staging:{root.Id}", "storage", "unavailable", $"Root {root.Id} repair staging cannot be inspected"); }
            }
            Add("downloads", "downloads", failed > 0 ? "warning" : "healthy", $"{failed} failed downloads", "/activity");
            var queue = services.GetRequiredService<DownloadQueueService>();
            foreach (var source in sources.All)
                Add($"cooldown:{source.Name}", "downloads", queue.CooldownRemaining(source.Name) > TimeSpan.Zero ? "warning" : "healthy",
                    queue.CooldownRemaining(source.Name) > TimeSpan.Zero ? $"{source.Name} is cooling down" : $"{source.Name} has no cooldown", "/activity");
            try
            {
                var scheduler = await schedulerFactory.GetScheduler(ct);
                Add("scheduler", "system", scheduler.IsStarted && !scheduler.InStandbyMode ? "healthy" : "warning", scheduler.IsStarted && !scheduler.InStandbyMode ? "Background scheduler is running" : "Background scheduler is paused");
                var pending = (await db.Database.GetPendingMigrationsAsync(ct)).Count();
                Add("database", "system", pending > 0 ? "warning" : "healthy", pending > 0 ? $"{pending} database migrations pending" : "Database is reachable and up to date");
                if (File.Exists(Path.Combine(paths.ConfigDir, "health-migration-error.txt"))) Add("migration-history", "system", "warning", "A previous startup migration failed. Check startup logs.");
            }
            catch { Add("database", "system", "unavailable", "Database or scheduler diagnostics could not complete"); }
            var old = await db.HealthChecks.Where(c => c.Category != "job").ToListAsync(ct);
            foreach (var check in checks)
            {
                var row = old.FirstOrDefault(r => r.Id == check.Id);
                if (row == null) { row = new() { Id = check.Id }; db.HealthChecks.Add(row); old.Add(row); }
                row.Category = check.Category; row.Message = check.Message; row.Url = check.Url;
                if (HealthTransitions.Observe(row, check.Status, check.Connectivity, DateTime.UtcNow))
                    Notify(row, !HealthTransitions.IsIssue(check.Status));
            }
            if (!checks.Any(c => c.Id == "library-check"))
                foreach (var row in old.Where(r => r.Id.StartsWith("legacy:") && !checks.Any(c => c.Id == r.Id) && r.Status != "healthy"))
                {
                    row.Status = row.NotifiedStatus = "healthy";
                    row.ChangedAt = row.CheckedAt = DateTime.UtcNow;
                    Notify(row, true);
                }
            await db.SaveChangesAsync(ct);

            async Task Probe(string name, string urlKey, string? secretKey, bool required, Func<string, string?, CancellationToken, Task<bool>> ping)
            {
                var url = await settings.GetAsync(urlKey, ct);
                var secret = secretKey == null ? null : await settings.GetAsync(secretKey, ct);
                if (string.IsNullOrWhiteSpace(url)) { Add(name, "connections", required || !string.IsNullOrWhiteSpace(secret) ? "warning" : "disabled", $"{name}: not configured", "/settings?tab=connections"); return; }
                if (secretKey != null && string.IsNullOrWhiteSpace(secret)) { Add(name, "connections", "warning", $"{name}: configuration is incomplete", "/settings?tab=connections"); return; }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var ok = false;
                try { ok = await ping(url, secret, timeout.Token).WaitAsync(timeout.Token); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } catch { }
                Add(name, "connections", ok ? "healthy" : "error", $"{name}: {(ok ? "connected" : "connection failed")}", "/settings?tab=connections", true);
            }
        }
        finally { Gate.Release(); }
    }

    private void Notify(HealthCheckRecord row, bool recovered)
    {
        var title = recovered ? "Health recovered" : "Health issue";
        var body = recovered ? $"Resolved: {row.Message}" : row.Message;
        var level = row.Status == "error" ? NotificationLevel.Error : recovered ? NotificationLevel.Info : NotificationLevel.Warning;
        db.HealthHistory.Add(new() { Kind = "transition", Message = body });
        notifications.Dispatch(NotificationEventType.HealthIssue, new(NotificationEventType.HealthIssue, title, body, Level: level));
        inbox.Raise(InboxEventType.HealthIssue, new(title, body, Level: level, Url: "/health"), InboxAudience.Admins);
    }
}

