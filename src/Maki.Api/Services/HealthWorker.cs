using System.Text.Json;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Inbox;
using Maki.Core.Notifications;
using Maki.Core.Reading;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

public class HealthWorker(IServiceScopeFactory scopes, ILogger<HealthWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
                var settings = scope.ServiceProvider.GetRequiredService<IAppSettings>();
                var options = JsonSerializer.Deserialize<HealthOptions>(await settings.GetAsync("health.options", stoppingToken) ?? "{}", HealthScanService.Json) ?? new();
                var baselineText = await settings.GetAsync("health.incrementalSince", stoppingToken);
                if (baselineText == null)
                {
                    baselineText = DateTime.UtcNow.ToString("O");
                    await settings.SetAsync("health.incrementalSince", baselineText, stoppingToken);
                }
                var baseline = DateTime.Parse(baselineText, null, System.Globalization.DateTimeStyles.RoundtripKind);
                if (options.AutomaticScanning)
                {
                    var zone = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone ?? TimeZoneInfo.Local.Id);
                    var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
                    var date = local.ToString("yyyy-MM-dd");
                    if (local.Hour >= options.ScanHour && await settings.GetAsync("health.lastscheduled", stoppingToken) != date)
                    {
                        db.HealthScans.Add(new());
                        await db.SaveChangesAsync(stoppingToken);
                        await settings.SetAsync("health.lastscheduled", date, stoppingToken);
                    }
                    // Imports and downloads change ChapterFile.DateAdded or add a new row.
                    var series = await db.ChapterFiles.Where(f => f.DateAdded >= baseline && !db.HealthFiles.Any(h => h.ChapterFileId == f.Id && !h.Removed && h.AnalyzedAt >= f.DateAdded))
                        .Select(f => f.SeriesId).Distinct().Order().Take(100).ToListAsync(stoppingToken);
                    foreach (var seriesId in series)
                        if (!await db.HealthScans.AnyAsync(s => s.SeriesId == seriesId && (s.Status == "pending" || s.Status == "running"), stoppingToken))
                            db.HealthScans.Add(new() { SeriesId = seriesId });
                    await db.SaveChangesAsync(stoppingToken);
                }
                var scan = await db.HealthScans.Where(s => s.Status == "pending" || s.Status == "running").OrderBy(s => s.Id).FirstOrDefaultAsync(stoppingToken);
                if (scan != null)
                {
                    var before = await db.HealthFindings.MaxAsync(f => (int?)f.Id, stoppingToken) ?? 0;
                    using var scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    HealthScanService.Running[scan.Id] = scanCancellation;
                    try { await scope.ServiceProvider.GetRequiredService<HealthScanService>().RunAsync(scan, scanCancellation.Token); }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { db.ChangeTracker.Clear(); }
                    finally { HealthScanService.Running.TryRemove(scan.Id, out _); }
                    var found = await db.HealthFindings.CountAsync(f => f.Id > before && f.State == "open", stoppingToken);
                    if (found > 0)
                    {
                        var body = $"File scan found {found} new findings. Review them on Health.";
                        scope.ServiceProvider.GetRequiredService<InboxService>().Raise(InboxEventType.HealthIssue, new("File health scan", body, Url: "/health?tab=files"), InboxAudience.Admins);
                        scope.ServiceProvider.GetRequiredService<NotificationService>().Dispatch(NotificationEventType.HealthIssue, new(NotificationEventType.HealthIssue, "File health scan", body));
                    }
                }
                foreach (var op in await db.HealthOperations.Where(o => o.Kind == "repair" && o.Status == "downloading").ToListAsync(stoppingToken))
                {
                    var items = await db.DownloadQueue.Where(q => q.HealthOperationId == op.Id).ToListAsync(stoppingToken);
                    if (items.Any(i => i.Status is QueueStatus.Failed or QueueStatus.Cancelled))
                    {
                        op.Status = "failed"; op.Error = "A replacement download failed. Request a new repair after reviewing Activity.";
                        foreach (var item in items.Where(i => i.Status != QueueStatus.Completed))
                        { item.Status = QueueStatus.Cancelled; scope.ServiceProvider.GetRequiredService<DownloadQueueService>().CancelWork(item.Id); }
                    }
                    else if (items.Count > 0 && items.All(i => i.Status == QueueStatus.Completed))
                    {
                        var file = await db.HealthFiles.FindAsync([op.FileId], stoppingToken);
                        var root = file == null ? null : await db.RootFolders.FindAsync([file.RootFolderId], stoppingToken);
                        if (root == null) { op.Status = "failed"; op.Error = "Root folder no longer exists"; }
                        else
                        {
                            var candidates = new List<RepairCandidate>();
                            foreach (var item in items)
                            {
                                var relative = $".maki/health/{op.Id}/chapter-{item.ChapterId}.cbz";
                                var analysis = await ArchiveHealthAnalyzer.AnalyzeAsync(HealthPaths.Resolve(root.Path, relative), stoppingToken);
                                candidates.Add(new(item.ChapterId!.Value, relative, analysis.Hash ?? "", analysis, SourceMappingId: item.SourceMappingId));
                            }
                            op.JournalJson = JsonSerializer.Serialize(candidates, HealthScanService.Json);
                            op.Status = "review";
                        }
                    }
                    var nextStatus = op.Status;
                    var nextJournal = op.JournalJson;
                    var nextError = op.Error;
                    await HealthOperationService.MutationGate.WaitAsync(stoppingToken);
                    try
                    {
                        await db.Entry(op).ReloadAsync(stoppingToken);
                        if (op.Status == "downloading")
                        {
                            op.Status = nextStatus; op.JournalJson = nextJournal; op.Error = nextError;
                            await db.SaveChangesAsync(stoppingToken);
                        }
                    }
                    finally { HealthOperationService.MutationGate.Release(); }
                }
                await db.SaveChangesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Health worker pass failed; retrying"); }
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }
}
