using System.Threading.Channels;

namespace Maki.Api.Services;

/// <summary>
/// Which match lane a series waits in. <see cref="Interactive"/> is read first, so somebody adding
/// one series is never stuck behind a library import or an import list working through hundreds.
/// </summary>
public enum SourceMatchLane
{
    Interactive,
    Background
}

/// <summary>
/// Hands series ids to <see cref="SourceMatchWorkerHostedService"/>: two match lanes, and a link
/// stage for library imports whose files can only be linked once the match has synced chapters.
/// <para>
/// Unbounded and ids-only, the same shape as the download queue channel: the durable record of what
/// still needs doing is <c>Series.SourceMatchPending</c> and <c>Series.PendingImportLink</c>, so
/// nothing is lost if the channels are dropped on shutdown. The worker re-queues every flagged
/// series on the next start.
/// </para>
/// </summary>
public class SourceMatchQueue
{
    private readonly Channel<int> interactive = Channel.CreateUnbounded<int>();
    private readonly Channel<int> background = Channel.CreateUnbounded<int>();
    private readonly Channel<int> link = Channel.CreateUnbounded<int>(
        new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<int> LinkReader => link.Reader;

    public void Enqueue(int seriesId, SourceMatchLane lane = SourceMatchLane.Interactive) =>
        (lane == SourceMatchLane.Interactive ? interactive : background).Writer.TryWrite(seriesId);

    public void EnqueueLink(int seriesId) => link.Writer.TryWrite(seriesId);

    internal bool TryReadMatch(out int seriesId) =>
        interactive.Reader.TryRead(out seriesId) || background.Reader.TryRead(out seriesId);

    /// <summary>The next series to match, interactive lane first.</summary>
    public async ValueTask<int> ReadMatchAsync(CancellationToken ct)
    {
        while (true)
        {
            if (TryReadMatch(out var id))
            {
                return id;
            }

            await Task.WhenAny(
                interactive.Reader.WaitToReadAsync(ct).AsTask(),
                background.Reader.WaitToReadAsync(ct).AsTask());
            ct.ThrowIfCancellationRequested();
        }
    }
}
