using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Recommendations;
using Maki.Data.Identity;
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
                new TestLocalizer(),
                db,
                null!, // reader service not needed for this test
                new ContinueReadingService(db),
                null!, // settings
                null!, // import service
                null!, // user metrics
                null!, // achievements
                null!, // app paths
                null!, // logger
                null!, // current user
                null!  // kavita user resolver
            );

            var result = await controller.Continue(seriesId, CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var data = (dynamic)ok.Value;
            Assert.Equal(ch2, data.chapterId);
            Assert.Equal(0, data.page);
        }
    }

    // Hiding writes a UserSeriesState row, which needs a user on the scope to stamp its owner.
    private HomeController UserController()
    {
        var context = _db.NewContext(userId: 1);
        return new HomeController(context, new ContinueReadingService(context));
    }

    [Fact]
    public async Task Hidden_series_drop_out_of_both_rails()
    {
        var continued = _db.SeedSeries("Continued");
        var finished = _db.SeedSeries("Finished");
        var kept = _db.SeedSeries("Kept");
        SeedProgress(continued, SeedChapter(continued, 1), 5, false, Base);
        SeedProgress(finished, SeedChapter(finished, 1), 0, true, Base);
        SeedChapter(finished, 2);
        SeedProgress(kept, SeedChapter(kept, 1), 5, false, Base);

        Assert.IsType<NoContentResult>(await UserController().HideFromReading(continued, CancellationToken.None));
        Assert.IsType<NoContentResult>(await UserController().HideFromReading(finished, CancellationToken.None));

        var response = Reading(await UserController().Reading(ct: CancellationToken.None));

        Assert.Equal(["Kept"], response.ContinueReading.Select(i => i.SeriesTitle));
        Assert.Empty(response.JumpBackIn);
    }

    [Fact]
    public async Task Reading_a_hidden_series_again_brings_it_back()
    {
        var seriesId = _db.SeedSeries("Berserk");
        var first = SeedChapter(seriesId, 1);
        var second = SeedChapter(seriesId, 2);
        SeedProgress(seriesId, first, 5, false, Base);
        await UserController().HideFromReading(seriesId, CancellationToken.None);

        SeedProgress(seriesId, second, 3, false, DateTime.UtcNow.AddMinutes(1));

        var response = Reading(await UserController().Reading(ct: CancellationToken.None));

        Assert.Equal(second, Assert.Single(response.ContinueReading).ChapterId);
    }

    [Fact]
    public async Task Unhiding_restores_the_series()
    {
        var seriesId = _db.SeedSeries("Berserk");
        SeedProgress(seriesId, SeedChapter(seriesId, 1), 5, false, Base);
        await UserController().HideFromReading(seriesId, CancellationToken.None);

        Assert.IsType<NoContentResult>(await UserController().UnhideFromReading(seriesId, CancellationToken.None));

        var response = Reading(await UserController().Reading(ct: CancellationToken.None));
        Assert.Single(response.ContinueReading);
    }

    [Fact]
    public async Task Hiding_an_unknown_series_is_not_found()
    {
        Assert.IsType<NotFoundResult>(await UserController().HideFromReading(999, CancellationToken.None));
    }

    // ---- from-anime ----

    private readonly ReadingProgressGate _gate = new();

    private async Task<IReadOnlyList<HomeAnimeResumeItem>> FromAnime(int userId = 1, bool allRootFolders = true)
    {
        var context = _db.NewContext(userId, allRootFolders);
        var controller = new HomeController(context, new ContinueReadingService(context));
        var result = await controller.FromAnime(
            AnimeResumeFixture.Service(_db, context, _gate), ct: CancellationToken.None);
        return Assert.IsAssignableFrom<IReadOnlyList<HomeAnimeResumeItem>>(
            Assert.IsType<OkObjectResult>(result).Value);
    }

    private int SeedFromAnime(string title, int mangaBakaId, int? score, int userId = 1)
    {
        var seriesId = AnimeResumeFixture.SeedAnimeSeries(_db, title, mangaBakaId);
        AnimeResumeFixture.SeedSignal(_db, userId, animeId: mangaBakaId, mangaBakaId: mangaBakaId, score: score);
        AnimeResumeFixture.SeedChapters(_db, seriesId, 1, 2, 3, 4, 5, 6);
        return seriesId;
    }

    [Fact]
    public async Task From_anime_orders_by_score_then_title_with_unscored_last()
    {
        AnimeResumeFixture.OptIn(_db, 1);
        SeedFromAnime("Middling", 1, 7);
        SeedFromAnime("Zeta", 2, 9);
        SeedFromAnime("Unscored", 3, null);
        SeedFromAnime("Alpha", 4, 9);

        var items = await FromAnime();

        Assert.Equal(["Alpha", "Zeta", "Middling", "Unscored"], items.Select(i => i.SeriesTitle));
        var first = items[0];
        Assert.Equal(5m, first.CoveredTo);
        Assert.Equal("Ch.6", first.ResumeChapterLabel);
    }

    [Fact]
    public async Task From_anime_drops_series_read_or_watched_up_to_the_frontier()
    {
        AnimeResumeFixture.OptIn(_db, 1);
        var caughtUp = SeedFromAnime("Caught up", 1, 8);
        var started = SeedFromAnime("Started", 2, 8);
        using (var db = _db.NewContext())
        {
            var fifth = db.Chapters.Where(c => c.SeriesId == caughtUp).AsEnumerable().Single(c => c.Number == 5m).Id;
            var third = db.Chapters.Where(c => c.SeriesId == started).AsEnumerable().Single(c => c.Number == 3m).Id;
            AnimeResumeFixture.SeedProgress(_db, 1, caughtUp, fifth, watched: true);
            AnimeResumeFixture.SeedProgress(_db, 1, started, third);
        }

        var items = await FromAnime();

        Assert.Equal(started, Assert.Single(items).SeriesId);
    }

    [Fact]
    public async Task From_anime_drops_series_with_nothing_to_mark_and_nothing_read()
    {
        AnimeResumeFixture.OptIn(_db, 1);
        var onlyLater = AnimeResumeFixture.SeedAnimeSeries(_db, "Only later chapters", 1);
        AnimeResumeFixture.SeedSignal(_db, 1, animeId: 1, mangaBakaId: 1);
        AnimeResumeFixture.SeedChapters(_db, onlyLater, 6, 7, 8);
        var shown = SeedFromAnime("Shown", 2, 8);

        var items = await FromAnime();

        Assert.Equal(shown, Assert.Single(items).SeriesId);
    }

    [Fact]
    public async Task From_anime_respects_hide_from_home()
    {
        AnimeResumeFixture.OptIn(_db, 1);
        var hidden = SeedFromAnime("Hidden", 1, 8);
        var shown = SeedFromAnime("Shown", 2, 8);
        await UserController().HideFromReading(hidden, CancellationToken.None);

        var items = await FromAnime();

        Assert.Equal(shown, Assert.Single(items).SeriesId);
    }

    [Fact]
    public async Task From_anime_only_offers_series_in_granted_root_folders()
    {
        var reader = _db.SeedUser("reader", allRootFolders: false);
        AnimeResumeFixture.OptIn(_db, reader);
        var granted = SeedFromAnime("Granted", 1, 8, reader);
        SeedFromAnime("Elsewhere", 2, 8, reader);
        using (var db = _db.NewContext())
        {
            var folder = db.Series.Single(s => s.Id == granted).RootFolderId;
            db.UserRootFolders.Add(new UserRootFolder { UserId = reader, RootFolderId = folder });
            db.SaveChanges();
        }

        var items = await FromAnime(reader, allRootFolders: false);

        Assert.Equal(granted, Assert.Single(items).SeriesId);
    }
}
