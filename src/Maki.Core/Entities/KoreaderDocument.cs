namespace Maki.Core.Entities;

public enum KoreaderDigestKind
{
    /// <summary>KOReader's partial MD5 of the file's bytes.</summary>
    Binary = 0,

    /// <summary>MD5 of a name the file may be saved under on the device.</summary>
    FileName = 1,
}

/// <summary>
/// A KOReader document id that resolves to a chapter file. A file collects several: one binary digest
/// per version of its bytes (a ComicInfo rewrite changes them, while the copy on the device keeps the
/// old ones) and one per name it may carry on the device.
/// </summary>
public class KoreaderDocument
{
    public int Id { get; set; }
    public int ChapterFileId { get; set; }
    public KoreaderDigestKind Kind { get; set; }

    /// <summary>Lowercase hex MD5, exactly what KOReader sends.</summary>
    public string Digest { get; set; } = string.Empty;

    /// <summary>For a binary digest, the file length and write time it was computed from.</summary>
    public string? FileVersion { get; set; }

    public DateTime CreatedAt { get; set; }
}
