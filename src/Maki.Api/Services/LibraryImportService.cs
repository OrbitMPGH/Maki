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
using Maki.Core.Storage;
using Maki.Core.Tags;
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
/// <param name="Kind">
/// One of <see cref="ImportCandidateKind"/>. An archived series' folder is a restore: importing
/// brings the series back into the library before linking.
/// </param>
/// <param name="MatchSource">
/// One of <see cref="ImportMatchSource"/>: what found the first match. Null when there is none.
/// </param>
/// <param name="MatchHint">The series name or web link the files carried, when that found the first match.</param>
public record ImportScanCandidate(
    string FolderName,
    string CleanedTitle,
    int ComicCount,
    int RecognizedCount,
    IReadOnlyList<MetadataSearchResult> Matches,
    int? ExistingSeriesId = null,
    string Kind = ImportCandidateKind.New,
    string? MatchSource = null,
    string? MatchHint = null,
    IReadOnlyList<string>? LooseFiles = null);

public static class ImportMatchSource
{
    /// <summary>A MangaBaka, AniList, MyAnimeList or MangaUpdates link in the files' ComicInfo.</summary>
    public const string Link = "link";

    public const string ComicInfo = FolderHintOrigin.ComicInfo;
    public const string SeriesJson = FolderHintOrigin.SeriesJson;

    /// <summary>The cleaned folder name.</summary>
    public const string Folder = "folder";

    /// <summary>The series already in the library that owns the folder.</summary>
    public const string Library = "library";
}

/// <param name="FolderName">
/// The folder relative to the root, <c>/</c>-separated: a series folder, one inside grouping folders
/// (<c>A-Z/Berserk</c>), or for <paramref name="LooseFiles"/> the folder they are gathered into.
/// </param>
/// <param name="LooseFiles">Comics directly in the root, moved into <paramref name="FolderName"/> by the import.</param>
public record ImportRequestItem(string FolderName, string MetadataProviderId, IReadOnlyList<string>? LooseFiles = null);

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
    SeriesArchiveService archive,
    ILogger<LibraryImportService> logger,
    LibraryImportResolver? resolver = null,
    AutoTagRuleService? autoTags = null)
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
        var dirs = ImportFolders(rootFolder.Path, claimed, ignored);
        var loose = LooseFileGroups(rootFolder.Path);
        var candidates = new ImportScanCandidate?[dirs.Count + loose.Count];

        // Listing a folder's archives and searching its title are independent per folder and touch
        // no DbContext, so a large root does not have to pay for them one at a time.
        await Parallel.ForEachAsync(
            Enumerable.Range(0, candidates.Length),
            new ParallelOptions { MaxDegreeOfParallelism = ScanConcurrency, CancellationToken = ct },
            async (index, itemCt) => candidates[index] = index < dirs.Count
                ? await ScanFolderAsync(dirs[index].Dir, dirs[index].Name, provider, withoutFiles, itemCt)
                : await ScanLooseGroupAsync(rootFolder.Path, loose[index - dirs.Count], provider, itemCt));

        return await ResolveByTopMatchAsync(candidates.OfType<ImportScanCandidate>().ToList(), ct);
    }

    /// <summary>
    /// A folder no series is named after can still be one the import fills: the import finds the
    /// series by the match's provider id, so the scan does too, and the row says restore the way the
    /// import will act on it. Only archived series are resolved here; an active file-less match keeps
    /// the row new, as before.
    /// </summary>
    private async Task<List<ImportScanCandidate>> ResolveByTopMatchAsync(
        List<ImportScanCandidate> candidates, CancellationToken ct)
    {
        static int? TopMatchId(ImportScanCandidate c) =>
            c.ExistingSeriesId is null && c.Matches.Count > 0 &&
            int.TryParse(c.Matches[0].ProviderId, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                ? id
                : null;

        var ids = candidates.Select(TopMatchId).OfType<int>().Distinct().ToList();
        if (ids.Count == 0)
        {
            return candidates;
        }

        var series = await db.Series.AsNoTracking()
            .Where(s => s.MangaBakaId != null && ids.Contains(s.MangaBakaId.Value) && s.ArchivedAt != null &&
                        !db.ChapterFiles.Any(f => f.SeriesId == s.Id))
            .ToListAsync(ct);
        var byId = series
            .GroupBy(s => s.MangaBakaId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.Id).First());
        return candidates
            .Select(c => TopMatchId(c) is { } id && byId.TryGetValue(id, out var existing)
                ? c with { ExistingSeriesId = existing.Id, Kind = KindOf(existing) }
                : c)
            .ToList();
    }

    private const int ScanConcurrency = 4;

    /// <summary>Grouping folders descended through below the root (<c>Publisher/A-Z/Series</c> is two).</summary>
    internal const int MaxGroupingDepth = 2;

    /// <param name="Name">Relative to the root, <c>/</c>-separated.</param>
    internal sealed record ImportFolder(string Dir, string Name);

    /// <summary>
    /// Every folder the scan offers: the root's own subfolders, and the subfolders of any grouping
    /// folder (<see cref="IsGroupingFolder"/>) down to <see cref="MaxGroupingDepth"/> levels.
    /// </summary>
    internal static List<ImportFolder> ImportFolders(
        string rootPath, IReadOnlySet<string> claimed, IReadOnlySet<string> ignored)
    {
        var found = new List<ImportFolder>();
        Walk(rootPath, null, 0);
        return found;

        void Walk(string dir, string? prefix, int depth)
        {
            foreach (var child in Directory.GetDirectories(dir).Order(StringComparer.Ordinal))
            {
                var leaf = Path.GetFileName(child);
                var name = prefix is null ? leaf : $"{prefix}/{leaf}";
                if (leaf.StartsWith('.') || LibraryPaths.IsLink(child) || ignored.Contains(name) ||
                    (prefix is null && claimed.Contains(leaf)))
                {
                    continue;
                }

                if (depth < MaxGroupingDepth && IsGroupingFolder(child))
                {
                    Walk(child, name, depth + 1);
                }
                else
                {
                    found.Add(new ImportFolder(child, name));
                }
            }
        }
    }

    /// <summary>
    /// A folder like <c>A-Z</c> or <c>Viz</c> that holds series folders rather than being one: no
    /// comic or image of its own, at least one subfolder, and no subfolder that is a chapter or volume
    /// of a series (a folder of loose pages, or most of them named like <c>Vol. 01</c>).
    /// </summary>
    internal static bool IsGroupingFolder(string dir)
    {
        try
        {
            if (Directory.EnumerateFiles(dir).Any(f =>
                    ComicSourceScanner.IsArchive(f) || ComicFile.IsPdf(f) || CbzReader.IsImage(f)))
            {
                return false;
            }

            var children = Directory.GetDirectories(dir)
                .Where(d => !Path.GetFileName(d).StartsWith('.') && !LibraryPaths.IsLink(d))
                .ToList();
            if (children.Count == 0 ||
                children.Any(d => Directory.EnumerateFiles(d).Any(CbzReader.IsImage)))
            {
                return false;
            }

            var numbered = children.Count(d => ReleaseNameParser.ParseFileName(Path.GetFileName(d)).IsRecognized);
            return numbered * 2 <= children.Count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <param name="FolderName">The folder they would be gathered into, free in the root.</param>
    /// <param name="Files">File names directly in the root.</param>
    internal sealed record LooseGroup(string FolderName, string SeriesName, List<string> Files);

    /// <summary>
    /// Comics sitting directly in the root, grouped by the series their names carry, each group with
    /// a folder name nothing in the root uses yet. Nothing moves until the group is imported.
    /// </summary>
    internal static List<LooseGroup> LooseFileGroups(string rootPath)
    {
        List<string> files;
        HashSet<string> taken;
        try
        {
            files = Directory.EnumerateFiles(rootPath)
                .Where(f => !Path.GetFileName(f).StartsWith('.') && !LibraryPaths.IsLink(f) &&
                            (ComicSourceScanner.IsArchive(f) || ComicFile.IsPdf(f)))
                .Order(StringComparer.Ordinal)
                .ToList();
            taken = Directory.GetFileSystemEntries(rootPath)
                .Select(Path.GetFileName)
                .OfType<string>()
                .ToHashSet(LibraryPaths.FolderComparer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var groups = new List<LooseGroup>();
        foreach (var group in files
                     .Select(f => (File: Path.GetFileName(f), Series: LooseSeriesName(Path.GetFileName(f))))
                     .Where(x => x.Series is not null)
                     .GroupBy(x => x.Series!, StringComparer.OrdinalIgnoreCase))
        {
            var name = group.First().Series!;
            var folder = name;
            for (var n = 2; taken.Contains(folder); n++)
            {
                folder = $"{name} ({n})";
            }

            taken.Add(folder);
            groups.Add(new LooseGroup(folder, name, group.Select(x => x.File).ToList()));
        }

        return groups;
    }

    private static readonly HashSet<char> UnsafeInFolderNames =
        [.. Path.GetInvalidFileNameChars(), '/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>The series a loose file's name carries, made safe as a folder name; null when it names none.</summary>
    internal static string? LooseSeriesName(string fileName)
    {
        var bare = ComicSourceScanner.IsArchive(fileName) || ComicFile.IsPdf(fileName)
            ? Path.GetFileNameWithoutExtension(fileName)
            : fileName;
        var parsed = ReleaseTitleParser.Parse(bare);
        var title = parsed.TitleWithoutTrailingNumber ?? parsed.TitleCandidates.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var safe = new string(title.Select(c => UnsafeInFolderNames.Contains(c) ? ' ' : c).ToArray());
        safe = string.Join(' ', safe.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim('.', ' ');
        return safe.Length == 0 ? null : safe;
    }

    private async Task<ImportScanCandidate?> ScanLooseGroupAsync(
        string rootPath, LooseGroup group, IMetadataProvider provider, CancellationToken ct)
    {
        var comics = new List<ComicSource>();
        foreach (var file in group.Files)
        {
            try
            {
                comics.AddRange(ComicSourceScanner.Scan(Path.Combine(rootPath, file)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Skipping unreadable file {File} in the import scan", file);
            }
        }

        if (comics.Count == 0)
        {
            return null;
        }

        var recognized = comics.Count(c => ReleaseNameParser.ParseFileName(c.Name).IsRecognized);
        // No series.json: the root's own would describe every loose file at once.
        var (matches, source, hint) = await FindMatchesAsync(null, comics, group.SeriesName, provider, ct);
        return new ImportScanCandidate(
            group.FolderName, group.SeriesName, comics.Count, recognized, matches, null, ImportCandidateKind.New,
            source, hint, group.Files);
    }

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

        if (!IsImportFolderPath(folderName))
        {
            return "error.libraryImport.invalidFolderName";
        }

        // The unique index compares exactly, while folder names ignore case, so "Extras" and
        // "extras" ignored at once would both get in without this.
        await IgnoreGate.WaitAsync(ct);
        try
        {
            if (!await IsIgnoredAsync(rootFolderId, folderName, ct))
            {
                await InsertIgnoredFolderAsync(rootFolderId, folderName, ct);
            }
        }
        finally
        {
            IgnoreGate.Release();
        }

        return null;
    }

    private static readonly SemaphoreSlim IgnoreGate = new(1, 1);

    private async Task<bool> IsIgnoredAsync(int rootFolderId, string folderName, CancellationToken ct) =>
        (await db.ImportIgnoredFolders
            .AsNoTracking()
            .Where(x => x.RootFolderId == rootFolderId)
            .Select(x => x.FolderName)
            .ToListAsync(ct))
        .Contains(folderName, LibraryPaths.FolderComparer);

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
            if (!await IsIgnoredAsync(rootFolderId, folderName, ct))
            {
                throw;
            }
        }
    }

    /// <summary>
    /// False when there was no such entry the caller may see. Also removes rows naming the same
    /// folder in another case, which the scan reads as the same folder and would keep skipping.
    /// </summary>
    public async Task<bool> UnignoreFolderAsync(int id, CancellationToken ct)
    {
        var row = await db.ImportIgnoredFolders.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (row is null || !CanSeeRoot(row.RootFolderId))
        {
            return false;
        }

        db.ImportIgnoredFolders.RemoveRange((await db.ImportIgnoredFolders
                .Where(x => x.RootFolderId == row.RootFolderId)
                .ToListAsync(ct))
            .Where(x => LibraryPaths.FolderComparer.Equals(x.FolderName, row.FolderName)));
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
        !folderName.Contains('\\') &&
        folderName.Trim('.', ' ').Length > 0;

    /// <summary>
    /// A plain folder name, or one inside up to <see cref="MaxGroupingDepth"/> grouping folders with
    /// every segment a plain name, so nothing taken off a request can walk out of the root.
    /// </summary>
    internal static bool IsImportFolderPath(string? folderName) =>
        !string.IsNullOrEmpty(folderName) &&
        folderName.Split('/') is var segments &&
        segments.Length <= MaxGroupingDepth + 1 &&
        segments.All(IsPlainFolderName);

    /// <summary>The last segment of an import folder path: the name it keeps once moved to the root.</summary>
    internal static string LeafOf(string folderName) => folderName[(folderName.LastIndexOf('/') + 1)..];

    internal static bool IsNested(string folderName) => folderName.Contains('/');

    private async Task<ImportScanCandidate?> ScanFolderAsync(
        string dir, string folderName, IMetadataProvider provider, IReadOnlyDictionary<string, Series> withoutFiles,
        CancellationToken ct)
    {
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
        var cleanedTitle = ReleaseNameParser.CleanFolderTitle(LeafOf(folderName));

        if (existing?.MangaBakaId is { } mangaBakaId)
        {
            // Importing matches the series by provider id, so offering anything else would
            // add a second copy instead of filling this one.
            IReadOnlyList<MetadataSearchResult> own =
            [
                new MetadataSearchResult(mangaBakaId.ToString(CultureInfo.InvariantCulture), existing.Title,
                    null, existing.Year, existing.Status, null, null)
            ];
            return new ImportScanCandidate(
                folderName, cleanedTitle, comics.Count, recognized, own, existing.Id, KindOf(existing),
                ImportMatchSource.Library);
        }

        var (matches, source, hint) = await FindMatchesAsync(dir, comics, cleanedTitle, provider, ct);
        return new ImportScanCandidate(
            folderName, cleanedTitle, comics.Count, recognized, matches, existing?.Id, KindOf(existing), source, hint);
    }

    private const int MaxMatches = 5;

    /// <summary>
    /// Candidates for one folder, best first: the series a web link in the files names (an exact id
    /// beats any title), then a search on the series name the files carry, then one on the folder name.
    /// </summary>
    private async Task<(IReadOnlyList<MetadataSearchResult> Matches, string? Source, string? Hint)> FindMatchesAsync(
        string? dir, IReadOnlyList<ComicSource> comics, string cleanedTitle, IMetadataProvider provider,
        CancellationToken ct)
    {
        var hints = FolderMetadataHints.Read(dir, comics);
        var found = new List<(MetadataSearchResult Match, string Source, string? Hint)>();

        if (resolver is not null && hints.WebLinks.Count > 0)
        {
            try
            {
                if (await resolver.ResolveLinksAsync(hints.WebLinks, ct) is var (linked, link))
                {
                    found.Add((linked, ImportMatchSource.Link, link));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not resolve the web links in {Folder}", dir);
            }
        }

        var hintName = hints.SeriesName is { } name &&
                       !string.Equals(ReleaseNameParser.CleanFolderTitle(name), cleanedTitle,
                           StringComparison.OrdinalIgnoreCase)
            ? name
            : null;
        if (hintName is not null)
        {
            found.AddRange((await SearchAsync(hintName)).Select(m => (m, hints.NameOrigin!, (string?)hintName)));
        }

        found.AddRange((await SearchAsync(cleanedTitle)).Select(m => (m, ImportMatchSource.Folder, (string?)null)));

        var distinct = found
            .GroupBy(f => f.Match.ProviderId, StringComparer.Ordinal)
            .Select(g => g.First())
            .Take(MaxMatches)
            .ToList();
        return distinct.Count == 0
            ? ([], null, null)
            : (distinct.Select(d => d.Match).ToList(), distinct[0].Source, distinct[0].Hint);

        async Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(string query)
        {
            try
            {
                // Deliberately unfiltered: this names folders that are already sitting in the
                // caller's own root folder, so a ceiling here hides nothing they cannot already see
                // and would instead leave those folders permanently unmatchable, with nothing on
                // screen to say why. The ceiling governs discovering new series, not adopting files.
                return (await provider.SearchAsync(query, ContentRating.Pornographic, ct)).Take(MaxMatches).ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Metadata search failed for {Title}", query);
                return [];
            }
        }
    }

    private static string KindOf(Series? existing) => existing switch
    {
        null => ImportCandidateKind.New,
        { ArchivedAt: not null } => ImportCandidateKind.Restore,
        _ => ImportCandidateKind.InLibrary,
    };

    /// <summary>
    /// What importing <paramref name="item"/> would do, worked out without writing anything: the
    /// folder decision and the comic plan the import itself runs, read rather than carried out.
    /// Takes no locks, so a folder another import is moving at the same moment can read differently
    /// when the import runs; the import re-decides under its locks either way.
    /// </summary>
    public async Task<LibraryImportPlan> PlanAsync(int rootFolderId, ImportRequestItem item, CancellationToken ct = default)
    {
        var (rootFolder, sourceDir, sourceError) = await ResolveSourceAsync(rootFolderId, item, ct);
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

        if (existingSeries?.ArchivedAt is not null && !currentUser.Has(MakiPermission.ArchiveSeries))
        {
            return new LibraryImportPlan(item.FolderName, localizer.Get("error.series.archived"));
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

        // Loose pages at the folder's root are named after the folder they are built in, which a
        // rename or merge has given the series' name by then.
        var plan = item.LooseFiles is { Count: > 0 } loose
            ? PlanLooseComics(rootFolder.Path, loose, sourceDir)
            : PlanComics(sourceDir,
                decision.Action is ImportFolderAction.Rename or ImportFolderAction.Merge ? decision.TargetName : null);
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

        // Only a new series gets a cover written into its folder, and never over one already there; see ImportAsync.
        var writesCover = existingSeries is null && metadata.CoverUrl is not null &&
                          !File.Exists(Path.Combine(sourceDir, LibraryCoverFileName)) &&
                          await appSettings.GetAsync(SettingKeys.LibraryWriteCoverToFolder, ct) == "true";
        return new LibraryImportPlan(
            item.FolderName, null, metadata.Title, existingSeries?.Id, decision.Action, decision.TargetName,
            decision.SeriesFolderName, linkDeferred, files, skipped, writesCover, KindOf(existingSeries),
            item.LooseFiles?.Count ?? 0);
    }

    /// <summary>The cover <see cref="CoverService.WriteLibraryCoverAsync(int, string, CancellationToken)"/> writes.</summary>
    internal const string LibraryCoverFileName = "cover.jpg";

    private static string ChapterLabel(Chapter chapter) =>
        chapter.Number?.ToString("0.###", CultureInfo.InvariantCulture) ?? chapter.Title ?? "?";

    public async Task<ImportResult> ImportAsync(
        int rootFolderId, ImportRequestItem item, bool updateComicInfo = true, string? operationId = null,
        CancellationToken ct = default)
    {
        var (rootFolder, sourceDir, sourceError) = await ResolveSourceAsync(rootFolderId, item, ct);
        if (sourceError is not null)
        {
            return new ImportResult(item.FolderName, false, localizer.Get(sourceError));
        }

        if (item.LooseFiles is not { Count: > 0 } loose)
        {
            return await ImportFolderAsync(rootFolder, sourceDir, item, updateComicInfo, operationId, [], ct);
        }

        // Moved into their folder only now the import is confirmed, and moved back when it ends
        // without keeping anything, so a refused or failed import leaves the root as it was.
        var gathered = new List<string>();
        ImportResult result;
        try
        {
            GatherLooseFiles(rootFolder.Path, sourceDir, loose, gathered);
            result = await ImportFolderAsync(rootFolder, sourceDir, item, updateComicInfo, operationId, gathered, ct);
        }
        catch
        {
            UngatherLooseFiles(rootFolder.Path, sourceDir, gathered);
            throw;
        }

        if (!result.Success && result.SeriesId is null)
        {
            UngatherLooseFiles(rootFolder.Path, sourceDir, gathered);
        }

        return result;
    }

    private async Task<ImportResult> ImportFolderAsync(
        RootFolder rootFolder, string sourceDir, ImportRequestItem item, bool updateComicInfo, string? operationId,
        IReadOnlyList<string> gathered, CancellationToken ct)
    {
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

        if (existingSeries?.ArchivedAt is not null)
        {
            if (await db.ChapterFiles.AnyAsync(f => f.SeriesId == existingSeries.Id, ct))
            {
                return new ImportResult(item.FolderName, false,
                    localizer.Get("error.libraryImport.alreadyInLibrary", new { title = metadata.Title }));
            }

            if (!currentUser.Has(MakiPermission.ArchiveSeries))
            {
                return new ImportResult(item.FolderName, false, localizer.Get("error.series.archived"));
            }
        }

        var owedLink = updateComicInfo ? PendingImportLink.LinkAndComicInfo : PendingImportLink.Link;
        if (existingSeries is not null)
        {
            ImportResult reimported;
            bool restoring;
            using (await SeriesLocks.SeriesAsync(existingSeries.Id, ct))
            {
                // Archived or restored by somebody else between the read above and the lock.
                await db.Entry(existingSeries).ReloadAsync(ct);
                if (await db.ChapterFiles.AnyAsync(f => f.SeriesId == existingSeries.Id, ct))
                {
                    return new ImportResult(item.FolderName, false,
                        localizer.Get("error.libraryImport.alreadyInLibrary", new { title = metadata.Title }));
                }

                // Linked into the archived row as it is, the files would sit in a series that never
                // syncs, so the import restores it once the files are in.
                restoring = existingSeries.ArchivedAt is not null;
                if (restoring && !currentUser.Has(MakiPermission.ArchiveSeries))
                {
                    return new ImportResult(item.FolderName, false, localizer.Get("error.series.archived"));
                }

                try
                {
                    reimported = await ReimportIntoExistingAsync(
                        existingSeries, rootFolder, item, sourceDir, updateComicInfo, operationId, restoring, gathered,
                        ct);
                }
                catch (Exception ex)
                {
                    return await RecoverFailedImportAsync(
                        existingSeries, item, existingSeries.FolderName, created: false, ex, owedLink);
                }
            }

            // A deferred link is still to come; the match that runs it syncs the series itself.
            if (restoring && reimported.Success && !reimported.LinkPending)
            {
                _ = archive.StartRestoreRefresh(existingSeries.Id);
            }

            return reimported;
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

            var (added, skipped, built, registered) =
                await RegisterAndDeferLinkAsync(series, item, targetDir, owedLink, operationId, ct);
            var wroteCover = !coverExisted && File.Exists(folderCover);
            await RecordBatchFolderAsync(rootFolder.Id, operationId, series, created: true, item.FolderName, targetDir,
                new ImportOperations
                {
                    FolderAction = decision.Action,
                    Built = built,
                    RegisteredFileIds = registered.Keys.ToList(),
                    RegisteredPaths = registered,
                    WroteCover = wroteCover,
                    CoverHashes = wroteCover && coverService.LibraryCoverHash(series.Id) is { } coverHash
                        ? [coverHash]
                        : [],
                    Gathered = [.. gathered],
                });

            // After registering rather than at the insert, so an import rolled back below leaves no
            // "added" behind it. Still after the orphan adoption, so a series removed and put back
            // reads as one continuous history.
            await stats.RecordAsync(StatsEventType.SeriesAdded, series.Id, series.Title, ct: ct);
            if (autoTags is not null)
            {
                await autoTags.ApplyOnAddAsync(series, AutoTagAddedVia.LibraryImport,
                    currentUser.IsAuthenticated ? currentUser.UserId : null, ct);
            }

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
    private async Task<(int Added, List<ImportSkippedFile> Unreadable, List<ImportBuiltFile> Built,
            Dictionary<int, string> Registered)>
        RegisterAndDeferLinkAsync(
            Series series, ImportRequestItem item, string targetDir, PendingImportLink owedLink, string? operationId,
            CancellationToken ct, bool locked = false, bool restore = false)
    {
        using var seriesLock = locked ? null : await SeriesLocks.SeriesAsync(series.Id, ct);
        await events.ImportProgress(item.FolderName, ImportStage.AddingFiles, operationId: operationId);
        var (cbzFiles, unreadable, built) = MaterializeComics(targetDir);
        var added = await cbzLinkService.RegisterFilesAsync(series, targetDir, cbzFiles, "import", ct);
        series.SourceMatchPending = true;
        series.PendingImportLink = owedLink;
        // In the save that sets the flags: the match worker and its startup recovery pass over an
        // archived series, so flags on one still archived would never be linked.
        if (restore)
        {
            archive.ClearArchive(series);
        }

        await db.SaveChangesAsync(ct);
        var registered = await RegisteredFilesAsync(series.Id, ct);
        sourceMatchQueue.Enqueue(series.Id, SourceMatchLane.Background);

        var skipped = unreadable
            .Select(f => new ImportSkippedFile(Path.GetRelativePath(targetDir, f), ImportSkipReason.Unreadable))
            .ToList();
        return (added, skipped, BuiltFiles(targetDir, built), registered);
    }

    /// <summary>Every row the series has, with its path: an import only ever runs into a series with no files.</summary>
    private Task<Dictionary<int, string>> RegisteredFilesAsync(int seriesId, CancellationToken ct) =>
        db.ChapterFiles.Where(f => f.SeriesId == seriesId).ToDictionaryAsync(f => f.Id, f => f.RelativePath, ct);

    private static List<ImportBuiltFile> BuiltFiles(string dir, IEnumerable<PlannedComic> built) =>
        built.Select(c => new ImportBuiltFile(
                Path.GetRelativePath(dir, c.Target),
                Path.GetRelativePath(dir, c.Source.Path),
                c.Source.Entry,
                c.Action,
                c.Aside is null ? null : Path.GetRelativePath(dir, c.Aside),
                JsonNamingPolicy.CamelCase.ConvertName(c.Source.Kind.ToString()),
                c.Source.Pages,
                ImportBuiltFile.DigestOf(c.Target)))
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
    /// The root folder, and the scanned folder inside it, for an item off the request. Returns the
    /// error key instead when the caller may not see the root, the name is not a folder path inside
    /// it, or the folder is gone. For loose files the folder must still be free and every file must
    /// still be a comic directly in the root.
    /// </summary>
    private async Task<(RootFolder Root, string SourceDir, string? ErrorKey)> ResolveSourceAsync(
        int rootFolderId, ImportRequestItem item, CancellationToken ct)
    {
        var rootFolder = CanSeeRoot(rootFolderId) ? await db.RootFolders.FindAsync([rootFolderId], ct) : null;
        if (rootFolder is null)
        {
            return (null!, null!, "error.series.rootFolderNotFound");
        }

        // FolderName comes straight off the request. Anything but a folder path inside the root could
        // move or rewrite files anywhere on disk the process can reach.
        var folderName = item.FolderName;
        var loose = item.LooseFiles is { Count: > 0 };
        var valid = loose ? IsPlainFolderName(folderName) : IsImportFolderPath(folderName);
        var sourceDir = valid ? LibraryPaths.ResolveNoLinks(rootFolder.Path, folderName) : null;
        if (sourceDir is null)
        {
            return (rootFolder, null!, "error.libraryImport.invalidFolderName");
        }

        if (!loose)
        {
            return Directory.Exists(sourceDir)
                ? (rootFolder, sourceDir, null)
                : (rootFolder, sourceDir, "error.libraryImport.folderGone");
        }

        if (SameVolumeMove.Occupied(sourceDir))
        {
            return (rootFolder, sourceDir, "error.libraryImport.renameTargetExists");
        }

        foreach (var file in item.LooseFiles!)
        {
            var path = IsPlainFolderName(file) ? LibraryPaths.ResolveNoLinks(rootFolder.Path, file) : null;
            if (path is null || !(ComicSourceScanner.IsArchive(path) || ComicFile.IsPdf(path)))
            {
                return (rootFolder, sourceDir, "error.libraryImport.invalidFolderName");
            }

            if (!File.Exists(path) || LibraryPaths.IsLink(path))
            {
                return (rootFolder, sourceDir, "error.libraryImport.folderGone");
            }
        }

        return (rootFolder, sourceDir, null);
    }

    /// <summary>
    /// Moves loose files from the root into <paramref name="dir"/>, recording each one moved. A
    /// rename within one folder never copies, so this is <see cref="SameVolumeMove"/> and refuses
    /// rather than replacing anything.
    /// </summary>
    internal static void GatherLooseFiles(string rootPath, string dir, IReadOnlyList<string> files, List<string> gathered)
    {
        Directory.CreateDirectory(dir);
        foreach (var file in files)
        {
            var from = Path.Combine(rootPath, file);
            var to = Path.Combine(dir, file);
            var outcome = SameVolumeMove.Move(from, to);
            if (outcome.Result != MoveResult.Moved && !SameVolumeMove.Landed(from, to))
            {
                throw new IOException($"Could not move {file} into {Path.GetFileName(dir)}", outcome.Error);
            }

            gathered.Add(file);
        }
    }

    /// <summary>
    /// Puts gathered files back in the root and removes the folder they were gathered into when that
    /// leaves it empty. A file whose root name was taken meanwhile stays where it is.
    /// </summary>
    internal void UngatherLooseFiles(string rootPath, string dir, List<string> gathered)
    {
        foreach (var file in gathered.ToList())
        {
            var outcome = SameVolumeMove.Move(Path.Combine(dir, file), Path.Combine(rootPath, file));
            if (outcome.Result == MoveResult.Moved)
            {
                gathered.Remove(file);
            }
            else
            {
                logger.LogWarning(outcome.Error, "Could not move {File} back out of {Folder}", file, dir);
            }
        }

        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir, recursive: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// <see cref="PlanComics"/> for loose files that are still in the root: planned as if they were
    /// already in <paramref name="dir"/>, which is where the import gathers them before it runs.
    /// </summary>
    internal static ComicPlan PlanLooseComics(string rootPath, IReadOnlyList<string> files, string dir)
    {
        var comics = new List<PlannedComic>();
        var unreadable = new List<string>();
        var duplicates = new List<string>();
        var sources = new List<ComicSource>();
        foreach (var file in files.Order(StringComparer.Ordinal))
        {
            var gatheredPath = Path.Combine(dir, file);
            var found = ComicSourceScanner.Scan(Path.Combine(rootPath, file));
            if (found.Count == 0)
            {
                unreadable.Add(gatheredPath);
            }

            sources.AddRange(found.Select(source => source with { Path = gatheredPath }));
        }

        foreach (var group in sources.GroupBy(c => Path.GetFileNameWithoutExtension(c.Name), StringComparer.OrdinalIgnoreCase))
        {
            var ordered = group.OrderBy(ComicSourceScanner.Preference).ToList();
            duplicates.AddRange(ordered.Skip(1).Select(c => c.Path).Where(p => p != ordered[0].Path));
            var source = ordered[0];
            if (source.Kind is ComicSourceKind.Cbz or ComicSourceKind.Pdf)
            {
                comics.Add(new PlannedComic(source, source.Path, ImportFileAction.Register, null));
                continue;
            }

            var target = Path.Combine(dir, source.Name);
            if (source.Entry is null && string.Equals(target, source.Path, StringComparison.OrdinalIgnoreCase))
            {
                if (ComicSourceConverter.AsideFor(source.Path) is { } aside &&
                    !sources.Any(s => string.Equals(s.Path, aside, StringComparison.OrdinalIgnoreCase)))
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

        return new ComicPlan(comics, unreadable, duplicates.Distinct().ToList());
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

        if (IsNested(folderName))
        {
            return DecideNestedFolder(
                namingMode, standardName, mangaBakaId, rootPath, sourceDir, folderName, otherFolders,
                existingSeriesFolderName);
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

    /// <summary>
    /// A folder inside grouping folders outside the rename mode. <see cref="Series.FolderName"/> is one
    /// folder directly in the root everywhere it is read (downloads, rescans, renames, the claimed
    /// check), so the import moves the folder up to the root under its own name rather than storing a
    /// nested path; under the rename mode <see cref="DecideFolder"/> already moves it there.
    /// </summary>
    private static FolderDecision DecideNestedFolder(
        string namingMode, string standardName, int? mangaBakaId, string rootPath, string sourceDir,
        string folderName, IReadOnlySet<string> otherFolders, string? existingSeriesFolderName)
    {
        var leaf = LeafOf(folderName);
        if (otherFolders.Contains(leaf))
        {
            return new FolderDecision(ImportFolderAction.Keep, folderName, folderName,
                "error.libraryImport.folderOwnedByOtherSeries");
        }

        if (!RenameTargetFree(rootPath, sourceDir, leaf))
        {
            return existingSeriesFolderName is not null &&
                   LibraryPaths.FolderComparer.Equals(leaf, existingSeriesFolderName)
                ? new FolderDecision(ImportFolderAction.Merge, leaf, leaf)
                : new FolderDecision(ImportFolderAction.Keep, folderName, folderName,
                    "error.libraryImport.renameTargetExists", new { name = leaf });
        }

        if (namingMode != FolderNamingMode.KeepOriginalNewStandard)
        {
            return new FolderDecision(ImportFolderAction.Rename, leaf, leaf);
        }

        var seriesFolderName = SeriesCreationService.FreeFolderName(
            standardName, mangaBakaId,
            name => !otherFolders.Contains(name) &&
                    (LibraryPaths.FolderComparer.Equals(name, leaf) ||
                     (existingSeriesFolderName is not null &&
                      LibraryPaths.FolderComparer.Equals(name, existingSeriesFolderName)) ||
                     !SeriesCreationService.HoldsComics(rootPath, name)));
        return new FolderDecision(ImportFolderAction.Rename, leaf, seriesFolderName);
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
    /// chapters exist to link against. With <paramref name="restore"/> the archived series is put
    /// back once its files are in, so a refusal or a failed link leaves it archived.
    /// </summary>
    private async Task<ImportResult> ReimportIntoExistingAsync(
        Series series, RootFolder rootFolder, ImportRequestItem item, string sourceDir,
        bool updateComicInfo, string? operationId, bool restore, IReadOnlyList<string> gathered, CancellationToken ct)
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
            Gathered = [.. gathered],
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
            if (series.RootFolderId != rootFolder.Id)
                await HealthInventory.MoveSeriesAsync(db, series.Id, rootFolder.Id, [], ct);
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
            var (added, unreadableSkipped, deferredBuilt, deferredFiles) = await RegisterAndDeferLinkAsync(
                series, item, targetDir, owedLink, operationId, ct, locked: true, restore: restore);
            operations.Built = deferredBuilt;
            operations.RegisteredFileIds = deferredFiles.Keys.ToList();
            operations.RegisteredPaths = deferredFiles;
            await RecordBatchFolderAsync(
                rootFolder.Id, operationId, series, created: false, item.FolderName, targetDir, operations);
            return new ImportResult(item.FolderName, true, null, series.Id, seriesFolderName,
                Warnings: warnings.Count > 0 ? warnings : null,
                Skipped: unreadableSkipped.Count > 0 ? unreadableSkipped : null,
                FilesAdded: added, LinkPending: true);
        }

        operations.PreviousFileRemovedAt = await db.Chapters
            .Where(c => c.SeriesId == series.Id && c.FileRemovedAt != null)
            .ToDictionaryAsync(c => c.Id, c => c.FileRemovedAt!.Value, ct);
        var (cbzFiles, unreadable, built) = MaterializeComics(targetDir);
        var linkStage = updateComicInfo ? ImportStage.UpdatingComicInfo : ImportStage.LinkingFiles;
        var (linked, unrecognized) = await cbzLinkService.LinkFilesAsync(
            series, targetDir, cbzFiles, "import",
            (current, total) => events.ImportProgress(item.FolderName, linkStage, current, total, operationId: operationId),
            updateComicInfo, ct: ct);
        var skipped = await SkippedFilesAsync(series.Id, targetDir, cbzFiles, unreadable, ct);
        operations.Built = BuiltFiles(targetDir, built);
        operations.RegisteredPaths = await RegisteredFilesAsync(series.Id, ct);
        operations.RegisteredFileIds = operations.RegisteredPaths.Keys.ToList();
        await RecordBatchFolderAsync(
            rootFolder.Id, operationId, series, created: false, item.FolderName, targetDir, operations);

        // Last, so anything above that throws leaves the series archived.
        if (restore)
        {
            await archive.RestoreUnderLockAsync(series, redownloadRemoved: false, CancellationToken.None);
        }

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
    /// <param name="rootName">See <see cref="ComicSourceScanner.Scan"/>; the import itself never passes it.</param>
    internal static ComicPlan PlanComics(string dir, string? rootName = null)
    {
        var sources = ComicSourceScanner.Scan(dir, rootName);

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
