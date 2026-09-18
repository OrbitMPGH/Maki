using Maki.Core.Security;

namespace Maki.Core.Entities;

/// <summary>
/// Where one anime sits on a watcher's list. Narrower than <c>ScrobbleStatus</c> on purpose: that
/// one describes what Maki may push back to a tracker, while this one only has to describe what a
/// reader already did, and "dropped" is the part that carries the most meaning here.
/// </summary>
public enum AnimeWatchStatus { Watching, Completed, OnHold, Dropped, Planning }

/// <summary>
/// One entry from a connected tracker's <em>anime</em> list, plus whatever manga it was matched to.
/// <para>
/// Stored rather than derived per request because the match is expensive: MyAnimeList needs one
/// call per anime to learn its related manga, so a re-sync that re-matched everything would cost a
/// few hundred requests for a list that barely moved. <see cref="MatchAttemptedAtUtc"/> is what
/// keeps that cost to new rows.
/// </para>
/// </summary>
public class AnimeSignal : IUserOwned
{
    public long Id { get; set; }
    public int UserId { get; set; }

    /// <summary>The tracker's <c>Name</c>: "anilist" or "mal".</summary>
    public string Service { get; set; } = string.Empty;

    /// <summary>The anime's id on <see cref="Service"/>.</summary>
    public long AnimeId { get; set; }

    public string? Title { get; set; }

    /// <summary>The watcher's score, normalized to 1-10. Null when they never scored it.</summary>
    public int? Score { get; set; }

    public AnimeWatchStatus Status { get; set; }

    /// <summary>The catalogue series this anime's source manga resolved to, when it resolved.</summary>
    public long? MangaBakaId { get; set; }

    public long? AniListMangaId { get; set; }
    public long? MalMangaId { get; set; }

    /// <summary>
    /// When the relation lookup last ran, successful or not. Null means it never has, which is the
    /// only case the sync spends a per-anime request on.
    /// </summary>
    public DateTime? MatchAttemptedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
