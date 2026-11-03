using Maki.Core.Security;

namespace Maki.Core.Entities;

/// <summary>
/// A title the reader took off Home's "Continue from the anime" rail. Keyed on the MangaBaka id
/// because most cards there have no <see cref="Series"/> row, and so the choice survives an add.
/// Only the rail reads it; the series page callout has its own dismissal on <c>UserSeriesState</c>.
/// </summary>
public class AnimeResumeRailDismissal : IUserOwned
{
    public long Id { get; set; }
    public int UserId { get; set; }
    public int MangaBakaId { get; set; }

    /// <summary>
    /// The anime frontier the card showed when it was dismissed: it comes back once a newly watched
    /// season moves the frontier past this. Null keeps it off the rail for good.
    /// </summary>
    public double? CoveredTo { get; set; }

    public DateTime DismissedAtUtc { get; set; }
}
