using Maki.Api.Controllers;
using Maki.Api.Dtos;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Sources;
using Microsoft.AspNetCore.Mvc;

namespace Maki.Api.Tests;

/// <summary>The one total Reading and Downloads count against, and the specials rule that scopes it.</summary>
public sealed class SeriesProgressScopeTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static Series Series(int? total = null, bool locked = false,
        NewChapterMonitorMode mode = NewChapterMonitorMode.All) => new()
    {
        Title = "S", TotalChapters = total, MonitorNewItems = mode,
        LockedFields = locked ? SeriesMetadataField.TotalChapters : default,
    };

    [Fact]
    public void Kept_specials_are_added_on_top_of_the_MangaBaka_total()
    {
        var t = SeriesProgressScope.Compute(Series(203), known: 221, had: 220, specials: 19, specialsHad: 19,
            highestMain: 201, read: 105, readSpecials: 8, unmonitorSpecials: false);

        Assert.Equal(new SeriesProgressScope.Totals(222, 220, 105, true, 19, 0, 19), t);
    }

    [Fact]
    public void Unmonitored_specials_leave_every_count()
    {
        var t = SeriesProgressScope.Compute(Series(203), 221, 220, 19, 19, 201, 105, 8, unmonitorSpecials: true);

        Assert.Equal(new SeriesProgressScope.Totals(203, 201, 97, false, 19, 0, 0), t);
    }

    [Fact]
    public void Main_only_series_skips_specials_without_the_global_setting()
    {
        var t = SeriesProgressScope.Compute(
            Series(203, mode: NewChapterMonitorMode.MainOnly), 221, 220, 19, 19, 201, 105, 8, false);

        Assert.False(t.SpecialsCounted);
        Assert.Equal(203, t.Total);
    }

    [Fact]
    public void Sources_past_an_unlocked_total_raise_it_and_a_locked_total_stays()
    {
        Assert.Equal(160, SeriesProgressScope.Compute(Series(150), 160, 0, 0, 0, 160, null, 0, true).Total);
        Assert.Equal(150, SeriesProgressScope.Compute(Series(150, locked: true), 160, 0, 0, 0, 160, null, 0, true).Total);
    }

    [Fact]
    public void More_main_rows_than_the_highest_number_raise_the_total()
    {
        // Chapters 0 to 99: a hundred rows, highest 99, MangaBaka 99.
        var t = SeriesProgressScope.Compute(Series(99), known: 100, had: 100, specials: 0, specialsHad: 0,
            highestMain: 99, read: null, readSpecials: 0, unmonitorSpecials: true);

        Assert.Equal(100, t.Total);
    }

    [Fact]
    public void With_no_total_and_nothing_numbered_every_main_chapter_listed_counts()
    {
        var t = SeriesProgressScope.Compute(Series(), known: 12, had: 3, specials: 2, specialsHad: 0,
            highestMain: null, read: null, readSpecials: 0, unmonitorSpecials: true);

        Assert.Equal(10, t.Total);
        Assert.Null(t.Read);
    }

    [Theory]
    [InlineData(NewChapterMonitorMode.All)]
    [InlineData(NewChapterMonitorMode.Smart)]
    public void A_switched_off_prelude_leaves_the_total(NewChapterMonitorMode mode)
    {
        // Chapters 0 to 17, chapter 0 switched off and never downloaded; MangaBaka says 13.
        var t = SeriesProgressScope.Compute(Series(13, mode: mode), known: 18, had: 17, specials: 0, specialsHad: 0,
            highestMain: 17, read: 17, readSpecials: 0, unmonitorSpecials: false,
            new SeriesProgressScope.SwitchedOff(1, 0, 17));

        Assert.Equal((17, 17, 17, 1), (t.Total, t.Have, t.Read, t.Skipped));
    }

    [Fact]
    public void Switching_off_the_last_chapters_lowers_the_highest_floor()
    {
        var t = SeriesProgressScope.Compute(Series(), known: 20, had: 10, specials: 0, specialsHad: 0,
            highestMain: 20, read: null, readSpecials: 0, unmonitorSpecials: false,
            new SeriesProgressScope.SwitchedOff(3, 0, 17));

        Assert.Equal((17, 3), (t.Total, t.Skipped));
    }

    [Fact]
    public void A_higher_MangaBaka_total_keeps_its_floor_and_reports_nothing_skipped()
    {
        var t = SeriesProgressScope.Compute(Series(30), known: 18, had: 17, specials: 0, specialsHad: 0,
            highestMain: 17, read: null, readSpecials: 0, unmonitorSpecials: false,
            new SeriesProgressScope.SwitchedOff(1, 0, 17));

        Assert.Equal((30, 0), (t.Total, t.Skipped));
    }

    [Fact]
    public void Switched_off_specials_leave_the_kept_specials()
    {
        var t = SeriesProgressScope.Compute(Series(10), known: 13, had: 11, specials: 3, specialsHad: 1,
            highestMain: 10, read: null, readSpecials: 0, unmonitorSpecials: false,
            new SeriesProgressScope.SwitchedOff(2, 2, 10));

        Assert.Equal((11, 2, 1), (t.Total, t.Skipped, t.IncludedSpecials));
    }

    // Chapters 1 to 26 plus specials 7.1, 7.5 and 13.5, against a MangaBaka total of 29 that counts them.
    [Fact]
    public void A_MangaBaka_total_that_counts_the_specials_loses_them_when_they_are_switched_off()
    {
        var t = SeriesProgressScope.Compute(Series(29), known: 29, had: 26, specials: 3, specialsHad: 0,
            highestMain: 26, read: 26, readSpecials: 0, unmonitorSpecials: false,
            new SeriesProgressScope.SwitchedOff(3, 3, 26));

        Assert.Equal((26, 26, 3), (t.Total, t.Read, t.Skipped));
    }

    [Fact]
    public void A_MangaBaka_total_that_counts_the_specials_is_not_raised_by_them_again()
    {
        Assert.Equal(29, SeriesProgressScope.Compute(Series(29), 29, 29, 3, 3, 26, 29, 3, false).Total);
        Assert.Equal(26, SeriesProgressScope.Compute(Series(29), 29, 29, 3, 3, 26, 29, 3, true).Total);
    }

    [Fact]
    public void A_listing_short_of_the_MangaBaka_total_keeps_it_whole()
    {
        var t = SeriesProgressScope.Compute(Series(100), known: 60, had: 60, specials: 5, specialsHad: 5,
            highestMain: 55, read: null, readSpecials: 0, unmonitorSpecials: true);

        Assert.Equal(100, t.Total);
    }

    [Fact]
    public void A_None_series_ignores_the_switches_it_stamped_off_itself()
    {
        var t = SeriesProgressScope.Compute(Series(mode: NewChapterMonitorMode.None), known: 50, had: 5, specials: 0,
            specialsHad: 0, highestMain: 50, read: null, readSpecials: 0, unmonitorSpecials: false,
            new SeriesProgressScope.SwitchedOff(45, 0, 5));

        Assert.Equal((50, 0), (t.Total, t.Skipped));
    }

    [Fact]
    public void A_locked_total_stays_and_reports_no_main_chapter_skipped()
    {
        var t = SeriesProgressScope.Compute(Series(18, locked: true), known: 18, had: 17, specials: 0, specialsHad: 0,
            highestMain: 17, read: null, readSpecials: 0, unmonitorSpecials: false,
            new SeriesProgressScope.SwitchedOff(1, 0, 17));

        Assert.Equal((18, 0), (t.Total, t.Skipped));
    }

    [Fact]
    public async Task List_leaves_switched_off_chapters_out_but_keeps_removed_ones()
    {
        var user = _db.SeedUser("reader");
        var id = _db.SeedSeries("Prelude", configure: s => s.TotalChapters = 2);
        using (var db = _db.NewContext())
        {
            var file = new ChapterFile { SeriesId = id, RelativePath = "c.cbz" };
            db.ChapterFiles.Add(file);
            db.SaveChanges();
            db.Chapters.Add(new Chapter { SeriesId = id, Number = 0m, Wanted = false });
            db.Chapters.Add(new Chapter { SeriesId = id, Number = 1m, ChapterFileId = file.Id });
            db.Chapters.Add(new Chapter { SeriesId = id, Number = 2m, Wanted = false, FileRemovedAt = DateTime.UtcNow });
            db.Chapters.Add(new Chapter { SeriesId = id, Number = 3m, Wanted = false });
            db.SaveChanges();
        }

        var row = await Row(user, id, new FakeAppSettings());

        Assert.Equal((2, 2, 2), (row.ProgressTotal, row.ProgressHave, row.ProgressSkipped));
    }

    [Fact]
    public async Task List_counts_specials_in_sql_and_drops_them_when_unmonitored()
    {
        var user = _db.SeedUser("reader");
        var id = _db.SeedSeries("Counted", configure: s => s.TotalChapters = 4);
        using (var db = _db.NewContext())
        {
            var file = new ChapterFile { SeriesId = id, RelativePath = "v1.cbz" };
            db.ChapterFiles.Add(file);
            db.SaveChanges();
            foreach (var n in new decimal?[] { 1m, 1.5m, 2m, 3m, 3.5m })
            {
                var chapter = new Chapter { SeriesId = id, Number = n, ChapterFileId = file.Id };
                db.Chapters.Add(chapter);
                db.SaveChanges();
                if (n is 1m or 1.5m or 2m)
                {
                    db.ChapterProgress.Add(new ChapterProgress
                    {
                        UserId = user, SeriesId = id, ChapterId = chapter.Id, Completed = true,
                        StartedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                    });
                }
            }
            db.SaveChanges();
        }

        var kept = await Row(user, id, new FakeAppSettings());
        Assert.Equal((6, 5, 3, true, 2), (kept.ProgressTotal, kept.ProgressHave, kept.ProgressRead, kept.SpecialsCounted, kept.ListedSpecials));

        var skipped = await Row(user, id, new FakeAppSettings().Set(SettingKeys.MonitoringUnmonitorSpecials, "true"));
        Assert.Equal((4, 3, 2, false), (skipped.ProgressTotal, skipped.ProgressHave, skipped.ProgressRead, skipped.SpecialsCounted));
    }

    private async Task<SeriesDto> Row(int user, int id, FakeAppSettings settings)
    {
        using var db = _db.NewContext(user);
        var controller = new SeriesController(new TestLocalizer(), db, null!, null!, null!, null!, null!, null!, null!,
            null!, null!, settings, null!, null!, null!, null!, null!, null!, null!, null!,
            new SourceAvailability(settings, new SourceRegistry([])), null!, new NullUserSettings(), null!, null!, null!);
        var list = (IEnumerable<SeriesDto>)((OkObjectResult)await controller.List(default)).Value!;
        return list.Single(s => s.Id == id);
    }

    private sealed class NullUserSettings : IUserSettings
    {
        public int UserId => 1;

        public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<Dictionary<string, string>> GetManyAsync(
            IReadOnlyCollection<string> keys, CancellationToken ct = default) =>
            Task.FromResult(new Dictionary<string, string>());

        public Task SetAsync(string key, string? value, CancellationToken ct = default) => Task.CompletedTask;
    }
}
