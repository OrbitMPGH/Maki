using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Metadata.MangaBaka;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// The shared Discover rail caches: one build per ceiling serves readers with and without feedback.
/// </summary>
public class DiscoverRailCacheTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Answers every browse query with as many picks as asked for, and counts them.</summary>
    private sealed class CountingStore() : MangaBakaLocalStore(
        new MangaBakaDumpOptions("", ""), new FakeAppSettings(), NullLogger<MangaBakaLocalStore>.Instance)
    {
        private int _queries;
        public int Queries => _queries;
        public TaskCompletionSource? Gate { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public override async Task<IReadOnlyList<MangaBakaRecommendation>> GetBrowseAsync(
            BrowseFeed feed, int limit, string? genre = null,
            RecommendationFilters? filters = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _queries);
            if (Gate is { } gate)
            {
                Entered.TrySetResult();
                await gate.Task.WaitAsync(ct);
            }

            return [.. Enumerable.Range(1, limit).Select(i => new MangaBakaRecommendation(
                $"{i}", $"Pick {i}", null, null, null, SeriesStatus.Completed, 80, null, [], [], false, null, null))];
        }
    }

    private static DiscoverService Service(CountingStore store) =>
        new(store, null!, null!, null!, NullLogger<DiscoverService>.Instance);

    [Fact]
    public async Task One_build_serves_both_rail_depths()
    {
        var store = new CountingStore();
        var discover = Service(store);

        var shallow = await discover.GetFeedsAsync(refresh: false, ContentRating.Safe);
        var queries = store.Queries;
        var deep = await discover.GetFeedsAsync(
            refresh: false, ContentRating.Safe, depth: DiscoverService.RefillRailSize);

        Assert.Equal(queries, store.Queries);
        Assert.All(shallow, r => Assert.Equal(DiscoverService.RailSize, r.Items.Count));
        Assert.All(deep, r => Assert.Equal(DiscoverService.RefillRailSize, r.Items.Count));
    }
}
