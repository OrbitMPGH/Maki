using Maki.Core.Download;

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
        using var content = new StreamContent(new StallingStream());
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<TimeoutException>(() => PageDownloader.CopyWithStallTimeoutAsync(
            content, destination, "https://cdn.test/1.jpg", TimeSpan.FromMilliseconds(100), CancellationToken.None));
        Assert.Equal(4, destination.Length);
    }

    /// <summary>Hands out a few bytes, then never completes another read until cancelled.</summary>
    private sealed class StallingStream : Stream
    {
        private bool _sent;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!_sent)
            {
                _sent = true;
                "page"u8.CopyTo(buffer.Span);
                return 4;
            }

            await Task.Delay(Timeout.Infinite, ct);
            return 0;
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
