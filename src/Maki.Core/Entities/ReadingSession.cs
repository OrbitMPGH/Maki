using Maki.Core.Security;

namespace Maki.Core.Entities;

/// <summary>
/// One sitting of reading, stitched server-side from progress reports using a 10 minute gap rule:
/// a report within 10 minutes of the previous sitting's end extends it, otherwise a new row starts.
/// Append-only and never purged.
/// </summary>
public class ReadingSession : IUserOwned
{
    public long Id { get; set; }
    public int UserId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime EndedAt { get; set; }
    public int ActiveSeconds { get; set; }
    public int ChaptersCompleted { get; set; }
}
