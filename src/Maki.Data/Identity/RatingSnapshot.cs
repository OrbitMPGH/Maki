using Maki.Core.Entities;
using Maki.Core.Security;

namespace Maki.Data.Identity;

/// <summary>
/// One change to a reader's score on a series, "so far" or final, with how far they had read. This
/// is the per-volume story ("a 7 at ch. 12, a 9 at the end") kept as history rather than as a
/// second rating system: nothing ranks or syncs from it.
/// </summary>
public class RatingSnapshot : IUserOwned
{
    public long Id { get; set; }
    public int UserId { get; set; }
    public int SeriesId { get; set; }
    public int Rating { get; set; }
    public bool Final { get; set; }

    /// <summary>The highest chapter number read when the score was set, or null with nothing read.</summary>
    public double? AtChapter { get; set; }

    public RatingSnapshotOrigin Origin { get; set; }
    public DateTime CreatedAt { get; set; }
}
