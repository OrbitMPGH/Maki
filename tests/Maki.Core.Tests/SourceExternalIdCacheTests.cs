using Maki.Core.Sources;

namespace Maki.Core.Tests;

/// <summary>The external-id lookup shares one fetch, and one failure, with the callers queued behind it.</summary>
public class SourceExternalIdCacheTests
{
    private sealed class SlowSource : ISource
    {
        public string Name => "slow";
        public string DisplayName => Name;
        public string BaseUrl => "https://slow.test";
        public SourceCapabilities Capabilities => SourceCapabilities.None;

        public int Calls;
        public Exception? Throws;

        public async Task<IReadOnlyDictionary<string, string>?> GetExternalIdsAsync(
            string sourceSeriesId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            await Task.Delay(200, ct);
            return Throws is null ? new Dictionary<string, string> { ["mal"] = "1" } : throw Throws;
        }

        public Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
            string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task Concurrent_lookups_share_one_fetch()
    {
        var source = new SlowSource();
        var cache = new SourceExternalIdCache(TimeProvider.System);

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => cache.GetAsync(source, "s")));

        Assert.Equal(1, source.Calls);
        Assert.All(results, r => Assert.Equal("1", r!["mal"]));
    }

    [Fact]
    public async Task Callers_queued_behind_a_failure_share_it_and_a_timeout_stays_a_timeout()
    {
        var source = new SlowSource { Throws = new TaskCanceledException("HttpClient.Timeout", new TimeoutException()) };
        var cache = new SourceExternalIdCache(TimeProvider.System);

        var calls = Enumerable.Range(0, 3).Select(_ => cache.GetAsync(source, "s")).ToList();

        await Assert.ThrowsAsync<TaskCanceledException>(() => calls[0]);
        await Assert.ThrowsAsync<TimeoutException>(() => calls[1]);
        await Assert.ThrowsAsync<TimeoutException>(() => calls[2]);
        Assert.Equal(1, source.Calls);
    }
}
