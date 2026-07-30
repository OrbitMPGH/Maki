using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Entities;
using Microsoft.AspNetCore.Mvc;

namespace Maki.Api.Tests;

/// <summary>
/// The Home dashboard's two rails. Most of these pin behaviour that is easy to "simplify" back
/// into a bug: tombstones must not hijack Continue reading, a series with nothing left to read
/// must drop out of Jump back in, and neither rail may consult <see cref="ReadingState"/>.
/// </summary>
public class HomeControllerTests : IDisposable
{
    private static readonly DateTime Base = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private HomeController Controller()
    {
        var context = _db.NewContext();
        return new HomeController(context, new ContinueReadingService(context));
    }

    /// <summary>Seeds a downloaded chapter (chapter + backing file) and returns its chapter id.</summary>
    private int SeedChapter(int seriesId, decimal? number, DateTime? addedAt = null, string? title = null)
    {
        using var db = _db.NewContext();
        var file = new ChapterFile
        {
            SeriesId = seriesId,
            RelativePath = $"{seriesId}-{number}.cbz",
            DateAdded = addedAt ?? Base
        };
        db.ChapterFiles.Add(file);
        db.SaveChanges();

        var chapter = new Chapter
        {
            SeriesId = seriesId,
            Number = number,
            Title = title,
            IsOneShot = number is null,
            ChapterFileId = file.Id
        };
        db.Chapters.Add(chapter);
        db.SaveChanges();
        return chapter.Id;
    }

    /// <summary>A chapter with no file — monitored but not downloaded.</summary>
    private int SeedMissingChapter(int seriesId, decimal number)
    {
        using var db = _db.NewContext();
        var chapter = new Chapter { SeriesId = seriesId, Number = number };
        db.Chapters.Add(chapter);
        db.SaveChanges();
        return chapter.Id;
    }

    private void SeedProgress(
        int seriesId, int chapterId, int pageIndex, bool completed,
        DateTime updatedAt, DateTime? unreadAt = null, int pageCount = 20)
    {
        using var db = _db.NewContext();
        db.ChapterProgress.Add(new ChapterProgress
        {
            UserId = 1,
            SeriesId = seriesId,
            ChapterId = chapterId,
            PageIndex = pageIndex,
            PageCount = pageCount,
            Completed = completed,
            UnreadAt = unreadAt,
            StartedAt = updatedAt,
            UpdatedAt = updatedAt
        });
        db.SaveChanges();
    }

    private static HomeReadingResponse Reading(IActionResult result) =>
        Assert.IsType<HomeReadingResponse>(Assert.IsType<OkObjectResult>(result).Value);

    private static IReadOnlyList<HomeRecentSeriesItem> Recent(IActionResult result) =>
        Assert.IsType<List<HomeRecentSeriesItem>>(Assert.IsType<OkObjectResult>(result).Value);

    [Fact]
    public async Task Continue_returns_the_part_read_chapter_with_its_resume_page()
    {
        var seriesId = _db.SeedSeries("Berserk");
        var chapterId = SeedChapter(seriesId, 1);
        SeedChapter(seriesId, 2);
        SeedProgress(seriesId, chapterId, pageIndex: 5, completed: false, updatedAt: Base);

        var response = Reading(await Controller().Reading(ct: CancellationToken.None));

        var item = Assert.Single(response.ContinueReading);
        Assert.Equal(chapterId, item.ChapterId);
        Assert.Equal(5, item.Page);
        Assert.Equal(20, item.PageCount);
        Assert.Equal("Ch.1", item.ChapterLabel);
        Assert.Equal(2, item.UnreadChapters);
        Assert.Empty(response.JumpBackIn);
    }

    [Fact]
    public async Task Continue_skips_unread_tombstones()
    {
        var seriesId = _db.SeedSeries();
        var first = SeedChapter(seriesId, 1);
        var second = SeedChapter(seriesId, 2);
        SeedProgress(seriesId, first, pageIndex: 0, completed: true, updatedAt: Base);
        // Marked unread: the most recently touched incomplete row, but resuming into it would
        // hijack Continue reading.
        SeedProgress(seriesId, second, pageIndex: 0, completed: false,
            updatedAt: Base.AddHours(1), unreadAt: Base.AddHours(1));

        var response = Reading(await Controller().Reading(ct: CancellationToken.None));

        Assert.Empty(response.ContinueReading);
        // Still unread, so it is what Jump back in offers.
        var item = Assert.Single(response.JumpBackIn);
        Assert.Equal(second, item.ChapterId);
    }

    [Fact]
    public async Task Continue_orders_by_most_recently_touched()
    {
        var older = _db.SeedSeries("Older");
        var newer = _db.SeedSeries("Newer");
        SeedProgress(older, SeedChapter(older, 1), 3, false, Base);
        SeedProgress(newer, SeedChapter(newer, 1), 3, false, Base.AddDays(1));

        var response = Reading(await Controller().Reading(ct: CancellationToken.None));

        Assert.Equal(["Newer", "Older"], response.ContinueReading.Select(i => i.SeriesTitle));
    }

    [Fact]
    public async Task Continue_ignores_completed_rows()
    {
        var seriesId = _db.SeedSeries();
        var chapterId = SeedChapter(seriesId, 1);
        SeedChapter(seriesId, 2);
        // Completed but with a page index left behind — sticky completion wins over the position.
        SeedProgress(seriesId, chapterId, pageIndex: 19, completed: true, updatedAt: Base);

        var response = Reading(await Controller().Reading(ct: CancellationToken.None));

        Assert.Empty(response.ContinueReading);
        Assert.Single(response.JumpBackIn);
    }

    [Fact]
    public async Task JumpBackIn_excludes_series_already_in_continue()
    {
        var seriesId = _db.SeedSeries();
        var first = SeedChapter(seriesId, 1);
        var second = SeedChapter(seriesId, 2);
        SeedProgress(seriesId, first, pageIndex: 0, completed: true, updatedAt: Base);
        SeedProgress(seriesId, second, pageIndex: 4, completed: false, updatedAt: Base.AddHours(1));

        var response = Reading(await Controller().Reading(ct: CancellationToken.None));

        Assert.Single(response.ContinueReading);
        Assert.Empty(response.JumpBackIn);
    }

    [Fact]
    public async Task JumpBackIn_drops_series_with_every_downloaded_chapter_read()
    {
        var seriesId = _db.SeedSeries();
        var only = SeedChapter(seriesId, 1);
        SeedProgress(seriesId, only, pageIndex: 0, completed: true, updatedAt: Base);

        var response = Reading(await Controller().Reading(ct: CancellationToken.None));

        Assert.Empty(response.JumpBackIn);
    }

    [Fact]
    public async Task JumpBackIn_ignores_chapters_with_no_file()
    {
        var seriesId = _db.SeedSeries();
        var downloaded = SeedChapter(seriesId, 1);
        SeedMissingChapter(seriesId, 2); // known but not downloaded — not offerable
        SeedProgress(seriesId, downloaded, pageIndex: 0, completed: true, updatedAt: Base);

        var response = Reading(await Controller().Reading(ct: CancellationToken.None));

        Assert.Empty(response.JumpBackIn);
    }

    [Fact]
    public async Task JumpBackIn_orders_one_shots_last()
    {
        var seriesId = _db.SeedSeries();
        var read = SeedChapter(seriesId, 1);
        SeedChapter(seriesId, null, title: "Bonus"); // one-shot: no number
        var numbered = SeedChapter(seriesId, 2);
        SeedProgress(seriesId, read, pageIndex: 0, completed: true, updatedAt: Base);

        var response = Reading(await Controller().Reading(ct: CancellationToken.None));

        var item = Assert.Single(response.JumpBackIn);
        Assert.Equal(numbered, item.ChapterId);
        Assert.Equal("Ch.2", item.ChapterLabel);
    }

    [Fact]
    public async Task Reading_ignores_ReadingState()
    {
        var seriesId = _db.SeedSeries();
        var chapterId = SeedChapter(seriesId, 1);
        SeedChapter(seriesId, 2);
        SeedProgress(seriesId, chapterId, pageIndex: 5, completed: false, updatedAt: Base);

        using (var db = _db.NewContext())
        {
            // Duplicate rows per SeriesId are legal (two Kavita series can resolve to one local
            // series), and MaxChapter is a forward-only mark that can name chapters never opened.
            // Neither may leak into the rails, and a join would also multiply the rows.
            db.ReadingStates.Add(new ReadingState
            {
                UserId = 1,
                SeriesId = seriesId, KavitaSeriesId = 1, MaxChapter = 999, UpdatedAt = Base
            });
            db.ReadingStates.Add(new ReadingState
            {
                UserId = 1,
                SeriesId = seriesId, KavitaSeriesId = 2, MaxChapter = 999, UpdatedAt = Base
            });
            db.SaveChanges();
        }

        var response = Reading(await Controller().Reading(ct: CancellationToken.None));

        var item = Assert.Single(response.ContinueReading);
        Assert.Equal(chapterId, item.ChapterId);
        Assert.Equal(2, item.UnreadChapters);
        Assert.Empty(response.JumpBackIn);
    }

    [Fact]
    public async Task RecentlyAdded_groups_files_by_series_and_orders_by_newest()
    {
        var older = _db.SeedSeries("Older");
        var newer = _db.SeedSeries("Newer");
        SeedChapter(older, 1, addedAt: Base);
        SeedChapter(newer, 1, addedAt: Base.AddDays(1));

        var items = Recent(await Controller().RecentlyAdded(ct: CancellationToken.None));

        Assert.Equal(["Newer", "Older"], items.Select(i => i.SeriesTitle));
    }

    [Fact]
    public async Task RecentlyAdded_counts_files_per_series_and_labels_the_newest()
    {
        var seriesId = _db.SeedSeries("Vagabond");
        SeedChapter(seriesId, 1, addedAt: Base);
        SeedChapter(seriesId, 2, addedAt: Base.AddHours(1));
        SeedChapter(seriesId, 3, addedAt: Base.AddHours(2));

        var item = Assert.Single(Recent(await Controller().RecentlyAdded(ct: CancellationToken.None)));

        Assert.Equal(3, item.NewChapterCount);
        Assert.Equal(Base.AddHours(2), item.AddedAt);
        Assert.Equal("Ch.3", item.NewestChapterLabel);
        // Nothing read yet, so the card's Read button points at the first chapter.
        Assert.NotNull(item.ReadChapterId);
    }

    [Fact]
    public async Task RecentlyAdded_read_chapter_is_null_once_everything_is_read()
    {
        var seriesId = _db.SeedSeries();
        var chapterId = SeedChapter(seriesId, 1);
        SeedProgress(seriesId, chapterId, pageIndex: 0, completed: true, updatedAt: Base);

        var item = Assert.Single(Recent(await Controller().RecentlyAdded(ct: CancellationToken.None)));

        Assert.Null(item.ReadChapterId);
    }

    [Fact]
    public async Task Continue_reading_returns_chapter_after_last_completed()
    {
        // User reads chapter 1 but skips chapter 0 — button should point at chapter 2.
        var seriesId = _db.SeedSeries("ContinueAfterLast");
        var ch0 = SeedChapter(seriesId, 0);
        var ch1 = SeedChapter(seriesId, 1);
        var ch2 = SeedChapter(seriesId, 2);
        SeedProgress(seriesId, ch1, pageIndex: 0, completed: true, updatedAt: Base);

        var response = Reading(await Controller().Reading(ct: CancellationToken.None));

        // Jump back in should show chapter 2 (next unread after last completed).
        var item = Assert.Single(response.JumpBackIn.Where(i => i.SeriesTitle == "ContinueAfterLast"));
        Assert.Equal(ch2, item.ChapterId);
    }

    [Fact]
    public async Task Reader_continue_returns_next_unread_after_last_completed()
    {
        var seriesId = _db.SeedSeries();
        var ch0 = SeedChapter(seriesId, 0);
        var ch1 = SeedChapter(seriesId, 1);
        var ch2 = SeedChapter(seriesId, 2);
        // Read chapter 1, skip chapters 0 and 2.
        SeedProgress(seriesId, ch1, pageIndex: 0, completed: true, updatedAt: Base);

        using (var db = _db.NewContext())
        {
            var controller = new ReaderController(
                db,
                null!, // reader service not needed for this test
                new ContinueReadingService(db),
                null!, // settings
                null!, // import service
                null!, // app paths
                null!  // logger
            );

            var result = await controller.Continue(seriesId, CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var data = (dynamic)ok.Value;
            Assert.Equal(ch2, data.chapterId);
            Assert.Equal(0, data.page);
        }
    }
}
