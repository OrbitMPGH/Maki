using Maki.Api.Configuration;
using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Recommendations;
using Maki.Data.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// The Home dashboard's reading list. Most of these pin behaviour that is easy to "simplify" back
/// into a bug: tombstones must not hijack Continue reading, a series with nothing left to read
/// must drop out, a part-read chapter read past since must not come back, and nothing may consult
/// <see cref="ReadingState"/>.
/// </summary>
public class HomeControllerTests : IDisposable
{
    private static readonly DateTime Base = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    /// <summary>
    /// A cover store pointed at an empty folder: these rails are about chapters, not art, so every
    /// series resolves to "no cover, no colour" without reading a real config directory.
    /// </summary>
    private static CoverAccentCache Accents()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "maki-tests", "home-controller-accents"));
        var covers = new CoverService(null!, paths, new FakeAppSettings(), NullLogger<CoverService>.Instance);
        return new CoverAccentCache(covers, NullLogger<CoverAccentCache>.Instance);
    }

    private HomeController Controller()
    {
        var context = _db.NewContext();
        return new HomeController(context, new ContinueReadingService(context), Accents());
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
        DateTime updatedAt, DateTime? unreadAt = null, int pageCount = 20, bool watched = false)
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
            Watched = watched,
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
    }

    private void RemoveFile(int chapterId)
    {
        using var db = _db.NewContext();
        var chapter = db.Chapters.Single(c => c.Id == chapterId);
        chapter.ChapterFileId = null;
        chapter.FileRemovedAt = Base;
        db.SaveChanges();
    }

    [Fact]
    public async Task A_part_read_chapter_whose_file_was_removed_moves_on_to_the_next()
    {
        var seriesId = _db.SeedSeries("Berserk");
        var removed = SeedChapter(seriesId, 1);
        var next = SeedChapter(seriesId, 2);
        SeedProgress(seriesId, removed, pageIndex: 5, completed: false, updatedAt: Base);
        RemoveFile(removed);

        var response = Reading(await Controller().Reading(ct: CancellationToken.None));

        var item = Assert.Single(response.ContinueReading);
        Assert.Equal(next, item.ChapterId);
        Assert.Equal(0, item.Page);
    }

    [Fact]
    public async Task Continue_resumes_the_part_read_chapter_that_still_has_a_file()
    {
        var seriesId = _db.SeedSeries("Berserk");
        var removed = SeedChapter(seriesId, 1);
        var kept = SeedChapter(seriesId, 2);
        SeedProgress(seriesId, kept, pageIndex: 3, completed: false, updatedAt: Base);
        SeedProgress(seriesId, removed, pageIndex: 5, completed: false, updatedAt: Base.AddHours(1));
        RemoveFile(removed);

        var response = Reading(await Controller().Reading(ct: CancellationToken.None));

        var item = Assert.Single(response.ContinueReading);
        Assert.Equal(kept, item.ChapterId);
        Assert.Equal(3, item.Page);
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

        // Still unread, so it is offered as the next chapter, from the start.
        var item = Assert.Single(response.ContinueReading);
        Assert.Equal(second, item.ChapterId);
        Assert.Equal(0, item.Page);
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
    public async Task Orders_by_last_read_whether_part_way_through_a_chapter_or_between_two()
    {
        var midChapter = _db.SeedSeries("Mid chapter");
        var betweenChapters = _db.SeedSeries("Between chapters");
        SeedProgress(midChapter, SeedChapter(midChapter, 1), 3, false, Base);
        SeedProgress(betweenChapters, SeedChapter(betweenChapters, 1), 0, true, Base.AddDays(1));
        SeedChapter(betweenChapters, 2);

        var response = Reading(await Controller().Reading(ct: CancellationToken.None));

        Assert.Equal(["Between chapters", "Mid chapter"], response.ContinueReading.Select(i => i.SeriesTitle));
        Assert.Equal(Base.AddDays(1), response.ContinueReading[0].LastReadAt);
    }

    [Fact]
    public async Task A_completed_chapter_offers_the_next_one_from_the_start()
    {
        var seriesId = _db.SeedSeries();
        var chapterId = SeedChapter(seriesId, 1);
        var next = SeedChapter(seriesId, 2);
        // Completed but with a page index left behind — sticky completion wins over the position.
        SeedProgress(seriesId, chapterId, pageIndex: 19, completed: true, updatedAt: Base);

        var response = Reading(await Controller().Reading(ct: CancellationToken.None));

        var item = Assert.Single(response.ContinueReading);
        Assert.Equal(next, item.ChapterId);
        Assert.Equal(0, item.Page);
        Assert.Equal(0, item.PageCount);
    }

    [Fact]
    public async Task A_series_is_listed_once_at_its_newest_part_read_chapter()
    {
        var seriesId = _db.SeedSeries();
        var first = SeedChapter(seriesId, 1);
        var second = SeedChapter(seriesId, 2);
        SeedProgress(seriesId, first, pageIndex: 0, completed: true, updatedAt: Base);
        SeedProgress(seriesId, second, pageIndex: 4, completed: false, updatedAt: Base.AddHours(1));

        var response = Reading(await Controller().Reading(ct: CancellationToken.None));

        var item = Assert.Single(response.ContinueReading);
        Assert.Equal(second, item.ChapterId);
        Assert.Equal(4, item.Page);
    }

    [Fact]
    public async Task A_part_read_chapter_read_past_since_is_abandoned()
    {
        // Left chapter 1 half-way, came back later and read 2 and 3 through: the list offers 4, and
        // is dated by the last chapter read rather than by the abandoned one.
        var seriesId = _db.SeedSeries();
        var abandoned = SeedChapter(seriesId, 1);
        var second = SeedChapter(seriesId, 2);
        var third = SeedChapter(seriesId, 3);
        var fourth = SeedChapter(seriesId, 4);
        SeedProgress(seriesId, abandoned, pageIndex: 6, completed: false, updatedAt: Base);
        SeedProgress(seriesId, second, pageIndex: 19, completed: true, updatedAt: Base.AddDays(30));
        SeedProgress(seriesId, third, pageIndex: 19, completed: true, updatedAt: Base.AddDays(31));

        var response = Reading(await Controller().Reading(ct: CancellationToken.None));

        var item = Assert.Single(response.ContinueReading);
        Assert.Equal(fourth, item.ChapterId);
        Assert.Equal(0, item.Page);
        Assert.Equal(Base.AddDays(31), item.LastReadAt);
    }

    [Fact]
    public async Task The_next_chapter_resumes_at_the_page_reached_when_it_was_started_earlier()
    {
        // Started chapter 2, went back and re-read chapter 1: chapter 2 is next, at its page.
        var seriesId = _db.SeedSeries();
        var first = SeedChapter(seriesId, 1);
        var second = SeedChapter(seriesId, 2);
        SeedProgress(seriesId, second, pageIndex: 6, completed: false, updatedAt: Base);
        SeedProgress(seriesId, first, pageIndex: 19, completed: true, updatedAt: Base.AddHours(1));

        var response = Reading(await Controller().Reading(ct: CancellationToken.None));

        var item = Assert.Single(response.ContinueReading);
        Assert.Equal(second, item.ChapterId);
        Assert.Equal(6, item.Page);
        Assert.Equal(20, item.PageCount);
    }

    [Fact]
    public async Task Reading_drops_series_with_every_downloaded_chapter_read()
    {
        var seriesId = _db.SeedSeries();
        var only = SeedChapter(seriesId, 1);
        SeedProgress(seriesId, only, pageIndex: 0, completed: true, updatedAt: Base);

        var response = Reading(await Controller().Reading(ct: CancellationToken.None));

        Assert.Empty(response.ContinueReading);
    }

    [Fact]
    public async Task Reading_fills_past_finished_series_across_candidate_chunks()
    {
        // limit 1 resolves two candidates at a time; the newest two are fully read, the third is not.
        var open = _db.SeedSeries("Open");
        SeedProgress(open, SeedChapter(open, 1), pageIndex: 0, completed: true, updatedAt: Base);
        SeedChapter(open, 2);
        foreach (var (title, hours) in new[] { ("Done B", 1), ("Done A", 2) })
        {
            var done = _db.SeedSeries(title);
            SeedProgress(done, SeedChapter(done, 1), pageIndex: 0, completed: true, updatedAt: Base.AddHours(hours));
        }

        var response = Reading(await Controller().Reading(limit: 1, ct: CancellationToken.None));

        Assert.Equal(open, Assert.Single(response.ContinueReading).SeriesId);
    }

    [Fact]
    public async Task Reading_ignores_watched_chapters()
    {
        var seriesId = _db.SeedSeries();
        var watched = SeedChapter(seriesId, 1);
        SeedChapter(seriesId, 2);
        SeedProgress(seriesId, watched, pageIndex: 0, completed: true, updatedAt: Base, watched: true);

        var response = Reading(await Controller().Reading(ct: CancellationToken.None));

        Assert.Empty(response.ContinueReading);
    }

    [Fact]
    public async Task Reading_ignores_chapters_with_no_file()
    {
        var seriesId = _db.SeedSeries();
        var downloaded = SeedChapter(seriesId, 1);
        SeedMissingChapter(seriesId, 2); // known but not downloaded — not offerable
        SeedProgress(seriesId, downloaded, pageIndex: 0, completed: true, updatedAt: Base);

        var response = Reading(await Controller().Reading(ct: CancellationToken.None));

        Assert.Empty(response.ContinueReading);
    }

    [Fact]
    public async Task Reading_orders_one_shots_last()
    {
        var seriesId = _db.SeedSeries();
        var read = SeedChapter(seriesId, 1);
        SeedChapter(seriesId, null, title: "Bonus"); // one-shot: no number
        var numbered = SeedChapter(seriesId, 2);
        SeedProgress(seriesId, read, pageIndex: 0, completed: true, updatedAt: Base);

        var response = Reading(await Controller().Reading(ct: CancellationToken.None));

        var item = Assert.Single(response.ContinueReading);
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
            // Neither may leak into the list, and a join would also multiply the rows.
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

    private void EditFile(int chapterId, Action<ChapterFile> edit)
    {
        using var db = _db.NewContext();
        var fileId = db.Chapters.Single(c => c.Id == chapterId).ChapterFileId;
        edit(db.ChapterFiles.Single(f => f.Id == fileId));
        db.SaveChanges();
    }

    [Fact]
    public async Task RecentlyAdded_leaves_out_relinked_and_redownloaded_files()
    {
        var relinked = _db.SeedSeries("Relinked");
        var redownloaded = _db.SeedSeries("Redownloaded");
        var fresh = _db.SeedSeries("Fresh");
        EditFile(SeedChapter(relinked, 1, addedAt: Base.AddDays(2)),
            f => f.SourceName = ChapterFile.RelinkSourceName);
        EditFile(SeedChapter(redownloaded, 1, addedAt: Base.AddDays(2)),
            f => f.ReplacedAtUtc = f.DateAdded);
        SeedChapter(fresh, 1, addedAt: Base);

        var items = Recent(await Controller().RecentlyAdded(ct: CancellationToken.None));

        Assert.Equal(["Fresh"], items.Select(i => i.SeriesTitle));
    }

    [Fact]
    public async Task RecentlyAdded_keeps_a_file_upgraded_since_it_was_added()
    {
        var seriesId = _db.SeedSeries("Upgraded");
        EditFile(SeedChapter(seriesId, 1, addedAt: Base), f => f.ReplacedAtUtc = Base.AddDays(3));

        var item = Assert.Single(Recent(await Controller().RecentlyAdded(ct: CancellationToken.None)));

        Assert.Equal(Base, item.AddedAt);
    }

    [Fact]
    public async Task RecentlyAdded_drops_a_series_with_everything_read()
    {
        var seriesId = _db.SeedSeries();
        var chapterId = SeedChapter(seriesId, 1);
        SeedProgress(seriesId, chapterId, pageIndex: 0, completed: true, updatedAt: Base);

        Assert.Empty(Recent(await Controller().RecentlyAdded(ct: CancellationToken.None)));
    }

    [Fact]
    public async Task RecentlyAdded_counts_only_the_latest_batch()
    {
        // Weekly releases: each arrived on its own, so only the newest is new.
        var seriesId = _db.SeedSeries("Weekly");
        for (var week = 0; week < 11; week++)
        {
            SeedChapter(seriesId, week + 1, addedAt: Base.AddDays(7 * week));
        }

        var item = Assert.Single(Recent(await Controller().RecentlyAdded(ct: CancellationToken.None)));

        Assert.Equal(1, item.NewChapterCount);
        Assert.Equal("Ch.11", item.NewestChapterLabel);
    }

    [Fact]
    public async Task RecentlyAdded_drops_a_series_once_its_latest_batch_is_read()
    {
        // An older chapter is still unread, but nothing new is.
        var seriesId = _db.SeedSeries("Caught up on new");
        SeedChapter(seriesId, 1, addedAt: Base);
        var newest = SeedChapter(seriesId, 2, addedAt: Base.AddDays(7));
        SeedProgress(seriesId, newest, pageIndex: 0, completed: true, updatedAt: Base.AddDays(8));

        Assert.Empty(Recent(await Controller().RecentlyAdded(ct: CancellationToken.None)));
    }

    [Fact]
    public async Task RecentlyAdded_counts_and_labels_the_unread_part_of_the_batch()
    {
        var seriesId = _db.SeedSeries("Half read");
        SeedChapter(seriesId, 1, addedAt: Base);
        SeedChapter(seriesId, 2, addedAt: Base.AddMinutes(5));
        var third = SeedChapter(seriesId, 3, addedAt: Base.AddMinutes(10));
        SeedProgress(seriesId, third, pageIndex: 0, completed: true, updatedAt: Base.AddHours(1));

        var item = Assert.Single(Recent(await Controller().RecentlyAdded(ct: CancellationToken.None)));

        Assert.Equal(2, item.NewChapterCount);
        Assert.Equal("Ch.2", item.NewestChapterLabel);
        Assert.Equal(Base.AddMinutes(10), item.AddedAt);
    }

    [Fact]
    public async Task RecentlyAdded_fills_past_read_series_across_candidate_chunks()
    {
        // limit 1 resolves two candidates at a time; the newest two are read, the third is not.
        var open = _db.SeedSeries("Open");
        SeedChapter(open, 1, addedAt: Base);
        foreach (var (title, hours) in new[] { ("Read B", 1), ("Read A", 2) })
        {
            var read = _db.SeedSeries(title);
            SeedProgress(read, SeedChapter(read, 1, addedAt: Base.AddHours(hours)), 0, true, Base.AddHours(3));
        }

        var items = Recent(await Controller().RecentlyAdded(limit: 1, ct: CancellationToken.None));

        Assert.Equal(open, Assert.Single(items).SeriesId);
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

        // Chapter 2: the next unread after the last completed.
        var item = Assert.Single(response.ContinueReading.Where(i => i.SeriesTitle == "ContinueAfterLast"));
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
                null!, // thumbnails
                new ContinueReadingService(db),
                null!, // settings
                null!, // import service
                null!, // achievement queue
                null!, // app paths
                null!, // current user
                null!, // kavita user resolver
                null!  // app settings
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
        return new HomeController(context, new ContinueReadingService(context), Accents());
    }

    [Fact]
    public async Task Hidden_series_drop_off_the_list()
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

    private async Task<IReadOnlyList<HomeAnimeResumeItem>> FromAnime(int userId = 1, bool allRootFolders = true) =>
        (await FromAnimePage(userId, allRootFolders)).Items;

    private async Task<(IReadOnlyList<HomeAnimeResumeItem> Items, string? Total)> FromAnimePage(
        int userId = 1, bool allRootFolders = true, int limit = AnimeResumeService.RailLimit)
    {
        var context = _db.NewContext(userId, allRootFolders);
        var controller = new HomeController(context, new ContinueReadingService(context), Accents())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        var result = await controller.FromAnime(
            AnimeResumeFixture.Service(_db, context, _gate), limit, CancellationToken.None);
        var page = Assert.IsType<AnimeResumeService.RailPage>(Assert.IsType<OkObjectResult>(result).Value);
        return (page.Items, page.Total.ToString());
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
    public async Task From_anime_caps_the_response_but_reports_the_real_total()
    {
        AnimeResumeFixture.OptIn(_db, 1);
        for (var i = 1; i <= AnimeResumeService.RailLimit + 3; i++)
        {
            SeedFromAnime($"Series {i:000}", i, 8);
        }

        var (items, total) = await FromAnimePage();

        Assert.Equal(AnimeResumeService.RailLimit, items.Count);
        Assert.Equal((AnimeResumeService.RailLimit + 3).ToString(), total);
    }

    [Fact]
    public async Task From_anime_returns_past_the_rail_cap_when_asked()
    {
        AnimeResumeFixture.OptIn(_db, 1);
        for (var i = 1; i <= AnimeResumeService.RailLimit + 3; i++)
        {
            SeedFromAnime($"Series {i:000}", i, 8);
        }

        var (items, total) = await FromAnimePage(limit: AnimeResumeService.MaxRailLimit);

        Assert.Equal(AnimeResumeService.RailLimit + 3, items.Count);
        Assert.Equal((AnimeResumeService.RailLimit + 3).ToString(), total);
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
