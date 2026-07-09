using System.Text.Json;
using Mangarr.Api.Services;
using Mangarr.Core.Download;
using Mangarr.Core.Entities;
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

        var torrents = await qbittorrent.ListAsync(qbt.Url, qbt.Username, qbt.Password, qbt.Category, ct);
        var claimedHashes = pending
            .Select(q => ReleaseInfoOf(q)?.TorrentHash)
            .Where(h => h != null)
            .ToHashSet();

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
                await ImportAsync(item, torrent, ct);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Oldest unclaimed torrent added after the item was grabbed — used when the hash is unknown.</summary>
    private static QBittorrentClient.QbtTorrent? ClaimTorrent(
        DownloadQueueItem item,
        IReadOnlyList<QBittorrentClient.QbtTorrent> torrents,
        HashSet<string?> claimedHashes)
    {
        var queuedUnix = new DateTimeOffset(item.QueuedAt).ToUnixTimeSeconds();
        return torrents
            .Where(t => !claimedHashes.Contains(t.Hash) && t.AddedOn >= queuedUnix - 120)
            .OrderBy(t => t.AddedOn)
            .FirstOrDefault();
    }

    private async Task ImportAsync(DownloadQueueItem item, QBittorrentClient.QbtTorrent torrent, CancellationToken ct)
    {
        var series = item.Series!;
        var rootFolder = series.RootFolder!;

        if (!Directory.Exists(torrent.ContentPath) && !File.Exists(torrent.ContentPath))
        {
            item.Status = QueueStatus.Failed;
            item.ErrorMessage = $"Download path not accessible from Mangarr: {torrent.ContentPath}";
            return;
        }

        var cbzFiles = File.Exists(torrent.ContentPath)
            ? (Path.GetExtension(torrent.ContentPath).Equals(".cbz", StringComparison.OrdinalIgnoreCase)
                ? new[] { torrent.ContentPath }
                : [])
            : Directory.GetFiles(torrent.ContentPath, "*.cbz", SearchOption.AllDirectories);

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
            series, seriesDir, copied, $"torrent:{ReleaseInfoOf(item)?.Indexer}", ct);

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
