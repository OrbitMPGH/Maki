using System.IO.Compression;
using System.Text;
using Maki.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public sealed class ReaderArchiveCacheTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("maki-archive-cache-").FullName;
    private readonly ManualClock _clock = new();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    private ReaderArchiveCache NewCache() => new(NullLogger<ReaderArchiveCache>.Instance, _clock);

    private string WriteZip(string relative, params (string Name, string Body)[] entries)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var staging = path + ".tmp";
        using (var archive = ZipFile.Open(staging, ZipArchiveMode.Create))
        {
            foreach (var (name, body) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(body);
            }
        }

        File.Move(staging, path, overwrite: true);
        return path;
    }

    private static string Pages(int count) => string.Join(",", Enumerable.Range(1, count));

    private static async Task<string?> ReadAsync(ReaderArchiveCache cache, string path, string entry)
    {
        await using var stream = await cache.OpenPageAsync(path, entry, CancellationToken.None);
        if (stream is null) return null;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task Concurrent_misses_for_one_archive_read_it_once_and_pages_reuse_that_read()
    {
        var path = WriteZip("a/vol.cbz", Enumerable.Range(1, 40).Select(i => ($"{i:000}.jpg", $"page {i}")).ToArray());
        var cache = NewCache();
        var size = new FileInfo(path).Length;

        var results = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => cache.GetAsync(7, size, path))));

        Assert.All(results, r => Assert.Same(results[0], r));
        Assert.Equal(40, results[0].Pages.Count);
        Assert.Equal(1, cache.Loads);

        var pages = await Task.WhenAll(Enumerable.Range(1, 40)
            .Select(i => Task.Run(() => ReadAsync(cache, path, $"{i:000}.jpg"))));

        Assert.Equal(Enumerable.Range(1, 40).Select(i => $"page {i}"), pages);
        Assert.Equal(1, cache.Handles.Parses);
    }

    [Fact]
    public async Task A_replaced_file_is_reparsed_instead_of_read_through_the_old_directory()
    {
        var path = WriteZip("b/ch.cbz", ("001.jpg", "old"));
        var cache = NewCache();

        Assert.Equal("old", await ReadAsync(cache, path, "001.jpg"));

        WriteZip("b/ch.cbz", ("000.jpg", "cover"), ("001.jpg", "replacement"));

        Assert.Equal("replacement", await ReadAsync(cache, path, "001.jpg"));
        Assert.Equal("cover", await ReadAsync(cache, path, "000.jpg"));
        Assert.Equal(2, cache.Handles.Parses);
    }

    // Windows refuses a File.Move over an open file and a rename of a folder holding one, even
    // with FileShare.Delete, so the cache must not keep the archive open between requests.
    [Fact]
    public async Task No_file_handle_is_held_between_reads()
    {
        var path = WriteZip("series/ch.cbz", ("001.jpg", "old"));
        var cache = NewCache();
        Assert.Equal("old", await ReadAsync(cache, path, "001.jpg"));

        WriteZip("series/ch.cbz", ("001.jpg", "new"));
        var moved = Path.Combine(_root, "renamed");
        Directory.Move(Path.GetDirectoryName(path)!, moved);

        Assert.Equal("new", await ReadAsync(cache, Path.Combine(moved, "ch.cbz"), "001.jpg"));
    }

    [Fact]
    public async Task Unreadable_archives_and_entries_are_null()
    {
        var cache = NewCache();
        var notZip = Path.Combine(_root, "junk.cbz");
        await File.WriteAllTextAsync(notZip, "not a zip");
        var ok = WriteZip("c/ok.cbz", ("001.jpg", "page"));

        Assert.Null(await ReadAsync(cache, notZip, "001.jpg"));
        Assert.Null(await ReadAsync(cache, ok, "missing.jpg"));
        Assert.Empty((await cache.GetAsync(1, 9, notZip)).Pages);
    }

    [Fact]
    public async Task Parsed_directories_are_bounded_and_expire_when_idle()
    {
        var cache = NewCache();
        var paths = Enumerable.Range(0, ReaderArchiveHandles.Capacity + 3)
            .Select(i => WriteZip($"d/{i}.cbz", ("001.jpg", "p")))
            .ToList();

        foreach (var path in paths)
        {
            await ReadAsync(cache, path, "001.jpg");
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(ReaderArchiveHandles.Capacity, cache.Handles.Count);

        _clock.Advance(ReaderArchiveHandles.IdleTimeout + TimeSpan.FromSeconds(1));
        await ReadAsync(cache, paths[0], "001.jpg");

        Assert.Equal(1, cache.Handles.Count);
    }

    [Fact]
    public async Task Invalidate_forgets_the_page_list_and_the_parsed_directory()
    {
        var path = WriteZip("e/ch.cbz", ("001.jpg", "p"));
        var cache = NewCache();
        var size = new FileInfo(path).Length;
        await cache.GetAsync(3, size, path);

        cache.Invalidate(3);
        await cache.GetAsync(3, size, path);

        Assert.Equal(2, cache.Loads);
        Assert.Equal(2, cache.Handles.Parses);
    }

    [Fact]
    public async Task A_cancelled_waiter_does_not_stop_the_load_or_the_other_waiters()
    {
        var path = WriteZip("f/vol.cbz", Enumerable.Range(1, 40).Select(i => ($"{i:000}.jpg", $"page {i}")).ToArray());
        var cache = NewCache();
        var size = new FileInfo(path).Length;

        using var cts = new CancellationTokenSource();
        var cancelling = Task.Run(() => cache.GetAsync(11, size, path, cts.Token));
        var others = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => cache.GetAsync(11, size, path)))
            .ToArray();
        cts.Cancel();

        // Whether the cancelled call was the single-flight winner (whose own load ignores its
        // caller's token) or a follower (whose wait was cancelled), the load itself must still
        // finish and every other waiter must get its result.
        try
        {
            await cancelling;
        }
        catch (OperationCanceledException)
        {
        }

        var results = await Task.WhenAll(others);
        Assert.All(results, r => Assert.Same(results[0], r));
        Assert.Equal(40, results[0].Pages.Count);
        Assert.Equal(1, cache.Loads);
    }

    [Fact]
    public async Task A_load_that_throws_fails_every_waiter_and_is_not_cached()
    {
        var cache = NewCache();

        // No path at all: LoadAsync throws before it ever touches the filesystem, which is fast
        // enough that concurrent callers rarely land on the same single-flight task rather than
        // each becoming their own winner - so this pins the invariant that holds either way:
        // nobody swallows the failure, and the key is left in a state the next call can retry.
        var waiters = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() => cache.GetAsync(13, 5, null!, CancellationToken.None)))
            .ToArray();

        foreach (var waiter in waiters)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => waiter);
        }

        var loadsAfterFailure = cache.Loads;
        Assert.True(loadsAfterFailure >= 1);

        // Nothing was cached from the failed attempt(s), so a fresh call for the same key re-runs
        // the load instead of returning a cached failure or a stale result.
        var goodPath = WriteZip("g/ch.cbz", ("001.jpg", "p"));
        var size = new FileInfo(goodPath).Length;
        var info = await cache.GetAsync(13, size, goodPath);

        Assert.Single(info.Pages);
        Assert.True(cache.Loads > loadsAfterFailure);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }
}
