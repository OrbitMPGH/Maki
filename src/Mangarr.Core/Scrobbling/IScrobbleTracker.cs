using Mangarr.Core.Entities;

namespace Mangarr.Core.Scrobbling;

/// <summary>Internal reading status shared by all trackers.</summary>
public enum ScrobbleStatus
{
    Reading,
    Completed,
    PlanToRead,
    /// <summary>A user-set status we never stomp implicitly (paused, dropped, ...).</summary>
    Other,
}

/// <summary>The user's current list entry (and series totals) on a tracker.</summary>
public record RemoteEntry(
    int ProgressChapter = 0,
    int ProgressVolume = 0,
    ScrobbleStatus? Status = null, // null = not on the user's list
    int? TotalChapters = null,
    int? TotalVolumes = null,
    string Title = "");

/// <summary>A search result offered for matching.</summary>
public record ScrobbleCandidate(string Id, string Title, IReadOnlyList<string> AltTitles, string Url);

public class TrackerException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Persistence for tracker tokens (implemented over the DB in Mangarr.Api).</summary>
public interface IScrobbleTokenStore
{
    Task<ScrobbleToken?> GetAsync(string service, CancellationToken ct = default);
    Task SaveAsync(ScrobbleToken token, CancellationToken ct = default);
    Task DeleteAsync(string service, CancellationToken ct = default);
}

/// <summary>
/// One scrobble target site. Statuses passed to <see cref="UpdateAsync"/> are only
/// ever Reading, Completed or PlanToRead.
/// </summary>
public interface IScrobbleTracker
{
    /// <summary>Stable lowercase key persisted in mappings/sync state ("anilist", "mal", "mangabaka").</summary>
    string Name { get; }
    string Label { get; }
    /// <summary>True when the tracker uses OAuth (needs a Connect/Disconnect flow in the UI).</summary>
    bool UsesOAuth { get; }

    /// <summary>Credentials (client id/secret or PAT) are present in settings.</summary>
    Task<bool> ConfiguredAsync(CancellationToken ct = default);
    /// <summary>A usable user token/PAT exists.</summary>
    Task<bool> AuthenticatedAsync(CancellationToken ct = default);
    Task<string?> UsernameAsync(CancellationToken ct = default);

    Task<RemoteEntry> GetEntryAsync(string remoteId, CancellationToken ct = default);
    Task UpdateAsync(string remoteId, int chapter, int volume, ScrobbleStatus status, CancellationToken ct = default);

    /// <summary>
    /// Pushes the user's rating to the tracker. <paramref name="score"/> is on the internal 1–10
    /// scale (0 clears the score where the tracker supports it). Implementations map to their own
    /// scale (MAL 0–10, AniList 0–100).
    /// </summary>
    Task UpdateRatingAsync(string remoteId, int score, CancellationToken ct = default);
    Task<IReadOnlyList<ScrobbleCandidate>> SearchAsync(string title, CancellationToken ct = default);

    string EntryUrl(string remoteId);
}
