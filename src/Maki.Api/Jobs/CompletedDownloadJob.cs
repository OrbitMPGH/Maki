using System.Text.Json;
using System.Text.RegularExpressions;
using Maki.Api.Dtos;
using Maki.Api.Hubs;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Download;
using Maki.Core.Entities;
using Maki.Core.Indexers;
using Maki.Core.Paths;
using Maki.Core.Storage;
using Maki.Data;
using Microsoft.EntityFrameworkCore;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Tracks torrent queue items against qBittorrent: updates progress, claims
/// hashes for .torrent grabs (magnets carry theirs), and imports finished
/// downloads: CBZ files are hardlinked (or copied) into the series folder under their
/// names, linked to chapters via the shared CBZ linker, then given the configured chapter
/// name via <see cref="SeriesRenameService.RenameFilesAsync"/>. The series folder is left
/// alone — see <c>ApplyNamingAsync</c>.
/// </summary>
[DisallowConcurrentExecution]
public class CompletedDownloadJob(
    MakiDbContext db,
    ReleaseService releaseService,
    QBittorrentClient qbittorrent,
    CbzLinkService cbzLinkService,
    SeriesRenameService seriesRenameService,
    EventBroadcaster events,
    Maki.Core.Configuration.IAppSettings settings,
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

            var previousPagesDone = item.PagesDone;
            item.PagesTotal = 100;
            item.PagesDone = (int)(torrent.Progress * 100);

            if (torrent.IsComplete)
            {
                await ImportAsync(item, torrent, pathMap, ct);
            }

            if (item.PagesDone != previousPagesDone || torrent.IsComplete)
            {
                await BroadcastAsync(item);
            }
        }

        await db.SaveChangesAsync(ct);
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
        var series = item.Series!;
        var rootFolder = series.RootFolder!;

        // qBittorrent reports the path as it sees it; rewrite it to how Maki does
        // when the two run under different mounts (e.g. qBittorrent in Docker).
        var contentPath = PathRemapper.Map(torrent.ContentPath, pathMap.From, pathMap.To);

        if (!Directory.Exists(contentPath) && !File.Exists(contentPath))
        {
            item.Status = QueueStatus.Failed;
            item.ErrorMessage = $"Download path not accessible from Maki: {contentPath}";
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

        // Never a move — qBittorrent keeps seeding from where it downloaded. A hardlink gives the
        // library its own name for the same bytes; the copy is the fallback when the two folders
        // can't share an inode (different volumes, a share, a filesystem without hardlinks).
        var seriesDir = Path.Combine(rootFolder.Path, series.FolderName);
        Directory.CreateDirectory(seriesDir);

        var useHardlinks = await settings.GetAsync(SettingKeys.DownloadUseHardlinks, ct) != "false";
        var imported = new List<string>();
        var hardlinked = 0;
        var freshCopies = 0;
        foreach (var file in cbzFiles)
        {
            var target = Path.Combine(seriesDir, Path.GetFileName(file));
            if (!File.Exists(target))
            {
                try
                {
                    if (FileLinker.Place(file, target, useHardlinks) == FilePlacement.Hardlinked)
                    {
                        hardlinked++;
                    }
                    else
                    {
                        freshCopies++;
                    }
                }
                catch (IOException ex)
                {
                    item.Status = QueueStatus.Failed;
                    item.ErrorMessage = $"Could not import {Path.GetFileName(file)}: {ex.Message}";
                    return;
                }
            }

            imported.Add(target);
        }

        // Honor the global "don't modify my files" setting for adopted torrent files. Chapters Maki
        // downloads itself still get ComicInfo — those CBZs are built by Maki, not existing files.
        //
        // Only a file this import copied byte-for-byte is rewritten at all. Standardizing ComicInfo
        // builds a new archive and swaps it over the library's name: the seeded data survives, but
        // the sharing does not, so a hardlinked import would silently turn into the second full copy
        // hardlinking exists to avoid. Kavita grouping is what the space saving costs, and turning
        // hardlinks off is how a user picks the other side of that. A file already in the folder is
        // skipped for the same reason — it may be a hardlink from an earlier run, and whatever
        // imported it already decided about its ComicInfo.
        var writeComicInfo = await settings.GetAsync(SettingKeys.LibraryWriteComicInfo, ct) != "false"
                             && freshCopies == imported.Count;
        var (linked, unrecognized) = await cbzLinkService.LinkFilesAsync(
            series, seriesDir, imported, $"torrent:{ReleaseInfoOf(item)?.Indexer}",
            updateComicInfo: writeComicInfo, releaseName: torrent.Name, ct: ct);

        item.Status = QueueStatus.Completed;
        item.CompletedAt = DateTime.UtcNow;
        item.PagesDone = item.PagesTotal;
        logger.LogInformation(
            "Imported torrent '{Title}': {Files} file(s) ({Hardlinked} hardlinked), {Linked} linked to chapters, {Unrecognized} unrecognized",
            item.Title, imported.Count, hardlinked, linked, unrecognized);
        if (hardlinked > 0)
        {
            logger.LogInformation(
                "Left ComicInfo.xml untouched in '{Title}': the imported file(s) are hardlinks and still seeding",
                item.Title);
        }

        // Torrent files keep their release name until this runs; save now so the rename's
        // active-download check (which re-queries the row) sees this item as Completed rather
        // than still in-flight and refuses to rename the series it just finished importing into.
        await db.SaveChangesAsync(ct);
        await ApplyNamingAsync(series, imported, ct);
    }

    /// <summary>
    /// Names the files this import just added, and only those.
    /// <para>
    /// Deliberately not a whole-series rename. That would move the series folder too, off the back
    /// of one grabbed release and with nobody watching: the default folder format carries a release
    /// year that folders created before it do not, so the first torrent for a series would rewrite
    /// every path in it — and it would do so whatever <see cref="SettingKeys.LibraryFolderNamingMode"/>
    /// says, including for a user who asked Maki to leave their folder names alone. Renaming a
    /// series is what <c>POST /series/{id}/rename</c> is for, where the plan is shown first.
    /// </para>
    /// </summary>
    private async Task ApplyNamingAsync(Series series, List<string> imported, CancellationToken ct)
    {
        // Resolved by path rather than returned by the linker: LinkFilesAsync answers with counts,
        // and these files sit directly in the series folder, which is exactly how it stored them.
        var relativePaths = imported
            .Select(path => Path.Combine(series.FolderName, Path.GetFileName(path)))
            .ToList();
        var fileIds = await db.ChapterFiles
            .Where(f => f.SeriesId == series.Id && relativePaths.Contains(f.RelativePath))
            .Select(f => f.Id)
            .ToListAsync(ct);

        var result = await seriesRenameService.RenameFilesAsync(series.Id, fileIds, ct);
        if (!result.Applied)
        {
            logger.LogWarning(
                "Could not apply the chapter naming format to '{Title}' after torrent import: {Error}",
                series.Title, result.Error);
            return;
        }

        // Applied with warnings is the case that used to vanish: a skipped collision or a file that
        // was not where its row said still leaves the library half-named, and this job is the only
        // thing watching.
        foreach (var warning in result.Warnings)
        {
            logger.LogWarning("Naming '{Title}' after torrent import: {Warning}", series.Title, warning);
        }
    }

    private static ReleaseInfo? ReleaseInfoOf(DownloadQueueItem item) =>
        item.ReleaseInfoJson is null ? null : JsonSerializer.Deserialize<ReleaseInfo>(item.ReleaseInfoJson);
}
