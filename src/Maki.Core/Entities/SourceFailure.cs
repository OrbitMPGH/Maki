namespace Maki.Core.Entities;

/// <summary>
/// One download attempt that failed because of the source. The queue row cannot hold this: a failover
/// moves the row onto the next mapping and a retry flips it back to Queued, so the failure would be
/// forgotten or blamed on whichever source the row ended on. Kept for <see cref="Quality.SourceReliability.Window"/>.
/// </summary>
public class SourceFailure
{
    public int Id { get; set; }
    public int SourceMappingId { get; set; }
    public SourceMapping? SourceMapping { get; set; }
    public int SeriesId { get; set; }
    public string ErrorKey { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; }
}
