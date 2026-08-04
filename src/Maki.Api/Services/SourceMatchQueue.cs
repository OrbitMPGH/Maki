using System.Threading.Channels;

namespace Maki.Api.Services;

/// <summary>
/// Hands series ids to <see cref="SourceMatchWorkerHostedService"/> for background auto-matching.
/// <para>
/// Unbounded and ids-only, the same shape as the download queue channel: the durable record of what
/// still needs matching is <c>Series.SourceMatchPending</c>, so nothing is lost if the channel is
/// dropped on shutdown — the worker re-queues every flagged series on the next start.
/// </para>
/// </summary>
public class SourceMatchQueue
{
    private readonly Channel<int> channel = Channel.CreateUnbounded<int>(
        new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<int> Reader => channel.Reader;

    public void Enqueue(int seriesId) => channel.Writer.TryWrite(seriesId);
}
