namespace Maki.Core.Entities;

/// <summary>Which delete put a file in the recycle bin.</summary>
public enum RecycleReason
{
    DeleteFile,
    RemoveChapter,
    SeriesDelete
}

/// <summary>
/// One file the user deleted, waiting in <c>&lt;root&gt;/.maki-trash/bin</c> until it is restored,
/// deleted for good or purged. Holds everything a restore needs, because the series, chapters and
/// <see cref="ChapterFile"/> row it came from may all be gone by then. No foreign keys for the same
/// reason.
/// </summary>
public class RecycleBinEntry
{
    public int Id { get; set; }
    public int RootFolderId { get; set; }

    /// <summary>The root folder's path when the file was binned. The bin file lives under it even if the root folder row goes.</summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>Where the file was, relative to <see cref="RootPath"/>.</summary>
    public string RelativePath { get; set; } = string.Empty;

    public string OriginalPath { get; set; } = string.Empty;

    /// <summary>Where the file is now, relative to <see cref="RootPath"/> with forward slashes.</summary>
    public string BinPath { get; set; } = string.Empty;

    public int SeriesId { get; set; }
    public string SeriesTitle { get; set; } = string.Empty;

    /// <summary>The removed row's id. Null for a file Maki had no record of.</summary>
    public int? ChapterFileId { get; set; }

    /// <summary>Serialised <see cref="ChapterFile"/> as it was, or null for a file Maki had no record of.</summary>
    public string? FileJson { get; set; }

    /// <summary>Serialised list of the chapters the file backed: id, number, volume, language.</summary>
    public string ChaptersJson { get; set; } = "[]";

    public long Size { get; set; }
    public RecycleReason Reason { get; set; }
    public DateTime DeletedAtUtc { get; set; }
    public int? DeletedByUserId { get; set; }
    public string? DeletedByName { get; set; }
}
