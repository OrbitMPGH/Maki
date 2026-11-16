using Maki.Core.Scrobbling;

namespace Maki.Core.Tests;

public class ScrobblePlannerTests
{
    [Fact]
    public void PushesForwardProgress()
    {
        var entry = new RemoteEntry(ProgressChapter: 5, Status: ScrobbleStatus.Reading);
        var plan = ScrobblePlanner.Decide(entry, chapter: 12, volume: 0);

        Assert.True(plan.Write);
        Assert.Equal(12, plan.Chapter);
        Assert.Equal(ScrobbleStatus.Reading, plan.PushStatus);
    }

    [Fact]
    public void ACompletedSeriesCompletesAnEntryWhoseTotalCountsTheSkippedSpecials()
    {
        // 26 main chapters read, the three specials skipped, and the tracker counts all 29.
        var entry = new RemoteEntry(ProgressChapter: 26, Status: ScrobbleStatus.Reading, TotalChapters: 29);
        var plan = ScrobblePlanner.Decide(entry, chapter: 26, volume: 0, held: ScrobbleStatus.Completed);

        Assert.Equal((true, 29, ScrobbleStatus.Completed), (plan.Write, plan.Chapter, plan.PushStatus));
    }

    [Fact]
    public void ACompletedSeriesWithNoTrackerTotalKeepsTheMark()
    {
        var entry = new RemoteEntry(ProgressChapter: 20, Status: ScrobbleStatus.Reading);
        var plan = ScrobblePlanner.Decide(entry, chapter: 26, volume: 3, held: ScrobbleStatus.Completed);

        Assert.Equal((true, 26, 3, ScrobbleStatus.Completed), (plan.Write, plan.Chapter, plan.Volume, plan.PushStatus));
    }

    [Fact]
    public void ACompletedSeriesWithNoNewProgressStillCompletesTheEntry()
    {
        var entry = new RemoteEntry(ProgressChapter: 26, ProgressVolume: 3, Status: ScrobbleStatus.Paused,
            TotalChapters: 29, TotalVolumes: 4);
        var plan = ScrobblePlanner.Decide(entry, chapter: 0, volume: 0, held: ScrobbleStatus.Completed);

        Assert.Equal((true, 29, 4, ScrobbleStatus.Completed), (plan.Write, plan.Chapter, plan.Volume, plan.PushStatus));
    }

    [Fact]
    public void AnAlreadyCompletedEntryKeepsItsOwnProgressShape()
    {
        var entry = new RemoteEntry(ProgressChapter: 20, Status: ScrobbleStatus.Completed, TotalChapters: 29);
        var plan = ScrobblePlanner.Decide(entry, chapter: 26, volume: 0, held: ScrobbleStatus.Completed);

        Assert.Equal((26, ScrobbleStatus.Completed), (plan.Chapter, plan.PushStatus));
    }

    [Fact]
    public void AnAlreadyCompletedEntryIsLeftAlone()
    {
        var entry = new RemoteEntry(ProgressChapter: 29, Status: ScrobbleStatus.Completed, TotalChapters: 29);

        Assert.False(ScrobblePlanner.Decide(entry, chapter: 26, volume: 0, held: ScrobbleStatus.Completed).Write);
    }

    [Fact]
    public void AFinishedOneShotPushesCompletedAtChapterOne()
    {
        // A one-shot never raises the chapter mark, so without the finished signal it stays at 0
        // and an existing entry would never be touched again.
        var entry = new RemoteEntry(Status: ScrobbleStatus.PlanToRead);
        var plan = ScrobblePlanner.Decide(entry, chapter: 0, volume: 0, finished: true, completedLocally: true);

        Assert.True(plan.Write);
        Assert.Equal(1, plan.Chapter);
        Assert.Equal(ScrobbleStatus.Completed, plan.PushStatus);
    }

    [Fact]
    public void ASpecialOnlyReadOfAMultiChapterSeriesPushesNothing()
    {
        // Only an unnumbered special of a 20 chapter series was read: the caller hands over the raw
        // chapter 0 and the planner must not turn it into chapter 1 Reading.
        var entry = new RemoteEntry(ProgressChapter: 0, Status: ScrobbleStatus.PlanToRead, TotalChapters: 20);
        var plan = ScrobblePlanner.Decide(entry, chapter: 0, volume: 0, finished: true, completedLocally: true);

        Assert.False(plan.Write);
        Assert.Equal(0, plan.Chapter);
        Assert.Equal(ScrobbleStatus.PlanToRead, plan.RecordStatus);
    }

    [Fact]
    public void AnUnknownStatusOneShotNeedsTheTrackerToConfirmItIsOver()
    {
        // Neither the library nor the tracker says the work is finished: absence of evidence.
        var entry = new RemoteEntry(Status: ScrobbleStatus.PlanToRead, TotalChapters: null, Releasing: null);
        var plan = ScrobblePlanner.Decide(entry, chapter: 0, volume: 0, finished: true);

        Assert.False(plan.Write);
        Assert.Equal(ScrobbleStatus.PlanToRead, plan.RecordStatus);
    }

    [Fact]
    public void AnUnknownStatusOneShotCompletesWhenTheTrackerSaysItIsNotReleasing()
    {
        var entry = new RemoteEntry(Status: ScrobbleStatus.PlanToRead, TotalChapters: null, Releasing: false);
        var plan = ScrobblePlanner.Decide(entry, chapter: 0, volume: 0, finished: true);

        Assert.True(plan.Write);
        Assert.Equal(1, plan.Chapter);
        Assert.Equal(ScrobbleStatus.Completed, plan.PushStatus);
    }

    [Fact]
    public void AFinishedOneShotAlreadyCompletedIsLeftAlone()
    {
        var entry = new RemoteEntry(ProgressChapter: 1, Status: ScrobbleStatus.Completed, TotalChapters: 1);
        var plan = ScrobblePlanner.Decide(entry, chapter: 0, volume: 0, finished: true);

        Assert.False(plan.Write);
        Assert.Equal(ScrobbleStatus.Completed, plan.RecordStatus);
    }

    [Fact]
    public void ATrueOneShotCompletesWhenTheTrackerCountsOneChapter()
    {
        var entry = new RemoteEntry(Status: ScrobbleStatus.Reading, TotalChapters: 1);
        var plan = ScrobblePlanner.Decide(entry, chapter: 0, volume: 0, finished: true);

        Assert.True(plan.Write);
        Assert.Equal(1, plan.Chapter);
        Assert.Equal(ScrobbleStatus.Completed, plan.PushStatus);
    }

    [Fact]
    public void UnnumberedChaptersOfAnOngoingSeriesDoNotComplete()
    {
        var entry = new RemoteEntry(ProgressChapter: 3, Status: ScrobbleStatus.Reading, TotalChapters: 120);
        var plan = ScrobblePlanner.Decide(entry, chapter: 0, volume: 0, finished: true);

        Assert.False(plan.Write);
        Assert.Equal(ScrobbleStatus.Reading, plan.RecordStatus);
    }

    [Fact]
    public void AnOngoingSeriesWithNoKnownTotalDoesNotComplete()
    {
        // AniList sends chapters: null while a series is releasing, so the total says nothing.
        var entry = new RemoteEntry(Status: ScrobbleStatus.Reading, TotalChapters: null, Releasing: true);
        var plan = ScrobblePlanner.Decide(entry, chapter: 0, volume: 0, finished: true);

        Assert.False(plan.Write);
        Assert.Equal(ScrobbleStatus.Reading, plan.RecordStatus);
    }

    [Fact]
    public void AFinishedOneShotWithNoKnownTotalCompletes()
    {
        var entry = new RemoteEntry(Status: ScrobbleStatus.PlanToRead, TotalChapters: null, Releasing: false);
        var plan = ScrobblePlanner.Decide(entry, chapter: 0, volume: 0, finished: true);

        Assert.True(plan.Write);
        Assert.Equal(1, plan.Chapter);
        Assert.Equal(ScrobbleStatus.Completed, plan.PushStatus);
    }

    [Fact]
    public void AnUnfinishedOneShotStillOnlyListsWhenNotOnTheList()
    {
        var entry = new RemoteEntry(Status: ScrobbleStatus.Reading);
        var plan = ScrobblePlanner.Decide(entry, chapter: 0, volume: 0);

        Assert.False(plan.Write);
        Assert.Equal(ScrobbleStatus.Reading, plan.RecordStatus);
    }

    [Fact]
    public void NeverLowersRemoteProgress()
    {
        // Remote is further along than Kavita: pushed value is max(remote, kavita),
        // which equals remote — no progress change, no write.
        var entry = new RemoteEntry(ProgressChapter: 50, Status: ScrobbleStatus.Reading);
        var plan = ScrobblePlanner.Decide(entry, chapter: 12, volume: 0);

        Assert.False(plan.Write);
        Assert.Equal(50, plan.Chapter);
    }

    [Fact]
    public void CompletesWhenLastChapterRead()
    {
        var entry = new RemoteEntry(ProgressChapter: 10, Status: ScrobbleStatus.Reading, TotalChapters: 12);
        var plan = ScrobblePlanner.Decide(entry, chapter: 12, volume: 0);

        Assert.True(plan.Write);
        Assert.Equal(ScrobbleStatus.Completed, plan.PushStatus);
        Assert.Equal(12, plan.Chapter);
    }

    [Fact]
    public void ClampsChapterToTotal()
    {
        // Kavita can count extras past the official total.
        var entry = new RemoteEntry(ProgressChapter: 0, Status: ScrobbleStatus.Reading, TotalChapters: 12);
        var plan = ScrobblePlanner.Decide(entry, chapter: 14, volume: 0);

        Assert.Equal(12, plan.Chapter);
        Assert.Equal(ScrobbleStatus.Completed, plan.PushStatus);
    }

    [Fact]
    public void CompletesByVolumesOnlyWhenChapterTotalUnknown()
    {
        var chapterTotalKnown = new RemoteEntry(
            ProgressChapter: 5, ProgressVolume: 2, Status: ScrobbleStatus.Reading,
            TotalChapters: 100, TotalVolumes: 3);
        Assert.Equal(ScrobbleStatus.Reading,
            ScrobblePlanner.Decide(chapterTotalKnown, 6, 3).PushStatus);

        var chapterTotalUnknown = chapterTotalKnown with { TotalChapters = null };
        Assert.Equal(ScrobbleStatus.Completed,
            ScrobblePlanner.Decide(chapterTotalUnknown, 6, 3).PushStatus);
    }

    [Fact]
    public void NeverDemotesCompletedEntry()
    {
        var entry = new RemoteEntry(ProgressChapter: 100, Status: ScrobbleStatus.Completed, TotalChapters: 100);
        var plan = ScrobblePlanner.Decide(entry, chapter: 50, volume: 0);

        Assert.False(plan.Write);
        Assert.Equal(ScrobbleStatus.Completed, plan.RecordStatus);
    }

    [Fact]
    public void LeavesUserStatusAloneWithoutNewProgress()
    {
        // paused/dropped/planning ("other") is not stomped when Kavita shows nothing new
        var entry = new RemoteEntry(ProgressChapter: 20, Status: ScrobbleStatus.Other);
        var plan = ScrobblePlanner.Decide(entry, chapter: 20, volume: 0);

        Assert.False(plan.Write);
        Assert.Equal(ScrobbleStatus.Other, plan.RecordStatus);
    }

    [Fact]
    public void MovesUserStatusBackToReadingOnNewProgress()
    {
        var entry = new RemoteEntry(ProgressChapter: 20, Status: ScrobbleStatus.Other);
        var plan = ScrobblePlanner.Decide(entry, chapter: 25, volume: 0);

        Assert.True(plan.Write);
        Assert.Equal(ScrobbleStatus.Reading, plan.PushStatus);
        Assert.Equal(25, plan.Chapter);
    }

    [Fact]
    public void NoOpWhenNothingChanged()
    {
        var entry = new RemoteEntry(ProgressChapter: 12, ProgressVolume: 2, Status: ScrobbleStatus.Reading);
        var plan = ScrobblePlanner.Decide(entry, chapter: 12, volume: 2);

        Assert.False(plan.Write);
    }

    [Fact]
    public void NoProgressAddsUnlistedSeriesWithFallbackStatus()
    {
        var plan = ScrobblePlanner.Decide(new RemoteEntry(), chapter: 0, volume: 0,
            fallbackStatus: ScrobbleStatus.PlanToRead);

        Assert.True(plan.Write);
        Assert.Equal(0, plan.Chapter);
        Assert.Equal(ScrobbleStatus.PlanToRead, plan.PushStatus);
    }

    [Fact]
    public void NoProgressWithoutAFallbackStatusNeverListsTheSeries()
    {
        var plan = ScrobblePlanner.Decide(new RemoteEntry(), chapter: 0, volume: 0);

        Assert.False(plan.Write);
    }

    [Fact]
    public void NoProgressNeverTouchesExistingEntry()
    {
        // Series already on the list (any status) is never modified by plan-to-read sync.
        var entry = new RemoteEntry(ProgressChapter: 3, Status: ScrobbleStatus.Other);
        var plan = ScrobblePlanner.Decide(entry, chapter: 0, volume: 0,
            fallbackStatus: ScrobbleStatus.PlanToRead);

        Assert.False(plan.Write);
        Assert.Equal(3, plan.Chapter);
        Assert.Equal(ScrobbleStatus.Other, plan.RecordStatus);
    }

    [Fact]
    public void FirstSyncSetsReading()
    {
        var plan = ScrobblePlanner.Decide(new RemoteEntry(), chapter: 7, volume: 0);

        Assert.True(plan.Write);
        Assert.Equal(ScrobbleStatus.Reading, plan.PushStatus);
        Assert.Equal(7, plan.Chapter);
    }

    [Fact]
    public void AHeldStatusMovesAListedEntryWithoutTouchingItsProgress()
    {
        var entry = new RemoteEntry(ProgressChapter: 7, Status: ScrobbleStatus.Reading);
        var plan = ScrobblePlanner.Decide(entry, chapter: 0, volume: 0, held: ScrobbleStatus.Dropped);

        Assert.True(plan.Write);
        Assert.Equal(7, plan.Chapter);
        Assert.Equal(ScrobbleStatus.Dropped, plan.PushStatus);
    }

    [Fact]
    public void AHeldStatusNeverAddsAnUnlistedSeriesWithNothingRead()
    {
        var plan = ScrobblePlanner.Decide(new RemoteEntry(), chapter: 0, volume: 0, held: ScrobbleStatus.Paused);

        Assert.False(plan.Write);
    }

    [Fact]
    public void AHeldStatusReplacesReadingWhenProgressIsPushed()
    {
        var entry = new RemoteEntry(ProgressChapter: 5, Status: ScrobbleStatus.Reading);
        var plan = ScrobblePlanner.Decide(entry, chapter: 9, volume: 0, held: ScrobbleStatus.Paused);

        Assert.True(plan.Write);
        Assert.Equal(9, plan.Chapter);
        Assert.Equal(ScrobbleStatus.Paused, plan.PushStatus);
    }

    [Fact]
    public void AnEntryAlreadyAtTheHeldStatusIsLeftAlone()
    {
        var entry = new RemoteEntry(ProgressChapter: 9, Status: ScrobbleStatus.Dropped);
        var plan = ScrobblePlanner.Decide(entry, chapter: 9, volume: 0, held: ScrobbleStatus.Dropped);

        Assert.False(plan.Write);
        Assert.Equal(ScrobbleStatus.Dropped, plan.RecordStatus);
    }

    [Fact]
    public void AHeldStatusNeverDemotesACompletedEntry()
    {
        var entry = new RemoteEntry(ProgressChapter: 20, Status: ScrobbleStatus.Completed, TotalChapters: 20);

        Assert.False(ScrobblePlanner.Decide(entry, chapter: 0, volume: 0, held: ScrobbleStatus.Dropped).Write);
        Assert.Equal(ScrobbleStatus.Completed,
            ScrobblePlanner.Decide(entry, chapter: 20, volume: 0, held: ScrobbleStatus.Dropped).RecordStatus);
    }

    [Fact]
    public void APausedEntryOnTheTrackerMovesBackToReadingOnlyWhenTheReaderReadsFurther()
    {
        var entry = new RemoteEntry(ProgressChapter: 5, Status: ScrobbleStatus.Paused);

        Assert.False(ScrobblePlanner.Decide(entry, chapter: 5, volume: 0).Write);
        var plan = ScrobblePlanner.Decide(entry, chapter: 6, volume: 0);
        Assert.True(plan.Write);
        Assert.Equal(ScrobbleStatus.Reading, plan.PushStatus);
    }
}
