using Maki.Core.Download;
using Microsoft.Extensions.Time.Testing;

namespace Maki.Core.Tests;

public class PageCacheManifestTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "maki-manifest-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Same_source_chapter_keeps_the_pages_already_fetched()
    {
        var key = PageCacheManifest.Key(1, "abc", 3);
        PageCacheManifest.Prepare(_dir, key);
        File.WriteAllText(Path.Combine(_dir, "000.jpg"), "page");

        Assert.False(PageCacheManifest.Prepare(_dir, key));
        Assert.True(File.Exists(Path.Combine(_dir, "000.jpg")));
    }

    [Fact]
    public void Another_mapping_empties_the_directory_before_reuse()
    {
        PageCacheManifest.Prepare(_dir, PageCacheManifest.Key(1, "abc", 3));
        File.WriteAllText(Path.Combine(_dir, "000.jpg"), "page from source A");

        Assert.True(PageCacheManifest.Prepare(_dir, PageCacheManifest.Key(2, "xyz", 3)));
        Assert.False(File.Exists(Path.Combine(_dir, "000.jpg")));
        Assert.Equal(PageCacheManifest.Key(2, "xyz", 3), File.ReadAllText(Path.Combine(_dir, PageCacheManifest.FileName)));
    }

    [Fact]
    public void Pages_without_a_manifest_are_not_trusted()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "000.jpg"), "unknown provenance");

        Assert.True(PageCacheManifest.Prepare(_dir, PageCacheManifest.Key(1, "abc", 3)));
        Assert.False(File.Exists(Path.Combine(_dir, "000.jpg")));
    }

    [Fact]
    public async Task A_body_that_stops_arriving_fails_instead_of_hanging()
    {
        var time = new FakeTimeProvider();
        var stallTimeout = TimeSpan.FromSeconds(60);
        using var content = new StreamContent(new StallingStream(time, chunks: 1, gap: TimeSpan.Zero, stall: stallTimeout));
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<TimeoutException>(() => PageDownloader.CopyWithStallTimeoutAsync(
            content, destination, "https://cdn.test/1.jpg", stallTimeout, time, CancellationToken.None));
        Assert.Equal(4, destination.Length);
    }

    [Fact]
    public async Task A_slow_body_that_keeps_arriving_is_not_cut_off()
    {
        var time = new FakeTimeProvider();
        using var content = new StreamContent(new StallingStream(time, chunks: 6, gap: TimeSpan.FromSeconds(50)));
        using var destination = new MemoryStream();

        await PageDownloader.CopyWithStallTimeoutAsync(
            content, destination, "https://cdn.test/1.jpg", TimeSpan.FromSeconds(60), time, CancellationToken.None);

        Assert.Equal(24, destination.Length);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_a_stall()
    {
        var time = new FakeTimeProvider();
        using var cts = new CancellationTokenSource();
        using var content = new StreamContent(new StallingStream(time, chunks: 1, gap: TimeSpan.Zero, stall: TimeSpan.Zero, onStall: cts.Cancel));
        using var destination = new MemoryStream();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PageDownloader.CopyWithStallTimeoutAsync(
            content, destination, "https://cdn.test/1.jpg", TimeSpan.FromSeconds(60), time, cts.Token));
    }

    [Fact]
    public async Task A_slow_write_is_not_counted_as_a_stall()
    {
        var time = new FakeTimeProvider();
        using var content = new StreamContent(new StallingStream(time, chunks: 3, gap: TimeSpan.Zero));
        using var destination = new SlowWriteStream(time, TimeSpan.FromSeconds(90));

        await PageDownloader.CopyWithStallTimeoutAsync(
            content, destination, "https://cdn.test/1.jpg", TimeSpan.FromSeconds(60), time, CancellationToken.None);

        Assert.Equal(12, destination.Length);
    }

    /// <summary>Moves the fake clock forward on every write, the way a slow disk or share would.</summary>
    private sealed class SlowWriteStream(FakeTimeProvider time, TimeSpan perWrite) : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            time.Advance(perWrite);
            return base.WriteAsync(buffer, ct);
        }
    }

    /// <summary>
    /// Hands out <c>chunks</c> reads of a few bytes, moving the fake clock forward by <c>gap</c>
    /// before each one after the first. After the last chunk it either ends the body, or moves the
    /// clock by <c>stall</c> and then honours cancellation, the way a real socket read would once
    /// the stall timer has fired. Every wait is a clock advance, so nothing here depends on how
    /// promptly the test runner schedules timers.
    /// </summary>
    private sealed class StallingStream(
        FakeTimeProvider time, int chunks, TimeSpan gap, TimeSpan? stall = null, Action? onStall = null) : Stream
    {
        private int _sent;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_sent < chunks)
            {
                if (_sent > 0)
                {
                    time.Advance(gap);
                }

                ct.ThrowIfCancellationRequested();
                _sent++;
                "page"u8.CopyTo(buffer.Span);
                return ValueTask.FromResult(4);
            }

            if (stall is { } stallFor)
            {
                onStall?.Invoke();
                time.Advance(stallFor);
                ct.ThrowIfCancellationRequested();
                throw new InvalidOperationException("The stall timer did not fire");
            }

            return ValueTask.FromResult(0);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
