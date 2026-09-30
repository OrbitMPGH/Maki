using System.Globalization;
using System.Text.Json;
using Maki.Core.Configuration;
using Maki.Core.Download;
using Maki.Core.Entities;
using Maki.Core.Import;
using Maki.Core.Inbox;
using Maki.Core.Parsing;
using Maki.Core.Paths;
using Maki.Core.Quality;
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
/// <param name="UpgradeCount">For an upgrade download, covered chapters whose file this improves on.</param>
/// <param name="AlreadyMetCount">For an upgrade download, covered chapters whose file is already as good or protected.</param>
public record ImportPlanFile(
    string FileName,
    long Size,
    string? Label,
    IReadOnlyList<string> Chapters,
    IReadOnlyList<string> NewChapters,
    IReadOnlyList<ImportPlanExisting> Replaces,
    int UpgradeCount = 0,
    int AlreadyMetCount = 0);

/// <param name="Reason"><c>unmeasurable</c> or <c>fewer_pages</c>.</param>
public record VolumeGuardFailure(string File, string Reason);

/// <param name="Park">Leave it for the user: it would displace files, or the guard held it back.</param>
/// <param name="Guard">Set when the upgrade guard is why it parked; the item is already parked.</param>
/// <param name="SkipFiles">For an unattended upgrade import, the files to leave out.</param>
public record UnattendedDecision(bool Park, VolumeGuardFailure? Guard, IReadOnlySet<string>? SkipFiles);

public sealed record TakenChapter(int ChapterId, int? PreviousFileId);

/// <summary>
/// What a grouped <c>UpgradeHistory</c> row needs to rebuild the superseded file's row on revert.
/// Stored as <c>UpgradeHistory.DetailJson</c>.
/// </summary>
public sealed class VolumeReplacementDetail
{
    public string RelativePath { get; set; } = "";
    public List<int> ChapterIds { get; set; } = [];
    public int ReplacementFileId { get; set; }

    /// <summary>The file each of <see cref="ChapterIds"/> moved to, in the same order; a file split across two volumes names both.</summary>
    public List<int> ReplacementFileIds { get; set; } = [];

    public DateTime DateAdded { get; set; }

    /// <summary>
    /// Every chapter the group's imported files took, with the file it read from before (null when it
    /// had none). The same list sits on each row of a group, so losing one row loses nothing.
    /// </summary>
    public List<TakenChapter> TakenChapters { get; set; } = [];

    public int ReplacementFor(int index) =>
        index < ReplacementFileIds.Count && ReplacementFileIds[index] != 0 ? ReplacementFileIds[index] : ReplacementFileId;
    public bool Trusted { get; set; }

    public string Serialize() => JsonSerializer.Serialize(this, Maki.Core.Quality.QualitySnapshot.Json);

    public static VolumeReplacementDetail? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<VolumeReplacementDetail>(json, Maki.Core.Quality.QualitySnapshot.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <param name="ErrorKey">
/// Catalogue key, set when the plan could not be built at all (download path gone, say). Never
/// English prose: this reaches the queue's stored, render-at-read-time error columns as well as a
/// request that wants it worded immediately, so the caller decides how and when to render it.
/// </param>
/// <param name="ErrorArgs">Placeholders for <paramref name="ErrorKey"/>, or null when it has none.</param>
public record TorrentImportPlan(
    int QueueItemId,
    int SeriesId,
    string SeriesTitle,
    string ReleaseName,
    IReadOnlyList<ImportPlanFile> Files,
    string? ErrorKey = null,
    object? ErrorArgs = null)
{
    /// <summary>Whether importing this download would take chapters off files the library has.</summary>
    public bool HasConflicts => Files.Any(f => f.Replaces.Count > 0);

    public int NewChapterCount => Files.SelectMany(f => f.NewChapters).Distinct().Count();

    public int ReplacedFileCount => Files.SelectMany(f => f.Replaces).Select(r => r.ChapterFileId).Distinct().Count();

    /// <summary>The item carries a <c>TorrentUpgradeInfo</c>.</summary>
    public bool IsUpgrade { get; init; }

    /// <summary>For an upgrade, the files whose chapters are all already at cutoff or protected.</summary>
    public IReadOnlyList<string> SuggestedSkips { get; init; } = [];
}

/// <param name="Deleted">Superseded files moved to the trash, under <see cref="TorrentImportMode.Replace"/>.</param>
/// <param name="Skipped">Downloaded files left alone because they brought nothing new.</param>
/// <param name="Error">
/// Raw text: an exception message from reading somebody else's archive. Null when <see cref="ErrorKey"/>
/// carries a Maki-worded failure instead; the two are mutually exclusive, same split as
/// <c>DownloadQueueItem.ErrorKey</c>/<c>ErrorMessage</c>.
/// </param>
/// <param name="ErrorKey">Catalogue key for a failure Maki worded, or null. See <see cref="Error"/>.</param>
/// <param name="ErrorArgs">Placeholders for <paramref name="ErrorKey"/>, or null when it has none.</param>
public record TorrentImportOutcome(
    bool Applied, string? Error, int Imported, int Linked, int Unrecognized, int Deleted, int Skipped,
    IReadOnlyList<string> ImportedPaths, string? ErrorKey = null, object? ErrorArgs = null, Guid? HistoryGroupId = null);

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
    UpgradeEvaluationService evaluation,
    InboxService inbox,
    ILogger<TorrentImportService> logger)
{
    public const int GuardSamplePages = 6;

    // Queue ids a request is importing right now (QueueController.Import). The poll job skips those,
    // and treats any other row reading Importing as one whose request died with the process.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> ManualImports = new();

    public static bool TryBeginManualImport(int queueItemId) => ManualImports.TryAdd(queueItemId, 0);

    public static void EndManualImport(int queueItemId) => ManualImports.TryRemove(queueItemId, out _);

    public static bool IsManualImportRunning(int queueItemId) => ManualImports.ContainsKey(queueItemId);

    // Queue ids CompletedDownloadJob is importing. That path never persists Importing (the poll would
    // reset it), so the delete guards read this instead of the row.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> AutomaticImports = new();

    public static void BeginAutomaticImport(int queueItemId) => AutomaticImports.TryAdd(queueItemId, 0);

    public static void EndAutomaticImport(int queueItemId) => AutomaticImports.TryRemove(queueItemId, out _);

    public static int[] AutomaticImportIds() => AutomaticImports.Keys.ToArray();

    /// <summary>The series was deleted or moved between planning and taking its lock.</summary>
    public const string SeriesChangedKey = "error.torrentImport.seriesChanged";

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
            return Empty("error.torrentImport.notInQbittorrent");
        }

        if (!Directory.Exists(contentPath) && !File.Exists(contentPath))
        {
            return Empty("error.torrentImport.pathNotAccessible", new { path = contentPath });
        }

        var sources = ComicSourceScanner.Scan(contentPath);
        if (sources.Count == 0)
        {
            // Naming what was actually there: "no comics found" on its own reads exactly like a
            // download that arrived empty, and the two want completely different things done.
            // {detail} is ComicSourceScanner's own summary of what it found and is not translated.
            return Empty("error.torrentImport.noComicsFound",
                new { detail = ComicSourceScanner.Describe(contentPath) });
        }

        var upgradeInfo = TorrentUpgradeInfo.Parse(item.UpgradeInfoJson);
        var chapters = (await db.Chapters
            .Where(c => c.SeriesId == series.Id)
            .ToListAsync(ct))
            .Where(c => upgradeInfo?.Language is not { } language || ChapterFileLanguage.Of(c) == language)
            .ToList();
        var existingFiles = await db.ChapterFiles
            .Where(f => f.SeriesId == series.Id)
            .ToListAsync(ct);

        var isUpgrade = upgradeInfo is not null;
        var evaluator = isUpgrade ? await evaluation.ForSeriesAsync(series.Id, ct) : null;
        var releaseInfo = ReleaseInfoOf(item);
        var titleGroup = ReleaseTitleParser.Parse(releaseName).Group;
        var suggestedSkips = new List<string>();

        // An upgrade's verdict may name volume files, and the linker takes chapters off exactly those.
        var displaceable = upgradeInfo?.ReplacedFileIds ?? [];
        var volumeFileIds = existingFiles
            .Where(f => !displaceable.Contains(f.Id) && ReleaseNameParser.ParseFileName(f.RelativePath).IsVolume)
            .Select(f => f.Id)
            .ToHashSet();

        var files = new List<ImportPlanFile>();
        foreach (var source in sources.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            var parsed = ReleaseNameParser.ParseFileName(source.Name);
            var covered = ChaptersCoveredBy(chapters, parsed, source.Pages, volumeFileIds);

            var upgradeCount = 0;
            var alreadyMetCount = 0;
            if (evaluator is not null)
            {
                var candidate = evaluator.Score(TorrentUpgradeRules.Candidate(
                    releaseName, releaseInfo?.Indexer ?? "", titleGroup, parsed.IsVolume));
                var states = covered
                    .Select(c => TorrentUpgradeRules.StateOf(evaluator, c,
                        existingFiles.FirstOrDefault(f => f.Id == c.ChapterFileId), candidate))
                    .ToList();
                upgradeCount = states.Count(s => s == ChapterSpanState.Upgrade);
                alreadyMetCount = states.Count(s => s == ChapterSpanState.AlreadyMet);
                if (states.Count > 0 && states.All(s => s == ChapterSpanState.AlreadyMet))
                {
                    suggestedSkips.Add(source.Name);
                }
            }

            // With a displaceable set the linker leaves every other file its chapters, so only those count.
            var replaces = covered
                .Where(c => c.ChapterFileId is { } fileId && (displaceable.Count == 0 || displaceable.Contains(fileId)))
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
                source.Name,
                source.Size,
                ParsedLabel(parsed),
                covered.Select(Label).ToList(),
                covered.Where(c => c.ChapterFileId == null).Select(Label).ToList(),
                replaces,
                upgradeCount,
                alreadyMetCount));
        }

        return new TorrentImportPlan(item.Id, series.Id, series.Title, releaseName, files)
        {
            IsUpgrade = isUpgrade,
            SuggestedSkips = suggestedSkips
        };

        TorrentImportPlan Empty(string errorKey, object? args = null) =>
            new(item.Id, series.Id, series.Title, releaseName, [], errorKey, args);
    }

    /// <summary>
    /// Places the download's files in the series folder, links them, names them, and under
    /// <see cref="TorrentImportMode.Replace"/> deletes the files left backing nothing.
    /// </summary>
    /// <param name="plan">
    /// A plan the caller already built for this same item and content path, reused rather than
    /// rebuilt. <see cref="PlanAsync"/> opens and scans the page names of every volume archive in
    /// the download, so a caller that planned in order to decide whether to call this at all would
    /// otherwise pay for that walk twice. Null plans here.
    /// </param>
    /// <param name="skipFiles">Downloaded file names to leave out entirely, whatever the mode.</param>
    public async Task<TorrentImportOutcome> ImportAsync(
        DownloadQueueItem item, Series series, string? contentPath, TorrentImportMode mode,
        CancellationToken ct, TorrentImportPlan? plan = null, IReadOnlySet<string>? skipFiles = null)
    {
        plan ??= await PlanAsync(item, series, contentPath, ct);
        if (plan.ErrorKey is not null)
        {
            return new TorrentImportOutcome(false, null, 0, 0, 0, 0, 0, [], plan.ErrorKey, plan.ErrorArgs);
        }

        var rootFolder = series.RootFolder
            ?? await db.RootFolders.FirstOrDefaultAsync(r => r.Id == series.RootFolderId, ct);
        if (rootFolder is null)
        {
            return new TorrentImportOutcome(false, null, 0, 0, 0, 0, 0, [], "error.torrentImport.noRootFolder");
        }

        var wanted = (mode == TorrentImportMode.Replace
                ? plan.Files
                // Nothing new and something to lose: the file is exactly what the library already has.
                : plan.Files.Where(f => f.Replaces.Count == 0 || f.NewChapters.Count > 0))
            .Where(f => skipFiles is null || !skipFiles.Contains(f.FileName))
            .ToList();
        var skipped = plan.Files.Count - wanted.Count;
        if (wanted.Count == 0)
        {
            if (TorrentUpgradeInfo.Parse(item.UpgradeInfoJson) is { } nothingImported)
            {
                nothingImported.Outcome = TorrentUpgradeOutcomes.Applied;
                nothingImported.HistoryGroupId = null;
                item.UpgradeInfoJson = nothingImported.Serialize();
            }

            return new TorrentImportOutcome(true, null, 0, 0, 0, 0, skipped, []);
        }

        // Safe to key on the name: the scan already returns one comic per name, which is the same
        // set PlanAsync named its rows after.
        var byName = ComicSourceScanner.Scan(contentPath!)
            .ToDictionary(s => s.Name, s => s, StringComparer.Ordinal);
        var sourceFiles = wanted
            .Select(f => byName.GetValueOrDefault(f.FileName))
            .Where(s => s is not null)
            .Select(s => s!)
            .ToList();

        // Never a move — qBittorrent keeps seeding from where it downloaded. A hardlink gives the
        // library its own name for the same bytes; the copy is the fallback when the two folders
        // can't share an inode (different volumes, a share, a filesystem without hardlinks).
        var seriesDir = Path.Combine(rootFolder.Path, series.FolderName);
        var useHardlinks = await settings.GetAsync(SettingKeys.DownloadUseHardlinks, ct) != "false";
        var writeComicInfoSetting = await settings.GetAsync(SettingKeys.LibraryWriteComicInfo, ct) != "false";

        // From the first file placed through the trash moves: a plain copy lands at its final name, so
        // a rescan running beside it would adopt a half-written archive. Released before the inbox row.
        using var seriesLock = await SeriesLocks.SeriesAsync(series.Id, ct);

        // Everything above was read without the lock. A delete that ran meanwhile would get its
        // folder recreated and filled with files no row can own.
        var current = await db.Series.IgnoreQueryFilters()
            .Where(s => s.Id == series.Id)
            .Select(s => new { s.FolderName, s.RootFolderId })
            .FirstOrDefaultAsync(ct);
        if (current is null || current.FolderName != series.FolderName || current.RootFolderId != series.RootFolderId)
        {
            logger.LogWarning("Not importing '{Title}': series {SeriesId} was deleted or moved while it was planned",
                item.Title, series.Id);
            return new TorrentImportOutcome(false, null, 0, 0, 0, 0, skipped, [], SeriesChangedKey);
        }

        Directory.CreateDirectory(seriesDir);

        var imported = new List<string>();
        var hardlinked = 0;
        var freshCopies = 0;
        foreach (var source in sourceFiles)
        {
            var target = Path.Combine(seriesDir, source.Name);
            if (!File.Exists(target))
            {
                try
                {
                    // A CBZ or a zip under another name is placed as it is and can still be
                    // hardlinked; a RAR or a folder of loose pages is built into a new archive,
                    // which is a fresh copy however the setting reads.
                    if (ComicSourceConverter.Materialize(source, target, useHardlinks)
                        == FilePlacement.Hardlinked)
                    {
                        hardlinked++;
                    }
                    else
                    {
                        freshCopies++;
                    }
                }
                catch (Exception ex)
                {
                    // Deliberately every exception: reading somebody else's archive is the part of
                    // an import most likely to throw something unforeseen, and a named file in the
                    // outcome beats an unhandled failure inside the completed-download job.
                    return new TorrentImportOutcome(
                        false, $"Could not import {source.Name}: {ex.Message}", 0, 0, 0, 0, skipped, []);
                }
            }

            imported.Add(target);
        }

        // Which file each chapter reads from before anything is linked, so the files this import
        // actually supersedes can be told apart from ones that were already spare.
        var backedBefore = await db.Chapters
            .Where(c => c.SeriesId == series.Id && c.ChapterFileId != null)
            .Select(c => new BackedChapter(c.Id, c.Number, c.Language, c.ChapterFileId!.Value))
            .ToListAsync(ct);
        var upgrade = TorrentUpgradeInfo.Parse(item.UpgradeInfoJson);

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
        var writeComicInfo = writeComicInfoSetting && freshCopies == imported.Count;
        var (linked, unrecognized) = await cbzLinkService.LinkFilesAsync(
            series, seriesDir, imported, $"torrent:{ReleaseInfoOf(item)?.Indexer}",
            updateComicInfo: writeComicInfo, releaseName: ReleaseInfoOf(item)?.Title ?? item.Title,
            replaceExisting: mode == TorrentImportMode.Replace, ct: ct,
            // An upgrade may only take chapters off the files its verdict marked; a protected or
            // already good file inside the volume's span keeps its chapter.
            displaceableFileIds: mode == TorrentImportMode.Replace && upgrade is { ReplacedFileIds.Count: > 0 }
                ? upgrade.ReplacedFileIds.ToHashSet()
                : null,
            language: upgrade?.Language);

        var importedRows = await ImportedRowsAsync(series, imported, ct);
        var hash = ReleaseInfoOf(item)?.TorrentHash;
        foreach (var row in importedRows)
        {
            if (hash is not null)
            {
                row.ReleaseHash = hash;
            }

            archives.Invalidate(row.Id);
        }

        await db.SaveChangesAsync(ct);

        var (deleted, groupId) = mode == TorrentImportMode.Replace
            ? await TrashSupersededAsync(series, rootFolder.Path, backedBefore, item,
                importedRows.Select(r => r.Id).ToHashSet(), ct)
            : (0, null);
        seriesLock.Dispose();

        if (upgrade is not null)
        {
            upgrade.Outcome = TorrentUpgradeOutcomes.Applied;
            upgrade.HistoryGroupId = groupId;
            item.UpgradeInfoJson = upgrade.Serialize();
            if (groupId is not null && item.QueuedByUserId is null)
            {
                inbox.RaiseForSeries(InboxEventType.VolumeUpgraded, new InboxMessage(
                    Key: "inbox.upgrade.volume",
                    Params: InboxMessage.Args(new
                    {
                        fileName = Path.GetFileName(imported[0]),
                        replaced = deleted
                    }),
                    SeriesId: series.Id,
                    Url: $"/series/{series.Id}"), series.Id);
            }
        }

        logger.LogInformation(
            "Imported torrent '{Title}': {Files} file(s) ({Hardlinked} hardlinked), {Linked} linked to chapters, " +
            "{Unrecognized} unrecognized, {Skipped} skipped, {Deleted} superseded file(s) moved to the trash",
            item.Title, imported.Count, hardlinked, linked, unrecognized, skipped, deleted);
        if (hardlinked > 0)
        {
            logger.LogInformation(
                "Left ComicInfo.xml untouched in '{Title}': the imported file(s) are hardlinks and still seeding",
                item.Title);
        }

        return new TorrentImportOutcome(
            true, null, imported.Count, linked, unrecognized, deleted, skipped, imported, HistoryGroupId: groupId);
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

        // RenameFilesAsync takes no lock of its own: the public RenameAsync holds it around the same code.
        SeriesRenameResult result;
        using (await SeriesLocks.SeriesAsync(series.Id, ct))
        {
            var fileIds = await db.ChapterFiles
                .Where(f => f.SeriesId == series.Id && relativePaths.Contains(f.RelativePath))
                .Select(f => f.Id)
                .ToListAsync(ct);
            result = await seriesRenameService.RenameFilesAsync(series.Id, fileIds, ct);
        }

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

    private sealed record BackedChapter(int ChapterId, decimal? Number, string? Language, int FileId);

    private async Task<List<ChapterFile>> ImportedRowsAsync(Series series, IReadOnlyList<string> importedPaths, CancellationToken ct)
    {
        var relativePaths = importedPaths
            .Select(path => Path.Combine(series.FolderName, Path.GetFileName(path)))
            .ToList();
        return await db.ChapterFiles
            .Where(f => f.SeriesId == series.Id && relativePaths.Contains(f.RelativePath))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Moves the files this import left backing nothing into the trash: every chapter that read from
    /// them now reads from an imported file instead. A file that was already spare before the import
    /// (an adopted archive nothing matched, an extra) is untouched; it wasn't superseded, it was just
    /// never used. Each moved file gets an <c>UpgradeHistory</c> row, all sharing one group id, so the
    /// whole import can be reverted. This is the one code path that trashes a user's file without
    /// them naming it, hence <c>LibraryPaths.Resolve</c> and the per-file failure handling. A move is
    /// a rename, so a hardlinked file keeps its bytes and the torrent keeps seeding.
    /// </summary>
    private async Task<(int Trashed, Guid? GroupId)> TrashSupersededAsync(
        Series series, string rootPath, IReadOnlyList<BackedChapter> backedBefore, DownloadQueueItem item,
        IReadOnlySet<int> importedFileIds, CancellationToken ct)
    {
        var backedNow = await db.Chapters
            .Where(c => c.SeriesId == series.Id && c.ChapterFileId != null)
            .Select(c => new { c.Id, FileId = c.ChapterFileId!.Value })
            .ToDictionaryAsync(c => c.Id, c => c.FileId, ct);

        var nowFiles = backedNow.Values.ToHashSet();
        var superseded = backedBefore.Select(b => b.FileId).Distinct().Where(id => !nowFiles.Contains(id)).ToList();
        if (superseded.Count == 0)
        {
            return (0, null);
        }

        var rows = await db.ChapterFiles
            .Where(f => f.SeriesId == series.Id && superseded.Contains(f.Id))
            .ToListAsync(ct);
        var evaluator = await evaluation.ForSeriesAsync(series.Id, ct);
        var releaseInfo = ReleaseInfoOf(item);
        var groupId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var afterByFile = new Dictionary<int, QualitySnapshot>();

        // Every chapter an imported file took and where it read from before, so a revert can hand a
        // chapter back to a file this import did not supersede.
        var beforeById = backedBefore.ToDictionary(b => b.ChapterId, b => b.FileId);
        var taken = backedNow
            .Where(c => importedFileIds.Contains(c.Value) && beforeById.GetValueOrDefault(c.Key) != c.Value)
            .Select(c => new TakenChapter(c.Key, beforeById.TryGetValue(c.Key, out var previous) ? previous : null))
            .ToList();

        // The history row is saved before the file moves and taken back if the move fails, so the
        // trash never holds a file without a row; once the first row is written nothing is cancelled.
        var trashed = 0;
        foreach (var row in rows)
        {
            var backed = backedBefore.Where(b => b.FileId == row.Id)
                .OrderBy(b => b.Number is null).ThenBy(b => b.Number).ThenBy(b => b.ChapterId)
                .ToList();
            var replacements = backed.Select(b => backedNow.GetValueOrDefault(b.ChapterId)).ToList();
            var replacementId = replacements.FirstOrDefault(id => id != 0);

            // SQLite reuses rowids after a delete, so a later adopt can land on this id with a
            // different archive behind it and the cache's size guard would not notice.
            archives.Invalidate(row.Id);
            var path = LibraryPaths.Resolve(rootPath, row.RelativePath);
            if (path is null || !File.Exists(path))
            {
                // Already gone from disk: nothing to put aside or bring back, so no history row.
                db.ChapterFiles.Remove(row);
                await db.SaveChangesAsync(CancellationToken.None);
                continue;
            }

            UpgradeTrash.EnsureFolder(rootPath, series.Id);
            var trashRelative = UpgradeTrash.NewRelativePath(rootPath, series.Id,
                row.Id.ToString(CultureInfo.InvariantCulture), Path.GetFileName(row.RelativePath));
            var trashPath = LibraryPaths.Resolve(rootPath, trashRelative)!;

            var beforeScore = evaluator?.Evaluate(row, backed[0].Language) is { } current ? current.Score.Score : 0;
            if (!afterByFile.TryGetValue(replacementId, out var after))
            {
                after = await AfterSnapshotAsync(rootPath, replacementId, backed[0].Language, releaseInfo, evaluator,
                    CancellationToken.None);
                afterByFile[replacementId] = after;
            }

            var history = new UpgradeHistory
            {
                SeriesId = series.Id,
                ChapterId = backed[0].ChapterId,
                ChapterFileId = row.Id,
                QueueItemId = item.Id == 0 ? null : item.Id,
                ProfileId = evaluator?.Profile.Id ?? 0,
                ProfileVersion = evaluator?.Profile.Version ?? 0,
                QueuedByUserId = item.QueuedByUserId,
                BeforeJson = UpgradeEvaluator.Snapshot(row, beforeScore).Serialize(),
                AfterJson = after.Serialize(),
                TrashPath = trashRelative,
                TrashBytes = new FileInfo(path).Length,
                CreatedAtUtc = now,
                GroupId = groupId,
                DetailJson = new VolumeReplacementDetail
                {
                    RelativePath = row.RelativePath,
                    ChapterIds = [.. backed.Select(b => b.ChapterId)],
                    ReplacementFileId = replacementId,
                    ReplacementFileIds = replacements,
                    DateAdded = row.DateAdded,
                    Trusted = row.Trusted,
                    TakenChapters = taken
                }.Serialize()
            };
            db.UpgradeHistory.Add(history);
            db.ChapterFiles.Remove(row);
            await db.SaveChangesAsync(CancellationToken.None);

            if (!await UpgradeTrash.MoveIntoTrashAsync(path, trashPath, logger, CancellationToken.None))
            {
                logger.LogWarning("Could not move superseded file {Path} to the trash", row.RelativePath);
                db.UpgradeHistory.Remove(history);
                db.ChapterFiles.Add(row);
                await db.SaveChangesAsync(CancellationToken.None);
                continue;
            }

            trashed++;
            logger.LogInformation("Moved superseded file {Path} for '{Title}' to the trash", row.RelativePath, series.Title);
        }

        return trashed > 0 ? (trashed, (Guid?)groupId) : (0, null);
    }

    private async Task<QualitySnapshot> AfterSnapshotAsync(
        string rootPath, int fileId, string? language, ReleaseInfo? releaseInfo, UpgradeEvaluator? evaluator,
        CancellationToken ct)
    {
        var file = await db.ChapterFiles.FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (file is null)
        {
            return new QualitySnapshot { SourceName = $"torrent:{releaseInfo?.Indexer}", ReleaseName = releaseInfo?.Title };
        }

        var snapshot = UpgradeEvaluator.Snapshot(file,
            evaluator?.Score(evaluator.CandidateFor(file, language)).Score ?? 0);
        snapshot.ReleaseHash = releaseInfo?.TorrentHash ?? file.ReleaseHash;
        if (LibraryPaths.Resolve(rootPath, file.RelativePath) is { } path && File.Exists(path))
        {
            try
            {
                var measured = ChapterFileMeasurer.MeasureArchive(path, GuardSamplePages, ct);
                snapshot.PageCount = measured.PageCount;
                snapshot.MedianWidth = measured.MedianWidth;
                snapshot.MedianHeight = measured.MedianHeight;
                snapshot.ImageFormat = measured.ImageFormat;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Could not measure {Path}", path);
            }
        }

        return snapshot;
    }

    /// <summary>
    /// The check an upgrade download passes before it may replace anything on its own: every volume
    /// file it would import must be measurable and carry at least as many pages as the single-chapter
    /// files it replaces, less the profile's page tolerance. Null when it passes or the item is not an
    /// upgrade. A failure parks the item for the user rather than importing it.
    /// </summary>
    public async Task<VolumeGuardFailure?> CheckVolumeGuardAsync(
        DownloadQueueItem item, Series series, string contentPath, TorrentImportPlan plan, CancellationToken ct)
    {
        if (TorrentUpgradeInfo.Parse(item.UpgradeInfoJson) is not { } upgrade)
        {
            return null;
        }

        var byName = ComicSourceScanner.Scan(contentPath).ToDictionary(s => s.Name, s => s, StringComparer.Ordinal);
        var files = await db.ChapterFiles.AsNoTracking().Where(f => f.SeriesId == series.Id).ToDictionaryAsync(f => f.Id, ct);
        var tolerance = (await evaluation.ForSeriesAsync(series.Id, ct))?.Profile.PageTolerancePercent ?? 10;
        var replaced = upgrade.ReplacedFileIds.ToHashSet();
        var skipped = plan.SuggestedSkips.ToHashSet(StringComparer.Ordinal);

        // Every file that would take chapters off a replaced file is measured, volume or chapter.
        foreach (var planned in plan.Files.Where(f => !skipped.Contains(f.FileName)))
        {
            if (!planned.Replaces.Any(r => replaced.Contains(r.ChapterFileId)) ||
                !byName.TryGetValue(planned.FileName, out var source))
            {
                continue;
            }

            var measured = Measure(source, ct);
            if (measured?.MedianWidth is null)
            {
                return new VolumeGuardFailure(planned.FileName, UpgradeReasons.Unmeasurable);
            }

            var expected = planned.Replaces
                .Select(r => r.ChapterFileId)
                .Where(replaced.Contains)
                .Distinct()
                .Select(id => files.GetValueOrDefault(id))
                .Where(f => f is not null && !ReleaseNameParser.ParseFileName(f.RelativePath).IsVolume)
                .Sum(f => f!.PageCount ?? 0);
            if (measured.PageCount < expected * (1 - tolerance / 100.0))
            {
                return new VolumeGuardFailure(planned.FileName, UpgradeReasons.FewerPages);
            }
        }

        return null;
    }

    /// <summary>
    /// What the unattended job does with a finished download. An auto-grabbed upgrade whose verdict named
    /// files to replace imports on its own once the guard passes, skipping the suggested files; anything
    /// else, a grabbed proposal or an upgrade that names no file included, parks when it would displace one.
    /// </summary>
    public async Task<UnattendedDecision> DecideUnattendedAsync(
        DownloadQueueItem item, Series series, string contentPath, TorrentImportPlan plan, CancellationToken ct)
    {
        if (TorrentUpgradeInfo.Parse(item.UpgradeInfoJson) is not { Outcome: TorrentUpgradeOutcomes.Pending } upgrade)
        {
            return new UnattendedDecision(plan.HasConflicts, null, null);
        }

        upgrade.SkipFileNames = [.. plan.SuggestedSkips];
        item.UpgradeInfoJson = upgrade.Serialize();

        // A proposal the user grabbed waits for them with the plan filled in; only the search's own
        // auto-grabs replace files unattended.
        if (upgrade.ProposalId is not null || upgrade.ReplacedFileIds.Count == 0)
        {
            return new UnattendedDecision(plan.HasConflicts, null, null);
        }

        if (await CheckVolumeGuardAsync(item, series, contentPath, plan, ct) is { } failure)
        {
            ParkForGuard(item, failure);
            return new UnattendedDecision(true, failure, null);
        }

        return new UnattendedDecision(false, null, plan.SuggestedSkips.ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>Parks an upgrade download that failed <see cref="CheckVolumeGuardAsync"/> for the user to settle.</summary>
    public static void ParkForGuard(DownloadQueueItem item, VolumeGuardFailure failure)
    {
        item.Status = QueueStatus.AwaitingImport;
        item.PagesDone = item.PagesTotal;
        item.SetError("error.upgrades.volumeGuard", new { file = failure.File, reason = failure.Reason });
        if (TorrentUpgradeInfo.Parse(item.UpgradeInfoJson) is { } upgrade)
        {
            upgrade.Outcome = TorrentUpgradeOutcomes.Parked;
            item.UpgradeInfoJson = upgrade.Serialize();
        }
    }

    private ChapterFileMeasurement? Measure(ComicSource source, CancellationToken ct)
    {
        try
        {
            if (source.Entry is null && source.Kind is ComicSourceKind.Cbz or ComicSourceKind.Zip)
            {
                return ChapterFileMeasurer.MeasureArchive(source.Path, GuardSamplePages, ct);
            }

            if (source.Kind == ComicSourceKind.LooseImages && source.Pages.Count > 0)
            {
                var step = Math.Max(1, source.Pages.Count / GuardSamplePages);
                var sample = new List<(string Name, byte[] Bytes)>();
                for (var i = 0; i < source.Pages.Count && sample.Count < GuardSamplePages; i += step)
                {
                    var page = Path.Combine(source.Path, source.Pages[i]);
                    if (File.Exists(page))
                    {
                        sample.Add((source.Pages[i], File.ReadAllBytes(page)));
                    }
                }

                var measured = ChapterFileMeasurer.Measure(sample);
                return measured with { PageCount = source.Pages.Count };
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not measure {Name} for the upgrade guard", source.Name);
        }

        return null;
    }

    /// <summary>
    /// The chapters a downloaded file would end up backing: its own number for a chapter file, and
    /// for a compilation both the volume range the provider assigns and the chapter markers in its
    /// page names, which is the pair <c>CbzLinkService</c> links on. When the page names carry
    /// markers, the range only reaches chapters nothing backs yet, the same limit the linker has.
    /// A chapter already on one of <paramref name="volumeFileIds"/> is never counted: the linker does
    /// not take chapters off a volume it was not told to displace, so the import would neither gain
    /// nor replace it.
    /// </summary>
    public static List<Chapter> ChaptersCoveredBy(
        List<Chapter> chapters, ParsedReleaseFile parsed, IReadOnlyList<string> pages,
        IReadOnlySet<int>? volumeFileIds = null)
    {
        if (volumeFileIds is { Count: > 0 })
        {
            chapters = chapters.Where(c => c.ChapterFileId is not { } fileId || !volumeFileIds.Contains(fileId)).ToList();
        }
        if (parsed.IsChapter)
        {
            return chapters.Where(c => c.Number == parsed.Number).ToList();
        }

        if (!parsed.IsVolume)
        {
            return [];
        }

        var end = parsed.VolumeEnd ?? parsed.Volume;
        var contained = VolumeChapterScanner.ChaptersInNames(pages).ToHashSet();
        return chapters
            .Where(c => (c.Number is { } n && contained.Contains(n)) ||
                        (c.Volume >= parsed.Volume && c.Volume <= end
                         && (contained.Count == 0 || c.ChapterFileId is null)))
            .ToList();
    }

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
