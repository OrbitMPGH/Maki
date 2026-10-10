using System.Globalization;
using System.Text.Json;
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

    /// <summary>A second copy of another comic in the folder (an X.cbr beside X.cbz); only one is used.</summary>
    public const string Duplicate = "duplicate";

    /// <summary>A merge left it in the scanned folder: the series folder already has a file of that name.</summary>
    public const string LeftBehind = "leftBehind";
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

        // Dismissed by hand ("Extras", "Art"): skipped before the folder is listed or searched.
        var ignored = (await db.ImportIgnoredFolders
                .Where(x => x.RootFolderId == rootFolderId)
                .Select(x => x.FolderName)
                .ToListAsync(ct))
            .ToHashSet(LibraryPaths.FolderComparer);

        var provider = metadataProviders.First();
        var dirs = Directory.GetDirectories(rootFolder.Path)
            .Order()
            .Where(dir =>
            {
                var folderName = Path.GetFileName(dir);
                return !folderName.StartsWith('.') && !claimed.Contains(folderName) && !ignored.Contains(folderName) &&
                       !LibraryPaths.IsLink(dir);
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

    public async Task<List<ImportIgnoredFolder>> IgnoredFoldersAsync(int rootFolderId, CancellationToken ct) =>
        CanSeeRoot(rootFolderId)
            ? await db.ImportIgnoredFolders
                .AsNoTracking()
                .Where(x => x.RootFolderId == rootFolderId)
                .OrderBy(x => x.FolderName)
                .ToListAsync(ct)
            : [];

    /// <summary>
    /// Keeps <paramref name="folderName"/> out of every later scan of this root. Returns the error key
    /// when it cannot, null when it is ignored (or already was).
    /// </summary>
    public async Task<string?> IgnoreFolderAsync(int rootFolderId, string folderName, CancellationToken ct)
    {
        if (!CanSeeRoot(rootFolderId) || !await db.RootFolders.AnyAsync(r => r.Id == rootFolderId, ct))
        {
            return "error.series.rootFolderNotFound";
        }

        if (!IsPlainFolderName(folderName))
        {
            return "error.libraryImport.invalidFolderName";
        }

        var taken = (await db.ImportIgnoredFolders
                .Where(x => x.RootFolderId == rootFolderId)
                .Select(x => x.FolderName)
                .ToListAsync(ct))
            .Contains(folderName, LibraryPaths.FolderComparer);
        if (!taken)
        {
            await InsertIgnoredFolderAsync(rootFolderId, folderName, ct);
        }

        return null;
    }

    /// <summary>
    /// Adds the row, treating the unique index refusing it as done: two clicks, or two admins,
    /// ignoring one folder at once both pass the check above, and either way the folder is ignored.
    /// </summary>
    internal async Task InsertIgnoredFolderAsync(int rootFolderId, string folderName, CancellationToken ct)
    {
        var row = new ImportIgnoredFolder
        {
            RootFolderId = rootFolderId, FolderName = folderName, CreatedAt = DateTime.UtcNow,
        };
        db.ImportIgnoredFolders.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.Entry(row).State = EntityState.Detached;
            if (!await db.ImportIgnoredFolders.AsNoTracking()
                    .AnyAsync(x => x.RootFolderId == rootFolderId && x.FolderName == folderName, ct))
            {
                throw;
            }
        }
    }

    /// <summary>False when there was no such entry the caller may see.</summary>
    public async Task<bool> UnignoreFolderAsync(int id, CancellationToken ct)
    {
        var row = await db.ImportIgnoredFolders.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (row is null || !CanSeeRoot(row.RootFolderId))
        {
            return false;
        }

        db.ImportIgnoredFolders.Remove(row);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private bool CanSeeRoot(int rootFolderId) =>
        currentUser.AllRootFolders || currentUser.RootFolderIds.Contains(rootFolderId);

    /// <summary>
    /// Exactly one entry directly inside the root, never an absolute path (Path.Combine would discard
    /// the root entirely) or a ".."-laden one that walks out of it.
    /// </summary>
    private static bool IsPlainFolderName(string? folderName) =>
        !string.IsNullOrEmpty(folderName) &&
        Path.GetFileName(folderName) == folderName &&
        folderName.Trim('.', ' ').Length > 0;

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

        if (comics.Count == 0)
        {
            // Nothing here to import: the empty folder Maki made when a series was added, or a
            // folder of covers, notes or anything else that is not a comic. Not worth a search.
            return null;
        }

        var existing = withoutFiles.GetValueOrDefault(folderName);

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

    /// <summary>
    /// What importing <paramref name="item"/> would do, worked out without writing anything: the
    /// folder decision and the comic plan the import itself runs, read rather than carried out.
    /// Takes no locks, so a folder another import is moving at the same moment can read differently
    /// when the import runs; the import re-decides under its locks either way.
    /// </summary>
    public async Task<LibraryImportPlan> PlanAsync(int rootFolderId, ImportRequestItem item, CancellationToken ct = default)
    {
        var (rootFolder, sourceDir, sourceError) = await ResolveSourceAsync(rootFolderId, item.FolderName, ct);
        if (sourceError is not null)
        {
            return new LibraryImportPlan(item.FolderName, localizer.Get(sourceError));
        }

        var metadata = await metadataProviders.First().GetAsync(item.MetadataProviderId, ct);
        if (metadata is null)
        {
            return new LibraryImportPlan(item.FolderName, localizer.Get("error.libraryImport.metadataLookupFailed"));
        }

        var existingSeries = metadata.MangaBakaId is { } existingId
            ? await db.Series.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(s => s.MangaBakaId == existingId, ct)
            : null;
        if (existingSeries is not null &&
            (!await db.Series.AnyAsync(s => s.Id == existingSeries.Id, ct) ||
             await db.ChapterFiles.AnyAsync(f => f.SeriesId == existingSeries.Id, ct)))
        {
            return new LibraryImportPlan(item.FolderName,
                localizer.Get("error.libraryImport.alreadyInLibrary", new { title = metadata.Title }));
        }

        var series = existingSeries ?? SeriesMetadataMapper.NewFromMetadata(metadata);
        var standardName = await naming.BuildSeriesFolderNameAsync(series, ct);
        var namingMode = await GetFolderNamingModeAsync(ct);
        var otherFolders = await SeriesCreationService.SeriesFoldersInRootAsync(db, rootFolder.Id, existingSeries?.Id, ct);
        var decision = DecideFolder(
            namingMode, standardName, series.MangaBakaId, rootFolder.Path, sourceDir, item.FolderName, otherFolders,
            existingSeries?.FolderName);
        if (decision.ErrorKey is not null)
        {
            return new LibraryImportPlan(item.FolderName, localizer.Get(decision.ErrorKey, decision.ErrorArgs));
        }

        var plan = PlanComics(sourceDir);
        var comics = plan.Comics.Select(c => (Comic: c, Name: Path.GetRelativePath(sourceDir, c.Target))).ToList();
        var skipped = plan.Unreadable
            .Select(f => new ImportSkippedFile(Path.GetRelativePath(sourceDir, f), ImportSkipReason.Unreadable))
            .Concat(plan.Duplicates
                .Select(f => new ImportSkippedFile(Path.GetRelativePath(sourceDir, f), ImportSkipReason.Duplicate)))
            .ToList();

        if (decision.Action == ImportFolderAction.Merge)
        {
            // A merge moves only the names the series folder does not have yet. What stays behind is
            // not imported, and the series folder's own comics are registered with the moved ones.
            var targetDir = TargetDirOf(rootFolder.Path, decision.TargetName);
            var leftBehind = comics
                .Where(c => File.Exists(Path.Combine(targetDir, Path.GetRelativePath(sourceDir, c.Comic.Source.Path))))
                .ToList();
            skipped.AddRange(leftBehind.Select(c =>
                new ImportSkippedFile(Path.GetRelativePath(sourceDir, c.Comic.Source.Path), ImportSkipReason.LeftBehind)));
            // After the move one scan of the series folder sees both sides, and it keeps one comic per
            // name the way ComicSourceScanner.Scan does: a ready CBZ over anything to repack.
            var merged = comics
                .Except(leftBehind)
                .Select(c => (c.Comic, c.Name, Moved: true))
                .Concat(PlanComics(targetDir).Comics
                    .Select(c => (Comic: c, Name: Path.GetRelativePath(targetDir, c.Target), Moved: false)))
                .GroupBy(c => Path.GetFileNameWithoutExtension(c.Comic.Source.Name), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderBy(c => ComicSourceScanner.Preference(c.Comic.Source)).ToList())
                .ToList();
            skipped.AddRange(merged
                .SelectMany(g => g.Skip(1))
                .Where(c => c.Moved)
                .Select(c => new ImportSkippedFile(
                    Path.GetRelativePath(sourceDir, c.Comic.Source.Path), ImportSkipReason.Duplicate)));
            comics = merged.Select(g => (g[0].Comic, g[0].Name)).ToList();
        }

        var chapters = existingSeries is null
            ? []
            : await db.Chapters.AsNoTracking().Where(c => c.SeriesId == existingSeries.Id).ToListAsync(ct);
        var linkDeferred = chapters.Count == 0;
        var files = comics
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .Select(c =>
            {
                var parsed = ReleaseNameParser.ParseFileName(c.Comic.Target);
                // The link stage's lone-file rule: one file, one chapter, a name that says nothing.
                var lone = comics.Count == 1 && !parsed.IsRecognized && (linkDeferred || chapters.Count == 1);
                IReadOnlyList<string>? covered = linkDeferred
                    ? null
                    : lone
                        ? chapters.Select(ChapterLabel).ToList()
                        : TorrentImportService.ChaptersCoveredBy(chapters, parsed, c.Comic.Source.Pages, null, c.Name)
                            .Select(ChapterLabel)
                            .ToList();
                var unlinked = lone ? null
                    : !parsed.IsRecognized ? ImportSkipReason.Unrecognized
                    : covered is { Count: 0 } ? ImportSkipReason.NoMatchingChapter
                    : null;
                return new LibraryImportPlanFile(
                    c.Name,
                    Path.GetRelativePath(sourceDir, c.Comic.Source.Path),
                    c.Comic.Source.Entry,
                    JsonNamingPolicy.CamelCase.ConvertName(c.Comic.Source.Kind.ToString()),
                    c.Comic.Action,
                    c.Comic.Aside is null ? null : Path.GetRelativePath(sourceDir, c.Comic.Aside),
                    c.Comic.Source.Size,
                    parsed.Number?.ToString("0.###", CultureInfo.InvariantCulture),
                    parsed.Volume,
                    parsed.VolumeEnd,
                    covered,
                    unlinked,
                    lone);
            })
            .ToList();

        // Only a new series gets a cover written into its folder; see ImportAsync.
        var writesCover = existingSeries is null && metadata.CoverUrl is not null &&
                          await appSettings.GetAsync(SettingKeys.LibraryWriteCoverToFolder, ct) == "true";
        return new LibraryImportPlan(
            item.FolderName, null, metadata.Title, existingSeries?.Id, decision.Action, decision.TargetName,
            decision.SeriesFolderName, linkDeferred, files, skipped, writesCover,
            writesCover && File.Exists(Path.Combine(sourceDir, LibraryCoverFileName)));
    }

    /// <summary>The cover <see cref="CoverService.WriteLibraryCoverAsync(int, string, CancellationToken)"/> writes.</summary>
    internal const string LibraryCoverFileName = "cover.jpg";

    private static string ChapterLabel(Chapter chapter) =>
        chapter.Number?.ToString("0.###", CultureInfo.InvariantCulture) ?? chapter.Title ?? "?";

    public async Task<ImportResult> ImportAsync(
        int rootFolderId, ImportRequestItem item, bool updateComicInfo = true, string? operationId = null,
        CancellationToken ct = default)
    {
        var (rootFolder, sourceDir, sourceError) = await ResolveSourceAsync(rootFolderId, item.FolderName, ct);
        if (sourceError is not null)
        {
            return new ImportResult(item.FolderName, false, localizer.Get(sourceError));
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
        var decision = DecideFolder(
            namingMode, standardName, series.MangaBakaId, rootFolder.Path, sourceDir, item.FolderName, otherFolders,
            existingSeriesFolderName: null);
        if (decision.ErrorKey is not null)
        {
            return new ImportResult(item.FolderName, false, localizer.Get(decision.ErrorKey, decision.ErrorArgs));
        }

        var targetDir = sourceDir;
        var seriesFolderName = decision.SeriesFolderName;
        string? renamedFrom = null;
        if (decision.Action == ImportFolderAction.Rename)
        {
            targetDir = TargetDirOf(rootFolder.Path, decision.TargetName);
            await events.ImportProgress(item.FolderName, ImportStage.RenamingFolder, operationId: operationId);
            SeriesRenameService.MovePath(sourceDir, targetDir, Directory.Move);
            logger.LogInformation("Renamed '{Old}' -> '{New}'", item.FolderName, decision.TargetName);
            renamedFrom = sourceDir;
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

            var folderCover = Path.Combine(targetDir, LibraryCoverFileName);
            var coverExisted = File.Exists(folderCover);
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

            var (added, skipped, built, fileIds) =
                await RegisterAndDeferLinkAsync(series, item, targetDir, owedLink, operationId, ct);
            await RecordBatchFolderAsync(rootFolder.Id, operationId, series, created: true, item.FolderName, targetDir,
                new ImportOperations
                {
                    FolderAction = decision.Action,
                    Built = built,
                    RegisteredFileIds = fileIds,
                    WroteCover = !coverExisted && File.Exists(folderCover),
                });

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
    private async Task<(int Added, List<ImportSkippedFile> Unreadable, List<ImportBuiltFile> Built, List<int> FileIds)>
        RegisterAndDeferLinkAsync(
            Series series, ImportRequestItem item, string targetDir, PendingImportLink owedLink, string? operationId,
            CancellationToken ct, bool locked = false)
    {
        using var seriesLock = locked ? null : await SeriesLocks.SeriesAsync(series.Id, ct);
        await events.ImportProgress(item.FolderName, ImportStage.AddingFiles, operationId: operationId);
        var (cbzFiles, unreadable, built) = MaterializeComics(targetDir);
        var added = await cbzLinkService.RegisterFilesAsync(series, targetDir, cbzFiles, "import", ct);
        series.SourceMatchPending = true;
        series.PendingImportLink = owedLink;
        await db.SaveChangesAsync(ct);
        // Every row the series has: an import only ever runs into a series with no files.
        var fileIds = await db.ChapterFiles.Where(f => f.SeriesId == series.Id).Select(f => f.Id).ToListAsync(ct);
        sourceMatchQueue.Enqueue(series.Id, SourceMatchLane.Background);

        var skipped = unreadable
            .Select(f => new ImportSkippedFile(Path.GetRelativePath(targetDir, f), ImportSkipReason.Unreadable))
            .ToList();
        return (added, skipped, BuiltFiles(targetDir, built), fileIds);
    }

    private static List<ImportBuiltFile> BuiltFiles(string dir, IEnumerable<PlannedComic> built) =>
        built.Select(c => new ImportBuiltFile(
                Path.GetRelativePath(dir, c.Target),
                Path.GetRelativePath(dir, c.Source.Path),
                c.Source.Entry,
                c.Action,
                c.Aside is null ? null : Path.GetRelativePath(dir, c.Aside)))
            .ToList();

    /// <summary>
    /// Keeps what one folder's import did, so it can be undone. A failure here is logged and the
    /// import still succeeds: the files are in the library either way, only the undo is lost.
    /// </summary>
    private async Task RecordBatchFolderAsync(
        int rootFolderId, string? operationId, Series series, bool created, string originalFolderName,
        string targetDir, ImportOperations operations)
    {
        var row = new ImportBatchFolder
        {
            BatchId = operationId is { Length: > 0 and <= 64 } ? operationId : Guid.NewGuid().ToString("N"),
            RootFolderId = rootFolderId,
            SeriesId = series.Id,
            SeriesTitle = series.Title,
            CreatedSeries = created,
            OriginalFolderName = originalFolderName,
            FolderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(targetDir)),
            OperationsJson = operations.Serialize(),
            UserId = currentUser.IsAuthenticated && currentUser.UserId > 0 ? currentUser.UserId : null,
            CreatedAt = DateTime.UtcNow,
        };
        try
        {
            db.ImportBatchFolders.Add(row);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.Entry(row).State = EntityState.Detached;
            logger.LogWarning(ex, "Could not record the import of '{Folder}', so it cannot be undone", originalFolderName);
        }
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
    /// The root folder, and the scanned folder inside it, for a folder name off the request. Returns
    /// the error key instead when the caller may not see the root, the name is not a plain folder
    /// name, or the folder is gone.
    /// </summary>
    private async Task<(RootFolder Root, string SourceDir, string? ErrorKey)> ResolveSourceAsync(
        int rootFolderId, string folderName, CancellationToken ct)
    {
        var rootFolder = CanSeeRoot(rootFolderId) ? await db.RootFolders.FindAsync([rootFolderId], ct) : null;
        if (rootFolder is null)
        {
            return (null!, null!, "error.series.rootFolderNotFound");
        }

        // FolderName comes straight off the request. Anything but a plain name could move or
        // rewrite files anywhere on disk the process can reach.
        var sourceDir = IsPlainFolderName(folderName) ? LibraryPaths.ResolveNoLinks(rootFolder.Path, folderName) : null;
        if (sourceDir is null)
        {
            return (rootFolder, null!, "error.libraryImport.invalidFolderName");
        }

        return Directory.Exists(sourceDir)
            ? (rootFolder, sourceDir, null)
            : (rootFolder, sourceDir, "error.libraryImport.folderGone");
    }

    /// <param name="Action">One of <see cref="ImportFolderAction"/>.</param>
    /// <param name="TargetName">The folder the files end up in.</param>
    /// <param name="SeriesFolderName">What <see cref="Series.FolderName"/> is set to.</param>
    internal sealed record FolderDecision(
        string Action, string TargetName, string SeriesFolderName, string? ErrorKey = null, object? ErrorArgs = null);

    /// <summary>
    /// What happens to the scanned folder under the folder naming setting: kept, renamed to the
    /// series folder format, or, for a series already in the library whose standard folder exists,
    /// merged into it. Shared by the import and its preview, which only reads the result.
    /// </summary>
    /// <param name="existingSeriesFolderName">The series' folder when it is already in the library, else null.</param>
    internal static FolderDecision DecideFolder(
        string namingMode, string standardName, int? mangaBakaId, string rootPath, string sourceDir,
        string folderName, IReadOnlySet<string> otherFolders, string? existingSeriesFolderName)
    {
        var existing = existingSeriesFolderName is not null;
        if (namingMode == FolderNamingMode.Rename)
        {
            // Two series in one folder rescan each other's files and delete them with their own. A new
            // series never renames onto another folder; an existing one merges into its own.
            var wanted = SeriesCreationService.FreeFolderName(
                standardName, mangaBakaId,
                existing
                    ? name => !otherFolders.Contains(name)
                    : name => !otherFolders.Contains(name) && RenameTargetFree(rootPath, sourceDir, name));
            if (string.Equals(folderName, wanted, StringComparison.Ordinal))
            {
                return new FolderDecision(ImportFolderAction.Keep, folderName, folderName);
            }

            var targetDir = TargetDirOf(rootPath, wanted);
            // The same folder under another spelling on a case-insensitive filesystem is a rename:
            // merging it into itself would move nothing and then delete it.
            if (LibraryPaths.IsSameDirectory(sourceDir, targetDir) || !Directory.Exists(targetDir))
            {
                return new FolderDecision(ImportFolderAction.Rename, wanted, wanted);
            }

            return existing
                ? new FolderDecision(ImportFolderAction.Merge, wanted, wanted)
                : new FolderDecision(ImportFolderAction.Keep, folderName, folderName,
                    "error.libraryImport.renameTargetExists", new { name = wanted });
        }

        // The files stay in this folder, so it must not already be another series' own.
        if (otherFolders.Contains(folderName))
        {
            return new FolderDecision(ImportFolderAction.Keep, folderName, folderName,
                "error.libraryImport.folderOwnedByOtherSeries");
        }

        if (namingMode != FolderNamingMode.KeepOriginalNewStandard)
        {
            return new FolderDecision(ImportFolderAction.Keep, folderName, folderName);
        }

        // Existing files stay where they are; future downloads go into a separate, standard-named
        // folder that isn't created until something downloads into it.
        var seriesFolderName = SeriesCreationService.FreeFolderName(
            standardName, mangaBakaId,
            name => !otherFolders.Contains(name) &&
                    (LibraryPaths.FolderComparer.Equals(name, folderName) ||
                     (existing && LibraryPaths.FolderComparer.Equals(name, existingSeriesFolderName)) ||
                     !SeriesCreationService.HoldsComics(rootPath, name)));
        return new FolderDecision(ImportFolderAction.Keep, folderName, seriesFolderName);
    }

    private static string TargetDirOf(string rootPath, string name) =>
        LibraryPaths.Resolve(rootPath, name) ?? Path.Combine(rootPath, name);

    /// <summary>
    /// True when a rename of <paramref name="sourceDir"/> to <paramref name="name"/> would not land on
    /// another folder. The source folder itself counts as free, so a re-cased spelling renames in place.
    /// </summary>
    internal static bool RenameTargetFree(string rootPath, string sourceDir, string name)
    {
        var target = TargetDirOf(rootPath, name);
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
        var decision = DecideFolder(
            namingMode, standardName, series.MangaBakaId, rootFolder.Path, sourceDir, item.FolderName, otherFolders,
            existingSeriesFolderName: series.FolderName);
        if (decision.ErrorKey is not null)
        {
            return new ImportResult(item.FolderName, false, localizer.Get(decision.ErrorKey, decision.ErrorArgs));
        }

        var targetDir = sourceDir;
        var seriesFolderName = decision.SeriesFolderName;
        var warnings = new List<string>();
        var operations = new ImportOperations
        {
            FolderAction = decision.Action,
            PreviousSeriesFolderName = series.FolderName,
            PreviousRootFolderId = series.RootFolderId,
        };
        if (decision.Action == ImportFolderAction.Rename)
        {
            targetDir = TargetDirOf(rootFolder.Path, decision.TargetName);
            await events.ImportProgress(item.FolderName, ImportStage.RenamingFolder, operationId: operationId);
            SeriesRenameService.MovePath(sourceDir, targetDir, Directory.Move);
            logger.LogInformation("Renamed '{Old}' -> '{New}'", item.FolderName, decision.TargetName);
        }
        else if (decision.Action == ImportFolderAction.Merge)
        {
            // The series' standardized folder already exists (e.g. an empty folder created when it
            // was added), so fold the scanned folder's files into it.
            targetDir = TargetDirOf(rootFolder.Path, decision.TargetName);
            await events.ImportProgress(item.FolderName, ImportStage.MergingFolder, operationId: operationId);
            var leftBehind = MergeDirectory(sourceDir, targetDir, operations.Moved);
            logger.LogInformation("Merged '{Old}' into existing '{New}'", item.FolderName, decision.TargetName);
            if (leftBehind.Count > 0)
            {
                logger.LogWarning(
                    "Left {Count} files in '{Old}' whose names already exist in '{New}': {Files}",
                    leftBehind.Count, item.FolderName, decision.TargetName, string.Join(", ", leftBehind));
                warnings.Add(localizer.Get("error.libraryImport.mergeLeftFiles",
                    new { count = leftBehind.Count, folder = item.FolderName }));
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
            var (added, unreadableSkipped, deferredBuilt, deferredIds) = await RegisterAndDeferLinkAsync(
                series, item, targetDir, owedLink, operationId, ct, locked: true);
            operations.Built = deferredBuilt;
            operations.RegisteredFileIds = deferredIds;
            await RecordBatchFolderAsync(
                rootFolder.Id, operationId, series, created: false, item.FolderName, targetDir, operations);
            return new ImportResult(item.FolderName, true, null, series.Id, seriesFolderName,
                Warnings: warnings.Count > 0 ? warnings : null,
                Skipped: unreadableSkipped.Count > 0 ? unreadableSkipped : null,
                FilesAdded: added, LinkPending: true);
        }

        var (cbzFiles, unreadable, built) = MaterializeComics(targetDir);
        var linkStage = updateComicInfo ? ImportStage.UpdatingComicInfo : ImportStage.LinkingFiles;
        var (linked, unrecognized) = await cbzLinkService.LinkFilesAsync(
            series, targetDir, cbzFiles, "import",
            (current, total) => events.ImportProgress(item.FolderName, linkStage, current, total, operationId: operationId),
            updateComicInfo, ct: ct);
        var skipped = await SkippedFilesAsync(series.Id, targetDir, cbzFiles, unreadable, ct);
        operations.Built = BuiltFiles(targetDir, built);
        operations.RegisteredFileIds =
            await db.ChapterFiles.Where(f => f.SeriesId == series.Id).Select(f => f.Id).ToListAsync(ct);
        await RecordBatchFolderAsync(
            rootFolder.Id, operationId, series, created: false, item.FolderName, targetDir, operations);

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
    internal (List<string> Files, List<string> Unreadable, List<PlannedComic> Built) MaterializeComics(string targetDir)
    {
        var plan = PlanComics(targetDir);
        var files = new List<string>();
        var unreadable = plan.Unreadable.ToList();
        var built = new List<PlannedComic>();

        foreach (var comic in plan.Comics)
        {
            if (comic.Action is ImportFileAction.Register or ImportFileAction.UseExisting)
            {
                files.Add(comic.Target);
                continue;
            }

            // Read again rather than trusted from the plan: a name taken since is the user's file,
            // which must never be recorded as one Maki built.
            if (comic.Action == ImportFileAction.Build && File.Exists(comic.Target))
            {
                files.Add(comic.Target);
                continue;
            }

            try
            {
                ComicSourceConverter.Materialize(comic.Source, comic.Target);
                logger.LogInformation(
                    "Built {Target} from {Source}", comic.Source.Name, Path.GetFileName(comic.Source.Path));
                files.Add(comic.Target);
                built.Add(comic);
            }
            catch (Exception ex)
            {
                // One unreadable archive must not cost the folder its other files.
                logger.LogWarning(ex, "Could not build a CBZ from {Source}", comic.Source.Path);
                unreadable.Add(comic.Source.Entry is null ? comic.Source.Path : comic.Target);
            }
        }

        return (files, unreadable, built);
    }

    /// <param name="Target">The file the import registers, absolute.</param>
    /// <param name="Action">One of <see cref="ImportFileAction"/>.</param>
    /// <param name="Aside">For a rebuild in place, where the original is moved to.</param>
    internal sealed record PlannedComic(ComicSource Source, string Target, string Action, string? Aside);

    /// <param name="Unreadable">Archives and PDFs the scan read nothing out of.</param>
    /// <param name="Duplicates">Second copies of a comic that is used (an X.cbr beside X.cbz).</param>
    internal sealed record ComicPlan(
        List<PlannedComic> Comics, List<string> Unreadable, List<string> Duplicates);

    /// <summary>
    /// What <see cref="MaterializeComics"/> will do in <paramref name="dir"/>, decided without
    /// writing anything. The import runs exactly this plan and the preview shows it, so the two are
    /// one set of rules.
    /// </summary>
    internal static ComicPlan PlanComics(string dir)
    {
        var sources = ComicSourceScanner.Scan(dir);

        // An archive the scan read nothing out of drops from its list without a trace. One whose
        // name a found comic shares is a second copy of it (an X.cbr beside X.cbz), not a failure.
        var found = sources
            .SelectMany(s => new[] { s.Path, Path.Combine(dir, s.Name) })
            .Select(WithoutExtension)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var used = sources.Select(s => s.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unreadable = new List<string>();
        var duplicates = new List<string>();
        foreach (var file in LibraryPaths.EnumerateFilesNoLinks(dir)
                     .Where(f => ComicSourceScanner.IsArchive(f) || ComicFile.IsPdf(f))
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            if (!found.Contains(WithoutExtension(file)))
            {
                unreadable.Add(file);
            }
            else if (!used.Contains(file))
            {
                duplicates.Add(file);
            }
        }

        var comics = new List<PlannedComic>();
        foreach (var source in sources)
        {
            if (source.Kind is ComicSourceKind.Cbz or ComicSourceKind.Pdf)
            {
                comics.Add(new PlannedComic(source, source.Path, ImportFileAction.Register, null));
                continue;
            }

            // A 7z or RAR under a ".cbz" name targets its own path; Materialize rebuilds it in place.
            var target = Path.Combine(dir, source.Name);
            if (File.Exists(target) && !ComicSourceConverter.IsSameFile(target, source.Path))
            {
                comics.Add(new PlannedComic(source, target, ImportFileAction.UseExisting, null));
            }
            else if (source.Entry is null && ComicSourceConverter.IsSameFile(target, source.Path))
            {
                // Materialize refuses when the name the original would move to is taken, and the
                // import then reports the file as unreadable, so the plan does too.
                if (ComicSourceConverter.AsideFor(source.Path) is { } aside && !File.Exists(aside))
                {
                    comics.Add(new PlannedComic(source, target, ImportFileAction.RebuildInPlace, aside));
                }
                else
                {
                    unreadable.Add(source.Path);
                }
            }
            else
            {
                comics.Add(new PlannedComic(source, target, ImportFileAction.Build, null));
            }
        }

        return new ComicPlan(comics, unreadable, duplicates);
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
    internal static List<string> MergeDirectory(string sourceDir, string targetDir, List<string>? moved = null)
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
            moved?.Add(rel);
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
