using System.Text.Json;
using System.Text.RegularExpressions;
using Mangarr.Api.Services;
using Mangarr.Core.Download;
using Mangarr.Core.Entities;
using Mangarr.Core.Indexers;
using Mangarr.Core.Paths;
using Mangarr.Data;
using Microsoft.EntityFrameworkCore;
using Quartz;

namespace Mangarr.Api.Jobs;

/// <summary>
/// Tracks torrent queue items against qBittorrent: updates progress, claims
/// hashes for .torrent grabs (magnets carry theirs), and imports finished
/// downloads — CBZ files are copied into the series folder under their original
/// names and linked to chapters via the shared CBZ linker.
/// </summary>
[DisallowConcurrentExecution]
public class CompletedDownloadJob(
    MangarrDbContext db,
    ReleaseService releaseService,
    QBittorrentClient qbittorrent,
    CbzLinkService cbzLinkService,
    ILogger<CompletedDownloadJob> logger) : IJob
{
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
        var torrents = await qbittorrent.ListAsync(qbt.Url, qbt.Username, qbt.Password, qbt.Category, ct);

        // Hashes already tied to any torrent item — including completed and failed ones,
        // whose torrents keep seeding in qBittorrent. Excluding only pending items let a
        // finished previous download be re-claimed by a new hashless (.torrent) item and
        // imported into the wrong series' folder.
        var claimedJson = await db.DownloadQueue
            .Where(q => q.Protocol == AcquisitionProtocol.Torrent && q.ReleaseInfoJson != null)
            .Select(q => q.ReleaseInfoJson!)
            .ToListAsync(ct);
        var claimedHashes = claimedJson
            .Select(j => JsonSerializer.Deserialize<ReleaseInfo>(j)?.TorrentHash)
            .Where(h => h != null)
            .Select(h => h!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var item in pending)
        {
            var info = ReleaseInfoOf(item);
            if (info is null)
            {
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
                    item.ErrorMessage = "Torrent never appeared in qBittorrent";
                }

                continue;
            }

            if (info.TorrentHash is null)
            {
                info = info with { TorrentHash = torrent.Hash };
                item.ReleaseInfoJson = JsonSerializer.Serialize(info);
                claimedHashes.Add(torrent.Hash);
            }

            item.PagesTotal = 100;
            item.PagesDone = (int)(torrent.Progress * 100);

            if (torrent.IsComplete)
            {
                await ImportAsync(item, torrent, pathMap, ct);
            }
        }

        await db.SaveChangesAsync(ct);
    }

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
        var series = item.Series!;
        var rootFolder = series.RootFolder!;

        // qBittorrent reports the path as it sees it; rewrite it to how Mangarr does
        // when the two run under different mounts (e.g. qBittorrent in Docker).
        var contentPath = PathRemapper.Map(torrent.ContentPath, pathMap.From, pathMap.To);

        if (!Directory.Exists(contentPath) && !File.Exists(contentPath))
        {
            item.Status = QueueStatus.Failed;
            item.ErrorMessage = $"Download path not accessible from Mangarr: {contentPath}";
            return;
        }

        var cbzFiles = File.Exists(contentPath)
            ? (Path.GetExtension(contentPath).Equals(".cbz", StringComparison.OrdinalIgnoreCase)
                ? new[] { contentPath }
                : [])
            : Directory.GetFiles(contentPath, "*.cbz", SearchOption.AllDirectories);

        if (cbzFiles.Length == 0)
        {
            item.Status = QueueStatus.Failed;
            item.ErrorMessage = "No CBZ files found in the completed download";
            return;
        }

        item.Status = QueueStatus.Importing;

        // Copy (not move — qBittorrent keeps seeding) into the series folder.
        var seriesDir = Path.Combine(rootFolder.Path, series.FolderName);
        Directory.CreateDirectory(seriesDir);

        var copied = new List<string>();
        foreach (var file in cbzFiles)
        {
            var target = Path.Combine(seriesDir, Path.GetFileName(file));
            if (!File.Exists(target))
            {
                File.Copy(file, target);
            }

            copied.Add(target);
        }

        var (linked, unrecognized) = await cbzLinkService.LinkFilesAsync(
            series, seriesDir, copied, $"torrent:{ReleaseInfoOf(item)?.Indexer}", ct: ct);

        item.Status = QueueStatus.Completed;
        item.CompletedAt = DateTime.UtcNow;
        item.PagesDone = item.PagesTotal;
        logger.LogInformation(
            "Imported torrent '{Title}': {Files} file(s), {Linked} linked to chapters, {Unrecognized} unrecognized",
            item.Title, copied.Count, linked, unrecognized);
    }

    private static ReleaseInfo? ReleaseInfoOf(DownloadQueueItem item) =>
        item.ReleaseInfoJson is null ? null : JsonSerializer.Deserialize<ReleaseInfo>(item.ReleaseInfoJson);
}
