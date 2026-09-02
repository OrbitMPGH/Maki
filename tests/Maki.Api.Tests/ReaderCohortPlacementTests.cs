using Maki.Api.Services;
using Maki.Metadata.ReaderCohorts;
using Xunit;

namespace Maki.Api.Tests;

/// <summary>
/// Where a reader lands against the shipped cohorts, and why the surface built on it is a hint
/// rather than a second score.
/// </summary>
public class ReaderCohortPlacementTests
{
    /// <summary>
    /// The claim the whole feature rests on: somebody who OWNS a lot of one thing and READS
    /// another is placed by what they read. Nothing here even sees the shelf — the caller passes
    /// the read population — but the weighting has to actually follow it rather than drifting to
    /// whichever cohort is largest.
    /// </summary>
    [Fact]
    public void AReaderIsPlacedByWhatTheyFinished()
    {
        // Cohort 0 finishes the romcoms, cohort 1 the action titles, and cohort 1 is three times
        // the size so a rate that forgot to divide by cohort size would pick it every time.
        var index = Index(
            cohortReaders: [100, 300],
            global: [(1L, 40), (2L, 40), (3L, 40), (4L, 40)],
            cells:
            [
                (1L, 0, 60), (2L, 0, 55),
                (3L, 1, 180), (4L, 1, 165),
            ]);

        var romcom = ReaderCohortService.Place(index, [1L, 2L], topCohorts: 2);
        var action = ReaderCohortService.Place(index, [3L, 4L], topCohorts: 2);

        Assert.True(romcom[0] > romcom.GetValueOrDefault(1));
        Assert.True(action[1] > action.GetValueOrDefault(0));
    }

    /// <summary>
    /// A reader whose finished series the artifact has never heard of cannot be placed, and that
    /// has to come back as "no cohorts" rather than as an even split across all of them — an even
    /// split is the all-readers average wearing a personal label.
    /// </summary>
    [Fact]
    public void AnUnplaceableReaderGetsNoCohorts()
    {
        var index = Index(
            cohortReaders: [100],
            global: [(1L, 40)],
            cells: [(1L, 0, 60)]);

        Assert.Empty(ReaderCohortService.Place(index, [999L], topCohorts: 5));
    }

    /// <summary>
    /// Weights are a mix over the cohorts kept, so they have to sum to one however many survive:
    /// the hint divides by them, and a mix that summed to something else would scale every score.
    /// </summary>
    [Fact]
    public void WeightsAreAMixThatSumsToOne()
    {
        var index = Index(
            cohortReaders: [100, 100, 100],
            global: [(1L, 40), (2L, 40)],
            cells: [(1L, 0, 60), (1L, 1, 30), (2L, 2, 20)]);

        var weights = ReaderCohortService.Place(index, [1L, 2L], topCohorts: 3);

        Assert.Equal(1.0, weights.Values.Sum(), 6);
    }

    /// <summary>
    /// The cap is a cap. Placing into every cohort would average the answer back to the crowd mean,
    /// which is the number the hint exists to differ from.
    /// </summary>
    [Fact]
    public void NoMoreCohortsAreKeptThanAsked()
    {
        var index = Index(
            cohortReaders: [100, 100, 100, 100],
            global: [(1L, 40)],
            cells: [(1L, 0, 60), (1L, 1, 50), (1L, 2, 40), (1L, 3, 30)]);

        Assert.Equal(2, ReaderCohortService.Place(index, [1L], topCohorts: 2).Count);
    }

    /// <summary>
    /// A title nearly every cohort finished says almost nothing about which cohort somebody belongs
    /// to, and the inverse-frequency term is what expresses that. Measured it is very nearly inert
    /// on the real artifact, because the cohort rates are already popularity-normalised — this
    /// pins the intent so nobody removes it believing it was doing the opposite.
    /// </summary>
    [Fact]
    public void ARareTitleMovesPlacementMoreThanAUniversalOne()
    {
        var index = Index(
            cohortReaders: [100, 100],
            // Series 1 is niche (40 of 200 readers), series 2 is universal (198 of 200).
            global: [(1L, 40), (2L, 198)],
            cells: [(1L, 0, 40), (2L, 1, 99), (2L, 0, 99)]);

        var weights = ReaderCohortService.Place(index, [1L, 2L], topCohorts: 2);

        // Both cohorts finished the universal title equally, so only the niche one separates them.
        Assert.True(weights[0] > weights.GetValueOrDefault(1));
    }

    private static ReaderCohortIndex Index(
        int[] cohortReaders,
        (long Id, int Completions)[] global,
        (long Id, int Cohort, int Completions)[] cells) =>
        ReaderCohortIndexBuilder.Build(
            globalRows: [.. global.Select(g => (g.Id, g.Completions, 0, (float?)null))],
            cohortRows: [.. cells.Select(c => (c.Id, c.Cohort, c.Completions, 0, (float?)null))],
            cohortReaders: cohortReaders,
            completionP99: 100,
            generatedAt: null);
}
