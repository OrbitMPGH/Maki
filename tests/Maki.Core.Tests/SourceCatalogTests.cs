using Maki.Core.Sources;

namespace Maki.Core.Tests;

public class SourceCatalogTests
{
    [Fact]
    public async Task An_empty_catalog_is_not_refetched_on_the_next_search()
    {
        var catalog = new SourceCatalog(TimeSpan.FromHours(1));
        var fetches = 0;
        Task<List<SourceSeriesResult>> Fetch(CancellationToken _)
        {
            fetches++;
            return Task.FromResult(new List<SourceSeriesResult>());
        }

        await catalog.SearchAsync("one piece", Fetch);
        await catalog.SearchAsync("naruto", Fetch);

        Assert.Equal(1, fetches);
    }

    [Fact]
    public async Task A_populated_catalog_is_served_from_cache()
    {
        var catalog = new SourceCatalog(TimeSpan.FromHours(1));
        var fetches = 0;
        Task<List<SourceSeriesResult>> Fetch(CancellationToken _)
        {
            fetches++;
            return Task.FromResult(new List<SourceSeriesResult> { new("a", "One Piece", "https://x/a", null) });
        }

        var first = await catalog.SearchAsync("one piece", Fetch);
        await catalog.SearchAsync("one piece", Fetch);

        Assert.Single(first);
        Assert.Equal(1, fetches);
    }
}
