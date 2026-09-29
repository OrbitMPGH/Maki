namespace Maki.Core.Entities;

/// <summary>Persisted as an int: append only, never renumber.</summary>
public enum TorrentProposalStatus
{
    Pending = 0,
    Accepted = 1,
    Dismissed = 2,
    Expired = 3
}

/// <summary>
/// A torrent volume release the volume search found for a series but would not grab on its own. One
/// row per release guid per series, so a dismissed row also stops the same release being proposed again.
/// </summary>
public class TorrentProposal
{
    public int Id { get; set; }
    public int SeriesId { get; set; }
    public string ReleaseGuid { get; set; } = "";

    /// <summary>The search result as it came back, so a grab needs no second search.</summary>
    public string ReleaseInfoJson { get; set; } = "{}";

    public string Title { get; set; } = "";
    public string Indexer { get; set; } = "";
    public long SizeBytes { get; set; }

    /// <summary>Serialised <c>ReleaseSpan</c>.</summary>
    public string SpanJson { get; set; } = "{}";

    public string ReasonsJson { get; set; } = "[]";
    public int UpgradeCount { get; set; }
    public int AlreadyMetCount { get; set; }
    public int SkippedCount { get; set; }
    public int MissingCount { get; set; }
    public int UnknownCount { get; set; }

    /// <summary>The release's format score under the series' profile when it was found.</summary>
    public int Score { get; set; }

    public TorrentProposalStatus Status { get; set; }
    public int? QueueItemId { get; set; }
    public int? ResolvedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
}
