namespace Mangarr.Core.Entities;

/// <summary>OAuth/PAT credentials for one scrobble tracker ("anilist" | "mal" | "mangabaka").</summary>
public class ScrobbleToken
{
    public string Service { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public string? RefreshToken { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public string? Username { get; set; }
}

/// <summary>
/// Kavita series → remote tracker id. An empty <see cref="RemoteId"/> means the
/// series is deliberately ignored for that service.
/// </summary>
public class ScrobbleMapping
{
    public int Id { get; set; }
    public int KavitaSeriesId { get; set; }
    public string Service { get; set; } = string.Empty;
    public string RemoteId { get; set; } = string.Empty;
    /// <summary>library | weblink | derived | search | manual | ignored</summary>
    public string Method { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Last progress pushed (or observed) per Kavita series per tracker.</summary>
public class ScrobbleSyncState
{
    public int Id { get; set; }
    public int KavitaSeriesId { get; set; }
    public string Service { get; set; } = string.Empty;
    public int Chapter { get; set; }
    public int Volume { get; set; }
    public string? Status { get; set; }
    public string Title { get; set; } = string.Empty;
    public DateTime SyncedAt { get; set; }
    public string? Error { get; set; }
}

/// <summary>A series that could not be matched automatically and needs user review.</summary>
public class ScrobbleUnmatched
{
    public int Id { get; set; }
    public int KavitaSeriesId { get; set; }
    public string Service { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    /// <summary>JSON list of {id, title, url} search candidates.</summary>
    public string CandidatesJson { get; set; } = "[]";
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Scrobble activity log line (capped to the most recent 500 rows).</summary>
public class ScrobbleLogEntry
{
    public int Id { get; set; }
    public DateTime Timestamp { get; set; }
    /// <summary>info | warning | error</summary>
    public string Level { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
