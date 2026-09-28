namespace Maki.Core.Import;

/// <summary>How a comic found in a download or an import folder becomes a CBZ in the library.</summary>
public enum ComicSourceKind
{
    /// <summary>Already a CBZ. Placed as it is, so a torrent import can still hardlink it.</summary>
    Cbz,

    /// <summary>A zip under another name. A CBZ is a zip, so this is also placed as it is.</summary>
    Zip,

    /// <summary>RAR, 7z or tar: read out and written back into a zip container.</summary>
    Repack,

    /// <summary>A folder of loose page images, the usual shape of an older set.</summary>
    LooseImages,

    /// <summary>A PDF. Placed as it is, read in place, never converted to a CBZ.</summary>
    Pdf
}

/// <summary>
/// One comic inside a completed download or an import folder, described without unpacking
/// anything so a plan can be shown before any work is done.
/// </summary>
/// <param name="Name">
/// The file name this produces (CBZ, or PDF for <see cref="ComicSourceKind.Pdf"/>). Everything
/// downstream reads the volume and chapter off it, so it keeps the source's own name and only
/// changes the extension.
/// </param>
/// <param name="Path">The archive or folder it comes from.</param>
/// <param name="Size">Bytes on disk, or the entry's uncompressed size for a nested archive.</param>
/// <param name="Pages">
/// Image entry names in reading order, which is what the chapter markers embedded in a
/// compilation's page names are read from. Empty for a nested archive, whose own entries cannot be
/// listed without extracting it first.
/// </param>
/// <param name="Entry">
/// The entry inside <paramref name="Path"/> when the comic is an archive nested in another one — a
/// .cbr holding one .rar per chapter is a real scanlation shape.
/// </param>
public sealed record ComicSource(
    string Name,
    ComicSourceKind Kind,
    string Path,
    long Size,
    IReadOnlyList<string> Pages,
    string? Entry = null)
{
    /// <summary>Whether the file itself can be placed in the library, rather than rebuilt.</summary>
    public bool IsReadyToPlace => Kind is ComicSourceKind.Cbz or ComicSourceKind.Zip or ComicSourceKind.Pdf;
}
