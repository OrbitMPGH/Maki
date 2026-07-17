using Mangarr.Api.Hubs;
using Mangarr.Core.Configuration;
using Mangarr.Core.Entities;
using Mangarr.Core.Metadata;
using Mangarr.Core.Naming;
using Mangarr.Core.Parsing;
using Mangarr.Data;
using Microsoft.EntityFrameworkCore;

namespace Mangarr.Api.Services;

public record ImportScanCandidate(
    string FolderName,
    string CleanedTitle,
    int CbzCount,
    int RecognizedCount,
    IReadOnlyList<MetadataSearchResult> Matches);

public record ImportRequestItem(string FolderName, string MetadataProviderId);

public record ImportResult(
    string FolderName,
    bool Success,
    string? Error,
    int? SeriesId = null,
    string? NewFolderName = null,
    int FilesLinked = 0,
    int FilesUnrecognized = 0);

/// <summary>
/// Imports an existing on-disk library: scans unclaimed folders in a root,
/// matches them to metadata, standardizes the folder name to the sanitized
/// English title, and links existing CBZ files (kept under their original
/// names) to synced chapters.
/// </summary>
public class LibraryImportService(
    MangarrDbContext db,
    IEnumerable<IMetadataProvider> metadataProviders,
    CoverService coverService,
    SourceMatchService sourceMatchService,
    ChapterSyncService chapterSyncService,
    CbzLinkService cbzLinkService,
    EventBroadcaster events,
    IAppSettings appSettings,
    ILogger<LibraryImportService> logger)
{
    public async Task<List<ImportScanCandidate>> ScanAsync(int rootFolderId, CancellationToken ct = default)
    {
        var rootFolder = await db.RootFolders.FindAsync([rootFolderId], ct)
            ?? throw new InvalidOperationException("Root folder not found");

        // A folder is "claimed" only if its series already has downloaded/linked files.
        // Series that were added but never downloaded stay importable so their on-disk
        // files can be linked in without re-adding the series.
        var seriesInRoot = await db.Series
            .Where(s => s.RootFolderId == rootFolderId)
            .Select(s => new { s.Id, s.FolderName })
            .ToListAsync(ct);
        var rootSeriesIds = seriesInRoot.Select(s => s.Id).ToList();
        var idsWithFiles = (await db.ChapterFiles
                .Where(f => rootSeriesIds.Contains(f.SeriesId))
                .Select(f => f.SeriesId)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet();
        var claimed = seriesInRoot
            .Where(s => idsWithFiles.Contains(s.Id))
            .Select(s => s.FolderName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var provider = metadataProviders.First();
        var candidates = new List<ImportScanCandidate>();

        foreach (var dir in Directory.GetDirectories(rootFolder.Path).OrderBy(d => d))
        {
            var folderName = Path.GetFileName(dir);
            if (folderName.StartsWith('.') || claimed.Contains(folderName))
            {
                continue;
            }

            var cbzFiles = Directory.GetFiles(dir, "*.cbz", SearchOption.AllDirectories);
            var recognized = cbzFiles.Count(f => ReleaseNameParser.ParseFileName(f).IsRecognized);
            var cleanedTitle = ReleaseNameParser.CleanFolderTitle(folderName);

            IReadOnlyList<MetadataSearchResult> matches = [];
            try
            {
                matches = (await provider.SearchAsync(cleanedTitle, ct)).Take(5).ToList();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Metadata search failed for {Title}", cleanedTitle);
            }

            candidates.Add(new ImportScanCandidate(folderName, cleanedTitle, cbzFiles.Length, recognized, matches));
        }

        return candidates;
    }

    public async Task<ImportResult> ImportAsync(
        int rootFolderId, ImportRequestItem item, bool updateComicInfo = true, CancellationToken ct = default)
    {
        var rootFolder = await db.RootFolders.FindAsync([rootFolderId], ct)
            ?? throw new InvalidOperationException("Root folder not found");

        var sourceDir = Path.Combine(rootFolder.Path, item.FolderName);
        if (!Directory.Exists(sourceDir))
        {
            return new ImportResult(item.FolderName, false, "Folder no longer exists");
        }

        await events.ImportProgress(item.FolderName, "Fetching metadata");
        var provider = metadataProviders.First();
        var metadata = await provider.GetAsync(item.MetadataProviderId, ct);
        if (metadata is null)
        {
            return new ImportResult(item.FolderName, false, "Metadata lookup failed");
        }

        // Already in the library? If the existing series has no downloaded/linked files,
        // treat this as re-linking on-disk files into it rather than a failure. If it
        // already has files, adding another folder for it would be ambiguous, so refuse.
        var existingSeries = metadata.MangaBakaId is { } existingId
            ? await db.Series.FirstOrDefaultAsync(s => s.MangaBakaId == existingId, ct)
            : null;
        if (existingSeries is not null)
        {
            if (await db.ChapterFiles.AnyAsync(f => f.SeriesId == existingSeries.Id, ct))
            {
                return new ImportResult(item.FolderName, false,
                    $"'{metadata.Title}' is already in the library with downloaded files. " +
                    "Delete it first if you want to re-import from scratch.");
            }

            return await ReimportIntoExistingAsync(existingSeries, rootFolder, item, sourceDir, updateComicInfo, ct);
        }

        // Standardize the folder name to the sanitized English title.
        var standardName = FileNameSanitizer.Sanitize(metadata.Title);
        var targetDir = Path.Combine(rootFolder.Path, standardName);
        if (!string.Equals(item.FolderName, standardName, StringComparison.Ordinal))
        {
            if (Directory.Exists(targetDir))
            {
                return new ImportResult(item.FolderName, false,
                    $"Cannot rename: '{standardName}' already exists in the root folder");
            }

            await events.ImportProgress(item.FolderName, "Renaming folder");
            Directory.Move(sourceDir, targetDir);
            logger.LogInformation("Renamed '{Old}' -> '{New}'", item.FolderName, standardName);
        }

        var series = new Series
        {
            Title = metadata.Title,
            SortTitle = metadata.Title.ToLowerInvariant(),
            OriginalTitle = metadata.OriginalTitle,
            Status = metadata.Status,
            Overview = metadata.Description,
            Year = metadata.Year,
            Genres = [.. metadata.Genres],
            Tags = [.. metadata.Tags],
            MangaBakaId = metadata.MangaBakaId,
            AniListId = metadata.AniListId,
            MalId = metadata.MalId,
            MangaUpdatesId = metadata.MangaUpdatesId,
            MonitorNewItems =
                await appSettings.GetAsync(SettingKeys.MonitoringUnmonitorSpecials, ct) == "true"
                    ? NewChapterMonitorMode.MainOnly
                    : NewChapterMonitorMode.All,
            RootFolderId = rootFolder.Id,
            FolderName = standardName,
            TotalChapters = metadata.TotalChapters,
            TotalVolumes = metadata.TotalVolumes,
            AuthorStory = metadata.AuthorStory,
            AuthorArt = metadata.AuthorArt,
            Added = DateTime.UtcNow,
            LastMetadataRefresh = DateTime.UtcNow
        };
        db.Series.Add(series);
        await db.SaveChangesAsync(ct);

        if (metadata.CoverUrl != null)
        {
            await events.ImportProgress(item.FolderName, "Downloading cover");
            var coverPath = await coverService.DownloadCoverAsync(series.Id, metadata.CoverUrl, ct);
            if (coverPath != null)
            {
                series.CoverPath = coverPath;
            }
        }

        // Link scraper sources and pull the chapter list before matching files.
        try
        {
            await events.ImportProgress(item.FolderName, "Finding sources");
            var mapped = await sourceMatchService.AutoMatchAsync(series, ct);
            if (mapped.Count > 0)
            {
                await events.ImportProgress(item.FolderName, "Syncing chapters");
                await chapterSyncService.SyncSeriesAsync(series.Id, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Source matching failed during import of {Title}", series.Title);
        }

        var cbzFiles = Directory.GetFiles(targetDir, "*.cbz", SearchOption.AllDirectories);
        var linkStage = updateComicInfo ? "Updating ComicInfo" : "Linking files";
        var (linked, unrecognized) = await cbzLinkService.LinkFilesAsync(
            series, targetDir, cbzFiles, "import",
            (current, total) => events.ImportProgress(item.FolderName, linkStage, current, total),
            updateComicInfo, ct);

        return new ImportResult(item.FolderName, true, null, series.Id, standardName, linked, unrecognized);
    }

    /// <summary>
    /// Re-links the on-disk CBZ files in <paramref name="sourceDir"/> to a series that is
    /// already in the library but has no downloaded files yet — without re-adding the series
    /// or re-fetching its metadata. Reconciles the folder to the standardized name and ensures
    /// chapters exist to link against.
    /// </summary>
    private async Task<ImportResult> ReimportIntoExistingAsync(
        Series series, RootFolder rootFolder, ImportRequestItem item, string sourceDir,
        bool updateComicInfo, CancellationToken ct)
    {
        var standardName = FileNameSanitizer.Sanitize(series.Title);
        var targetDir = Path.Combine(rootFolder.Path, standardName);
        if (!string.Equals(item.FolderName, standardName, StringComparison.Ordinal))
        {
            if (Directory.Exists(targetDir))
            {
                // The series' standardized folder already exists (e.g. an empty folder created
                // when it was added) — fold the scanned folder's files into it.
                await events.ImportProgress(item.FolderName, "Merging folder");
                MergeDirectory(sourceDir, targetDir);
                logger.LogInformation("Merged '{Old}' into existing '{New}'", item.FolderName, standardName);
            }
            else
            {
                await events.ImportProgress(item.FolderName, "Renaming folder");
                Directory.Move(sourceDir, targetDir);
                logger.LogInformation("Renamed '{Old}' -> '{New}'", item.FolderName, standardName);
            }
        }

        // Point the series at this location if it drifted (root folder or folder name).
        if (series.RootFolderId != rootFolder.Id ||
            !string.Equals(series.FolderName, standardName, StringComparison.Ordinal))
        {
            series.RootFolderId = rootFolder.Id;
            series.FolderName = standardName;
            await db.SaveChangesAsync(ct);
        }

        // Make sure there are chapters to match the files against. A series added but never
        // refreshed may have no sources/chapters yet.
        if (!await db.Chapters.AnyAsync(c => c.SeriesId == series.Id, ct))
        {
            try
            {
                await events.ImportProgress(item.FolderName, "Finding sources");
                var mapped = await sourceMatchService.AutoMatchAsync(series, ct);
                if (mapped.Count > 0)
                {
                    await events.ImportProgress(item.FolderName, "Syncing chapters");
                    await chapterSyncService.SyncSeriesAsync(series.Id, ct);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Source matching failed during re-import of {Title}", series.Title);
            }
        }

        var cbzFiles = Directory.GetFiles(targetDir, "*.cbz", SearchOption.AllDirectories);
        var linkStage = updateComicInfo ? "Updating ComicInfo" : "Linking files";
        var (linked, unrecognized) = await cbzLinkService.LinkFilesAsync(
            series, targetDir, cbzFiles, "import",
            (current, total) => events.ImportProgress(item.FolderName, linkStage, current, total),
            updateComicInfo, ct);

        return new ImportResult(item.FolderName, true, null, series.Id, standardName, linked, unrecognized);
    }

    /// <summary>Moves every file from <paramref name="sourceDir"/> into <paramref name="targetDir"/>
    /// (preserving sub-paths, skipping name collisions), then removes the now-empty source.</summary>
    private static void MergeDirectory(string sourceDir, string targetDir)
    {
        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, file);
            var dest = Path.Combine(targetDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (!File.Exists(dest))
            {
                File.Move(file, dest);
            }
        }

        try
        {
            Directory.Delete(sourceDir, recursive: true);
        }
        catch (IOException)
        {
            // Leftover files (collisions) or a locked handle — leave the folder in place.
        }
    }
}
