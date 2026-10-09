using Maki.Api.Services;
using Maki.Metadata.Catalogue;
using Maki.Metadata.Tests;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Xunit;

namespace Maki.Api.Tests;

/// <summary>
/// Places where the content-rating ceiling has to be applied before hydration: the cohort rail's
/// draw, so a restricted reader does not get a short rail, and the creator work counts.
/// </summary>
public sealed class ContentCeilingTests : IDisposable
{
    private const int Dim = 4;

    private readonly DumpDbBuilder _dump = new();

    public void Dispose()
    {
        _dump.Dispose();
        SqliteConnection.ClearAllPools();
    }

    private DiscoverService Discover()
    {
        var options = new MangaBakaDumpOptions(_dump.Path, Path.GetTempPath());
        var embedding = new EmbeddingOptions("", "", "", EmbeddingModelProfile.Base);
        return new DiscoverService(
            new MangaBakaLocalStore(options, new FakeAppSettings(), NullLogger<MangaBakaLocalStore>.Instance),
            null!,
            new VectorIndexCache(embedding, options, NullLogger<VectorIndexCache>.Instance),
            new CatalogueIndexCache(options, NullLogger<CatalogueIndexCache>.Instance),
            NullLogger<DiscoverService>.Instance);
    }

    private void SeedCreator()
    {
        _dump.AddSeries(100, "Safe Work", contentRating: "safe", authorsJson: """["Circle A"]""")
            .AddSeries(101, "Adult Work", contentRating: "pornographic", authorsJson: """["Circle B"]""")
            .AddSeries(103, "Unscored Work", contentRating: "suggestive", authorsJson: """["Circle C"]""");
    }

    [Fact]
    public async Task ACreatorWithOnlyAdultWorksIsNotFoundForARestrictedCeiling()
    {
        SeedCreator();
        var request = new CreatorRequest("Circle B", Filters: new RecommendationFilters(ContentRatings: ContentRating.All));

        Assert.Null(await Discover().GetCreatorAsync(request, default, ContentRating.Safe));
        Assert.NotNull(await Discover().GetCreatorAsync(request, default, ContentRating.Pornographic));
    }

    [Fact]
    public async Task APickedRatingFilterNeverHidesACreatorFromAnUnrestrictedUser()
    {
        SeedCreator();
        var request = new CreatorRequest(
            "Circle B", Filters: new RecommendationFilters(ContentRatings: [ContentRating.Safe]));

        var profile = await Discover().GetCreatorAsync(request, default, ContentRating.Pornographic);

        Assert.NotNull(profile);
        Assert.Equal(1, profile.WorkCount);
    }

    [Fact]
    public async Task AnUnscoredWorkInsideTheCeilingKeepsItsCreatorVisible()
    {
        SeedCreator();
        var request = new CreatorRequest("Circle C");

        var profile = await Discover().GetCreatorAsync(request, default, ContentRating.Suggestive);

        Assert.NotNull(profile);
        Assert.Equal(1, profile.WorkCount);
    }

    [Fact]
    public async Task WorksTheIndexLacksAreCountedFromTheDump()
    {
        SeedCreator();
        var index = Build();

        var inIndexOnly = await Discover().VisibleWorkCountAsync(
            index, [100L], ContentRating.Allowed(ContentRating.Safe), default);
        var withUnscored = await Discover().VisibleWorkCountAsync(
            index, [100L, 103L], ContentRating.Allowed(ContentRating.Suggestive), default);

        Assert.Equal(1, inIndexOnly);
        Assert.Equal(2, withUnscored);
    }

    [Fact]
    public void ARestrictedCeilingRemovesOverCeilingTitlesFromTheDraw()
    {
        var index = Build();

        var before = ReaderCohortRailService.AcceptFor(index, RecommendationFilters.None);
        var filters = ReaderCohortRailService.WithCeiling(null, ContentRating.Safe)!;
        var after = ReaderCohortRailService.AcceptFor(index, filters);

        Assert.Equal([100L, 101L, 102L], new[] { 100L, 101L, 102L }.Where(before));
        Assert.Equal([100L], new[] { 100L, 101L, 102L }.Where(after));
    }

    [Fact]
    public void ARequestedRatingIsNarrowedToTheCeiling()
    {
        var filters = ReaderCohortRailService.WithCeiling(
            new RecommendationFilters(ContentRatings: [ContentRating.Safe, ContentRating.Pornographic]),
            ContentRating.Suggestive)!;

        Assert.Equal([ContentRating.Safe], filters.ContentRatings);
    }

    [Fact]
    public void AnUnrestrictedCeilingLeavesTheFiltersAlone()
    {
        Assert.Null(ReaderCohortRailService.WithCeiling(null, ContentRating.Pornographic));
    }

    private static VectorIndex Build()
    {
        long[] ids = [100, 101, 102];
        var count = ids.Length;
        var data = new sbyte[count * Dim];
        var scales = new float[count];
        for (var i = 0; i < count; i++)
        {
            var vector = new float[Dim];
            vector[i % Dim] = 1f;
            scales[i] = EmbeddingMath.Quantize(vector, data.AsSpan(i * Dim, Dim));
        }

        return VectorIndex.FromQuantized(
            ids,
            data,
            scales,
            Dim,
            new VectorIndexColumns(
                Years: Enumerable.Repeat(VectorIndex.Unknown, count).ToArray(),
                Ratings: Enumerable.Repeat(75f, count).ToArray(),
                Chapters: Enumerable.Repeat(100, count).ToArray(),
                Types: new byte[count],
                Statuses: new byte[count],
                Genres: JaggedInts.From(new int[count][]),
                Authors: JaggedInts.From(new int[count][]),
                Artists: JaggedInts.From(new int[count][]),
                Popularity: Enumerable.Repeat(VectorIndex.Unknown, count).ToArray(),
                TagBlobs: new byte[]?[count],
                ContentRatings: [0, 2, 3],
                Franchise: Enumerable.Repeat(VectorIndex.Unknown, count).ToArray()),
            new VectorIndexVocabularies(
                new Dictionary<string, byte>(), new Dictionary<string, byte>(),
                new Dictionary<string, int>(), new Dictionary<string, int>(),
                new Dictionary<string, int[]>(),
                new Dictionary<string, byte>
                {
                    [ContentRating.Safe] = 0,
                    [ContentRating.Suggestive] = 1,
                    [ContentRating.Erotica] = 2,
                    [ContentRating.Pornographic] = 3,
                }));
    }
}
