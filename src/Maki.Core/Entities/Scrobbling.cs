using Maki.Core.Security;

namespace Maki.Core.Entities;

/// <summary>
/// OAuth/PAT credentials for one scrobble tracker ("anilist" | "mal" | "mangabaka"), belonging to
/// one user. The key is <c>(UserId, Service)</c>: an app registration (client id/secret) is
/// per-instance and stays in <c>AppConfig</c>, but the token it is exchanged for names a person's
/// account on the remote site, so it cannot be shared.
/// </summary>
public class ScrobbleToken : IUserOwned
{
    public int UserId { get; set; }
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
public class ScrobbleMapping : IUserOwned
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int KavitaSeriesId { get; set; }
    public string Service { get; set; } = string.Empty;
    public string RemoteId { get; set; } = string.Empty;
    /// <summary>library | weblink | derived | search | manual | ignored</summary>
    public string Method { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Last progress pushed (or observed) per Kavita series per tracker.</summary>
public class ScrobbleSyncState : IUserOwned
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int KavitaSeriesId { get; set; }
    public string Service { get; set; } = string.Empty;
    public int Chapter { get; set; }
    public int Volume { get; set; }
    public string? Status { get; set; }
    public string Title { get; set; } = string.Empty;
    public DateTime SyncedAt { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Last progress pushed per <em>local</em> series per tracker, for series the built-in
/// reader tracks but Kavita has never reported. Deliberately a separate table rather than a
/// nullable second key on <see cref="ScrobbleSyncState"/>: that key space is shared with
/// ScrobbleMapping and ScrobbleUnmatched, reverse-derived by title in SeriesController, and
/// accepted in ScrobbleController request bodies — half-migrating it is how the Kavita path
/// would regress. Remote ids come straight off the Series cross-id columns, so there is no
/// mapping or unmatched-review flow here.
/// </summary>
public class SeriesScrobbleState : IUserOwned
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int SeriesId { get; set; }
    public string Service { get; set; } = string.Empty;
    public int Chapter { get; set; }
    public int Volume { get; set; }
    public string? Status { get; set; }
    public DateTime SyncedAt { get; set; }
    public string? Error { get; set; }
}

/// <summary>A series that could not be matched automatically and needs user review.</summary>
public class ScrobbleUnmatched : IUserOwned
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int KavitaSeriesId { get; set; }
    public string Service { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    /// <summary>JSON list of {id, title, url} search candidates.</summary>
    public string CandidatesJson { get; set; } = "[]";
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Scrobble activity log line (capped to the most recent 500 rows per user).</summary>
public class ScrobbleLogEntry : IUserOwned
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public DateTime Timestamp { get; set; }
    /// <summary>info | warning | error</summary>
    public string Level { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
