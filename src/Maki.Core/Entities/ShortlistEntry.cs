using Maki.Core.Security;

namespace Maki.Core.Entities;

/// <summary>
/// A catalogue title a reader wants to come back to without adding it. Keyed on the MangaBaka id
/// because a shortlisted title has no <see cref="Series"/> row. Not a taste signal: nothing in the
/// recommender reads this table.
/// </summary>
public class ShortlistEntry : IUserOwned
{
    public const int MaxPerUser = 2000;
    public const int TitleMaxLength = 300;
    public const int NoteMaxLength = 500;
    public const int SavedFromDetailMaxLength = 200;

    public long Id { get; set; }
    public int UserId { get; set; }
    public int MangaBakaId { get; set; }

    /// <summary>Display fallback for when the dump no longer has the row.</summary>
    public string Title { get; set; } = "";

    public string? Note { get; set; }
    public string SavedFrom { get; set; } = "";
    public string? SavedFromDetail { get; set; }

    /// <summary>The dump's status when saved, reset by "Got it".</summary>
    public SeriesStatus? SavedStatus { get; set; }

    /// <summary>The dump's chapter total when saved, reset by "Got it".</summary>
    public int? SavedTotalChapters { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
