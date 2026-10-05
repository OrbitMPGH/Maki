namespace Maki.Core.Entities;

/// <summary>
/// The upgrade scan's memo: one candidate copy of a chapter already looked at under one profile
/// version, and why it was not taken, so a loser is not probed again every day. A profile edit bumps
/// the version and so reopens every candidate.
/// </summary>
public class UpgradeAttempt
{
    public int Id { get; set; }
    public int ChapterId { get; set; }
    public int SeriesId { get; set; }
    public int SourceMappingId { get; set; }
    public string SourceChapterId { get; set; } = string.Empty;
    public int ProfileId { get; set; }
    public int ProfileVersion { get; set; }

    /// <summary>A <c>UpgradeReasons</c> code, stored raw and worded by the client.</summary>
    public string Reason { get; set; } = string.Empty;

    public bool Probed { get; set; }
    public int? CandidatePageCount { get; set; }
    public int? CandidateWidth { get; set; }
    public int? CandidateScore { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
