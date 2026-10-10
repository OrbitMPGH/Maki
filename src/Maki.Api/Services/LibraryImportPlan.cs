using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maki.Api.Services;

/// <summary>
/// How one comic the scan found becomes a file the import registers. Wire values; the client words
/// them. The same values describe a preview (<see cref="LibraryImportPlan"/>) and what an import did
/// (<see cref="ImportOperations"/>), so the two can be compared and undo reverses exactly the plan.
/// </summary>
public static class ImportFileAction
{
    /// <summary>A CBZ or PDF, registered where it is and never moved or copied.</summary>
    public const string Register = "register";

    /// <summary>A CBZ built beside the original: a repacked RAR, 7z or tar, a zip, or loose pages.</summary>
    public const string Build = "build";

    /// <summary>
    /// A RAR, 7z or tar under a ".cbz" name. The original is renamed to the extension its bytes call
    /// for and the CBZ is built under the old name.
    /// </summary>
    public const string RebuildInPlace = "rebuildInPlace";

    /// <summary>A file the scan could not read already holds the CBZ's name, so that file is registered.</summary>
    public const string UseExisting = "useExisting";
}

/// <summary>What happens to the scanned folder itself.</summary>
public static class ImportFolderAction
{
    /// <summary>The files stay in the folder under its current name.</summary>
    public const string Keep = "keep";

    /// <summary>The folder is renamed to the series folder format.</summary>
    public const string Rename = "rename";

    /// <summary>The folder's files are moved into the series' existing folder.</summary>
    public const string Merge = "merge";
}

/// <param name="Name">The registered file, relative to the folder the files end up in.</param>
/// <param name="Source">The archive or folder it comes from, relative to the scanned folder.</param>
/// <param name="Entry">The entry inside <paramref name="Source"/> for an archive nested in another one.</param>
/// <param name="Kind">The comic's container: cbz, zip, repack, looseImages or pdf.</param>
/// <param name="Action">One of <see cref="ImportFileAction"/>.</param>
/// <param name="Aside">For <see cref="ImportFileAction.RebuildInPlace"/>, where the original goes.</param>
/// <param name="Number">Chapter number read off the name, invariant, as the parser read it.</param>
/// <param name="Volume">Volume read off the name.</param>
/// <param name="VolumeEnd">Last volume of a range like v01-03.</param>
/// <param name="Chapters">
/// Chapter numbers this file is expected to link to. Null when linking waits for the background
/// source match, because the series has no chapters yet.
/// </param>
/// <param name="Unlinked">
/// Why the file is expected to back no chapter (an <see cref="ImportSkipReason"/>), or null.
/// </param>
/// <param name="LoneFile">
/// The folder's only comic with no number in its name: it links to the series' chapter when the
/// source lists exactly one.
/// </param>
public record LibraryImportPlanFile(
    string Name,
    string Source,
    string? Entry,
    string Kind,
    string Action,
    string? Aside,
    long Size,
    string? Number,
    int? Volume,
    int? VolumeEnd,
    IReadOnlyList<string>? Chapters,
    string? Unlinked,
    bool LoneFile = false);

/// <summary>
/// What importing one folder would do, worked out without writing anything. Built from the same
/// folder decision and the same comic plan the import itself runs, so the preview cannot drift from
/// the import.
/// </summary>
/// <param name="Error">Localized reason the import would refuse, or null.</param>
/// <param name="FolderAction">One of <see cref="ImportFolderAction"/>.</param>
/// <param name="TargetFolderName">The folder the files end up in.</param>
/// <param name="SeriesFolderName">The folder later downloads go to (differs in keep-new-standard mode).</param>
/// <param name="LinkDeferred">
/// Chapters are linked once the background source match has synced them, so
/// <see cref="LibraryImportPlanFile.Chapters"/> is null and the expected links are read off the names.
/// </param>
/// <param name="Skipped">Comics the import leaves out, with an <see cref="ImportSkipReason"/>.</param>
/// <param name="WritesCover">A cover.jpg is written into the folder.</param>
/// <param name="ReplacesCover">There already is a cover.jpg that writing the cover replaces.</param>
public record LibraryImportPlan(
    string FolderName,
    string? Error,
    string? SeriesTitle = null,
    int? ExistingSeriesId = null,
    string FolderAction = ImportFolderAction.Keep,
    string? TargetFolderName = null,
    string? SeriesFolderName = null,
    bool LinkDeferred = true,
    IReadOnlyList<LibraryImportPlanFile>? Files = null,
    IReadOnlyList<ImportSkippedFile>? Skipped = null,
    bool WritesCover = false,
    bool ReplacesCover = false);

/// <param name="Name">The CBZ Maki wrote, relative to the folder the files ended up in.</param>
/// <param name="Source">The original it was built from, relative to the same folder.</param>
/// <param name="Aside">For a rebuild in place, where the original was renamed to.</param>
/// <param name="Kind">The container the build read: zip, repack or looseImages.</param>
/// <param name="Pages">
/// The page names the build read. Undo deletes the CBZ only while every one of them is still in the
/// original, so a CBZ whose original was deleted or emptied since is kept as the only copy. Empty for
/// an archive nested in another one, whose pages cannot be listed without extracting it.
/// </param>
public sealed record ImportBuiltFile(
    string Name, string Source, string? Entry, string Action, string? Aside, string? Kind = null,
    IReadOnlyList<string>? Pages = null);

/// <summary>
/// What one folder's import changed, stored on <c>ImportBatchFolder.OperationsJson</c>. Undo
/// reverses these and nothing else: only files listed in <see cref="Built"/> were written by Maki,
/// and everything else in the folder belonged to the user before the import.
/// </summary>
public sealed class ImportOperations
{
    /// <summary>One of <see cref="ImportFolderAction"/>.</summary>
    public string FolderAction { get; set; } = ImportFolderAction.Keep;

    /// <summary>For a merge, the files moved out of the scanned folder, relative to it.</summary>
    public List<string> Moved { get; set; } = [];

    public List<ImportBuiltFile> Built { get; set; } = [];

    /// <summary>The ChapterFile rows this import added.</summary>
    public List<int> RegisteredFileIds { get; set; } = [];

    /// <summary>
    /// Each registered row's path when the import wrote it, relative to the root. A row whose path
    /// changed since (a series rename) means the recorded names no longer say where the files are.
    /// </summary>
    public Dictionary<int, string> RegisteredPaths { get; set; } = [];

    /// <summary>
    /// The undo's database half is done but part of the disk half failed; another undo retries the
    /// disk half only.
    /// </summary>
    public bool DiskPending { get; set; }

    /// <summary>The import wrote cover.jpg into a folder that had none.</summary>
    public bool WroteCover { get; set; }

    /// <summary>For an existing series, where it pointed before the import moved it.</summary>
    public string? PreviousSeriesFolderName { get; set; }

    public int? PreviousRootFolderId { get; set; }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    public static ImportOperations Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new ImportOperations();
        }

        try
        {
            return JsonSerializer.Deserialize<ImportOperations>(json, Json) ?? new ImportOperations();
        }
        catch (JsonException)
        {
            return new ImportOperations();
        }
    }
}
