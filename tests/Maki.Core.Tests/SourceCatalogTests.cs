using Maki.Core.Sources;
using Microsoft.Extensions.Time.Testing;

namespace Maki.Core.Tests;

/// <summary>
/// The cached-catalog search shared by the sources with no search endpoint (TCB Scans,
/// MANGA Plus, Flame Comics). Covers the title ranking and the TTL cache each of them
/// used to carry its own copy of.
/// </summary>
public class SourceCatalogTests
{
    private static SourceSeriesResult Entry(string title) =>
        new(title.ToLowerInvariant(), title, $"https://x.test/{title}");

    private static Func<CancellationToken, Task<List<SourceSeriesResult>>> Returning(params string[] titles) =>
        _ => Task.FromResult(titles.Select(Entry).ToList());

    [Theory]
    [InlineData("One Piece", "one piece", 3)] // punctuation and case are normalized away
    [InlineData("One Piece", "one-piece!", 3)]
    [InlineData("One Piece: Colour Walk", "one piece", 2)] // candidate starts with the query
    [InlineData("One Piece", "one piece colour walk", 2)] // query starts with the candidate
    [InlineData("The Legend of One Piece", "one piece", 1)] // contained mid-title
    [InlineData("Naruto", "one piece", 0)]
    [InlineData("", "one piece", 0)]
    public void Scoring_prefers_exact_then_prefix_then_containment(string candidate, string query, int expected) =>
        Assert.Equal(expected, SourceCatalog.ScoreOf(SourceCatalog.Normalize(query), SourceCatalog.Normalize(candidate)));

    [Fact]
    public async Task Search_returns_best_matches_first()
    {
        var catalog = new SourceCatalog(TimeSpan.FromMinutes(1));

        var results = await catalog.SearchAsync(
            "one piece",
            Returning("The Legend of One Piece", "One Piece: Colour Walk", "Naruto", "One Piece"));

        Assert.Equal(
            ["One Piece", "One Piece: Colour Walk", "The Legend of One Piece"],
            results.Select(r => r.Title));
    }

    [Fact]
    public async Task Empty_query_never_fetches_the_catalog()
    {
        var catalog = new SourceCatalog(TimeSpan.FromMinutes(1));
        var fetches = 0;

        var results = await catalog.SearchAsync("   ", ct =>
        {
            fetches++;
            return Returning("One Piece")(ct);
        });

        Assert.Empty(results);
        Assert.Equal(0, fetches);
    }

    [Fact]
    public async Task Catalog_is_fetched_once_within_its_ttl()
    {
        var catalog = new SourceCatalog(TimeSpan.FromMinutes(10));
        var fetches = 0;
        Task<List<SourceSeriesResult>> Fetch(CancellationToken ct)
        {
            fetches++;
            return Returning("One Piece")(ct);
        }

        await catalog.SearchAsync("one piece", Fetch);
        await catalog.SearchAsync("naruto", Fetch);
        await catalog.LoadAsync(Fetch);

        Assert.Equal(1, fetches);
    }

    [Fact]
    public async Task An_empty_catalog_is_held_for_a_minute_and_then_refetched()
    {
        // A failed or half-served fetch must not pin an empty catalog for the whole TTL, but it
        // must not be refetched on every search either.
        var time = new FakeTimeProvider();
        var catalog = new SourceCatalog(TimeSpan.FromMinutes(10), time);
        var fetches = 0;
        Task<List<SourceSeriesResult>> Fetch(CancellationToken ct)
        {
            fetches++;
            return Task.FromResult(fetches == 1 ? new List<SourceSeriesResult>() : Entry("One Piece").Yield());
        }

        Assert.Empty(await catalog.SearchAsync("one piece", Fetch));
        time.Advance(TimeSpan.FromSeconds(59));
        Assert.Empty(await catalog.SearchAsync("one piece", Fetch));
        Assert.Equal(1, fetches);

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Single(await catalog.SearchAsync("one piece", Fetch));
        Assert.Equal(2, fetches);
    }

    [Fact]
    public async Task A_populated_catalog_is_refetched_after_its_ttl()
    {
        var time = new FakeTimeProvider();
        var catalog = new SourceCatalog(TimeSpan.FromMinutes(10), time);
        var fetches = 0;
        Task<List<SourceSeriesResult>> Fetch(CancellationToken ct)
        {
            fetches++;
            return Returning("One Piece")(ct);
        }

        await catalog.LoadAsync(Fetch);
        time.Advance(TimeSpan.FromMinutes(9));
        await catalog.LoadAsync(Fetch);
        Assert.Equal(1, fetches);

        time.Advance(TimeSpan.FromMinutes(2));
        await catalog.LoadAsync(Fetch);
        Assert.Equal(2, fetches);
    }

    [Fact]
    public async Task Concurrent_callers_share_one_fetch()
    {
        var catalog = new SourceCatalog(TimeSpan.FromMinutes(10));
        var fetches = 0;
        var gate = new TaskCompletionSource();

        async Task<List<SourceSeriesResult>> Fetch(CancellationToken ct)
        {
            Interlocked.Increment(ref fetches);
            await gate.Task;
            return [Entry("One Piece")];
        }

        var waiters = Enumerable.Range(0, 5).Select(_ => catalog.LoadAsync(Fetch)).ToList();
        gate.SetResult();
        await Task.WhenAll(waiters);

        Assert.Equal(1, fetches);
        Assert.All(waiters, w => Assert.Single(w.Result));
    }
}

file static class ListExtensions
{
    public static List<T> Yield<T>(this T item) => [item];
}
