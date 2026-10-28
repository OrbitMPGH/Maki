using Maki.Metadata.MangaBaka;
using Microsoft.Extensions.Time.Testing;

namespace Maki.Metadata.Tests;

/// <summary>The idle timeout on catalogue and model downloads: a quiet socket fails, a slow one does not.</summary>
public class StallTimeoutStreamTests
{
    private static readonly TimeSpan Stall = TimeSpan.FromSeconds(60);

    private readonly FakeTimeProvider _time = new();

    /// <summary>Hands out one byte per read once <paramref name="wait"/> completes.</summary>
    private sealed class TrickleStream(int bytes, Func<CancellationToken, Task> wait) : Stream
    {
        private int _left = bytes;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await wait(ct);
            if (_left == 0)
            {
                return 0;
            }

            _left--;
            buffer.Span[0] = 1;
            return 1;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static Task Never(CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);

    [Fact]
    public async Task A_slow_but_steady_body_is_read_to_the_end()
    {
        // Every read takes most of a stall window, so the whole body spans many windows.
        var slow = new TrickleStream(15, _ =>
        {
            _time.Advance(Stall * 0.9);
            return Task.CompletedTask;
        });
        await using var stream = new StallTimeoutStream(slow, Stall, _time);
        using var output = new MemoryStream();

        await stream.CopyToAsync(output);

        Assert.Equal(15, output.Length);
    }

    [Fact]
    public async Task A_body_that_stops_arriving_times_out()
    {
        await using var stream = new StallTimeoutStream(new TrickleStream(5, Never), Stall, _time);

        var copy = stream.CopyToAsync(Stream.Null);
        _time.Advance(Stall);

        await Assert.ThrowsAsync<TimeoutException>(() => copy);
    }

    [Fact]
    public async Task Time_spent_between_reads_does_not_count_as_a_stall()
    {
        await using var stream = new StallTimeoutStream(new TrickleStream(2, _ => Task.CompletedTask), Stall, _time);
        var buffer = new byte[1];

        Assert.Equal(1, await stream.ReadAsync(buffer));
        _time.Advance(Stall * 2);
        Assert.Equal(1, await stream.ReadAsync(buffer));
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_not_reported_as_a_stall()
    {
        await using var stream = new StallTimeoutStream(new TrickleStream(5, Never), Stall, _time);
        using var cts = new CancellationTokenSource();

        var read = stream.ReadAsync(new byte[1], cts.Token).AsTask();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
    }
}
