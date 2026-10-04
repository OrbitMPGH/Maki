using Maki.Metadata.MangaBaka;

namespace Maki.Metadata.Tests;

/// <summary>The idle timeout on catalogue and model downloads: a quiet socket fails, a slow one does not.</summary>
public class StallTimeoutStreamTests
{
    private static readonly TimeSpan Stall = TimeSpan.FromMilliseconds(300);

    /// <summary>Hands out one byte per read after <paramref name="delay"/>, or never when it is infinite.</summary>
    private sealed class TrickleStream(int bytes, TimeSpan delay) : Stream
    {
        private int _left = bytes;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(delay, ct);
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

    [Fact]
    public async Task A_slow_but_steady_body_is_read_to_the_end()
    {
        // Six reads at half the stall each: well past the stall in total, never quiet for that long.
        await using var stream = new StallTimeoutStream(new TrickleStream(5, Stall / 2), Stall);
        using var output = new MemoryStream();

        await stream.CopyToAsync(output);

        Assert.Equal(5, output.Length);
    }

    [Fact]
    public async Task A_body_that_stops_arriving_times_out()
    {
        await using var stream = new StallTimeoutStream(new TrickleStream(5, Timeout.InfiniteTimeSpan), Stall);

        await Assert.ThrowsAsync<TimeoutException>(() => stream.CopyToAsync(Stream.Null));
    }

    [Fact]
    public async Task Time_spent_between_reads_does_not_count_as_a_stall()
    {
        await using var stream = new StallTimeoutStream(new TrickleStream(2, TimeSpan.Zero), Stall);
        var buffer = new byte[1];

        Assert.Equal(1, await stream.ReadAsync(buffer));
        await Task.Delay(Stall * 2);
        Assert.Equal(1, await stream.ReadAsync(buffer));
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_not_reported_as_a_stall()
    {
        await using var stream = new StallTimeoutStream(new TrickleStream(5, Timeout.InfiniteTimeSpan), TimeSpan.FromMinutes(1));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream.ReadAsync(new byte[1], cts.Token).AsTask());
    }
}
