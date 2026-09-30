using Maki.Api.Services;
using Maki.Core.Recommendations;
using Maki.Metadata.Catalogue;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Maki.Metadata.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// A never-show list makes every Discover search count as narrowed, which prefers the semantic
/// engine. When the query model cannot load, that must not turn into an empty answer: the title
/// index answers, with the never-show list still applied.
/// </summary>
public sealed class DiscoverSearchFallbackTests : IDisposable
{
    private readonly DumpDbBuilder _dump = new();

    public void Dispose() => _dump.Dispose();

    private sealed class StubSearcher(bool canEmbed, SemanticSearchOutcome outcome) : SemanticSearcher(
        null!, null!, null!, null!, null!, null!, null!, null!, NullLogger<SemanticSearcher>.Instance)
    {
        public int Searches { get; private set; }

        public override bool IsReady() => false;

        public override bool IsAvailable() => true;

        public override bool CanEmbed() => canEmbed;

        public override Task<SemanticSearchOutcome> SearchAsync(
            string query, RecommendationFilters? filters = null, int limit = 60, CancellationToken ct = default)
        {
            Searches++;
            return Task.FromResult(outcome);
        }
    }

    private DiscoverService Service(SemanticSearcher searcher)
    {
        _dump.AddSeries(1, "Berserk", genresJson: """["Action"]""")
            .AddSeries(2, "Berserk Horror Edition", genresJson: """["Horror"]""")
            .BuildSearchIndex();
        var options = new MangaBakaDumpOptions(_dump.Path, Path.GetTempPath());
        var store = new MangaBakaLocalStore(options, new FakeAppSettings(), NullLogger<MangaBakaLocalStore>.Instance);
        return new DiscoverService(
            store,
            searcher,
            new VectorIndexCache(new EmbeddingOptions("", "", "", EmbeddingModelProfile.Base), options,
                NullLogger<VectorIndexCache>.Instance),
            new CatalogueIndexCache(options, NullLogger<CatalogueIndexCache>.Instance),
            NullLogger<DiscoverService>.Instance);
    }

    private static DiscoverSearchRequest Request() => new(
        "berserk",
        new RecommendationFilters(
            ContentRatings: ["safe"],
            Hidden: [new CatalogueTerm(CatalogueRules.Genre, "Horror")]));

    [Fact]
    public async Task A_model_that_cannot_load_is_not_waited_on()
    {
        var searcher = new StubSearcher(canEmbed: false, SemanticSearchOutcome.Empty);

        var response = await Service(searcher).SearchAsync(Request());

        Assert.Equal(0, searcher.Searches);
        Assert.Equal("title", response.Mode);
        Assert.Equal("1", Assert.Single(response.Items).ProviderId);
    }

    [Fact]
    public async Task A_model_that_fails_while_searching_falls_back_to_titles()
    {
        var searcher = new StubSearcher(canEmbed: true, SemanticSearchOutcome.NotAvailable);

        var response = await Service(searcher).SearchAsync(Request());

        Assert.Equal(1, searcher.Searches);
        Assert.Equal("title", response.Mode);
        Assert.Equal("1", Assert.Single(response.Items).ProviderId);
    }

    [Fact]
    public async Task A_semantic_search_that_matched_nothing_stays_empty()
    {
        var searcher = new StubSearcher(canEmbed: true, SemanticSearchOutcome.Empty);

        var response = await Service(searcher).SearchAsync(Request());

        Assert.Equal("semantic", response.Mode);
        Assert.Empty(response.Items);
    }
}
