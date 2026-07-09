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
    ILogger<LibraryImportService> logger)
{
    public async Task<List<ImportScanCandidate>> ScanAsync(int rootFolderId, CancellationToken ct = default)
    {
        var rootFolder = await db.RootFolders.FindAsync([rootFolderId], ct)
            ?? throw new InvalidOperationException("Root folder not found");

        var claimed = (await db.Series
                .Where(s => s.RootFolderId == rootFolderId)
                .Select(s => s.FolderName)
                .ToListAsync(ct))
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

    public async Task<ImportResult> ImportAsync(int rootFolderId, ImportRequestItem item, CancellationToken ct = default)
    {
        var rootFolder = await db.RootFolders.FindAsync([rootFolderId], ct)
            ?? throw new InvalidOperationException("Root folder not found");

        var sourceDir = Path.Combine(rootFolder.Path, item.FolderName);
        if (!Directory.Exists(sourceDir))
        {
            return new ImportResult(item.FolderName, false, "Folder no longer exists");
        }

        var provider = metadataProviders.First();
        var metadata = await provider.GetAsync(item.MetadataProviderId, ct);
        if (metadata is null)
        {
            return new ImportResult(item.FolderName, false, "Metadata lookup failed");
        }

        if (metadata.MangaBakaId is int existing && await db.Series.AnyAsync(s => s.MangaBakaId == existing, ct))
        {
            return new ImportResult(item.FolderName, false, $"'{metadata.Title}' is already in the library");
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
            Monitored = true,
            MonitorNewItems = NewChapterMonitorMode.All,
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
            var coverPath = await coverService.DownloadCoverAsync(series.Id, metadata.CoverUrl, ct);
            if (coverPath != null)
            {
                series.CoverPath = coverPath;
            }
        }

        // Link scraper sources and pull the chapter list before matching files.
        try
        {
            var mapped = await sourceMatchService.AutoMatchAsync(series, ct);
            if (mapped.Count > 0)
            {
                await chapterSyncService.SyncSeriesAsync(series.Id, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Source matching failed during import of {Title}", series.Title);
        }

        var (linked, unrecognized) = await MatchFilesAsync(series, targetDir, ct);
        await db.SaveChangesAsync(ct);

        return new ImportResult(item.FolderName, true, null, series.Id, standardName, linked, unrecognized);
    }

    /// <summary>
    /// Creates ChapterFile records for existing CBZs (original names kept) and
    /// links them to synced chapters: chapter files by number, volume files to
    /// every chapter carrying a volume in the file's range.
    /// </summary>
    private async Task<(int Linked, int Unrecognized)> MatchFilesAsync(Series series, string seriesDir, CancellationToken ct)
    {
        var chapters = await db.Chapters.Where(c => c.SeriesId == series.Id).ToListAsync(ct);
        var linked = 0;
        var unrecognized = 0;

        foreach (var file in Directory.GetFiles(seriesDir, "*.cbz", SearchOption.AllDirectories).OrderBy(f => f))
        {
            var parsed = ReleaseNameParser.ParseFileName(file);
            var relativePath = Path.GetRelativePath(seriesDir, file);

            var chapterFile = new ChapterFile
            {
                SeriesId = series.Id,
                RelativePath = Path.Combine(series.FolderName, relativePath),
                Size = new FileInfo(file).Length,
                SourceName = "import",
                DateAdded = DateTime.UtcNow
            };
            db.ChapterFiles.Add(chapterFile);
            await db.SaveChangesAsync(ct); // need the file id for linking

            List<Chapter> targets = [];
            if (parsed.IsChapter)
            {
                var match = chapters.FirstOrDefault(c => c.Number == parsed.Number);
                if (match != null)
                {
                    targets.Add(match);
                }
            }
            else if (parsed.IsVolume)
            {
                var end = parsed.VolumeEnd ?? parsed.Volume;
                targets = chapters
                    .Where(c => c.Volume >= parsed.Volume && c.Volume <= end && c.ChapterFileId == null)
                    .ToList();
            }
            else
            {
                unrecognized++;
            }

            foreach (var chapter in targets)
            {
                chapter.ChapterFileId = chapterFile.Id;
            }

            if (targets.Count > 0)
            {
                linked++;
            }
        }

        return (linked, unrecognized);
    }
}
