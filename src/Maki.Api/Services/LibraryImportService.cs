using System.Globalization;
using Maki.Api.Hubs;
using Maki.Api.Localization;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Import;
using Maki.Core.Metadata;
using Maki.Core.Naming;
using Maki.Core.Parsing;
using Maki.Core.Paths;
using Maki.Core.Reading;
using Maki.Core.Security;
using Maki.Data;
using Maki.Metadata.MangaBaka;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Stage keys sent over <see cref="EventBroadcaster.ImportProgress"/>. Machine-readable, not prose:
/// the broadcast reaches every admin connection at once and they don't share a language, so
/// <c>frontend/src/pages/ImportPage.tsx</c> is what turns these into words.
/// </summary>
public static class ImportStage
{
    public const string FetchingMetadata = "fetchingMetadata";
    public const string RenamingFolder = "renamingFolder";
    public const string MergingFolder = "mergingFolder";
    public const string DownloadingCover = "downloadingCover";
    public const string AddingFiles = "addingFiles";
    public const string UpdatingComicInfo = "updatingComicInfo";
    public const string LinkingFiles = "linkingFiles";
    public const string Imported = "imported";
    public const string Failed = "failed";
}

/// <param name="ComicCount">
/// Comics in the folder, not CBZ files: a RAR volume, a plain zip and a folder of loose pages all
/// count, because the import builds a CBZ out of each of them.
/// </param>
/// <param name="ExistingSeriesId">
/// Set when the folder is the folder of a series already in the library that has no files yet.
/// Importing it links its comics into that series; the page hides these rows unless asked.
/// </param>
public record ImportScanCandidate(
    string FolderName,
    string CleanedTitle,
    int ComicCount,
    int RecognizedCount,
    IReadOnlyList<MetadataSearchResult> Matches,
    int? ExistingSeriesId = null);

public record ImportRequestItem(string FolderName, string MetadataProviderId);

public record ImportResult(
    string FolderName,
    bool Success,
    string? Error,
    int? SeriesId = null,
    string? NewFolderName = null,
    int FilesLinked = 0,
    int FilesUnrecognized = 0,
    IReadOnlyList<string>? Warnings = null,
    IReadOnlyList<ImportSkippedFile>? Skipped = null,
    int FilesAdded = 0,
    bool LinkPending = false);

/// <summary>
/// A comic in the imported folder that ended up backing no chapter. <paramref name="Reason"/> is
/// one of <see cref="ImportSkipReason"/>'s wire values; the client words it.
/// </summary>
public record ImportSkippedFile(string Name, string Reason);

public static class ImportSkipReason
{
    /// <summary>An archive that yields no pages: truncated, corrupt, or not a comic at all.</summary>
    public const string Unreadable = "unreadable";

    /// <summary>The name carries a chapter or volume the series' chapter list does not have.</summary>
    public const string NoMatchingChapter = "noMatchingChapter";

    /// <summary>No chapter or volume number could be read off the name.</summary>
    public const string Unrecognized = "unrecognized";
}

/// <summary>
/// Imports an existing on-disk library: scans unclaimed folders in a root,
/// matches them to metadata, applies the configured folder naming mode
/// (<see cref="SettingKeys.LibraryFolderNamingMode"/>), and links the comics it finds (kept under
/// their original names) to synced chapters. A shelf that predates Maki is not all CBZ, so a RAR
/// volume, a plain zip or a folder of loose pages becomes one too — see
/// <see cref="MaterializeComics"/>, which never removes what it read.
/// </summary>
public class LibraryImportService(
    MakiDbContext db,
    IEnumerable<IMetadataProvider> metadataProviders,
    CoverService coverService,
    CbzLinkService cbzLinkService,
    SourceMatchQueue sourceMatchQueue,
    EventBroadcaster events,
    IAppSettings appSettings,
    NamingService naming,
    StatsEventService stats,
    SeriesIdentityService identity,
    ILocalizer localizer,
    ICurrentUser currentUser,
    ILogger<LibraryImportService> logger)
{

    /// <summary>
    /// Splits a request into groups the import can run side by side. Items naming the same series
    /// or the same folder share a group and run in request order: the series lookup and the folder
    /// move are check-then-act, so two such items in parallel could both add the series or both
    /// adopt the folder, where one after the other the second sees the first's result.
    /// </summary>
    public static List<List<int>> ImportLanes(IReadOnlyList<ImportRequestItem> items)
    {
        var parent = Enumerable.Range(0, items.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);

        var byProvider = new Dictionary<string, int>(StringComparer.Ordinal);
        var byFolder = new Dictionary<string, int>(LibraryPaths.FolderComparer);
        for (var i = 0; i < items.Count; i++)
        {
            if (!byProvider.TryAdd(items[i].MetadataProviderId, i))
            {
                parent[Find(i)] = Find(byProvider[items[i].MetadataProviderId]);
            }

            if (!byFolder.TryAdd(items[i].FolderName, i))
            {
                parent[Find(i)] = Find(byFolder[items[i].FolderName]);
            }
        }

        return Enumerable.Range(0, items.Count)
            .GroupBy(Find)
            .Select(g => g.ToList())
            .ToList();
    }

    public async Task<List<ImportScanCandidate>> ScanAsync(int rootFolderId, CancellationToken ct = default)
    {
        if (!currentUser.AllRootFolders && !currentUser.RootFolderIds.Contains(rootFolderId))
        {
            throw new InvalidOperationException("Root folder not found");
        }

        var rootFolder = await db.RootFolders.FindAsync([rootFolderId], ct)
            ?? throw new InvalidOperationException("Root folder not found");

        // A folder is "claimed" once any series has files in it: its own folder, or the original
        // folder a keep-new-standard import left the files in while FolderName moved on. A series
        // that was added but never downloaded stays importable so files dropped into its folder
        // can be linked in without re-adding it.
        var seriesInRoot = await db.Series
            .AsNoTracking()
            .Where(s => s.RootFolderId == rootFolderId)
            .ToListAsync(ct);
        var rootSeriesIds = seriesInRoot.Select(s => s.Id).ToList();
        var files = await db.ChapterFiles
            .Where(f => rootSeriesIds.Contains(f.SeriesId))
            .Select(f => new { f.SeriesId, f.RelativePath })
            .ToListAsync(ct);
        var idsWithFiles = files.Select(f => f.SeriesId).ToHashSet();
        var claimed = seriesInRoot
            .Where(s => idsWithFiles.Contains(s.Id))
            .Select(s => s.FolderName)
            .Concat(files.Select(f => LibraryPaths.TopFolder(f.RelativePath)).OfType<string>())
            .ToHashSet(LibraryPaths.FolderComparer);
        var withoutFiles = new Dictionary<string, Series>(LibraryPaths.FolderComparer);
        foreach (var series in seriesInRoot.Where(s => !idsWithFiles.Contains(s.Id)))
        {
            withoutFiles.TryAdd(series.FolderName, series);
        }

        var provider = metadataProviders.First();
        var dirs = Directory.GetDirectories(rootFolder.Path)
            .Order()
            .Where(dir =>
            {
                var folderName = Path.GetFileName(dir);
                return !folderName.StartsWith('.') && !claimed.Contains(folderName) && !LibraryPaths.IsLink(dir);
            })
            .ToList();
        var candidates = new ImportScanCandidate?[dirs.Count];

        // Listing a folder's archives and searching its title are independent per folder and touch
        // no DbContext, so a large root does not have to pay for them one at a time.
        await Parallel.ForEachAsync(
            Enumerable.Range(0, dirs.Count),
            new ParallelOptions { MaxDegreeOfParallelism = ScanConcurrency, CancellationToken = ct },
            async (index, itemCt) =>
                candidates[index] = await ScanFolderAsync(dirs[index], provider, withoutFiles, itemCt));

        return candidates.OfType<ImportScanCandidate>().ToList();
    }

    private const int ScanConcurrency = 4;

    private async Task<ImportScanCandidate?> ScanFolderAsync(
        string dir, IMetadataProvider provider, IReadOnlyDictionary<string, Series> withoutFiles,
        CancellationToken ct)
    {
        var folderName = Path.GetFileName(dir);
        IReadOnlyList<ComicSource> comics;
        try
        {
            comics = ComicSourceScanner.Scan(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // lost+found on a mount root, System Volume Information on a drive root: one folder
            // the process cannot read must not fail the whole scan.
            logger.LogWarning(ex, "Skipping unreadable folder {Folder} in the import scan", dir);
            return null;
        }

        var existing = withoutFiles.GetValueOrDefault(folderName);
        if (existing is not null && comics.Count == 0)
        {
            // The empty folder Maki made when the series was added: nothing here to import.
            return null;
        }

        var recognized = comics.Count(c => ReleaseNameParser.ParseFileName(c.Name).IsRecognized);
        var cleanedTitle = ReleaseNameParser.CleanFolderTitle(folderName);

        IReadOnlyList<MetadataSearchResult> matches = [];
        if (existing?.MangaBakaId is { } mangaBakaId)
        {
            // Importing matches the series by provider id, so offering anything else would
            // add a second copy instead of filling this one.
            matches =
            [
                new MetadataSearchResult(mangaBakaId.ToString(CultureInfo.InvariantCulture), existing.Title,
                    null, existing.Year, existing.Status, null, null)
            ];
        }
        else
        {
            try
            {
                // Deliberately unfiltered: this names folders that are already sitting in the
                // caller's own root folder, so a ceiling here hides nothing they cannot already see
                // and would instead leave those folders permanently unmatchable, with nothing on
                // screen to say why. The ceiling governs discovering new series, not adopting files.
                matches = (await provider.SearchAsync(cleanedTitle, ContentRating.Pornographic, ct))
                    .Take(5)
                    .ToList();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Metadata search failed for {Title}", cleanedTitle);
            }
        }

        return new ImportScanCandidate(
            folderName, cleanedTitle, comics.Count, recognized, matches, existing?.Id);
    }

    public async Task<ImportResult> ImportAsync(
        int rootFolderId, ImportRequestItem item, bool updateComicInfo = true, string? operationId = null,
        CancellationToken ct = default)
    {
        var rootFolder = currentUser.AllRootFolders || currentUser.RootFolderIds.Contains(rootFolderId)
            ? await db.RootFolders.FindAsync([rootFolderId], ct)
            : null;
        if (rootFolder is null)
        {
            return new ImportResult(item.FolderName, false, localizer.Get("error.series.rootFolderNotFound"));
        }

        // FolderName comes straight off the request. It must name exactly one entry directly
        // inside the root, never an absolute path (Path.Combine would discard the root entirely)
        // or a ".."-laden one that walks out of it, or import could move/rewrite files anywhere
        // on disk the process can reach.
        if (string.IsNullOrEmpty(item.FolderName) ||
            Path.GetFileName(item.FolderName) != item.FolderName ||
            item.FolderName.Trim('.', ' ').Length == 0)
        {
            return new ImportResult(item.FolderName, false,
                localizer.Get("error.libraryImport.invalidFolderName"));
        }

        var sourceDir = LibraryPaths.ResolveNoLinks(rootFolder.Path, item.FolderName);
        if (sourceDir is null)
        {
            return new ImportResult(item.FolderName, false,
                localizer.Get("error.libraryImport.invalidFolderName"));
        }

        if (!Directory.Exists(sourceDir))
        {
            return new ImportResult(item.FolderName, false, localizer.Get("error.libraryImport.folderGone"));
        }

        await events.ImportProgress(item.FolderName, ImportStage.FetchingMetadata, operationId: operationId);
        var provider = metadataProviders.First();
        var metadata = await provider.GetAsync(item.MetadataProviderId, ct);
        if (metadata is null)
        {
            return new ImportResult(item.FolderName, false,
                localizer.Get("error.libraryImport.metadataLookupFailed"));
        }

        // Held from the lookup below through the insert: an add or an import list can be putting
        // the same work in the library at this moment.
        using var providerLock = metadata.MangaBakaId is { } lockId
            ? await SeriesLocks.ProviderIdAsync(lockId, ct)
            : null;

        // Already in the library? If the existing series has no downloaded/linked files,
        // treat this as re-linking on-disk files into it rather than a failure. If it
        // already has files, adding another folder for it would be ambiguous, so refuse.
        var existingSeries = metadata.MangaBakaId is { } existingId
            ? await db.Series.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.MangaBakaId == existingId, ct)
            : null;
        if (existingSeries is not null && !await db.Series.AnyAsync(s => s.Id == existingSeries.Id, ct))
        {
            return new ImportResult(item.FolderName, false,
                localizer.Get("error.libraryImport.alreadyInLibrary", new { title = metadata.Title }));
        }

        var owedLink = updateComicInfo ? PendingImportLink.LinkAndComicInfo : PendingImportLink.Link;
        if (existingSeries is not null)
        {
            using var seriesLock = await SeriesLocks.SeriesAsync(existingSeries.Id, ct);
            if (await db.ChapterFiles.AnyAsync(f => f.SeriesId == existingSeries.Id, ct))
            {
                return new ImportResult(item.FolderName, false,
                    localizer.Get("error.libraryImport.alreadyInLibrary", new { title = metadata.Title }));
            }

            try
            {
                return await ReimportIntoExistingAsync(
                    existingSeries, rootFolder, item, sourceDir, updateComicInfo, operationId, ct);
            }
            catch (Exception ex)
            {
                return await RecoverFailedImportAsync(
                    existingSeries, item, existingSeries.FolderName, created: false, ex, owedLink);
            }
        }

        // Standardize the folder name to the configured series folder format, unless the folder
        // naming setting says to leave the on-disk folder alone. The row is mapped up here rather
        // than after the rename because the format reads the year and the tracker ids off it.
        var series = SeriesMetadataMapper.NewFromMetadata(metadata);
        var standardName = await naming.BuildSeriesFolderNameAsync(series, ct);
        var namingMode = await GetFolderNamingModeAsync(ct);
        // Held from reading which folders are taken through the insert: imports run in parallel, and
        // two works standardizing to one name must not both be told it is free.
        using var folderNameLock = await SeriesLocks.FolderNamesAsync(ct);
        var otherFolders = await SeriesCreationService.SeriesFoldersInRootAsync(db, rootFolder.Id, null, ct);
        var targetDir = sourceDir;
        var seriesFolderName = item.FolderName;
        string? renamedFrom = null;
        if (namingMode == FolderNamingMode.Rename)
        {
            // Two series in one folder rescan each other's files and delete them with their own.
            var wanted = SeriesCreationService.FreeFolderName(
                standardName, series.MangaBakaId,
                name => !otherFolders.Contains(name) && RenameTargetFree(rootFolder.Path, sourceDir, name));
            if (!string.Equals(item.FolderName, wanted, StringComparison.Ordinal))
            {
                targetDir = LibraryPaths.Resolve(rootFolder.Path, wanted) ?? Path.Combine(rootFolder.Path, wanted);
                if (!LibraryPaths.IsSameDirectory(sourceDir, targetDir) && Directory.Exists(targetDir))
                {
                    return new ImportResult(item.FolderName, false,
                        localizer.Get("error.libraryImport.renameTargetExists", new { name = wanted }));
                }

                await events.ImportProgress(item.FolderName, ImportStage.RenamingFolder, operationId: operationId);
                SeriesRenameService.MovePath(sourceDir, targetDir, Directory.Move);
                logger.LogInformation("Renamed '{Old}' -> '{New}'", item.FolderName, wanted);
                seriesFolderName = wanted;
                renamedFrom = sourceDir;
            }
        }
        else
        {
            // The files stay in this folder, so it must not already be another series' own.
            if (otherFolders.Contains(item.FolderName))
            {
                return new ImportResult(item.FolderName, false,
                    localizer.Get("error.libraryImport.folderOwnedByOtherSeries"));
            }

            if (namingMode == FolderNamingMode.KeepOriginalNewStandard)
            {
                // Existing files stay where they are; future downloads go into a separate,
                // standard-named folder that isn't created until something downloads into it.
                seriesFolderName = SeriesCreationService.FreeFolderName(
                    standardName, series.MangaBakaId,
                    name => !otherFolders.Contains(name) &&
                            (LibraryPaths.FolderComparer.Equals(name, item.FolderName) ||
                             !SeriesCreationService.HoldsComics(rootFolder.Path, name)));
            }
        }

        series.MonitorNewItems = await MonitorDefaults.ForNewSeriesAsync(appSettings, ct);
        // Same per-rating default as the add path. An imported pornographic title would otherwise
        // start public and be scrobbled before anyone looked at it.
        series.Incognito = await IncognitoRatingRules.ResolveAsync(appSettings, series.ContentRating, ct);
        series.RootFolderId = rootFolder.Id;
        series.FolderName = seriesFolderName;
        db.Series.Add(series);
        await db.SaveChangesAsync(ct);
        folderNameLock.Dispose();
        providerLock?.Dispose();

        try
        {
            // Same as the Add path: importing a folder back after a delete re-attaches its history.
            await identity.AdoptOrphansAsync(series, ct);

            if (metadata.CoverUrl != null)
            {
                await events.ImportProgress(item.FolderName, ImportStage.DownloadingCover, operationId: operationId);
                var coverPath = await coverService.DownloadCoverAsync(series.Id, metadata.CoverUrl, ct);
                if (coverPath != null)
                {
                    series.CoverPath = coverPath;
                    await db.SaveChangesAsync(ct);
                    await coverService.WriteLibraryCoverAsync(series.Id, targetDir, ct);
                }
            }

            var (added, skipped) = await RegisterAndDeferLinkAsync(series, item, targetDir, owedLink, operationId, ct);

            // After registering rather than at the insert, so an import rolled back below leaves no
            // "added" behind it. Still after the orphan adoption, so a series removed and put back
            // reads as one continuous history.
            await stats.RecordAsync(StatsEventType.SeriesAdded, series.Id, series.Title, ct: ct);
            return new ImportResult(item.FolderName, true, null, series.Id, seriesFolderName,
                Skipped: skipped.Count > 0 ? skipped : null, FilesAdded: added, LinkPending: true);
        }
        catch (Exception ex)
        {
            return await RecoverFailedImportAsync(
                series, item, seriesFolderName, created: true, ex, owedLink,
                renamedFrom is null ? null : (renamedFrom, targetDir));
        }
    }

    /// <summary>
    /// Builds the folder's CBZs and registers them without linking, then hands the series to the
    /// background source match. Linking needs chapter rows and those only come from a source sync,
    /// which is the slow, rate-limited part of an import; <see cref="SourceMatchWorkerHostedService"/>
    /// links the files, and rewrites their ComicInfo.xml if asked, once the match is done.
    /// <para>
    /// The flag and the marker are set in the same locked section that registers the files, and the
    /// link stage clears the marker in the locked section that links them, so a match already queued
    /// for this series (an add, then an import of its folder) can never clear the flag before these
    /// files are on record and leave them unlinked.
    /// </para>
    /// </summary>
    private async Task<(int Added, List<ImportSkippedFile> Unreadable)> RegisterAndDeferLinkAsync(
        Series series, ImportRequestItem item, string targetDir, PendingImportLink owedLink, string? operationId,
        CancellationToken ct, bool locked = false)
    {
        using var seriesLock = locked ? null : await SeriesLocks.SeriesAsync(series.Id, ct);
        await events.ImportProgress(item.FolderName, ImportStage.AddingFiles, operationId: operationId);
        var (cbzFiles, unreadable) = MaterializeComics(targetDir);
        var added = await cbzLinkService.RegisterFilesAsync(series, targetDir, cbzFiles, "import", ct);
        series.SourceMatchPending = true;
        series.PendingImportLink = owedLink;
        await db.SaveChangesAsync(ct);
        sourceMatchQueue.Enqueue(series.Id, SourceMatchLane.Background);

        var skipped = unreadable
            .Select(f => new ImportSkippedFile(Path.GetRelativePath(targetDir, f), ImportSkipReason.Unreadable))
            .ToList();
        return (added, skipped);
    }

    /// <summary>
    /// What is left after an import threw or was cancelled part way. The series row is committed
    /// before anything is linked, and a folder with a series but no files is hidden from the scan
    /// and refused as already imported, so a new series that linked nothing is removed again. One
    /// that did link files stays, and the caller is sent to its page to rescan the rest.
    /// </summary>
    private async Task<ImportResult> RecoverFailedImportAsync(
        Series series, ImportRequestItem item, string folderName, bool created, Exception ex,
        PendingImportLink owedLink, (string From, string To)? renamed = null)
    {
        logger.LogError(ex, "Import of '{Folder}' into series {SeriesId} failed part way", item.FolderName, series.Id);
        var none = CancellationToken.None;
        var seriesId = series.Id;
        var title = series.Title;
        try
        {
            db.ChangeTracker.Clear();
            var linkedAny = await db.ChapterFiles.AnyAsync(f => f.SeriesId == seriesId, none);
            if (created && !linkedAny)
            {
                if (await db.Series.FindAsync([seriesId], none) is { } row)
                {
                    db.Series.Remove(row);
                    await db.SaveChangesAsync(none);
                }

                coverService.DeleteCover(seriesId);
                if (renamed is var (from, to))
                {
                    RestoreRenamedFolder(from, to);
                }

                return new ImportResult(item.FolderName, false, localizer.Get("error.libraryImport.failed"));
            }

            if (created)
            {
                await stats.RecordAsync(StatsEventType.SeriesAdded, seriesId, title, ct: none);
            }

            // Files went on record but the import stopped before handing the series to the match,
            // or the hand-off itself failed. Without this the files would wait for a restart.
            if (linkedAny && !await db.Chapters.AnyAsync(c => c.SeriesId == seriesId, none) &&
                await db.Series.FindAsync([seriesId], none) is { } kept)
            {
                kept.SourceMatchPending = true;
                kept.PendingImportLink = owedLink;
                await db.SaveChangesAsync(none);
                sourceMatchQueue.Enqueue(seriesId, SourceMatchLane.Background);
            }
        }
        catch (Exception cleanupEx)
        {
            logger.LogError(cleanupEx, "Could not clean up after the failed import of '{Folder}'", item.FolderName);
        }

        return new ImportResult(item.FolderName, false,
            localizer.Get("error.libraryImport.partial", new { title }), seriesId, folderName);
    }

    private void RestoreRenamedFolder(string from, string to)
    {
        try
        {
            if (Directory.Exists(to) && (!Directory.Exists(from) || LibraryPaths.IsSameDirectory(from, to)))
            {
                SeriesRenameService.MovePath(to, from, Directory.Move);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not move '{Folder}' back to '{Original}' after the failed import", to, from);
        }
    }

    /// <summary>
    /// True when a rename of <paramref name="sourceDir"/> to <paramref name="name"/> would not land on
    /// another folder. The source folder itself counts as free, so a re-cased spelling renames in place.
    /// </summary>
    internal static bool RenameTargetFree(string rootPath, string sourceDir, string name)
    {
        var target = LibraryPaths.Resolve(rootPath, name) ?? Path.Combine(rootPath, name);
        return LibraryPaths.IsSameDirectory(sourceDir, target) || !Directory.Exists(target);
    }

    /// <summary>
    /// Re-links the on-disk CBZ files in <paramref name="sourceDir"/> to a series that is
    /// already in the library but has no downloaded files yet — without re-adding the series
    /// or re-fetching its metadata. Reconciles the folder to the standardized name and ensures
    /// chapters exist to link against.
    /// </summary>
    private async Task<ImportResult> ReimportIntoExistingAsync(
        Series series, RootFolder rootFolder, ImportRequestItem item, string sourceDir,
        bool updateComicInfo, string? operationId, CancellationToken ct)
    {
        var standardName = await naming.BuildSeriesFolderNameAsync(series, ct);
        var namingMode = await GetFolderNamingModeAsync(ct);
        using var folderNameLock = await SeriesLocks.FolderNamesAsync(ct);
        var otherFolders = await SeriesCreationService.SeriesFoldersInRootAsync(db, rootFolder.Id, series.Id, ct);
        var targetDir = sourceDir;
        var seriesFolderName = item.FolderName;
        var warnings = new List<string>();
        if (namingMode == FolderNamingMode.Rename)
        {
            var wanted = SeriesCreationService.FreeFolderName(
                standardName, series.MangaBakaId, name => !otherFolders.Contains(name));
            if (!string.Equals(item.FolderName, wanted, StringComparison.Ordinal))
            {
                targetDir = LibraryPaths.Resolve(rootFolder.Path, wanted) ?? Path.Combine(rootFolder.Path, wanted);
                if (LibraryPaths.IsSameDirectory(sourceDir, targetDir))
                {
                    // The same folder under another spelling on a case-insensitive filesystem.
                    // Merging it into itself would move nothing and then delete it.
                    await events.ImportProgress(item.FolderName, ImportStage.RenamingFolder, operationId: operationId);
                    SeriesRenameService.MovePath(sourceDir, targetDir, Directory.Move);
                    logger.LogInformation("Renamed '{Old}' -> '{New}'", item.FolderName, wanted);
                }
                else if (Directory.Exists(targetDir))
                {
                    // The series' standardized folder already exists (e.g. an empty folder created
                    // when it was added), so fold the scanned folder's files into it.
                    await events.ImportProgress(item.FolderName, ImportStage.MergingFolder, operationId: operationId);
                    var leftBehind = MergeDirectory(sourceDir, targetDir);
                    logger.LogInformation("Merged '{Old}' into existing '{New}'", item.FolderName, wanted);
                    if (leftBehind.Count > 0)
                    {
                        logger.LogWarning(
                            "Left {Count} files in '{Old}' whose names already exist in '{New}': {Files}",
                            leftBehind.Count, item.FolderName, wanted, string.Join(", ", leftBehind));
                        warnings.Add(localizer.Get("error.libraryImport.mergeLeftFiles",
                            new { count = leftBehind.Count, folder = item.FolderName }));
                    }
                }
                else
                {
                    await events.ImportProgress(item.FolderName, ImportStage.RenamingFolder, operationId: operationId);
                    Directory.Move(sourceDir, targetDir);
                    logger.LogInformation("Renamed '{Old}' -> '{New}'", item.FolderName, wanted);
                }

                seriesFolderName = wanted;
            }
        }
        else
        {
            if (otherFolders.Contains(item.FolderName))
            {
                return new ImportResult(item.FolderName, false,
                    localizer.Get("error.libraryImport.folderOwnedByOtherSeries"));
            }

            if (namingMode == FolderNamingMode.KeepOriginalNewStandard)
            {
                seriesFolderName = SeriesCreationService.FreeFolderName(
                    standardName, series.MangaBakaId,
                    name => !otherFolders.Contains(name) &&
                            (LibraryPaths.FolderComparer.Equals(name, item.FolderName) ||
                             LibraryPaths.FolderComparer.Equals(name, series.FolderName) ||
                             !SeriesCreationService.HoldsComics(rootFolder.Path, name)));
            }
        }

        // Point the series at this location if it drifted (root folder or folder name).
        if (series.RootFolderId != rootFolder.Id ||
            !string.Equals(series.FolderName, seriesFolderName, StringComparison.Ordinal))
        {
            series.RootFolderId = rootFolder.Id;
            series.FolderName = seriesFolderName;
            await db.SaveChangesAsync(ct);
        }

        folderNameLock.Dispose();

        // A series added but never matched has no chapters to link against yet, so its files wait
        // for the background match like a new series' do. The caller holds the series lock.
        if (!await db.Chapters.AnyAsync(c => c.SeriesId == series.Id, ct))
        {
            var owedLink = updateComicInfo ? PendingImportLink.LinkAndComicInfo : PendingImportLink.Link;
            var (added, unreadableSkipped) = await RegisterAndDeferLinkAsync(
                series, item, targetDir, owedLink, operationId, ct, locked: true);
            return new ImportResult(item.FolderName, true, null, series.Id, seriesFolderName,
                Warnings: warnings.Count > 0 ? warnings : null,
                Skipped: unreadableSkipped.Count > 0 ? unreadableSkipped : null,
                FilesAdded: added, LinkPending: true);
        }

        var (cbzFiles, unreadable) = MaterializeComics(targetDir);
        var linkStage = updateComicInfo ? ImportStage.UpdatingComicInfo : ImportStage.LinkingFiles;
        var (linked, unrecognized) = await cbzLinkService.LinkFilesAsync(
            series, targetDir, cbzFiles, "import",
            (current, total) => events.ImportProgress(item.FolderName, linkStage, current, total, operationId: operationId),
            updateComicInfo, ct: ct);
        var skipped = await SkippedFilesAsync(series.Id, targetDir, cbzFiles, unreadable, ct);

        return new ImportResult(item.FolderName, true, null, series.Id, seriesFolderName, linked, unrecognized,
            warnings.Count > 0 ? warnings : null, skipped.Count > 0 ? skipped : null);
    }

    /// <summary>
    /// Every comic the import leaves backing no chapter, and why. Read after linking rather than
    /// counted during it, since the volume backfill and the lone-file rule link files late.
    /// </summary>
    internal async Task<List<ImportSkippedFile>> SkippedFilesAsync(
        int seriesId, string targetDir, IReadOnlyList<string> files, IReadOnlyList<string> unreadable,
        CancellationToken ct)
    {
        var linkedIds = (await db.Chapters
                .Where(c => c.SeriesId == seriesId && c.ChapterFileId != null)
                .Select(c => c.ChapterFileId!.Value)
                .ToListAsync(ct))
            .ToHashSet();
        var linkedPaths = (await db.ChapterFiles
                .Where(f => f.SeriesId == seriesId)
                .Select(f => new { f.Id, f.RelativePath })
                .ToListAsync(ct))
            .Where(f => linkedIds.Contains(f.Id))
            .Select(f => LibraryPaths.ComparisonKey(f.RelativePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(targetDir));
        var skipped = new List<ImportSkippedFile>();
        foreach (var file in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetRelativePath(targetDir, file);
            if (linkedPaths.Contains(LibraryPaths.ComparisonKey(Path.Combine(folderName, name))))
            {
                continue;
            }

            skipped.Add(new ImportSkippedFile(name, ReleaseNameParser.ParseFileName(file).IsRecognized
                ? ImportSkipReason.NoMatchingChapter
                : ImportSkipReason.Unrecognized));
        }

        skipped.AddRange(unreadable.Select(f =>
            new ImportSkippedFile(Path.GetRelativePath(targetDir, f), ImportSkipReason.Unreadable)));
        return skipped;
    }

    /// <summary>
    /// The folder's CBZ files, building one for anything that is not a CBZ yet. A shelf that
    /// predates Maki is full of RAR volumes, plain zips and folders of loose pages, and every one
    /// of them was invisible here — indistinguishable, from the outside, from an empty folder.
    /// <para>
    /// The original is always left where it is. This is the user's own library rather than a
    /// download, so nothing here may be the reason a file they still want disappears; a zip is
    /// hardlinked under its new name, so the common case costs no disk either.
    /// </para>
    /// </summary>
    internal (List<string> Files, List<string> Unreadable) MaterializeComics(string targetDir)
    {
        var files = new List<string>();
        var unreadable = new List<string>();
        var sources = ComicSourceScanner.Scan(targetDir);

        // An archive the scan read nothing out of drops from its list without a trace. One whose
        // name a found comic shares is a second copy of it (an X.cbr beside X.cbz), not a failure.
        var found = sources
            .SelectMany(s => new[] { s.Path, Path.Combine(targetDir, s.Name) })
            .Select(WithoutExtension)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        unreadable.AddRange(LibraryPaths.EnumerateFilesNoLinks(targetDir)
            .Where(f => ComicSourceScanner.IsArchive(f) || ComicFile.IsPdf(f))
            .Where(f => !found.Contains(WithoutExtension(f)))
            .OrderBy(f => f, StringComparer.Ordinal));

        foreach (var source in sources)
        {
            if (source.Kind is ComicSourceKind.Cbz or ComicSourceKind.Pdf)
            {
                files.Add(source.Path);
                continue;
            }

            // A 7z or RAR under a ".cbz" name targets its own path; Materialize rebuilds it in place.
            var target = Path.Combine(targetDir, source.Name);
            if (File.Exists(target) && !ComicSourceConverter.IsSameFile(target, source.Path))
            {
                files.Add(target);
                continue;
            }

            try
            {
                ComicSourceConverter.Materialize(source, target);
                logger.LogInformation(
                    "Built {Target} from {Source}", source.Name, Path.GetFileName(source.Path));
                files.Add(target);
            }
            catch (Exception ex)
            {
                // One unreadable archive must not cost the folder its other files.
                logger.LogWarning(ex, "Could not build a CBZ from {Source}", source.Path);
                unreadable.Add(source.Entry is null ? source.Path : target);
            }
        }

        return (files, unreadable);
    }

    private static string WithoutExtension(string path) =>
        Path.Combine(Path.GetDirectoryName(path) ?? "", Path.GetFileNameWithoutExtension(path));

    private async Task<string> GetFolderNamingModeAsync(CancellationToken ct)
    {
        var mode = await appSettings.GetAsync(SettingKeys.LibraryFolderNamingMode, ct);
        return FolderNamingMode.IsValid(mode) ? mode! : FolderNamingMode.Default;
    }

    /// <summary>
    /// Moves every file from <paramref name="sourceDir"/> into <paramref name="targetDir"/>,
    /// preserving sub-paths, then removes whatever folders that emptied. A file whose name is
    /// already taken in the target stays where it is, and so does every folder still holding one:
    /// nothing is deleted here, only moved. Returns the files left behind, relative to the source.
    /// </summary>
    internal static List<string> MergeDirectory(string sourceDir, string targetDir)
    {
        var leftBehind = new List<string>();
        foreach (var file in LibraryPaths.EnumerateFilesNoLinks(sourceDir).ToList())
        {
            var rel = Path.GetRelativePath(sourceDir, file);
            var dest = Path.Combine(targetDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (File.Exists(dest))
            {
                leftBehind.Add(rel);
                continue;
            }

            File.Move(file, dest);
        }

        // Deepest first, and never recursive: a folder that still holds a collision, a link or a
        // file that arrived meanwhile refuses the delete and stays.
        var folders = LibraryPaths.EnumerateDirectoriesNoLinks(sourceDir)
            .OrderByDescending(d => d.Length)
            .Append(sourceDir);
        foreach (var folder in folders)
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(folder).Any())
                {
                    Directory.Delete(folder, recursive: false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return leftBehind;
    }
}
