namespace Maki.Core.Entities;

/// <summary>
/// A folder in a root folder that somebody dismissed from the library import ("Extras", "Art").
/// The scan skips it before any metadata search, so junk folders stop coming back on every scan.
/// </summary>
public class ImportIgnoredFolder
{
    public int Id { get; set; }
    public int RootFolderId { get; set; }

    /// <summary>The folder's name directly inside the root, as the scan lists it.</summary>
    public string FolderName { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// One folder of one library import run, kept so the import can be undone. Rows sharing a
/// <see cref="BatchId"/> are one run. <see cref="OperationsJson"/> lists what the import did to the
/// disk and the database (the shared import plan model), so undo reverses exactly that and nothing
/// the user owned before.
/// </summary>
public class ImportBatchFolder
{
    public int Id { get; set; }
    public string BatchId { get; set; } = string.Empty;
    public int RootFolderId { get; set; }

    /// <summary>No foreign key: the series can be deleted after the import, which undo reports.</summary>
    public int? SeriesId { get; set; }

    public string SeriesTitle { get; set; } = string.Empty;

    /// <summary>The import added the series, rather than filling one already in the library.</summary>
    public bool CreatedSeries { get; set; }

    /// <summary>The folder's name when the import started.</summary>
    public string OriginalFolderName { get; set; } = string.Empty;

    /// <summary>The folder the files are in after the import (renamed, merged into, or unchanged).</summary>
    public string FolderName { get; set; } = string.Empty;

    public string OperationsJson { get; set; } = "{}";
    public int? UserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UndoneAt { get; set; }
}
