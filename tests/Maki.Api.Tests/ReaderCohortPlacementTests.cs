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
    /// The mix is the cohorts' affinities in proportion, not a sharpened version of them. This used
    /// to subtract the weakest kept cohort, which drove it to exactly zero and widened whatever lead
    /// the first cohort already had; under the slot draw that turns straight into screen space, so a
    /// reader who is 60/40 between two cohorts has to get a 60/40 mix rather than 100/0.
    /// </summary>
    [Fact]
    public void WeightsAreTheCohortAffinitiesInProportion()
    {
        // Cohort 0 finished twice as much of the reader's series as cohort 1, at equal cohort size.
        var index = Index(
            cohortReaders: [100, 100],
            global: [(1L, 40)],
            cells: [(1L, 0, 60), (1L, 1, 30)]);

        var weights = ReaderCohortService.Place(index, [1L], topCohorts: 2);

        Assert.Equal(2.0, weights[0] / weights[1], 6);
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

    /// <summary>
    /// The rail's whole reason for damping. Series 2 is finished by nearly everybody, so it has a
    /// high completion rate inside the reader's own cohort too — undamped it wins, which is the
    /// "list of the most popular series everyone has" the rail exists not to be. Dividing the
    /// overall rate back out puts the cohort's distinctive pick first instead.
    /// </summary>
    [Fact]
    public void DampingDemotesWhatEveryCohortFinished()
    {
        // Ten cohorts of 100, so a title everybody reads can actually have a high overall rate.
        // With only two cohorts it cannot: one cohort is half the population, so its own rate and
        // the global rate stay within a factor of two of each other and no damping short of pure
        // lift separates them.
        var index = Index(
            cohortReaders: [100, 100, 100, 100, 100, 100, 100, 100, 100, 100],
            // Series 1 is cohort 0's own: 30 of its 100 readers and almost nobody else's, 3% overall.
            // Series 2 is read by half of every cohort, so 50% overall and 50% inside cohort 0 too.
            global: [(1L, 30), (2L, 500)],
            cells: [(1L, 0, 30), (2L, 0, 50)]);

        var weights = new Dictionary<int, double> { [0] = 1.0 };
        var owned = new HashSet<long>();

        var undamped = ReaderCohortService.Rank(index, weights, owned, null, 10, damping: 0);
        var damped = ReaderCohortService.Rank(index, weights, owned, null, 10, damping: 0.5);

        // Undamped, the title half of everyone finished outranks the cohort's own by rate alone.
        Assert.Equal(2L, undamped[0]);
        Assert.Equal(1L, damped[0]);
    }

    /// <summary>
    /// The other half of the same rule, and the one that stops damping being read as "always prefer
    /// the obscure": a series this cohort genuinely reads far more than the population does keeps
    /// its place, because its rate is high for a reason the lift agrees with.
    /// </summary>
    [Fact]
    public void ACohortsOwnFavouriteSurvivesDamping()
    {
        var index = Index(
            cohortReaders: [100, 100, 100, 100, 100, 100, 100, 100, 100, 100],
            global: [(1L, 200), (2L, 60)],
            // Cohort 0 accounts for most of series 1's readers and few of series 2's.
            cells: [(1L, 0, 80), (2L, 0, 12)]);

        var damped = ReaderCohortService.Rank(
            index, new Dictionary<int, double> { [0] = 1.0 }, new HashSet<long>(), null, 10, 0.5);

        Assert.Equal(1L, damped[0]);
    }

    /// <summary>
    /// A rail recommending something already on the shelf is noise whether or not it was opened,
    /// so the exclusion is the whole library rather than just what was read.
    /// </summary>
    [Fact]
    public void OwnedSeriesNeverReachTheRail()
    {
        var index = Index(
            cohortReaders: [100],
            global: [(1L, 40), (2L, 40)],
            cells: [(1L, 0, 35), (2L, 0, 30)]);

        var ranked = ReaderCohortService.Rank(
            index, new Dictionary<int, double> { [0] = 1.0 }, new HashSet<long> { 1L }, null, 10, 0.5);

        Assert.Equal([2L], ranked);
    }

    /// <summary>
    /// Filters narrow the ranking rather than deleting rows out of an already-cut page, or a genre
    /// filter would return fewer than the rail asked for and read as "the cohorts had nothing".
    /// </summary>
    [Fact]
    public void AFilterNarrowsTheRankingRatherThanThePage()
    {
        var index = Index(
            cohortReaders: [100],
            global: [(1L, 40), (2L, 40), (3L, 40)],
            cells: [(1L, 0, 35), (2L, 0, 30), (3L, 0, 25)]);

        var ranked = ReaderCohortService.Rank(
            index, new Dictionary<int, double> { [0] = 1.0 }, new HashSet<long>(),
            accept: id => id != 1L, limit: 2, damping: 0.5);

        Assert.Equal([2L, 3L], ranked);
    }

    /// <summary>
    /// A series the artifact has cohort rows for but no all-readers row has no denominator, and a
    /// lift with no denominator is the cohort's own popularity wearing a lift's name.
    /// </summary>
    [Fact]
    public void ACandidateWithNoGlobalRowIsSkipped()
    {
        var index = Index(
            cohortReaders: [100],
            global: [(1L, 40)],
            cells: [(1L, 0, 35), (2L, 0, 90)]);

        var ranked = ReaderCohortService.Rank(
            index, new Dictionary<int, double> { [0] = 1.0 }, new HashSet<long>(), null, 10, 0.5);

        Assert.Equal([1L], ranked);
    }

    /// <summary>
    /// The failure this rail actually shipped with. A tight cohort produces much higher completion
    /// rates than a broad one — its readers concentrate on a few thousand titles instead of
    /// spreading over fifteen thousand — so summing every cohort's weighted lift into one list hands
    /// the strongest cohort <em>every</em> rank, not just the top few. A reader who had finished
    /// fifteen romances and three action manhwa got a rail of nothing but action manhwa. Here the
    /// broad cohort's lifts are roughly a ninth of the tight one's and it still has to reach the
    /// screen.
    /// </summary>
    [Fact]
    public void ABroadCohortStillReachesTheRailBesideATightOne()
    {
        var index = Index(
            cohortReaders: [100, 1000],
            global: [(1L, 60), (2L, 60), (3L, 60), (11L, 80), (12L, 80), (13L, 80)],
            cells:
            [
                (1L, 0, 40), (2L, 0, 38), (3L, 0, 36),
                (11L, 1, 50), (12L, 1, 48), (13L, 1, 46),
            ]);

        var ranked = ReaderCohortService.Rank(
            index, new Dictionary<int, double> { [0] = 0.6, [1] = 0.4 }, new HashSet<long>(),
            accept: null, limit: 5, damping: 0.5);

        Assert.Equal([1L, 11L, 2L, 12L, 3L], ranked);
    }

    /// <summary>
    /// Slots follow the mix, so the rail is readable as "this much of you is that cohort". Three
    /// quarters of the weight is six of eight slots, and the quarter that is left really does get
    /// the other two rather than a rounding error.
    /// </summary>
    [Fact]
    public void SlotsAreSharedInProportionToTheMix()
    {
        var index = Index(
            cohortReaders: [100, 100],
            global: [.. Enumerable.Range(0, 8).SelectMany(i => new[] { (1L + i, 40), (11L + i, 40) })],
            cells:
            [
                .. Enumerable.Range(0, 8).Select(i => (1L + i, 0, 60 - i)),
                .. Enumerable.Range(0, 8).Select(i => (11L + i, 1, 60 - i)),
            ]);

        var ranked = ReaderCohortService.Rank(
            index, new Dictionary<int, double> { [0] = 0.75, [1] = 0.25 }, new HashSet<long>(),
            accept: null, limit: 8, damping: 0.5);

        Assert.Equal(6, ranked.Count(id => id < 10));
        Assert.Equal(2, ranked.Count(id => id >= 10));
    }

    /// <summary>
    /// A cohort that runs out of candidates has to leave the draw rather than keep winning rounds it
    /// cannot fill, or the rail comes back short — and, since its banked credit only ever grows, the
    /// loop would never end.
    /// </summary>
    [Fact]
    public void AnExhaustedCohortDoesNotStallTheRail()
    {
        var index = Index(
            cohortReaders: [100, 100],
            global: [(1L, 40), (2L, 40), (3L, 40), (4L, 40), (11L, 40)],
            cells:
            [
                (1L, 0, 60), (2L, 0, 55), (3L, 0, 50), (4L, 0, 45),
                (11L, 1, 60),
            ]);

        var ranked = ReaderCohortService.Rank(
            index, new Dictionary<int, double> { [0] = 0.5, [1] = 0.5 }, new HashSet<long>(),
            accept: null, limit: 5, damping: 0.5);

        Assert.Equal(5, ranked.Count);
        Assert.Single(ranked, id => id == 11L);
    }

    /// <summary>
    /// Two cohorts wanting the same series is the normal case, not an edge one. It takes a single
    /// slot and the second cohort reaches further down its own list, so agreement between a reader's
    /// cohorts never costs the rail a row.
    /// </summary>
    [Fact]
    public void ASeriesTwoCohortsWantTakesOneSlot()
    {
        var index = Index(
            cohortReaders: [100, 100],
            global: [(1L, 40), (2L, 40), (3L, 40)],
            cells:
            [
                (1L, 0, 60), (2L, 0, 50),
                (1L, 1, 60), (3L, 1, 50),
            ]);

        var ranked = ReaderCohortService.Rank(
            index, new Dictionary<int, double> { [0] = 0.5, [1] = 0.5 }, new HashSet<long>(),
            accept: null, limit: 3, damping: 0.5);

        Assert.Equal([1L, 3L, 2L], ranked);
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
