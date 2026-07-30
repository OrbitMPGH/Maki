using Maki.Core.Security;

namespace Maki.Data.Identity;

/// <summary>
/// The parts of a series that belong to the reader rather than to the series: their score, and their
/// per-series reader override. Both used to be columns on <c>Series</c> — a shared row — which meant
/// one person's rating was pushed to <em>another</em> person's AniList profile and one person's
/// right-to-left preference applied to everybody.
/// <para>
/// Rows are created on demand: no row means "unrated, reader defaults", which is also what a fresh
/// user starts with, so nothing has to seed this table when an account is created.
/// </para>
/// </summary>
public class UserSeriesState : IUserOwned
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int SeriesId { get; set; }

    /// <summary>
    /// The user's own rating on a 1–10 scale (null = unrated). Pushed as a score to <em>their</em>
    /// connected trackers (MAL 0–10, AniList 0–100, MangaBaka) and used to weight the recommendation
    /// seed vector — highly-rated series pull recommendations harder than unrated ones.
    /// </summary>
    public int? Rating { get; set; }

    /// <summary>
    /// Per-series built-in-reader display override, as a <c>ReaderPrefsSpec</c> JSON blob; null means
    /// "use this user's global defaults". Opaque to the server apart from the one serializer in
    /// <c>ReaderPrefsSpec</c> — this is what lets a manhwa open vertical and left-to-right while
    /// manga stays paged and right-to-left.
    /// </summary>
    public string? ReaderPrefsJson { get; set; }

    public DateTime UpdatedAt { get; set; }
}
