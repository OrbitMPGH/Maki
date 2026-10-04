using Maki.Api.Services;
using Microsoft.Data.Sqlite;
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
    private readonly string _vectorDir = Path.Combine(Path.GetTempPath(), "maki-discover-fallback-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _dump.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_vectorDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

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

    private EmbeddingOptions? _embedding;
    private MangaBakaDumpOptions? _dumpOptions;

    private DiscoverService Service(SemanticSearcher searcher, bool vectorIndex = false)
    {
        _dump.AddSeries(1, "Berserk", year: 1989, rating: 80, genresJson: """["Action"]""")
            .AddSeries(2, "Berserk Horror Edition", year: 2016, rating: 70, genresJson: """["Horror"]""")
            .AddSeries(3, "Berserk Deluxe", year: 2019, type: "manhwa", genresJson: """["Action"]""")
            .BuildSearchIndex();
        var options = new MangaBakaDumpOptions(_dump.Path, Path.GetTempPath());
        var store = new MangaBakaLocalStore(options, new FakeAppSettings(), NullLogger<MangaBakaLocalStore>.Instance);
        var embedding = new EmbeddingOptions("", "", "", EmbeddingModelProfile.Base);
        _dumpOptions = options;
        if (vectorIndex)
        {
            Directory.CreateDirectory(_vectorDir);
            embedding = new EmbeddingOptions(_vectorDir, Path.Combine(_vectorDir, "embeddings.db"), _vectorDir,
                EmbeddingModelProfile.Base with { Dimensions = 4 });
            var vectors = new EmbeddingStore(embedding);
            vectors.EnsureSchema();
            vectors.UpsertBatch([(1L, "h", [1f, 0f, 0f, 0f]), (2L, "h", [0f, 1f, 0f, 0f])]);
        }

        _embedding = embedding;

        return new DiscoverService(
            store,
            searcher,
            new VectorIndexCache(embedding, options, NullLogger<VectorIndexCache>.Instance),
            new CatalogueIndexCache(options, NullLogger<CatalogueIndexCache>.Instance),
            NullLogger<DiscoverService>.Instance);
    }

    private static DiscoverSearchRequest Narrowed(RecommendationFilters filters) => new("berserk", filters);

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
        Assert.Equal(["1", "3"], response.Items.Select(i => i.ProviderId).Order());
    }

    [Fact]
    public async Task A_model_that_fails_while_searching_falls_back_to_titles()
    {
        var searcher = new StubSearcher(canEmbed: true, SemanticSearchOutcome.NotAvailable);

        var response = await Service(searcher).SearchAsync(Request());

        Assert.Equal(1, searcher.Searches);
        Assert.Equal("title", response.Mode);
        Assert.Equal(["1", "3"], response.Items.Select(i => i.ProviderId).Order());
    }

    [Fact]
    public async Task A_semantic_search_that_matched_nothing_stays_empty()
    {
        var searcher = new StubSearcher(canEmbed: true, SemanticSearchOutcome.Empty);

        var response = await Service(searcher).SearchAsync(Request());

        Assert.Equal("semantic", response.Mode);
        Assert.Empty(response.Items);
    }

    [Fact]
    public async Task Title_fallback_applies_the_year_and_type_filters()
    {
        // The title index honours only the content rating, so a narrowed query answered by it used
        // to list every title that matched the words, whatever year or type was asked for.
        var searcher = new StubSearcher(canEmbed: false, SemanticSearchOutcome.Empty);
        var service = Service(searcher);

        var byYear = await service.SearchAsync(Narrowed(new RecommendationFilters(YearMin: 2010, ContentRatings: ["safe"])));
        var byType = await service.SearchAsync(Narrowed(new RecommendationFilters(Types: ["manhwa"], ContentRatings: ["safe"])));

        Assert.Equal("title", byYear.Mode);
        Assert.Equal(["2", "3"], byYear.Items.Select(i => i.ProviderId).Order());
        Assert.Equal("3", Assert.Single(byType.Items).ProviderId);
    }

    [Fact]
    public async Task Title_fallback_tests_indexed_hits_against_the_vector_index()
    {
        // Rows the vector index holds are tested there; the one it does not hold falls to the dump.
        var searcher = new StubSearcher(canEmbed: false, SemanticSearchOutcome.Empty);
        var service = Service(searcher, vectorIndex: true);
        var index = await new VectorIndexCache(_embedding!, _dumpOptions!, NullLogger<VectorIndexCache>.Instance).GetAsync();
        Assert.Equal(2, index?.Count);

        var response = await service.SearchAsync(Narrowed(new RecommendationFilters(
            Genres: ["Action"], ContentRatings: ["safe"])));

        Assert.Equal("title", response.Mode);
        Assert.Equal(["1", "3"], response.Items.Select(i => i.ProviderId).Order());
    }
}
