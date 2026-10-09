using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Maki.Api.Dtos;
using Maki.Api.Hubs;
using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Core.Download;
using Maki.Core.Entities;
using Maki.Core.Inbox;
using Maki.Core.Indexers;
using Maki.Core.Notifications;
using Maki.Core.Paths;
using Maki.Data;
using Microsoft.EntityFrameworkCore;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Tracks torrent queue items against qBittorrent: updates progress, claims hashes for .torrent
/// grabs (magnets carry theirs), and hands finished downloads to <see cref="TorrentImportService"/>.
/// <para>
/// It imports on its own only when nothing is at stake. A download whose files cover chapters the
/// library already has is parked as <see cref="QueueStatus.AwaitingImport"/> instead: replacing
/// files is a decision, and an unattended job is the worst possible place to take it. The user
/// makes the call from the activity list (<c>POST queue/{id}/import</c>), and this job must never
/// advance such an item by itself — hence the explicit skip below.
/// </para>
/// </summary>
[DisallowConcurrentExecution]
public class CompletedDownloadJob(
    MakiDbContext db,
    ReleaseService releaseService,
    QBittorrentClient qbittorrent,
    TorrentImportService importer,
    EventBroadcaster events,
    ISchedulerFactory schedulerFactory,
    NotificationService notifications,
    InboxService inbox,
    IMessageCatalog localizer,
    IUserLocaleResolver locales,
    ILogger<CompletedDownloadJob> logger) : IJob
{
    /// <summary>How long a torrent whose hash is known may be missing from qBittorrent before its row fails.</summary>
    internal static readonly TimeSpan MissingTorrentGrace = TimeSpan.FromHours(6);

    // The job instance is created per run, so state that must outlive a poll is static. In memory
    // only: after a restart the grace period for a missing torrent starts over, which errs on waiting.
    private static readonly ConcurrentDictionary<int, DateTime> MissingSince = new();
    private static int _qbittorrentDown;

    public async Task Execute(IJobExecutionContext context)
    {
        var ct = context.CancellationToken;

        var pending = await db.DownloadQueue
            .Where(q => q.Protocol == AcquisitionProtocol.Torrent &&
                        q.Status != QueueStatus.Completed &&
                        q.Status != QueueStatus.Failed &&
                        q.Status != QueueStatus.Cancelled)
            .Include(q => q.Series)!.ThenInclude(s => s!.RootFolder)
            .ToListAsync(ct);

        if (pending.Count == 0)
        {
            return;
        }

        (string Url, string Username, string Password, string Category) qbt;
        try
        {
            qbt = await releaseService.GetQbtConfigAsync(ct);
        }
        catch (InvalidOperationException)
        {
            return; // not configured; nothing to poll
        }

        var pathMap = await releaseService.GetQbtPathMapAsync(ct);
        IReadOnlyList<QBittorrentClient.QbtTorrent> torrents;
        try
        {
            torrents = await qbittorrent.ListAsync(qbt.Url, qbt.Username, qbt.Password, qbt.Category, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException ||
                                   (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            // Polled every 15 seconds: logging each failure with its stack buried the log within a
            // day of qBittorrent being down. One line when it goes, one when it comes back.
            if (Interlocked.Exchange(ref _qbittorrentDown, 1) == 0)
            {
                logger.LogWarning("qBittorrent at {Url} is not answering ({Error}); torrent progress is paused until it does",
                    qbt.Url, ex.Message);
            }

            return;
        }

        if (Interlocked.Exchange(ref _qbittorrentDown, 0) == 1)
        {
            logger.LogInformation("qBittorrent at {Url} is answering again", qbt.Url);
        }

        foreach (var gone in MissingSince.Keys.Where(id => pending.All(q => q.Id != id)).ToList())
        {
            MissingSince.TryRemove(gone, out _);
        }

        // Hashes already tied to any torrent item — including completed and failed ones,
        // whose torrents keep seeding in qBittorrent. Excluding only pending items let a
        // finished previous download be re-claimed by a new hashless (.torrent) item and
        // imported into the wrong series' folder.
        //
        // Only a hashless item ever consults the set, so the history is read only while one is pending
        // instead of on every poll.
        var claimedHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (pending.Any(q => ReleaseInfoOf(q) is { TorrentHash: null }))
        {
            var claimedJson = await db.DownloadQueue
                .Where(q => q.Protocol == AcquisitionProtocol.Torrent && q.ReleaseInfoJson != null)
                .Select(q => q.ReleaseInfoJson!)
                .ToListAsync(ct);
            claimedHashes.UnionWith(claimedJson
                .Select(j => JsonSerializer.Deserialize<ReleaseInfo>(j)?.TorrentHash)
                .OfType<string>());
        }

        // Sent after the poll's single save, so a failed save cannot announce the same row twice.
        var announcements = new List<(DownloadQueueItem Item, bool Parked)>();

        foreach (var item in pending)
        {
            var info = ReleaseInfoOf(item);
            if (info is null)
            {
                continue;
            }

            // A request is importing it (QueueController.Import) and owns the row until it settles.
            // One reading Importing with no request behind it lost that request to a restart; it goes
            // back to the user's decision rather than being imported here unattended; conditionally,
            // since the request may have settled the row after this poll read it.
            if (item.Status == QueueStatus.Importing)
            {
                if (!TorrentImportService.IsManualImportRunning(item.Id) &&
                    await db.DownloadQueue
                        .Where(q => q.Id == item.Id && q.Status == QueueStatus.Importing)
                        .ExecuteUpdateAsync(s => s.SetProperty(q => q.Status, QueueStatus.AwaitingImport), ct) > 0)
                {
                    db.Entry(item).Property(q => q.Status).OriginalValue = QueueStatus.AwaitingImport;
                    item.Status = QueueStatus.AwaitingImport;
                    await BroadcastAsync(item);
                }

                continue;
            }

            var torrent = info.TorrentHash != null
                ? torrents.FirstOrDefault(t => t.Hash.Equals(info.TorrentHash, StringComparison.OrdinalIgnoreCase))
                : ClaimTorrent(item, torrents, claimedHashes);

            if (torrent is null)
            {
                // Grabbed via .torrent URL and not yet visible, or removed by the user.
                if (DateTime.UtcNow - item.QueuedAt > TimeSpan.FromHours(2) && info.TorrentHash is null)
                {
                    item.Status = QueueStatus.Failed;
                    item.SetError("error.download.torrentMissing");
                    await BroadcastAsync(item);
                    announcements.Add((item, false));
                }
                else if (info.TorrentHash is not null && item.Status != QueueStatus.AwaitingImport &&
                         DateTime.UtcNow - MissingSince.GetOrAdd(item.Id, DateTime.UtcNow) > MissingTorrentGrace)
                {
                    // Deleted in qBittorrent, moved to another category, or dropped by it. Nothing else
                    // settles a torrent row, so without this it read "Downloading" forever.
                    item.Status = QueueStatus.Failed;
                    item.SetError("error.download.torrentRemoved");
                    MissingSince.TryRemove(item.Id, out _);
                    await BroadcastAsync(item);
                    announcements.Add((item, false));
                }

                continue;
            }

            MissingSince.TryRemove(item.Id, out _);

            if (info.TorrentHash is null)
            {
                info = info with { TorrentHash = torrent.Hash };
                item.ReleaseInfoJson = JsonSerializer.Serialize(info);
                claimedHashes.Add(torrent.Hash);
            }

            var previousPagesDone = item.PagesDone;
            var previousStatus = item.Status;
            item.PagesTotal = 100;
            item.PagesDone = (int)(torrent.Progress * 100);

            if (torrent.IsComplete && item.Status != QueueStatus.AwaitingImport)
            {
                try
                {
                    await ImportAsync(item, torrent, pathMap, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One unimportable download must not take the pass with it. Everything above
                    // this (progress, claimed hashes, the other items' statuses) is only written
                    // by the single SaveChangesAsync below, so letting this escape would discard
                    // the whole poll and then do it again every fifteen seconds, for as long as
                    // the offending torrent sits in the queue.
                    logger.LogError(ex, "Could not import torrent '{Title}'", item.Title);
                    item.Status = QueueStatus.Failed;
                    item.SetRawError(ex.Message);
                }

                // Its series was deleted mid-import and the row cascaded away with it.
                if (db.Entry(item).State == EntityState.Detached)
                {
                    continue;
                }
            }

            // Only on a real change. A finished torrent stays finished, so keying this on
            // IsComplete pushed the same row to every client on every poll for as long as it sat
            // there — which an item parked for an import decision does indefinitely.
            if (item.PagesDone != previousPagesDone || item.Status != previousStatus)
            {
                await BroadcastAsync(item);
            }

            if (item.Status != previousStatus && item.Status is QueueStatus.Failed or QueueStatus.AwaitingImport)
            {
                announcements.Add((item, item.Status == QueueStatus.AwaitingImport));
            }
        }

        await db.SaveChangesAsync(ct);

        foreach (var (announced, parked) in announcements)
        {
            try
            {
                await AnnounceAsync(announced, parked, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not announce torrent '{Title}'", announced.Title);
            }
        }
    }

    /// <summary>
    /// Tells people about a torrent row that stopped, the way the scraper pipeline does for a failed
    /// chapter: a chat or webhook message in the instance language (never for an upgrade, which
    /// leaves the library as it was) and, for an automatic grab, an inbox row. A parked import needs
    /// somebody to decide, so it also reaches the inbox for a grab the user started themselves.
    /// </summary>
    private async Task AnnounceAsync(DownloadQueueItem item, bool parked, CancellationToken ct)
    {
        var locale = await locales.DefaultAsync(ct);
        var series = item.Series?.Title ?? localizer.GetFor(locale, "inbox.unknownSeries");
        var isUpgrade = item.Origin == DownloadOrigin.Upgrade || item.UpgradeInfoJson is not null;
        var release = item.Title ?? string.Empty;

        if (parked)
        {
            if (!isUpgrade)
            {
                notifications.Dispatch(NotificationEventType.DownloadFailed, new NotificationMessage(
                    NotificationEventType.DownloadFailed,
                    Title: localizer.GetFor(locale, "notify.import.awaiting.title"),
                    Body: localizer.GetFor(locale, "notify.import.awaiting.body", new { series, release }),
                    Level: NotificationLevel.Warning,
                    SeriesTitle: item.Series?.Title,
                    SeriesId: item.SeriesId));
            }

            inbox.RaiseForSeries(InboxEventType.DownloadFailed, new InboxMessage(
                Key: "inbox.import.awaiting",
                Params: InboxMessage.Args(new { release }),
                Level: NotificationLevel.Warning,
                SeriesId: item.SeriesId,
                Url: "/activity"), item.SeriesId);
            return;
        }

        var args = new Dictionary<string, object?>();
        if (!string.IsNullOrEmpty(item.ErrorParamsJson))
        {
            try
            {
                foreach (var (name, value) in JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item.ErrorParamsJson) ?? [])
                {
                    args[name] = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
                }
            }
            catch (JsonException)
            {
                // A reason with no values still reads better than none.
            }
        }

        var reason = item.ErrorKey is { } key
            ? localizer.GetFor(locale, key, args) + (item.ErrorMessage is { Length: > 0 } detail ? $": {detail}" : "")
            : item.ErrorMessage ?? localizer.GetFor(locale, "error.download.unexpected");

        if (!isUpgrade)
        {
            notifications.Dispatch(NotificationEventType.DownloadFailed, new NotificationMessage(
                NotificationEventType.DownloadFailed,
                Title: localizer.GetFor(locale, "notify.download.failed.title"),
                Body: localizer.GetFor(locale, "notify.download.failed.body", new
                {
                    series,
                    hasChapter = "no",
                    chapter = string.Empty,
                    reason,
                }),
                Level: NotificationLevel.Error,
                SeriesTitle: item.Series?.Title,
                SeriesId: item.SeriesId));
        }

        if (item.IsAutomatic)
        {
            args["hasChapter"] = "no";
            args["chapter"] = null;
            args["error"] = item.ErrorKey ?? item.ErrorMessage;
            inbox.RaiseForSeries(InboxEventType.DownloadFailed, new InboxMessage(
                Key: "inbox.download.failed",
                Params: args,
                Level: NotificationLevel.Error,
                SeriesId: item.SeriesId,
                Url: $"/series/{item.SeriesId}"), item.SeriesId);
        }
    }

    private Task BroadcastAsync(DownloadQueueItem item) =>
        events.QueueUpdated(QueueItemDto.FromEntity(item, chapter: null, item.Series!, "torrent"));

    /// <summary>
    /// Best guess at the qBittorrent torrent for an item grabbed via a .torrent URL (which
    /// carries no infohash): an unclaimed torrent added around the grab time. Among those,
    /// one whose name carries the series' title is strongly preferred, so a new item can't
    /// adopt an unrelated torrent that merely shares the time window. Falls back to the
    /// oldest in-window torrent (the original guess) when nothing matches, so an
    /// odd-naming indexer still resolves rather than stranding the download.
    /// </summary>
    private static QBittorrentClient.QbtTorrent? ClaimTorrent(
        DownloadQueueItem item,
        IReadOnlyList<QBittorrentClient.QbtTorrent> torrents,
        HashSet<string> claimedHashes)
    {
        var queuedUnix = new DateTimeOffset(item.QueuedAt).ToUnixTimeSeconds();
        var eligible = torrents
            .Where(t => !claimedHashes.Contains(t.Hash) && t.AddedOn >= queuedUnix - 120)
            .OrderBy(t => t.AddedOn)
            .ToList();
        if (eligible.Count == 0)
        {
            return null;
        }

        var seriesTokens = PrimaryTitleTokens(item.Series?.Title ?? ReleaseInfoOf(item)?.Title);
        var named = eligible.FirstOrDefault(t => MatchesSeries(t.Name, seriesTokens));
        return named ?? eligible[0];
    }

    /// <summary>Whether a qBittorrent torrent name carries every one of the series' primary title tokens.</summary>
    private static bool MatchesSeries(string torrentName, HashSet<string> seriesTokens)
    {
        if (seriesTokens.Count == 0)
        {
            return false;
        }

        var tokens = Tokenize(torrentName);
        return seriesTokens.All(tokens.Contains);
    }

    /// <summary>
    /// Word tokens of the series' main title — the part before a subtitle separator, which
    /// release names usually keep while dropping the rest ("Frieren: Beyond…" → "frieren").
    /// </summary>
    private static HashSet<string> PrimaryTitleTokens(string? title) =>
        string.IsNullOrWhiteSpace(title) ? [] : Tokenize(SearchQuery.Candidates(title).Last());

    private static HashSet<string> Tokenize(string value) =>
        Regex.Split(value.ToLowerInvariant(), "[^a-z0-9]+")
            .Where(t => t.Length > 0)
            .ToHashSet();

    private async Task ImportAsync(
        DownloadQueueItem item, QBittorrentClient.QbtTorrent torrent, (string? From, string? To) pathMap, CancellationToken ct)
    {
        TorrentImportService.BeginAutomaticImport(item.Id);
        try
        {
            await ImportRegisteredAsync(item, torrent, pathMap, ct);
        }
        finally
        {
            TorrentImportService.EndAutomaticImport(item.Id);
        }
    }

    private async Task ImportRegisteredAsync(
        DownloadQueueItem item, QBittorrentClient.QbtTorrent torrent, (string? From, string? To) pathMap, CancellationToken ct)
    {
        var series = item.Series!;

        // qBittorrent reports the path as it sees it; rewrite it to how Maki does
        // when the two run under different mounts (e.g. qBittorrent in Docker).
        var contentPath = PathRemapper.Map(torrent.ContentPath, pathMap.From, pathMap.To);

        var plan = await importer.PlanAsync(item, series, contentPath, ct);
        if (plan.ErrorKey is not null)
        {
            item.Status = QueueStatus.Failed;
            item.SetError(plan.ErrorKey, plan.ErrorArgs);
            return;
        }

        var decision = await importer.DecideUnattendedAsync(item, series, contentPath!, plan, ct);
        if (decision.Guard is { } failure)
        {
            logger.LogInformation("Upgrade torrent '{Title}' held back for review: {File} is {Reason}",
                item.Title, failure.File, failure.Reason);
            return;
        }

        var skipFiles = decision.SkipFiles;
        if (decision.Park)
        {
            item.Status = QueueStatus.AwaitingImport;
            item.ClearError();
            item.PagesDone = item.PagesTotal;
            logger.LogInformation(
                "Torrent '{Title}' is waiting for an import decision: it covers {Files} file(s) " +
                "'{Series}' already has and brings {New} new chapter(s)",
                item.Title, plan.ReplacedFileCount, series.Title, plan.NewChapterCount);
            return;
        }

        var statusBefore = item.Status;
        item.Status = QueueStatus.Importing;
        // The plan just built, handed over rather than left to be rebuilt: PlanAsync reads the page
        // names out of every volume archive in the download, and the answer cannot have changed
        // between the conflict check above and this line.
        var outcome = await importer.ImportAsync(
            item, series, contentPath, TorrentImportMode.Replace, ct, plan, skipFiles);
        if (outcome.ErrorKey == TorrentImportService.SeriesChangedKey)
        {
            // Deleted: the row went with the series. Moved: the next poll loads the series afresh.
            if (await db.DownloadQueue.IgnoreQueryFilters().AnyAsync(q => q.Id == item.Id, ct))
            {
                item.Status = statusBefore;
            }
            else
            {
                db.Entry(item).State = EntityState.Detached;
            }

            return;
        }

        if (!outcome.Applied)
        {
            item.Status = QueueStatus.Failed;
            if (outcome.ErrorKey is not null) item.SetError(outcome.ErrorKey, outcome.ErrorArgs, outcome.Error);
            else item.SetRawError(outcome.Error);
            return;
        }

        item.Status = QueueStatus.Completed;
        item.CompletedAt = DateTime.UtcNow;
        item.PagesDone = item.PagesTotal;

        // Torrent files keep their release name until this runs; save now so the rename's
        // active-download check (which re-queries the row) sees this item as Completed rather
        // than still in-flight and refuses to rename the series it just finished importing into.
        await db.SaveChangesAsync(ct);
        await importer.ApplyNamingAsync(series, outcome.ImportedPaths, ct);
        if (outcome.Imported > 0)
        {
            await ChapterFileMeasureJob.TriggerAsync(schedulerFactory, logger);
        }
    }

    private static ReleaseInfo? ReleaseInfoOf(DownloadQueueItem item) =>
        item.ReleaseInfoJson is null ? null : JsonSerializer.Deserialize<ReleaseInfo>(item.ReleaseInfoJson);
}
