using Maki.Api.Services;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Xunit;

namespace Maki.Api.Tests;

/// <summary>
/// The cohort rail draws its slots before hydration, so the content-rating ceiling has to be part
/// of the draw's predicate or a restricted reader gets a short rail.
/// </summary>
public class ReaderCohortRailCeilingTests
{
    private const int Dim = 4;

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
