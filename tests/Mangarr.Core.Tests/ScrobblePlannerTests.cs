using Mangarr.Core.Scrobbling;

namespace Mangarr.Core.Tests;

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
}
