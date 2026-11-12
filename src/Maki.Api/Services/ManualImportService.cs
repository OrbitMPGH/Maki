using System.Globalization;
using Maki.Api.Localization;
using Maki.Core.Entities;
using Maki.Core.Import;
using Maki.Core.Parsing;
using Maki.Core.Paths;
using Maki.Core.Reading;
using Maki.Core.Security;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

public record ManualImportFolder(string Name, string Path);

/// <param name="Chapter">The chapter number the name carries, invariant, or null.</param>
public record ManualImportFile(string Name, string Path, long Size, string? Chapter, int? Volume, int? VolumeEnd);

/// <param name="Parent">Where "up" goes: "" for the top list, null when there is nothing above.</param>
public record ManualImportListing(
    string Path, string? Parent, IReadOnlyList<ManualImportFolder> Folders, IReadOnlyList<ManualImportFile> Files);

/// <param name="ErrorKey">Catalogue key when nothing was imported, else null.</param>
public record ManualImportResult(
    bool Applied, int Imported, int Linked, int Skipped, int Deleted, string? ErrorKey = null,
    object? ErrorArgs = null, string? Detail = null);

/// <summary>
/// Imports comics from a path on the server into a series the caller picks, through the same plan
/// and import a parked torrent uses (<see cref="TorrentImportService"/>), so the chapters each file
/// covers, the files it would displace and the hardlink rules are one set of rules.
/// <para>
/// Who may read which path: an admin may browse and import from anywhere the process can read; any
/// other caller with ImportLibrary only from inside the root folders they were granted, because a
/// listing of an arbitrary path is a read of the host. A path that is or overlaps a series folder,
/// or holds a whole root folder, is refused for everyone: importing it would move a series' own
/// files, or every series at once.
/// </para>
/// </summary>
public class ManualImportService(
    MakiDbContext db,
    TorrentImportService importer,
    ICurrentUser currentUser,
    ILogger<ManualImportService> logger)
{
    public const string ManualSourceName = "manual";

    private static readonly StringComparison PathComparison = StringComparison.OrdinalIgnoreCase;

    private bool IsAdmin => currentUser.Has(MakiPermission.Admin);

    private async Task<List<RootFolder>> VisibleRootsAsync(CancellationToken ct) =>
        currentUser.AllRootFolders || IsAdmin
            ? await db.RootFolders.AsNoTracking().ToListAsync(ct)
            : await db.RootFolders.AsNoTracking().Where(r => currentUser.RootFolderIds.Contains(r.Id)).ToListAsync(ct);

    internal static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    internal static bool IsWithin(string path, string folder)
    {
        var p = Normalize(path);
        var f = Normalize(folder);
        return string.Equals(p, f, PathComparison) ||
               p.StartsWith(f.EndsWith(Path.DirectorySeparatorChar) ? f : f + Path.DirectorySeparatorChar, PathComparison);
    }

    /// <summary>The full path when the caller may read it, else null.</summary>
    public async Task<string?> ReadablePathAsync(string? path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string full;
        try
        {
            full = Normalize(path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (IsAdmin)
        {
            return full;
        }

        var roots = await VisibleRootsAsync(ct);
        return roots.Any(r => IsWithin(full, r.Path) && !LibraryPaths.TraversesLink(r.Path, full)) ? full : null;
    }

    public async Task<ManualImportListing?> BrowseAsync(string? path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return IsAdmin ? HostTop() : await RootsTopAsync(ct);
        }

        var full = await ReadablePathAsync(path, ct);
        if (full is null || !Directory.Exists(full))
        {
            return null;
        }

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };
        var info = new DirectoryInfo(full);
        var folders = info.EnumerateDirectories("*", options)
            .Where(d => !d.Name.StartsWith('.') && d.LinkTarget is null)
            .Select(d => new ManualImportFolder(d.Name, d.FullName))
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var files = info.EnumerateFiles("*", options)
            .Where(f => f.LinkTarget is null && (ComicSourceScanner.IsArchive(f.Name) || ComicFile.IsPdf(f.Name)))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f =>
            {
                var parsed = ReleaseNameParser.ParseFileName(f.Name);
                return new ManualImportFile(f.Name, f.FullName, f.Length,
                    parsed.Number?.ToString("0.###", CultureInfo.InvariantCulture), parsed.Volume, parsed.VolumeEnd);
            })
            .ToList();

        string? parent;
        if (IsAdmin)
        {
            parent = Directory.GetParent(full)?.FullName ?? (OperatingSystem.IsWindows() ? "" : null);
        }
        else
        {
            // A granted user goes up as far as their root folder, then back to the list of roots.
            var up = Directory.GetParent(full)?.FullName;
            parent = up is not null && await ReadablePathAsync(up, ct) is not null ? up : "";
        }

        return new ManualImportListing(full, parent, folders, files);
    }

    private static ManualImportListing HostTop()
    {
        if (OperatingSystem.IsWindows())
        {
            var drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .Select(d => new ManualImportFolder(d.Name.TrimEnd('\\'), d.RootDirectory.FullName))
                .ToList();
            return new ManualImportListing("", null, drives, []);
        }

        return new ManualImportListing("", null, [new ManualImportFolder("/", "/")], []);
    }

    private async Task<ManualImportListing> RootsTopAsync(CancellationToken ct) =>
        new("", null,
            (await VisibleRootsAsync(ct)).Select(r => new ManualImportFolder(r.Path, r.Path)).ToList(), []);

    /// <summary>
    /// Why <paramref name="fullPath"/> may not be imported from, or null: it is gone, it is or holds a
    /// root folder, or it overlaps a folder some series' files live in.
    /// </summary>
    public async Task<(string Key, object? Args)?> SourceProblemAsync(string fullPath, CancellationToken ct)
    {
        if (!Directory.Exists(fullPath) && !File.Exists(fullPath))
        {
            return ("error.rootFolder.doesNotExist", new { path = fullPath });
        }

        var roots = await db.RootFolders.AsNoTracking().ToListAsync(ct);
        if (roots.Any(r => IsWithin(r.Path, fullPath)))
        {
            return Owned;
        }

        foreach (var root in roots.Where(r => IsWithin(fullPath, r.Path)))
        {
            var relative = Path.GetRelativePath(root.Path, fullPath);
            var top = LibraryPaths.TopFolder(relative);
            if (top is null || top.StartsWith('.'))
            {
                return Owned;
            }

            var seriesInRoot = db.Series.IgnoreQueryFilters().Where(s => s.RootFolderId == root.Id);
            var owned = await seriesInRoot.Select(s => s.FolderName).ToListAsync(ct);
            var filed = await db.ChapterFiles.IgnoreQueryFilters()
                .Where(f => seriesInRoot.Any(s => s.Id == f.SeriesId))
                .Select(f => f.RelativePath)
                .ToListAsync(ct);
            if (owned.Concat(filed.Select(LibraryPaths.TopFolder).OfType<string>())
                .Contains(top, LibraryPaths.FolderComparer))
            {
                return Owned;
            }
        }

        return null;
    }

    private static readonly (string, object?) Owned = ("error.libraryImport.folderOwnedByOtherSeries", null);

    private async Task<Series?> TargetSeriesAsync(int seriesId, CancellationToken ct) =>
        await db.Series.Include(s => s.RootFolder).FirstOrDefaultAsync(s => s.Id == seriesId, ct);

    /// <summary>The not-yet-saved row the shared import runs against, and the history entry once it applied.</summary>
    private DownloadQueueItem ItemFor(Series series, string fullPath) => new()
    {
        SeriesId = series.Id,
        Series = series,
        Protocol = AcquisitionProtocol.Torrent,
        Title = Path.GetFileName(Path.TrimEndingDirectorySeparator(fullPath)),
        Origin = DownloadOrigin.Manual,
        QueuedAt = DateTime.UtcNow,
        QueuedByUserId = currentUser.IsAuthenticated && currentUser.UserId > 0 ? currentUser.UserId : null,
    };

    /// <returns>Null when the series is not one the caller can see.</returns>
    public async Task<TorrentImportPlan?> PlanAsync(string fullPath, int seriesId, CancellationToken ct)
    {
        var series = await TargetSeriesAsync(seriesId, ct);
        if (series is null)
        {
            return null;
        }

        var item = ItemFor(series, fullPath);
        if (series.ArchivedAt is not null)
        {
            return new TorrentImportPlan(0, series.Id, series.Title, item.Title!, [], "error.series.archived");
        }

        if (await SourceProblemAsync(fullPath, ct) is var (key, args))
        {
            return new TorrentImportPlan(0, series.Id, series.Title, item.Title!, [], key, args);
        }

        return await importer.PlanAsync(item, series, fullPath, ct);
    }

    /// <summary>
    /// Imports <paramref name="fullPath"/> into the series and records it in the download history.
    /// The caller has checked the path is one it may read.
    /// </summary>
    /// <returns>Null when the series is not one the caller can see.</returns>
    public async Task<ManualImportResult?> ImportAsync(
        string fullPath, int seriesId, TorrentImportMode mode, IReadOnlySet<string>? skipFiles,
        ImportPlacement placement, CancellationToken ct)
    {
        var series = await TargetSeriesAsync(seriesId, ct);
        if (series is null)
        {
            return null;
        }

        return await ImportIntoAsync(series, fullPath, mode, skipFiles, placement, DownloadOrigin.Manual, ct);
    }

    /// <summary>The import itself, shared with the drop folder, which has already picked the series.</summary>
    internal async Task<ManualImportResult> ImportIntoAsync(
        Series series, string fullPath, TorrentImportMode mode, IReadOnlySet<string>? skipFiles,
        ImportPlacement placement, DownloadOrigin origin, CancellationToken ct)
    {
        if (series.ArchivedAt is not null)
        {
            return new ManualImportResult(false, 0, 0, 0, 0, "error.series.archived");
        }

        if (await SourceProblemAsync(fullPath, ct) is var (key, args))
        {
            return new ManualImportResult(false, 0, 0, 0, 0, key, args);
        }

        var item = ItemFor(series, fullPath);
        item.Origin = origin;
        var outcome = await importer.ImportAsync(
            item, series, fullPath, mode, ct, skipFiles: skipFiles, placement: placement, sourceName: ManualSourceName);
        if (!outcome.Applied)
        {
            return new ManualImportResult(false, 0, 0, outcome.Skipped, 0, outcome.ErrorKey ?? "error.libraryImport.failed",
                outcome.ErrorArgs, outcome.Error);
        }

        if (outcome.Imported > 0)
        {
            item.Series = null;
            item.Status = QueueStatus.Completed;
            item.CompletedAt = DateTime.UtcNow;
            db.DownloadQueue.Add(item);
            await db.SaveChangesAsync(ct);
            await importer.ApplyNamingAsync(series, outcome.ImportedPaths, ct);
            logger.LogInformation("Imported {Count} file(s) from {Path} into series {SeriesId}",
                outcome.Imported, fullPath, series.Id);
        }

        return new ManualImportResult(true, outcome.Imported, outcome.Linked, outcome.Skipped, outcome.Deleted);
    }
}
