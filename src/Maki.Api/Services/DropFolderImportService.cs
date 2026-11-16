using System.Collections.Concurrent;
using System.Globalization;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Import;
using Maki.Core.Inbox;
using Maki.Core.Notifications;
using Maki.Core.Parsing;
using Maki.Core.Paths;
using Maki.Core.Reading;
using Maki.Core.Scrobbling;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

public static class DropFolderPendingReason
{
    /// <summary>No library series has the name or id the comic carries.</summary>
    public const string Unmatched = "unmatched";

    /// <summary>More than one library series does.</summary>
    public const string Ambiguous = "ambiguous";

    /// <summary>Matched, but the import refused or failed; <see cref="DropFolderPending.Detail"/> says why.</summary>
    public const string Failed = "failed";

    /// <summary>Matched and imported, but it brought nothing the series lacks, so it was left where it is.</summary>
    public const string NothingNew = "nothingNew";
}

/// <param name="Reason">One of <see cref="DropFolderPendingReason"/>.</param>
/// <param name="SeriesTitles">The library series it could be, for an ambiguous or failed entry.</param>
public record DropFolderPending(
    string Name, string Path, string Reason, IReadOnlyList<string> SeriesTitles, string? Detail, DateTime SeenAt)
{
    /// <summary>Placeholders for <see cref="Detail"/>, which is a catalogue key the status read renders.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public object? DetailArgs { get; init; }

    public bool IsFolder => Directory.Exists(Path);

    /// <summary>The folder holding <see cref="Path"/>, where the manual import opens for a file.</summary>
    public string? Folder => System.IO.Path.GetDirectoryName(Path);
}

public record DropFolderStatus(
    bool Enabled, string? Path, DateTime? LastRunAt, string? LastError, IReadOnlyList<DropFolderPending> Pending);

/// <summary>
/// What the drop folder poll remembers between runs, in memory: the size each entry had at the
/// last poll (a file is touched only once it held still across two), what each entry looked like
/// when it was last settled, and what is waiting for a person.
/// </summary>
public sealed class DropFolderState
{
    internal readonly ConcurrentDictionary<string, DropFolderImportService.Signature> Seen = new(StringComparer.Ordinal);
    internal readonly ConcurrentDictionary<string, DropFolderImportService.Signature> Settled = new(StringComparer.Ordinal);
    internal volatile IReadOnlyList<DropFolderPending> Pending = [];
    internal readonly SemaphoreSlim Gate = new(1, 1);
    internal DateTime? LastRunAt;
    internal string? LastError;
    internal string? LastPath;
}

/// <summary>
/// Polls the drop folder. <see cref="FileSystemWatcher"/> sees nothing on SMB or NFS shares or a
/// Docker Desktop bind mount, so this is a poll. Each top-level entry (a folder or a comic file) is
/// touched only once its size and write time held across two polls. An entry whose ComicInfo link or
/// name names exactly one library series is imported into it through <see cref="ManualImportService"/>
/// with <see cref="TorrentImportMode.SkipExisting"/>, so nothing the library has is ever replaced
/// unattended. Anything unmatched, ambiguous or refused stays put and is listed for a manual import.
/// </summary>
public class DropFolderImportService(
    MakiDbContext db,
    IAppSettings settings,
    ManualImportService manualImport,
    DropFolderState state,
    InboxService inbox,
    ILogger<DropFolderImportService> logger,
    LibraryImportResolver? resolver = null)
{
    public const int DefaultIntervalMinutes = 5;

    internal readonly record struct Signature(int Files, long Bytes, long LatestWrite);

    private static readonly string[] PartialExtensions = [".part", ".partial", ".tmp", ".!qb", ".crdownload", ".download"];

    public async Task<int> IntervalMinutesAsync(CancellationToken ct) =>
        int.TryParse(await settings.GetAsync(SettingKeys.ImportDropFolderIntervalMinutes, ct), NumberStyles.None,
            CultureInfo.InvariantCulture, out var minutes)
            ? Math.Clamp(minutes, 1, 1440)
            : DefaultIntervalMinutes;

    public async Task<DropFolderStatus> StatusAsync(CancellationToken ct) =>
        new(await settings.GetAsync(SettingKeys.ImportDropFolderEnabled, ct) == "true",
            await settings.GetAsync(SettingKeys.ImportDropFolderPath, ct), state.LastRunAt, state.LastError,
            state.Pending);

    /// <summary>One poll when the interval has passed since the last, or now with <paramref name="force"/>.</summary>
    public async Task TickAsync(bool force, CancellationToken ct)
    {
        if (await settings.GetAsync(SettingKeys.ImportDropFolderEnabled, ct) != "true" ||
            await settings.GetAsync(SettingKeys.ImportDropFolderPath, ct) is not { Length: > 0 } path)
        {
            Reset(null);
            return;
        }

        if (!force && state.LastRunAt is { } last &&
            DateTime.UtcNow - last < TimeSpan.FromMinutes(await IntervalMinutesAsync(ct)) - TimeSpan.FromSeconds(5))
        {
            return;
        }

        if (!await state.Gate.WaitAsync(0, ct))
        {
            return;
        }

        try
        {
            if (!string.Equals(state.LastPath, path, StringComparison.Ordinal))
            {
                Reset(path);
            }

            state.LastRunAt = DateTime.UtcNow;
            await PollAsync(path, ct);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private void Reset(string? path)
    {
        state.Seen.Clear();
        state.Settled.Clear();
        state.Pending = [];
        state.LastPath = path;
        state.LastError = null;
    }

    private async Task PollAsync(string root, CancellationToken ct)
    {
        List<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(root)
                .Where(e => !Path.GetFileName(e).StartsWith('.') && !LibraryPaths.IsLink(e))
                .Where(e => Directory.Exists(e) || IsComic(e))
                .Order(StringComparer.Ordinal)
                .ToList();
            state.LastError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read the drop folder {Path}", root);
            state.LastError = ex.Message;
            return;
        }

        // Loaded once something has settled, so a quiet poll does not read every series.
        List<LibrarySeries>? library = null;
        var pending = state.Pending.Where(p => entries.Contains(p.Path, StringComparer.Ordinal))
            .ToDictionary(p => p.Path, StringComparer.Ordinal);
        var imported = 0;
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            var signature = SignatureOf(entry);
            var previous = state.Seen.TryGetValue(entry, out var seen) ? seen : (Signature?)null;
            state.Seen[entry] = signature ?? default;
            if (signature is not { } stable || previous != stable || stable.Files == 0)
            {
                continue;
            }

            if (state.Settled.TryGetValue(entry, out var settled) && settled == stable)
            {
                continue;
            }

            library ??= await LibraryAsync(ct);
            var outcome = await SettleAsync(entry, library, ct);
            if (outcome is null)
            {
                pending.Remove(entry);
                imported++;
                RemoveEmptyFolders(entry);
                // A copy leaves the entry as it was, and a move can leave originals it had to build
                // from: neither is offered again until it changes.
                if (SignatureOf(entry) is { } after)
                {
                    state.Settled[entry] = after;
                    state.Seen[entry] = after;
                }
            }
            else
            {
                pending[entry] = outcome;
                state.Settled[entry] = SignatureOf(entry) ?? stable;
            }
        }

        foreach (var gone in state.Seen.Keys.Where(k => !entries.Contains(k, StringComparer.Ordinal)).ToList())
        {
            state.Seen.TryRemove(gone, out _);
            state.Settled.TryRemove(gone, out _);
        }

        state.Pending = pending.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (imported > 0)
        {
            inbox.Raise(InboxEventType.ImportFinished, new InboxMessage(
                    Key: "inbox.libraryImport.finished",
                    Params: InboxMessage.Args(new { imported, failed = 0 }),
                    Level: NotificationLevel.Info,
                    Url: "/activity"),
                InboxAudience.Admins);
        }
    }

    private static bool IsComic(string path) =>
        File.Exists(path) && (ComicSourceScanner.IsArchive(path) || ComicFile.IsPdf(path));

    /// <summary>File count, bytes and newest write over everything in the entry; null while a partial download sits in it.</summary>
    internal static Signature? SignatureOf(string entry)
    {
        try
        {
            var files = File.Exists(entry) ? [entry] : LibraryPaths.EnumerateFilesNoLinks(entry).ToList();
            if (files.Any(f => PartialExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)))
            {
                return null;
            }

            var infos = files.Select(f => new FileInfo(f)).ToList();
            return new Signature(infos.Count, infos.Sum(i => i.Length),
                infos.Count == 0 ? 0 : infos.Max(i => i.LastWriteTimeUtc.Ticks));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal sealed record LibrarySeries(Series Series, HashSet<string> Titles);

    private async Task<List<LibrarySeries>> LibraryAsync(CancellationToken ct) =>
        (await db.Series.AsNoTracking().Where(s => s.ArchivedAt == null).ToListAsync(ct))
        .Select(s => new LibrarySeries(s, s.AltTitles.Select(a => a.Title).Append(s.Title)
            .Select(ScrobbleMatching.NormalizeTitle)
            .Where(t => t.Length > 0)
            .ToHashSet(StringComparer.Ordinal)))
        .ToList();

    /// <summary>Imports the entry, or says why it stays. Null when it was imported.</summary>
    private async Task<DropFolderPending?> SettleAsync(string entry, List<LibrarySeries> library, CancellationToken ct)
    {
        var name = Path.GetFileName(entry);
        var candidates = await CandidatesAsync(entry, library, ct);
        if (candidates.Count != 1)
        {
            return new DropFolderPending(name, entry,
                candidates.Count == 0 ? DropFolderPendingReason.Unmatched : DropFolderPendingReason.Ambiguous,
                candidates.Select(c => c.Title).Order(StringComparer.OrdinalIgnoreCase).ToList(), null, DateTime.UtcNow);
        }

        var series = await db.Series.Include(s => s.RootFolder).FirstAsync(s => s.Id == candidates[0].Id, ct);
        var placement = await settings.GetAsync(SettingKeys.ImportDropFolderPlacement, ct) == "move"
            ? ImportPlacement.Move
            : ImportPlacement.LinkOrCopy;
        try
        {
            var result = await manualImport.ImportIntoAsync(
                series, entry, TorrentImportMode.SkipExisting, null, placement, DownloadOrigin.Manual, ct);
            if (!result.Applied)
            {
                logger.LogWarning("Drop folder import of {Entry} into {Title} refused: {Key} {Detail}",
                    name, series.Title, result.ErrorKey, result.Detail);
                return new DropFolderPending(name, entry, DropFolderPendingReason.Failed, [series.Title],
                    result.ErrorKey, DateTime.UtcNow) { DetailArgs = result.ErrorArgs };
            }

            if (result.Imported == 0)
            {
                return new DropFolderPending(name, entry, DropFolderPendingReason.NothingNew, [series.Title], null,
                    DateTime.UtcNow);
            }

            logger.LogInformation("Drop folder imported {Count} file(s) from {Entry} into {Title}",
                result.Imported, name, series.Title);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Drop folder import of {Entry} into {Title} failed", name, series.Title);
            return new DropFolderPending(name, entry, DropFolderPendingReason.Failed, [series.Title],
                "error.libraryImport.failed", DateTime.UtcNow);
        }
    }

    /// <summary>
    /// The library series the entry names: the one a ComicInfo web link resolves to, else every series
    /// whose title or alt title equals a name the entry or its ComicInfo carries. Exact only.
    /// </summary>
    private async Task<List<Series>> CandidatesAsync(string entry, List<LibrarySeries> library, CancellationToken ct)
    {
        IReadOnlyList<ComicSource> comics;
        try
        {
            comics = ComicSourceScanner.Scan(entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var hints = FolderMetadataHints.Read(Directory.Exists(entry) ? entry : null, comics);
        if (resolver is not null && hints.WebLinks.Count > 0 &&
            await resolver.ResolveLinksAsync(hints.WebLinks, ct) is var (linked, _) &&
            int.TryParse(linked.ProviderId, NumberStyles.None, CultureInfo.InvariantCulture, out var mangaBakaId) &&
            library.Where(l => l.Series.MangaBakaId == mangaBakaId).Select(l => l.Series).ToList() is [var byId])
        {
            return [byId];
        }

        var names = NamesOf(Path.GetFileName(entry), File.Exists(entry));
        if (hints.SeriesName is { } hinted)
        {
            names.Add(ScrobbleMatching.NormalizeTitle(hinted));
        }

        names.RemoveWhere(n => n.Length == 0);
        return library.Where(l => l.Titles.Overlaps(names)).Select(l => l.Series).ToList();
    }

    /// <summary>The normalized titles a drop folder entry's name carries, with its volume and chapter span cut off.</summary>
    internal static HashSet<string> NamesOf(string name, bool isFile)
    {
        var bare = isFile ? Path.GetFileNameWithoutExtension(name) : name;
        var parsed = ReleaseTitleParser.Parse(bare);
        return parsed.TitleCandidates
            .Append(parsed.TitleWithoutTrailingNumber ?? "")
            .Append(ReleaseNameParser.CleanFolderTitle(bare))
            .Select(ScrobbleMatching.NormalizeTitle)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>After a move, the folders the entry left empty go, deepest first; never a recursive delete.</summary>
    private static void RemoveEmptyFolders(string entry)
    {
        if (!Directory.Exists(entry))
        {
            return;
        }

        foreach (var folder in LibraryPaths.EnumerateDirectoriesNoLinks(entry).OrderByDescending(d => d.Length).Append(entry))
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
    }
}
