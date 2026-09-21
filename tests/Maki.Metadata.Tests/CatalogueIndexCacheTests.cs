using Maki.Metadata.Catalogue;
using Maki.Metadata.MangaBaka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Metadata.Tests;

public class CatalogueIndexCacheTests : IDisposable
{
    private readonly DumpDbBuilder _db = new();

    public void Dispose() => _db.Dispose();

    private CatalogueIndexCache Cache(ILogger<CatalogueIndexCache>? logger = null) => new(
        new MangaBakaDumpOptions(_db.Path, Path.GetDirectoryName(_db.Path)!),
        logger ?? NullLogger<CatalogueIndexCache>.Instance);

    [Fact]
    public async Task Different_queries_reuse_the_same_catalogue_indexes()
    {
        _db.AddSeries(1, "Berserk", authorsJson: """["Kentaro Miura"]""")
            .AddSeries(2, "Uzumaki", authorsJson: """["Junji Ito"]""")
            .BuildSearchIndex();
        var cache = Cache();
        var first = (await cache.GetAsync())!;
        Assert.True(first.Credits.TryResolve("Kentaro Miura", CreditRole.Author, out _));

        var later = (await cache.GetAsync())!;
        Assert.True(later.Credits.TryResolve("Junji Ito", CreditRole.Author, out _));
        Assert.Same(first, later);
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => cache.GetAsync()));
        Assert.All(concurrent, indexes => Assert.Same(first, indexes));

        cache.Invalidate();
        Assert.NotSame(first, await cache.GetAsync());
    }

    [Fact]
    public async Task A_waiting_search_rechecks_the_dump_stamp_before_reusing_an_index()
    {
        _db.AddSeries(1, "First", authorsJson: """["Original Author"]""").BuildSearchIndex();
        using var logger = new PausingLogger();
        var cache = Cache(logger);
        var first = cache.GetAsync();
        await logger.CreditsBuilt.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // This caller observes the old stamp, then waits behind the first build.
        var waiting = cache.GetAsync();
        Task<CatalogueIndexes?> latest;
        try
        {
            var oldStamp = File.GetLastWriteTimeUtc(_db.Path);
            _db.AddSeries(2, "New", authorsJson: """["New Author"]""");
            File.SetLastWriteTimeUtc(_db.Path, oldStamp.AddSeconds(2));
            latest = cache.GetAsync();
        }
        finally
        {
            logger.Resume.Set();
        }

        var original = (await first)!;
        var refreshed = (await waiting)!;
        Assert.False(original.Credits.TryResolve("New Author", CreditRole.Author, out _));
        Assert.True(refreshed.Credits.TryResolve("New Author", CreditRole.Author, out _));
        Assert.NotSame(original, refreshed);
        Assert.Same(refreshed, await latest);
        Assert.Same(refreshed, await cache.GetAsync());
        Assert.Equal(2, logger.Builds);
    }

    private sealed class PausingLogger : ILogger<CatalogueIndexCache>, IDisposable
    {
        public TaskCompletionSource CreditsBuilt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Resume { get; } = new();
        private int _builds;
        public int Builds => _builds;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!formatter(state, exception).StartsWith("Built the credit index:", StringComparison.Ordinal)) return;
            if (Interlocked.Increment(ref _builds) != 1) return;
            CreditsBuilt.SetResult();
            if (!Resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test did not release the build");
        }

        public void Dispose() => Resume.Dispose();
    }

    [Fact]
    public async Task An_idle_cache_gives_its_memory_back_and_rebuilds_on_the_next_read()
    {
        _db.AddSeries(1, "Berserk", authorsJson: """["Kentaro Miura"]""").BuildSearchIndex();
        var cache = Cache();
        var first = (await cache.GetAsync())!;

        // Just read, so nothing goes yet. This is the half that matters: a window that fired while
        // the cache was being read every second would reload it on a timer forever.
        Assert.False(cache.ReleaseIfIdle(TimeSpan.FromHours(1)));
        Assert.Same(first, await cache.GetAsync());

        Assert.True(cache.ReleaseIfIdle(TimeSpan.Zero));
        // Nothing to release twice, so a second tick is a no-op rather than another log line.
        Assert.False(cache.ReleaseIfIdle(TimeSpan.Zero));

        var rebuilt = (await cache.GetAsync())!;
        Assert.NotSame(first, rebuilt);
        Assert.True(rebuilt.Credits.TryResolve("Kentaro Miura", CreditRole.Author, out _));
    }

    [Fact]
    public async Task Releasing_while_a_reader_holds_the_indexes_leaves_that_reader_working()
    {
        _db.AddSeries(1, "Berserk", authorsJson: """["Kentaro Miura"]""").BuildSearchIndex();
        var cache = Cache();
        var held = (await cache.GetAsync())!;

        Assert.True(cache.ReleaseIfIdle(TimeSpan.Zero));

        // The point of there being no lock around the unload: a caller mid-request owns its own
        // reference, so dropping the cache's field cannot pull the indexes out from under it.
        Assert.True(held.Credits.TryResolve("Kentaro Miura", CreditRole.Author, out _));
    }
}
