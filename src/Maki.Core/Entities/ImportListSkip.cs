using Maki.Core.Security;

namespace Maki.Core.Entities;

/// <summary>
/// Persisted as an int. Append only, never renumber.
/// </summary>
public enum ImportListSkipReason
{
    /// <summary>No MangaBaka id resolved. Deleting the row makes the next run try again.</summary>
    Unmatched = 0,

    /// <summary>Added by an import list and later deleted from the library. Never re-added.</summary>
    Removed = 1,

    /// <summary>The user dismissed it. Never retried.</summary>
    Ignored = 2,

    /// <summary>
    /// Added or requested by an import list, or already in the library or requested when the list
    /// reached it. Provenance, flipped to Removed on delete.
    /// </summary>
    Added = 3,
}

/// <summary>
/// One remote list entry an import list run has already dealt with, per (user, tracker, remote id).
/// <para>
/// Doubles as the provenance record for what a run added, rather than a second table: a row with
/// <see cref="ImportListSkipReason.Added"/> is written when the series is created or requested, and
/// <c>SeriesController.Delete</c> flips every such row for that <see cref="MangaBakaId"/> to
/// <see cref="ImportListSkipReason.Removed"/>. Without that, deleting an imported series would bring
/// it straight back on the next run, since it is still on the user's remote list. Any row, whatever
/// its reason, keeps the entry out of later runs.
/// </para>
/// </summary>
public class ImportListSkip : IUserOwned
{
    public int Id { get; set; }
    public int UserId { get; set; }

    /// <summary>The tracker's <c>IScrobbleTracker.Name</c>.</summary>
    public string Service { get; set; } = string.Empty;

    public string RemoteId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public ImportListSkipReason Reason { get; set; }

    /// <summary>The resolved MangaBaka id. Null for <see cref="ImportListSkipReason.Unmatched"/>.</summary>
    public int? MangaBakaId { get; set; }

    public DateTime CreatedAt { get; set; }
}
