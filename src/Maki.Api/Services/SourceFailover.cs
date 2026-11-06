namespace Maki.Api.Services;

/// <summary>
/// When a download that keeps failing should give up on its source and move to the next linked one.
/// A 404 already does that at once; the failures here are the slower kind, where the source answers
/// with an error, refuses us, or cannot be reached, and a retry on the same mapping just repeats it.
/// </summary>
public static class SourceFailover
{
    /// <summary>Failures on one source before the item moves on.</summary>
    public const int FailuresPerSource = 3;

    /// <summary>
    /// Only failures that say something about the source. A challenge or a rate limit has its own
    /// cooldown and clears by itself, and a disk or page error would follow the item to any source.
    /// </summary>
    public static bool CountsAgainstSource(string reasonKey) =>
        reasonKey is DownloadFailureReason.SourceRejected or DownloadFailureReason.SourceError or DownloadFailureReason.Network;

    /// <param name="reasonKey">The key the failure being recorded classified as.</param>
    /// <param name="failureCount">The item's <c>RetryCount</c> including this failure.</param>
    /// <param name="pinned">The user picked this source ("Find better copy"); falling back would hand them another.</param>
    /// <param name="healthRepair">A health repair is tied to the source the user approved.</param>
    /// <param name="upgrade">An upgrade is a specific candidate copy, not just the chapter.</param>
    /// <remarks>
    /// Every <see cref="FailuresPerSource"/>th failure rather than a counter of its own: the retry count
    /// already survives restarts and is never reset, so the cap on automatic retries still bounds how
    /// often an item can hop.
    /// </remarks>
    public static bool ShouldFailOver(string reasonKey, int failureCount, bool pinned, bool healthRepair, bool upgrade) =>
        !pinned && !healthRepair && !upgrade && CountsAgainstSource(reasonKey) &&
        failureCount > 0 && failureCount % FailuresPerSource == 0;
}
