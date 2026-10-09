using Maki.Metadata.CoRead;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Maki.Metadata.Tests;

public class ArtifactCacheFailureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "maki-failmemo-" + Guid.NewGuid().ToString("N"));

    public ArtifactCacheFailureTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void A_failure_stands_until_the_file_changes()
    {
        var path = Path.Combine(_dir, "a.db");
        File.WriteAllText(path, "one");
        var memo = new LoadFailureMemo();

        Assert.False(memo.ShouldSkip(path));
        memo.Record(memo.Observe(path));
        Assert.True(memo.ShouldSkip(path));

        File.WriteAllText(path, "two, longer");
        Assert.False(memo.ShouldSkip(path));
    }

    [Fact]
    public void A_file_replaced_during_the_load_is_not_remembered_as_the_one_that_failed()
    {
        var path = Path.Combine(_dir, "a.db");
        File.WriteAllText(path, "old");
        var memo = new LoadFailureMemo();
        var observedAtStart = memo.Observe(path);

        File.WriteAllText(path, "the replacement, longer");
        memo.Record(observedAtStart);

        Assert.False(memo.ShouldSkip(path));
    }

    [Fact]
    public void A_failure_lapses_after_the_backoff_and_on_clear()
    {
        var path = Path.Combine(_dir, "a.db");
        File.WriteAllText(path, "one");
        var now = DateTime.UtcNow;
        var memo = new LoadFailureMemo(() => now);

        memo.Record(memo.Observe(path));
        now += LoadFailureMemo.Backoff - TimeSpan.FromSeconds(1);
        Assert.True(memo.ShouldSkip(path));
        now += TimeSpan.FromSeconds(2);
        Assert.False(memo.ShouldSkip(path));

        memo.Record(memo.Observe(path));
        memo.Clear();
        Assert.False(memo.ShouldSkip(path));
    }

    [Fact]
    public async Task A_corrupt_graph_is_read_once_until_the_file_changes()
    {
        var path = Path.Combine(_dir, "coread-edges.db");
        File.WriteAllText(path, "this is not a sqlite file");
        var logger = new CountingLogger();
        var cache = new CoReadCache(new CoReadOptions(path, _dir), logger);

        Assert.Null(await cache.GetAsync());
        Assert.Null(await cache.GetAsync());
        Assert.Null(await cache.GetAsync());

        Assert.Equal(1, logger.Warnings);

        cache.Invalidate();
        Assert.Null(await cache.GetAsync());
        Assert.Equal(2, logger.Warnings);
    }

    private sealed class CountingLogger : ILogger<CoReadCache>
    {
        private int _warnings;
        public int Warnings => _warnings;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Interlocked.Increment(ref _warnings);
            }
        }
    }
}
