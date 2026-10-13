using Maki.Api.Localization;
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
    private bool _zoneWarned;

    /// <summary>
    /// The configured scan time zone, or the host's own when that id is unknown here. Options can be
    /// saved on one host and restored onto another with a different zone database, and a throw would
    /// stop every scan on the pass.
    /// </summary>
    private TimeZoneInfo ResolveZone(string? id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id ?? TimeZoneInfo.Local.Id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            if (!_zoneWarned)
            {
                _zoneWarned = true;
                logger.LogWarning("Health scan time zone {Zone} is not known on this host, using the host's own", id);
            }

            return TimeZoneInfo.Local;
        }
    }

    internal static readonly TimeSpan BaselineMargin = TimeSpan.FromHours(1);

    internal static IQueryable<ChapterFile> UnanalysedSince(MakiDbContext db, DateTime baseline) =>
        db.ChapterFiles.Where(f => f.DateAdded >= baseline &&
            !db.HealthFiles.Any(h => h.ChapterFileId == f.Id && !h.Removed && h.AnalyzedAt >= f.DateAdded));

    /// <summary>
    /// Moves the incremental baseline up to the oldest file still waiting for its first analysis, or
    /// to the newest file once none is. Without it every file added since the feature first ran is
    /// probed against the inventory on every pass for as long as the library exists.
    /// <para>
    /// The move never passes <see cref="BaselineMargin"/> before <paramref name="now"/>: a download
    /// stamps DateAdded before it measures and commits later, and a restored or renamed file keeps
    /// its old DateAdded, so a row can land below the newest one already seen.
    /// </para>
    /// </summary>
    internal static async Task<DateTime> AdvanceBaselineAsync(
        MakiDbContext db, IAppSettings settings, DateTime baseline, DateTime now, CancellationToken ct)
    {
        var next = await UnanalysedSince(db, baseline).MinAsync(f => (DateTime?)f.DateAdded, ct)
                   ?? await db.ChapterFiles.Where(f => f.DateAdded >= baseline).MaxAsync(f => (DateTime?)f.DateAdded, ct);
        if (next is not { } moved)
        {
            return baseline;
        }

        var utc = DateTime.SpecifyKind(moved, DateTimeKind.Utc);
        var ceiling = DateTime.SpecifyKind(now, DateTimeKind.Utc) - BaselineMargin;
        if (utc > ceiling)
        {
            utc = ceiling;
        }

        if (utc <= baseline)
        {
            return baseline;
        }

        await settings.SetAsync(SettingKeys.HealthIncrementalSince, utc.ToString("O"), ct);
        return utc;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
                var settings = scope.ServiceProvider.GetRequiredService<IAppSettings>();
                var options = JsonSerializer.Deserialize<HealthOptions>(await settings.GetAsync(SettingKeys.HealthOptions, stoppingToken) ?? "{}", HealthScanService.Json) ?? new();
                var baselineText = await settings.GetAsync(SettingKeys.HealthIncrementalSince, stoppingToken);
                if (baselineText == null)
                {
                    baselineText = DateTime.UtcNow.ToString("O");
                    await settings.SetAsync(SettingKeys.HealthIncrementalSince, baselineText, stoppingToken);
                }
                var baseline = DateTime.Parse(baselineText, null, System.Globalization.DateTimeStyles.RoundtripKind);
                if (options.AutomaticScanning)
                {
                    var zone = ResolveZone(options.TimeZone);
                    var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
                    var date = local.ToString("yyyy-MM-dd");
                    if (local.Hour >= options.ScanHour && await settings.GetAsync(SettingKeys.HealthLastScheduled, stoppingToken) != date)
                    {
                        db.HealthScans.Add(new());
                        await db.SaveChangesAsync(stoppingToken);
                        await settings.SetAsync(SettingKeys.HealthLastScheduled, date, stoppingToken);
                    }
                    // Imports and downloads change ChapterFile.DateAdded or add a new row.
                    // A scan skips a series while it has queue rows in flight and an offline root cannot
                    // be read at all, so neither would ever move AnalyzedAt and the same scan would be
                    // queued again on every pass.
                    var onlineRoots = (await db.RootFolders.Select(r => new { r.Id, r.Path }).ToListAsync(stoppingToken))
                        .Where(r => Directory.Exists(r.Path)).Select(r => r.Id).ToList();
                    var unanalysed = UnanalysedSince(db, baseline);
                    var series = await unanalysed
                        .Where(f => db.Series.Any(s => s.Id == f.SeriesId && onlineRoots.Contains(s.RootFolderId)))
                        .Where(f => !db.DownloadQueue.Any(q => q.SeriesId == f.SeriesId && q.Status != QueueStatus.Completed && q.Status != QueueStatus.Failed && q.Status != QueueStatus.Cancelled))
                        // An import still waiting on its match rewrites every file's ComicInfo when it
                        // links, so verifying the bytes before that reads them twice for nothing.
                        .Where(f => !db.Series.Any(s => s.Id == f.SeriesId && (s.SourceMatchPending || s.PendingImportLink != PendingImportLink.None)))
                        .Select(f => f.SeriesId).Distinct().Order().Take(100).ToListAsync(stoppingToken);
                    // Verified, unlike the scheduled sweep. These are the chapters that just
                    // arrived, so reading them costs a read of what was just written rather than of
                    // the whole library - and a download that fetched a truncated page is exactly
                    // what reading the bytes catches. Everything is verified once, as it lands.
                    foreach (var seriesId in series)
                        if (!await db.HealthScans.AnyAsync(s => s.SeriesId == seriesId && (s.Status == "pending" || s.Status == "running"), stoppingToken))
                            db.HealthScans.Add(new() { SeriesId = seriesId, Verify = true });
                    await db.SaveChangesAsync(stoppingToken);
                    await AdvanceBaselineAsync(db, settings, baseline, DateTime.UtcNow, stoppingToken);
                }
                var scan = await db.HealthScans.Where(s => s.Status == "pending" || s.Status == "running").OrderBy(s => s.Id).FirstOrDefaultAsync(stoppingToken);
                if (scan != null)
                {
                    var before = await db.HealthFindings.MaxAsync(f => (int?)f.Id, stoppingToken) ?? 0;
                    using var scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    HealthScanService.Running[scan.Id] = scanCancellation;
                    try { await scope.ServiceProvider.GetRequiredService<HealthScanService>().RunAsync(scan, scanCancellation.Token, options.ScanWorkers); }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { db.ChangeTracker.Clear(); }
                    finally { HealthScanService.Running.TryRemove(scan.Id, out _); }
                    var found = await db.HealthFindings.CountAsync(f => f.Id > before && f.State == "open", stoppingToken);
                    if (found > 0)
                    {
                        scope.ServiceProvider.GetRequiredService<InboxService>().Raise(
                            InboxEventType.HealthIssue,
                            new InboxMessage(
                                Key: "inbox.scan.findings",
                                Params: InboxMessage.Args(new { count = found }),
                                Url: "/health?tab=files"),
                            InboxAudience.Admins);

                        // Outbound to a chat channel, which has no language of its own to consult.
                        var localizer = scope.ServiceProvider.GetRequiredService<IMessageCatalog>();
                        var locale = await scope.ServiceProvider.GetRequiredService<IUserLocaleResolver>()
                            .DefaultAsync(stoppingToken);
                        scope.ServiceProvider.GetRequiredService<NotificationService>().Dispatch(
                            NotificationEventType.HealthIssue,
                            new(NotificationEventType.HealthIssue,
                                localizer.GetFor(locale, "notify.scan.findings.title"),
                                localizer.GetFor(locale, "notify.scan.findings.body", new { count = found })));
                    }
                }
                foreach (var op in await db.HealthOperations.Where(o => o.Kind == "repair" && o.Status == "downloading").ToListAsync(stoppingToken))
                {
                    var items = await db.DownloadQueue.Where(q => q.HealthOperationId == op.Id).ToListAsync(stoppingToken);
                    if (items.Any(i => i.Status is QueueStatus.Failed or QueueStatus.Cancelled))
                    {
                        op.Status = "failed"; op.ErrorKey = "health.operation.error.downloadFailed";
                        foreach (var item in items.Where(i => i.Status != QueueStatus.Completed))
                        { item.Status = QueueStatus.Cancelled; scope.ServiceProvider.GetRequiredService<DownloadQueueService>().CancelWork(item.Id); }
                    }
                    else if (items.Count > 0 && items.All(i => i.Status == QueueStatus.Completed))
                    {
                        var file = await db.HealthFiles.FindAsync([op.FileId], stoppingToken);
                        var root = file == null ? null : await db.RootFolders.FindAsync([file.RootFolderId], stoppingToken);
                        if (root == null) { op.Status = "failed"; op.ErrorKey = "health.operation.error.rootMissing"; }
                        else
                        {
                            var candidates = new List<RepairCandidate>();
                            foreach (var item in items)
                            {
                                var relative = $".maki/health/{op.Id}/chapter-{item.ChapterId}.cbz";
                                // Verified, always. Applying a replacement checks the candidate's
                                // hash and refuses one whose analysis found errors, and an indexed
                                // candidate has neither - it would be rejected at the last step
                                // after the download had already happened.
                                var analysis = await ArchiveHealthAnalyzer.AnalyzeAsync(
                                    HealthPaths.Resolve(root.Path, relative), stoppingToken, verify: true);
                                candidates.Add(new(item.ChapterId!.Value, relative, analysis.Hash ?? "", analysis, SourceMappingId: item.SourceMappingId));
                            }
                            op.JournalJson = JsonSerializer.Serialize(candidates, HealthScanService.Json);
                            op.Status = "review";
                        }
                    }
                    var nextStatus = op.Status;
                    var nextJournal = op.JournalJson;
                    var nextErrorKey = op.ErrorKey;
                    await HealthOperationService.MutationGate.WaitAsync(stoppingToken);
                    try
                    {
                        await db.Entry(op).ReloadAsync(stoppingToken);
                        if (op.Status == "downloading")
                        {
                            op.Status = nextStatus; op.JournalJson = nextJournal; op.ErrorKey = nextErrorKey;
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
