using Maki.Core.Entities;

namespace Maki.Core.Scrobbling;

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
    string Title = "",
    /// <summary>The user's score on the tracker, normalized to 1–10; null = unrated there.</summary>
    int? Score = null,
    /// <summary>
    /// Whether the tracker says the work is still being published (releasing, on hiatus, not yet
    /// out); null when it doesn't say.
    /// </summary>
    bool? Releasing = null,
    /// <summary>
    /// The tracker marks this entry as a re-read (AniList REPEATING), which <see cref="Status"/>
    /// reports as <see cref="ScrobbleStatus.Reading"/>.
    /// </summary>
    bool Repeating = false);

/// <summary>
/// One entry of a user's remote list, as returned by <see cref="IScrobbleTracker.ListAsync"/>.
/// Cross ids are whatever the tracker hands out alongside its own id (AniList exposes MAL ids,
/// Kitsu exposes mappings); null when unknown. MangaBakaId is only set by the MangaBaka tracker,
/// MangaUpdatesId (the lowercase base36 slug) only by the MangaUpdates tracker.
/// </summary>
public record RemoteListEntry(
    string RemoteId,
    ScrobbleStatus Status,
    string Title,
    long? AniListId = null,
    long? MalId = null,
    long? KitsuId = null,
    long? MangaBakaId = null,
    string? MangaUpdatesId = null);

/// <summary>A search result offered for matching.</summary>
public record ScrobbleCandidate(string Id, string Title, IReadOnlyList<string> AltTitles, string Url);

public class TrackerException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The tracker is refusing calls for a while (rate limit, upstream block). The message carries no
/// duration, since it is stored and read later; <see cref="Until"/> is the instant to render
/// relative to.
/// </summary>
public class TrackerBackOffException(string message, DateTimeOffset until) : TrackerException(message)
{
    public DateTimeOffset Until { get; } = until;
}

/// <summary>
/// The remote id no longer resolves on the tracker (a deleted or merged entry — AniList
/// answers such ids with an HTTP 404 "Not Found."). Signals the scrobble loop to drop the
/// stale mapping and re-match, rather than logging the same hard error every sync.
/// </summary>
public class TrackerEntryNotFoundException(string message, Exception? inner = null) : TrackerException(message, inner);

/// <summary>
/// Persistence for tracker tokens (implemented over the DB in Maki.Api), keyed by
/// <c>(userId, service)</c>.
/// <para>
/// The user id is explicit on every call rather than taken from an ambient "current user": the
/// scrobble tick is a background job that walks every connected account in turn, so there is no
/// current user to read, and getting it wrong means pushing one person's reading to another
/// person's AniList profile.
/// </para>
/// </summary>
public interface IScrobbleTokenStore
{
    Task<ScrobbleToken?> GetAsync(int userId, string service, CancellationToken ct = default);

    /// <summary>The owner comes from <see cref="ScrobbleToken.UserId"/>, which must be set.</summary>
    Task SaveAsync(ScrobbleToken token, CancellationToken ct = default);

    Task DeleteAsync(int userId, string service, CancellationToken ct = default);
}

/// <summary>
/// One scrobble target site. Statuses passed to <see cref="UpdateAsync"/> are only
/// ever Reading, Completed or PlanToRead.
/// <para>
/// Everything that reads or writes a remote list takes a <c>userId</c>, because the token it acts
/// with is that user's. Only <see cref="ConfiguredAsync"/> and <see cref="EntryUrl"/> don't: an app
/// registration (client id and secret) is per-instance, and a URL is just string formatting.
/// </para>
/// </summary>
public interface IScrobbleTracker
{
    /// <summary>Stable lowercase key persisted in mappings/sync state ("anilist", "mal", "mangabaka").</summary>
    string Name { get; }
    string Label { get; }

    /// <summary>When the tracker is backed off until, or null when it is not backing off.</summary>
    DateTimeOffset? BackOffUntil => null;

    /// <summary>True when the tracker uses OAuth (needs a Connect/Disconnect flow in the UI).</summary>
    bool UsesOAuth { get; }

    /// <summary>Credentials (client id/secret or PAT) are present in settings.</summary>
    Task<bool> ConfiguredAsync(CancellationToken ct = default);
    /// <summary>A usable token/PAT exists for this user.</summary>
    Task<bool> AuthenticatedAsync(int userId, CancellationToken ct = default);
    Task<string?> UsernameAsync(int userId, CancellationToken ct = default);

    Task<RemoteEntry> GetEntryAsync(int userId, string remoteId, CancellationToken ct = default);

    /// <summary>
    /// <see cref="GetEntryAsync"/> with <see cref="RemoteEntry.Score"/> filled in. A tracker whose score
    /// costs extra requests leaves it out of <see cref="GetEntryAsync"/>; only the rating import needs it.
    /// </summary>
    Task<RemoteEntry> GetEntryWithScoreAsync(int userId, string remoteId, CancellationToken ct = default) =>
        GetEntryAsync(userId, remoteId, ct);

    /// <param name="keepStatus">
    /// Write progress only and leave the entry's status as it is. Set when the entry is in a state
    /// the internal statuses cannot express (AniList's REPEATING), so a push does not overwrite it.
    /// </param>
    Task UpdateAsync(
        int userId, string remoteId, int chapter, int volume, ScrobbleStatus status,
        CancellationToken ct = default, bool keepStatus = false);

    /// <summary>
    /// Pushes the user's rating to the tracker. <paramref name="score"/> is on the internal 1–10
    /// scale (0 clears the score where the tracker supports it). Implementations map to their own
    /// scale (MAL 0–10, AniList 0–100).
    /// </summary>
    Task UpdateRatingAsync(int userId, string remoteId, int score, CancellationToken ct = default);

    Task<IReadOnlyList<ScrobbleCandidate>> SearchAsync(int userId, string title, CancellationToken ct = default);

    /// <summary>
    /// The user's whole manga list filtered to <paramref name="statuses"/>, paged through to the end.
    /// Used by import lists; throws <see cref="TrackerException"/> on auth or transport failure.
    /// </summary>
    Task<IReadOnlyList<RemoteListEntry>> ListAsync(
        int userId, IReadOnlyCollection<ScrobbleStatus> statuses, CancellationToken ct = default) =>
        throw new NotSupportedException($"{Name} cannot list entries");

    string EntryUrl(string remoteId);
}

/// <summary>One progress write handed to <see cref="IBatchScrobbleTracker.UpdateManyAsync"/>.</summary>
public record ScrobbleWrite(string RemoteId, int Chapter, int Volume, ScrobbleStatus Status, bool KeepStatus = false);

/// <summary>
/// A tracker that takes several progress writes in one call, for a site that limits how often a
/// list may be written rather than how much one write may carry.
/// </summary>
public interface IBatchScrobbleTracker : IScrobbleTracker
{
    int MaxBatch { get; }

    /// <summary>
    /// Writes <paramref name="writes"/> together. The result has one slot per write: null when it
    /// went through, otherwise why it did not. Only cancellation is thrown.
    /// </summary>
    Task<IReadOnlyList<Exception?>> UpdateManyAsync(
        int userId, IReadOnlyList<ScrobbleWrite> writes, CancellationToken ct = default);
}
