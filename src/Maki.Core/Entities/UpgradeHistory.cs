namespace Maki.Core.Entities;

/// <summary>One applied upgrade: what the file was before, what replaced it, and where the old copy went.</summary>
public class UpgradeHistory
{
    public int Id { get; set; }
    public int SeriesId { get; set; }
    public int ChapterId { get; set; }
    public int ChapterFileId { get; set; }
    public int? QueueItemId { get; set; }
    public int ProfileId { get; set; }
    public int ProfileVersion { get; set; }
    public int? QueuedByUserId { get; set; }

    /// <summary>Serialised <c>QualitySnapshot</c>.</summary>
    public string BeforeJson { get; set; } = "{}";

    /// <summary>Serialised <c>QualitySnapshot</c>.</summary>
    public string AfterJson { get; set; } = "{}";

    /// <summary>
    /// The discarded copy, relative to the series' root folder with forward slashes. After a revert it
    /// names the upgraded copy that was put aside instead. Null once purged.
    /// </summary>
    public string? TrashPath { get; set; }

    public long TrashBytes { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? RevertedAtUtc { get; set; }
}
