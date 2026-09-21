using System.Globalization;
using System.Text.Json;
using Maki.Core.Configuration;
using Maki.Core.Download;
using Maki.Core.Entities;
using Maki.Core.Parsing;
using Maki.Core.Paths;
using Maki.Core.Storage;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>What an import is allowed to do to files the library already has.</summary>
public enum TorrentImportMode
{
    /// <summary>
    /// Import everything and delete the files this download supersedes. A file is only deleted
    /// once every chapter that pointed at it points at an imported file instead.
    /// </summary>
    Replace,

    /// <summary>
    /// Import only what the library is missing: a downloaded file that brings no new chapter is
    /// left in the download folder, and no chapter is moved off the file backing it today.
    /// </summary>
    SkipExisting
}

/// <param name="RelativePath">The existing file's path, relative to the root folder.</param>
/// <param name="Chapters">Chapter numbers it backs today.</param>
public record ImportPlanExisting(int ChapterFileId, string RelativePath, long Size, IReadOnlyList<string> Chapters);

/// <param name="Label">"Vol.1", "Ch.24", or null when the name parses to neither.</param>
/// <param name="Chapters">Chapter numbers this file would cover.</param>
/// <param name="NewChapters">Of those, the ones no file backs today.</param>
/// <param name="Replaces">Files the library would stop using if this one is imported.</param>
public record ImportPlanFile(
    string FileName,
    long Size,
    string? Label,
    IReadOnlyList<string> Chapters,
    IReadOnlyList<string> NewChapters,
    IReadOnlyList<ImportPlanExisting> Replaces);

/// <param name="Error">Set when the plan could not be built at all (download path gone, say).</param>
public record TorrentImportPlan(
    int QueueItemId,
    int SeriesId,
    string SeriesTitle,
    string ReleaseName,
    IReadOnlyList<ImportPlanFile> Files,
    string? Error = null)
{
    /// <summary>Whether importing this download would take chapters off files the library has.</summary>
    public bool HasConflicts => Files.Any(f => f.Replaces.Count > 0);

    public int NewChapterCount => Files.SelectMany(f => f.NewChapters).Distinct().Count();

    public int ReplacedFileCount => Files.SelectMany(f => f.Replaces).Select(r => r.ChapterFileId).Distinct().Count();
}

/// <param name="Deleted">Superseded files removed from disk, under <see cref="TorrentImportMode.Replace"/>.</param>
/// <param name="Skipped">Downloaded files left alone because they brought nothing new.</param>
public record TorrentImportOutcome(
    bool Applied, string? Error, int Imported, int Linked, int Unrecognized, int Deleted, int Skipped,
    IReadOnlyList<string> ImportedPaths);

/// <summary>
/// Imports the CBZ files of a finished torrent into a series folder, and works out first whether
/// doing so would displace files the library already has.
/// <para>
/// Split out of <c>CompletedDownloadJob</c> because the same import has to run from two places
/// that know very different amounts: the job, which imports on its own when nothing is at stake,
/// and the queue endpoint, where someone has looked at the plan and said what should happen to the
/// existing files. Keeping one implementation is what stops the unattended path and the reviewed
/// path drifting into two sets of rules about deleting a user's library.
/// </para>
/// </summary>
public class TorrentImportService(
    MakiDbContext db,
    ReleaseService releaseService,
    QBittorrentClient qbittorrent,
    CbzLinkService cbzLinkService,
    SeriesRenameService seriesRenameService,
    ReaderArchiveCache archives,
    IAppSettings settings,
    ILogger<TorrentImportService> logger)
{
    /// <summary>
    /// Where qBittorrent put this item's data, as Maki sees it. Null when the torrent is gone, or
    /// its path isn't reachable from here (qBittorrent in a container with a different mount).
    /// </summary>
    public async Task<string?> ResolveContentPathAsync(DownloadQueueItem item, CancellationToken ct)
    {
        var hash = ReleaseInfoOf(item)?.TorrentHash;
        if (hash is null)
        {
            return null;
        }

        (string Url, string Username, string Password, string Category) qbt;
        try
        {
            qbt = await releaseService.GetQbtConfigAsync(ct);
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        var torrents = await qbittorrent.ListAsync(qbt.Url, qbt.Username, qbt.Password, qbt.Category, ct);
        var torrent = torrents.FirstOrDefault(t => t.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase));
        if (torrent is null)
        {
            return null;
        }

        var pathMap = await releaseService.GetQbtPathMapAsync(ct);
        return PathRemapper.Map(torrent.ContentPath, pathMap.From, pathMap.To);
    }

    /// <summary>
    /// What importing this download would do: which chapters each file covers, which of those the
    /// library is missing, and which existing files would be left backing nothing.
    /// </summary>
    public async Task<TorrentImportPlan> PlanAsync(
        DownloadQueueItem item, Series series, string? contentPath, CancellationToken ct)
    {
        var releaseName = ReleaseInfoOf(item)?.Title ?? item.Title ?? "Release";
        if (contentPath is null)
        {
            return Empty("The download is no longer in qBittorrent");
        }

        if (!Directory.Exists(contentPath) && !File.Exists(contentPath))
        {
            return Empty($"Download path not accessible from Maki: {contentPath}");
        }

        var cbzFiles = CbzFilesIn(contentPath);
        if (cbzFiles.Length == 0)
        {
            return Empty("No CBZ files found in the completed download");
        }

        var chapters = await db.Chapters
            .Where(c => c.SeriesId == series.Id)
            .ToListAsync(ct);
        var existingFiles = await db.ChapterFiles
            .Where(f => f.SeriesId == series.Id)
            .ToListAsync(ct);

        var files = new List<ImportPlanFile>();
        foreach (var path in cbzFiles.OrderBy(f => f, StringComparer.Ordinal))
        {
            var parsed = ReleaseNameParser.ParseFileName(path);
            var covered = ChaptersCoveredBy(chapters, parsed, path);

            var replaces = covered
                .Where(c => c.ChapterFileId != null)
                .GroupBy(c => c.ChapterFileId!.Value)
                .Select(g =>
                {
                    var existing = existingFiles.FirstOrDefault(f => f.Id == g.Key);
                    return new ImportPlanExisting(
                        g.Key,
                        existing?.RelativePath ?? "(unknown)",
                        existing?.Size ?? 0,
                        // Every chapter on that file, not just the ones this download covers:
                        // what matters to the reader is what they lose, not what we asked about.
                        chapters.Where(c => c.ChapterFileId == g.Key).Select(Label).ToList());
                })
                .ToList();

            files.Add(new ImportPlanFile(
                Path.GetFileName(path),
                new FileInfo(path).Length,
                ParsedLabel(parsed),
                covered.Select(Label).ToList(),
                covered.Where(c => c.ChapterFileId == null).Select(Label).ToList(),
                replaces));
        }

        return new TorrentImportPlan(item.Id, series.Id, series.Title, releaseName, files);

        TorrentImportPlan Empty(string error) =>
            new(item.Id, series.Id, series.Title, releaseName, [], error);
    }

    /// <summary>
    /// Places the download's files in the series folder, links them, names them, and under
    /// <see cref="TorrentImportMode.Replace"/> deletes the files left backing nothing.
    /// </summary>
    public async Task<TorrentImportOutcome> ImportAsync(
        DownloadQueueItem item, Series series, string? contentPath, TorrentImportMode mode, CancellationToken ct)
    {
        var plan = await PlanAsync(item, series, contentPath, ct);
        if (plan.Error is not null)
        {
            return new TorrentImportOutcome(false, plan.Error, 0, 0, 0, 0, 0, []);
        }

        var rootFolder = series.RootFolder
            ?? await db.RootFolders.FirstOrDefaultAsync(r => r.Id == series.RootFolderId, ct);
        if (rootFolder is null)
        {
            return new TorrentImportOutcome(false, "Series has no root folder", 0, 0, 0, 0, 0, []);
        }

        var wanted = mode == TorrentImportMode.Replace
            ? plan.Files
            // Nothing new and something to lose: the file is exactly what the library already has.
            : plan.Files.Where(f => f.Replaces.Count == 0 || f.NewChapters.Count > 0).ToList();
        var skipped = plan.Files.Count - wanted.Count;
        if (wanted.Count == 0)
        {
            return new TorrentImportOutcome(true, null, 0, 0, 0, 0, skipped, []);
        }

        var byName = CbzFilesIn(contentPath!).ToDictionary(Path.GetFileName, f => f, StringComparer.Ordinal);
        var sourceFiles = wanted
            .Select(f => byName.GetValueOrDefault(f.FileName))
            .Where(f => f is not null)
            .Select(f => f!)
            .ToList();

        // Never a move — qBittorrent keeps seeding from where it downloaded. A hardlink gives the
        // library its own name for the same bytes; the copy is the fallback when the two folders
        // can't share an inode (different volumes, a share, a filesystem without hardlinks).
        var seriesDir = Path.Combine(rootFolder.Path, series.FolderName);
        Directory.CreateDirectory(seriesDir);

        var useHardlinks = await settings.GetAsync(SettingKeys.DownloadUseHardlinks, ct) != "false";
        var imported = new List<string>();
        var hardlinked = 0;
        var freshCopies = 0;
        foreach (var file in sourceFiles)
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
                    return new TorrentImportOutcome(
                        false, $"Could not import {Path.GetFileName(file)}: {ex.Message}", 0, 0, 0, 0, skipped, []);
                }
            }

            imported.Add(target);
        }

        // Which file each chapter reads from before anything is linked, so the files this import
        // actually supersedes can be told apart from ones that were already spare.
        var backedBefore = await db.Chapters
            .Where(c => c.SeriesId == series.Id && c.ChapterFileId != null)
            .Select(c => c.ChapterFileId!.Value)
            .ToListAsync(ct);

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
            updateComicInfo: writeComicInfo, releaseName: ReleaseInfoOf(item)?.Title ?? item.Title,
            replaceExisting: mode == TorrentImportMode.Replace, ct: ct);

        var deleted = mode == TorrentImportMode.Replace
            ? await DeleteSupersededAsync(series, rootFolder.Path, backedBefore, ct)
            : 0;

        logger.LogInformation(
            "Imported torrent '{Title}': {Files} file(s) ({Hardlinked} hardlinked), {Linked} linked to chapters, " +
            "{Unrecognized} unrecognized, {Skipped} skipped, {Deleted} superseded file(s) deleted",
            item.Title, imported.Count, hardlinked, linked, unrecognized, skipped, deleted);
        if (hardlinked > 0)
        {
            logger.LogInformation(
                "Left ComicInfo.xml untouched in '{Title}': the imported file(s) are hardlinks and still seeding",
                item.Title);
        }

        return new TorrentImportOutcome(
            true, null, imported.Count, linked, unrecognized, deleted, skipped, imported);
    }

    /// <summary>
    /// Names the files an import just added, and only those.
    /// <para>
    /// Deliberately not a whole-series rename. That would move the series folder too, off the back
    /// of one grabbed release and with nobody watching: the default folder format carries a release
    /// year that folders created before it do not, so the first torrent for a series would rewrite
    /// every path in it — and it would do so whatever <see cref="SettingKeys.LibraryFolderNamingMode"/>
    /// says, including for a user who asked Maki to leave their folder names alone. Renaming a
    /// series is what <c>POST /series/{id}/rename</c> is for, where the plan is shown first.
    /// </para>
    /// </summary>
    /// <param name="importedPaths">Absolute paths <see cref="ImportAsync"/> placed in the folder.</param>
    public async Task ApplyNamingAsync(Series series, IReadOnlyList<string> importedPaths, CancellationToken ct)
    {
        if (importedPaths.Count == 0)
        {
            return;
        }

        // Gated here rather than at the two call sites so the unattended job and the reviewed
        // queue import can't disagree about it. A scene release's own name usually carries more
        // than the chapter format can express (edition, group, year), so "keep it" is a real
        // answer, and it's the one importing a series from disk has always given.
        if (await settings.GetAsync(SettingKeys.LibraryRenameImportedFiles, ct) == "false")
        {
            return;
        }

        // Resolved by path rather than returned by the linker: LinkFilesAsync answers with counts,
        // and these files sit directly in the series folder, which is exactly how it stored them.
        var relativePaths = importedPaths
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
        // was not where its row said still leaves the library half-named, and this is the only
        // thing watching.
        foreach (var warning in result.Warnings)
        {
            logger.LogWarning("Naming '{Title}' after torrent import: {Warning}", series.Title, warning);
        }
    }

    /// <summary>
    /// Deletes the files this import left backing nothing: every chapter that read from them now
    /// reads from an imported file instead. A file that was already spare before the import (an
    /// adopted archive nothing matched, an extra) is untouched — it wasn't superseded, it was just
    /// never used.
    /// </summary>
    private async Task<int> DeleteSupersededAsync(
        Series series, string rootPath, IReadOnlyCollection<int> backedBefore, CancellationToken ct)
    {
        var backedNow = await db.Chapters
            .Where(c => c.SeriesId == series.Id && c.ChapterFileId != null)
            .Select(c => c.ChapterFileId!.Value)
            .ToListAsync(ct);

        var superseded = backedBefore.Distinct().Except(backedNow).ToList();
        if (superseded.Count == 0)
        {
            return 0;
        }

        var rows = await db.ChapterFiles
            .Where(f => f.SeriesId == series.Id && superseded.Contains(f.Id))
            .ToListAsync(ct);

        var deleted = 0;
        foreach (var row in rows)
        {
            // Resolve, not Combine: a stored path escaping the root would turn this into an
            // arbitrary delete, and this is the one code path that removes a user's files without
            // them naming the file.
            var path = LibraryPaths.Resolve(rootPath, row.RelativePath);
            if (path is not null && File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "Could not delete superseded file {Path}", row.RelativePath);
                    continue;
                }
            }

            // SQLite reuses rowids after a delete, so a later adopt can land on this id with a
            // different archive behind it and the cache's size guard would not notice.
            archives.Invalidate(row.Id);
            db.ChapterFiles.Remove(row);
            deleted++;
            logger.LogInformation("Deleted superseded file {Path} for '{Title}'", row.RelativePath, series.Title);
        }

        await db.SaveChangesAsync(ct);
        return deleted;
    }

    /// <summary>
    /// The chapters a downloaded file would end up backing: its own number for a chapter file, and
    /// for a compilation both the volume range the provider assigns and the chapter markers in its
    /// page names, which is the pair <c>CbzLinkService</c> links on.
    /// </summary>
    private static List<Chapter> ChaptersCoveredBy(List<Chapter> chapters, ParsedReleaseFile parsed, string path)
    {
        if (parsed.IsChapter)
        {
            return chapters.Where(c => c.Number == parsed.Number).ToList();
        }

        if (!parsed.IsVolume)
        {
            return [];
        }

        var end = parsed.VolumeEnd ?? parsed.Volume;
        var contained = VolumeChapterScanner.ScanCbz(path).ToHashSet();
        return chapters
            .Where(c => (c.Volume >= parsed.Volume && c.Volume <= end) ||
                        (c.Number is { } n && contained.Contains(n)))
            .ToList();
    }

    private static string[] CbzFilesIn(string contentPath) =>
        File.Exists(contentPath)
            ? Path.GetExtension(contentPath).Equals(".cbz", StringComparison.OrdinalIgnoreCase)
                ? [contentPath]
                : []
            : Directory.Exists(contentPath)
                ? Directory.GetFiles(contentPath, "*.cbz", SearchOption.AllDirectories)
                : [];

    private static string Label(Chapter chapter) =>
        chapter.Number?.ToString("0.###", CultureInfo.InvariantCulture) ?? chapter.Title ?? "?";

    private static string? ParsedLabel(ParsedReleaseFile parsed)
    {
        if (parsed.IsChapter)
        {
            return $"Ch.{parsed.Number!.Value.ToString("0.###", CultureInfo.InvariantCulture)}";
        }

        if (!parsed.IsVolume)
        {
            return null;
        }

        return parsed.VolumeEnd is { } end && end != parsed.Volume
            ? $"Vol.{parsed.Volume}-{end}"
            : $"Vol.{parsed.Volume}";
    }

    public static ReleaseInfo? ReleaseInfoOf(DownloadQueueItem item) =>
        item.ReleaseInfoJson is null ? null : JsonSerializer.Deserialize<ReleaseInfo>(item.ReleaseInfoJson);
}
