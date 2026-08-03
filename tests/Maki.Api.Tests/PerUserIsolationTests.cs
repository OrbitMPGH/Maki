using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Security;
using Maki.Data.Identity;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// Two accounts reading the same library must not see each other's state. These are the tests that
/// would fail if a query filter, a unique index or an ownership stamp were dropped — the whole point
/// of the per-user split, expressed as behaviour rather than as schema.
/// </summary>
public sealed class PerUserIsolationTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private ReadingProgressService ServiceFor(int userId) =>
        new(_db.NewContext(userId), new ReadingProgressGate(), NullLogger<ReadingProgressService>.Instance);

    [Fact]
    public async Task TwoUsersReadingTheSameSeriesKeepSeparateMarks()
    {
        var alice = _db.SeedUser("alice");
        var bob = _db.SeedUser("bob");
        var seriesId = _db.SeedSeries();

        await ServiceFor(alice).TrackNativeAsync(alice, seriesId, "Berserk", 40, 0, default);
        await ServiceFor(bob).TrackNativeAsync(bob, seriesId, "Berserk", 3, 0, default);

        // Two rows over one series — legal, and required: the mark is forward-only, so a shared row
        // would let the further reader's progress mark the series read for the other one.
        using var db = _db.NewContext();
        var states = db.ReadingStates.OrderBy(r => r.Id).ToList();
        Assert.Equal(2, states.Count);
        Assert.Equal(40, states.Single(r => r.UserId == alice).MaxChapter);
        Assert.Equal(3, states.Single(r => r.UserId == bob).MaxChapter);
    }

    [Fact]
    public void AScopedContextSeesOnlyItsOwnRows()
    {
        var alice = _db.SeedUser("alice");
        var bob = _db.SeedUser("bob");
        var seriesId = _db.SeedSeries();
        var chapterId = SeedChapter(seriesId);

        SeedProgress(alice, seriesId, chapterId, pageIndex: 7);
        SeedProgress(bob, seriesId, chapterId, pageIndex: 2);

        // The unique index is (UserId, ChapterId), so both rows exist for one chapter…
        using (var unrestricted = _db.NewContext())
        {
            Assert.Equal(2, unrestricted.ChapterProgress.Count());
        }

        // …and a request-scoped context sees exactly one of them, without asking.
        using var asAlice = _db.NewContext(alice);
        var row = Assert.Single(asAlice.ChapterProgress.ToList());
        Assert.Equal(7, row.PageIndex);
    }

    [Fact]
    public void RatingsAndReaderOverridesAreNotShared()
    {
        var alice = _db.SeedUser("alice");
        var bob = _db.SeedUser("bob");
        var seriesId = _db.SeedSeries();

        using (var db = _db.NewContext())
        {
            db.UserSeriesStates.Add(new UserSeriesState { UserId = alice, SeriesId = seriesId, Rating = 10 });
            db.UserSeriesStates.Add(new UserSeriesState { UserId = bob, SeriesId = seriesId, Rating = 4 });
            db.SaveChanges();
        }

        using var asBob = _db.NewContext(bob);
        Assert.Equal(4, Assert.Single(asBob.UserSeriesStates.ToList()).Rating);
    }

    [Fact]
    public void AnInsertThatForgetsItsOwnerIsStampedFromTheScope()
    {
        var alice = _db.SeedUser("alice");
        var seriesId = _db.SeedSeries();

        using (var asAlice = _db.NewContext(alice))
        {
            // No UserId set — the backstop in SaveChanges fills it in from the scope. Without it the
            // row would be owned by user 0 and visible to nobody.
            asAlice.SavedFilters.Add(new SavedFilter { Name = "Ongoing", Spec = "{}" });
            asAlice.SaveChanges();
        }

        using var db = _db.NewContext();
        Assert.Equal(alice, Assert.Single(db.SavedFilters.ToList()).UserId);
        Assert.True(seriesId > 0);
    }

    [Fact]
    public void LibraryEventsStayVisibleToEveryoneAndReadEventsDoNot()
    {
        var alice = _db.SeedUser("alice");
        var bob = _db.SeedUser("bob");
        var seriesId = _db.SeedSeries();

        using (var db = _db.NewContext())
        {
            // Null owner: a library event. StatsBackfillService seeds these from file timestamps,
            // where no reader was ever recorded, so they belong to the instance.
            db.StatsEvents.Add(new StatsEvent
            {
                Type = StatsEventType.ChapterDownloaded,
                Timestamp = DateTime.UtcNow,
                SeriesId = seriesId,
                SeriesTitle = "Berserk",
            });
            db.StatsEvents.Add(new StatsEvent
            {
                Type = StatsEventType.ChaptersRead,
                UserId = alice,
                Timestamp = DateTime.UtcNow,
                SeriesId = seriesId,
                SeriesTitle = "Berserk",
            });
            db.SaveChanges();
        }

        using var asBob = _db.NewContext(bob);
        var visible = asBob.StatsEvents.ToList();
        Assert.Equal(StatsEventType.ChapterDownloaded, Assert.Single(visible).Type);
    }

    [Fact]
    public void ASeriesInAnUngrantedRootFolderIsInvisible()
    {
        var granted = _db.SeedUser("granted", MakiPermission.None, allRootFolders: false);
        var seriesId = _db.SeedSeries();

        int rootFolderId;
        using (var db = _db.NewContext())
        {
            rootFolderId = db.Series.Single(s => s.Id == seriesId).RootFolderId;
        }

        // No grant yet: the library is empty rather than "everything", which is the fail-closed half
        // of the access model — access is given, never assumed.
        using (var asUser = _db.NewContext(granted, allRootFolders: false))
        {
            Assert.Empty(asUser.Series.ToList());
        }

        using (var db = _db.NewContext())
        {
            db.UserRootFolders.Add(new UserRootFolder { UserId = granted, RootFolderId = rootFolderId });
            db.SaveChanges();
        }

        using (var asUser = _db.NewContext(granted, allRootFolders: false))
        {
            Assert.Single(asUser.Series.ToList());
        }
    }

    private int SeedChapter(int seriesId)
    {
        using var db = _db.NewContext();
        var chapter = new Chapter { SeriesId = seriesId, Number = 1, Language = "en" };
        db.Chapters.Add(chapter);
        db.SaveChanges();
        return chapter.Id;
    }

    private void SeedProgress(int userId, int seriesId, int chapterId, int pageIndex)
    {
        using var db = _db.NewContext();
        db.ChapterProgress.Add(new ChapterProgress
        {
            UserId = userId,
            SeriesId = seriesId,
            ChapterId = chapterId,
            PageIndex = pageIndex,
            PageCount = 20,
            StartedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }
}
