using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Data.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Tests;

/// <summary>
/// The "finished, archive it?" hint on the series page: when <see cref="SeriesArchiveService.FinishedHintAsync"/>
/// offers it, and that dismissing it writes the caller's own <see cref="UserSeriesState"/> row.
/// </summary>
public sealed class ArchiveFinishedHintTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static SeriesController Controller(Maki.Data.MakiDbContext db) =>
        new(new TestLocalizer(), db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);

    private int SeedCompleted(Action<Series>? configure = null) =>
        _db.SeedSeries("Berserk", configure: s =>
        {
            s.Status = SeriesStatus.Completed;
            configure?.Invoke(s);
        });

    private void SeedReader(int userId, int seriesId, bool finished)
    {
        using var db = _db.NewContext();
        db.ReadingStates.Add(new ReadingState
        {
            UserId = userId, SeriesId = seriesId, Title = "Berserk", Finished = finished, MaxChapter = 10,
        });
        db.SaveChanges();
    }

    private int[] SeedDownloadedChapters(int seriesId, int count)
    {
        using var db = _db.NewContext();
        var chapters = Enumerable.Range(1, count).Select(n => new Chapter
        {
            SeriesId = seriesId,
            Number = n,
            ChapterFile = new ChapterFile { SeriesId = seriesId, RelativePath = $"{n}.cbz", DateAdded = DateTime.UtcNow },
        }).ToList();
        db.Chapters.AddRange(chapters);
        db.SaveChanges();
        return chapters.Select(c => c.Id).ToArray();
    }

    private void SeedCompletedChapters(int userId, int seriesId, IEnumerable<int> chapterIds)
    {
        using var db = _db.NewContext();
        db.ChapterProgress.AddRange(chapterIds.Select(id => new ChapterProgress
        {
            UserId = userId, SeriesId = seriesId, ChapterId = id, Completed = true,
        }));
        db.SaveChanges();
    }

    private async Task<bool> Offered(int userId, int seriesId)
    {
        using var db = _db.NewContext(userId);
        var series = await db.Series.AsNoTracking().SingleAsync(s => s.Id == seriesId);
        return await SeriesArchiveService.FinishedHintAsync(db, series, default);
    }

    [Fact]
    public async Task Offered_when_completed_and_every_reader_has_finished()
    {
        var alice = _db.SeedUser("alice");
        var bob = _db.SeedUser("bob");
        var seriesId = SeedCompleted();
        SeedReader(alice, seriesId, finished: true);
        SeedReader(bob, seriesId, finished: true);

        Assert.True(await Offered(alice, seriesId));
    }

    [Fact]
    public async Task Not_offered_while_another_reader_has_not_finished()
    {
        var alice = _db.SeedUser("alice");
        var bob = _db.SeedUser("bob");
        var seriesId = SeedCompleted();
        SeedReader(alice, seriesId, finished: true);
        SeedReader(bob, seriesId, finished: false);

        Assert.False(await Offered(alice, seriesId));
    }

    [Fact]
    public async Task Not_offered_when_finished_but_downloaded_chapters_are_unread()
    {
        var alice = _db.SeedUser("alice");
        var seriesId = SeedCompleted();
        var chapters = SeedDownloadedChapters(seriesId, 3);
        SeedReader(alice, seriesId, finished: true);
        SeedCompletedChapters(alice, seriesId, [chapters[0], chapters[2]]);

        Assert.False(await Offered(alice, seriesId));
    }

    [Fact]
    public async Task Offered_once_every_downloaded_chapter_is_read()
    {
        var alice = _db.SeedUser("alice");
        var seriesId = SeedCompleted();
        var chapters = SeedDownloadedChapters(seriesId, 3);
        SeedReader(alice, seriesId, finished: true);
        SeedCompletedChapters(alice, seriesId, chapters);

        Assert.True(await Offered(alice, seriesId));
    }

    [Fact]
    public async Task Not_offered_while_another_finished_reader_has_unread_chapters()
    {
        var alice = _db.SeedUser("alice");
        var bob = _db.SeedUser("bob");
        var seriesId = SeedCompleted();
        var chapters = SeedDownloadedChapters(seriesId, 2);
        SeedReader(alice, seriesId, finished: true);
        SeedReader(bob, seriesId, finished: true);
        SeedCompletedChapters(alice, seriesId, chapters);
        SeedCompletedChapters(bob, seriesId, [chapters[1]]);

        Assert.False(await Offered(alice, seriesId));
    }

    [Fact]
    public async Task Not_offered_when_nobody_has_a_reading_state()
    {
        var alice = _db.SeedUser("alice");
        var seriesId = SeedCompleted();

        Assert.False(await Offered(alice, seriesId));
    }

    [Fact]
    public async Task Not_offered_to_a_user_who_has_not_finished_it_themselves_even_when_every_reader_has()
    {
        var alice = _db.SeedUser("alice");
        var bob = _db.SeedUser("bob");
        var carol = _db.SeedUser("carol");
        var seriesId = SeedCompleted();
        SeedReader(alice, seriesId, finished: true);
        SeedReader(bob, seriesId, finished: true);

        Assert.False(await Offered(carol, seriesId));
    }

    [Fact]
    public async Task Not_offered_when_the_series_is_archived()
    {
        var alice = _db.SeedUser("alice");
        var seriesId = SeedCompleted(s => s.ArchivedAt = DateTime.UtcNow);
        SeedReader(alice, seriesId, finished: true);

        Assert.False(await Offered(alice, seriesId));
    }

    [Fact]
    public async Task Not_offered_when_the_series_is_not_completed()
    {
        var alice = _db.SeedUser("alice");
        var seriesId = SeedCompleted(s => s.Status = SeriesStatus.Ongoing);
        SeedReader(alice, seriesId, finished: true);

        Assert.False(await Offered(alice, seriesId));
    }

    [Fact]
    public async Task Not_offered_once_the_caller_dismissed_it_but_still_offered_to_others()
    {
        var alice = _db.SeedUser("alice");
        var bob = _db.SeedUser("bob");
        var seriesId = SeedCompleted();
        SeedReader(alice, seriesId, finished: true);
        SeedReader(bob, seriesId, finished: true);

        using (var db = _db.NewContext(alice))
        {
            await Controller(db).DismissArchiveHint(seriesId, default);
        }

        Assert.False(await Offered(alice, seriesId));
        Assert.True(await Offered(bob, seriesId));
    }

    [Fact]
    public async Task Dismissing_stamps_the_callers_row_and_keeps_the_rating()
    {
        var alice = _db.SeedUser("alice");
        var seriesId = SeedCompleted();
        using (var seed = _db.NewContext())
        {
            seed.UserSeriesStates.Add(new UserSeriesState { UserId = alice, SeriesId = seriesId, Rating = 8 });
            seed.SaveChanges();
        }

        using (var db = _db.NewContext(alice))
        {
            Assert.IsType<NoContentResult>(await Controller(db).DismissArchiveHint(seriesId, default));
        }

        using var check = _db.NewContext();
        var state = Assert.Single(check.UserSeriesStates.IgnoreQueryFilters().ToList());
        Assert.Equal(alice, state.UserId);
        Assert.Equal(8, state.Rating);
        Assert.NotNull(state.ArchiveHintDismissedAt);
    }

    [Fact]
    public async Task Dismissing_a_missing_series_is_a_404_and_writes_nothing()
    {
        var alice = _db.SeedUser("alice");

        using var db = _db.NewContext(alice);
        Assert.IsType<NotFoundResult>(await Controller(db).DismissArchiveHint(9999, default));
        Assert.Empty(db.UserSeriesStates.IgnoreQueryFilters().ToList());
    }
}
