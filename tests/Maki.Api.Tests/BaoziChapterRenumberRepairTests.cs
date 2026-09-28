using Maki.Api.Services;
using Maki.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// Baozi chapter rows synced before the fix carry the zero-based slot as their Number. The repair
/// renumbers each row still numbered by its slot to the number in its stored label, only for a
/// series whose sole mapping is Baozi, and only when that doesn't collide with a chapter it isn't
/// touching.
/// </summary>
public sealed class BaoziChapterRenumberRepairTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private BaoziChapterRenumberRepairService Service() =>
        new(_db.NewContext(), NullLogger<BaoziChapterRenumberRepairService>.Instance);

    private static SourceMapping BaoziMapping(int priority = 1) => new()
    {
        SourceName = "baozimanhua",
        SourceSeriesId = "test_00001",
        Url = "https://cn.baozimh.com/comic/test_00001",
        Priority = priority
    };

    private int AddChapter(int seriesId, decimal? number, string language = "zh-Hans")
    {
        using var db = _db.NewContext();
        var chapter = new Chapter { SeriesId = seriesId, Number = number, Language = language };
        db.Chapters.Add(chapter);
        db.SaveChanges();
        return chapter.Id;
    }

    private void LinkChapter(int chapterId, int mappingId, int slot, string label)
    {
        using var db = _db.NewContext();
        db.ChapterSourceLinks.Add(new ChapterSourceLink
        {
            ChapterId = chapterId,
            SourceMappingId = mappingId,
            SourceChapterId = $"0_{slot}",
            NumberRaw = label
        });
        db.SaveChanges();
    }

    /// <summary>An old-style row: Number is the slot, the link holds the label.</summary>
    private int AddLegacyChapter(int seriesId, int mappingId, int slot, string label)
    {
        var id = AddChapter(seriesId, slot);
        LinkChapter(id, mappingId, slot, label);
        return id;
    }

    private int BaoziMappingIdOf(int seriesId)
    {
        using var db = _db.NewContext();
        return db.SourceMappings.Single(m => m.SeriesId == seriesId && m.SourceName == "baozimanhua").Id;
    }

    private List<decimal?> NumbersOf(int seriesId)
    {
        using var db = _db.NewContext();
        return db.Chapters.Where(c => c.SeriesId == seriesId).Select(c => c.Number).OrderBy(n => n).ToList();
    }

    [Fact]
    public async Task Baozi_only_series_takes_numbers_from_the_labels()
    {
        var series = _db.SeedSeries("Test Manhua", mappings: [BaoziMapping()]);
        var mappingId = BaoziMappingIdOf(series);
        for (var slot = 0; slot < 4; slot++)
        {
            AddLegacyChapter(series, mappingId, slot, $"第{slot + 1}话 标题");
        }

        var (shifted, shiftedSeries, skipped) = await Service().RepairAsync();

        Assert.Equal(4, shifted);
        Assert.Equal(1, shiftedSeries);
        Assert.Equal(0, skipped);
        Assert.Equal([1m, 2m, 3m, 4m], NumbersOf(series));
    }

    [Fact]
    public async Task Series_with_a_second_source_mapping_is_untouched()
    {
        var series = _db.SeedSeries("Test Manhua", mappings:
        [
            BaoziMapping(),
            new SourceMapping { SourceName = "mangadex", SourceSeriesId = "abc", Url = "https://mangadex.org/title/abc", Priority = 2 }
        ]);
        var mappingId = BaoziMappingIdOf(series);
        var chapter = AddLegacyChapter(series, mappingId, 0, "第1话");

        var (shifted, shiftedSeries, skipped) = await Service().RepairAsync();

        Assert.Equal(0, shifted);
        Assert.Equal(0, shiftedSeries);
        Assert.Equal(0, skipped);

        using var db = _db.NewContext();
        Assert.Equal(0m, db.Chapters.Single(c => c.Id == chapter).Number);
    }

    [Fact]
    public async Task A_collision_with_an_unlinked_chapter_skips_the_whole_series()
    {
        var series = _db.SeedSeries("Test Manhua", mappings: [BaoziMapping()]);
        var mappingId = BaoziMappingIdOf(series);
        AddLegacyChapter(series, mappingId, 0, "第1话");
        AddLegacyChapter(series, mappingId, 1, "第2话");
        AddChapter(series, 2m);

        var (shifted, shiftedSeries, skipped) = await Service().RepairAsync();

        Assert.Equal(0, shifted);
        Assert.Equal(0, shiftedSeries);
        Assert.Equal(1, skipped);
        Assert.Equal([0m, 1m, 2m], NumbersOf(series));
    }

    [Fact]
    public async Task Rows_already_rematched_by_a_newer_sync_are_left_alone()
    {
        var series = _db.SeedSeries("Test Manhua", mappings: [BaoziMapping()]);
        var mappingId = BaoziMappingIdOf(series);
        // Number already equals the label while the slot is one lower.
        var first = AddChapter(series, 1m);
        LinkChapter(first, mappingId, 0, "第1话");
        var second = AddChapter(series, 2m);
        LinkChapter(second, mappingId, 1, "第2话");

        var (shifted, shiftedSeries, skipped) = await Service().RepairAsync();

        Assert.Equal(0, shifted);
        Assert.Equal(0, shiftedSeries);
        Assert.Equal(0, skipped);
        Assert.Equal([1m, 2m], NumbersOf(series));
    }

    [Fact]
    public async Task An_unnumbered_extra_does_not_offset_later_chapters()
    {
        var series = _db.SeedSeries("Test Manhua", mappings: [BaoziMapping()]);
        var mappingId = BaoziMappingIdOf(series);
        var first = AddLegacyChapter(series, mappingId, 0, "第1话");
        var extra = AddLegacyChapter(series, mappingId, 1, "番外 夏日篇");
        var second = AddLegacyChapter(series, mappingId, 2, "第2话");

        var (_, _, skipped) = await Service().RepairAsync();

        Assert.Equal(0, skipped);
        using var db = _db.NewContext();
        Assert.Equal(1m, db.Chapters.Single(c => c.Id == first).Number);
        Assert.Equal(2m, db.Chapters.Single(c => c.Id == second).Number);

        // Shaped like the one-shot the fixed source lists, so the next sync matches it by title.
        var extraRow = db.Chapters.Single(c => c.Id == extra);
        Assert.Null(extraRow.Number);
        Assert.True(extraRow.IsOneShot);
        Assert.Equal("番外 夏日篇", extraRow.Title);
    }

    [Fact]
    public async Task Marker_is_written_even_when_nothing_changes()
    {
        await Service().RunOnceAsync();

        using var db = _db.NewContext();
        Assert.Equal(1, db.AppConfig.Count(c => c.Key == BaoziChapterRenumberRepairService.MarkerKey));
    }

    [Fact]
    public async Task Second_run_is_a_no_op_due_to_the_marker()
    {
        var series = _db.SeedSeries("Test Manhua", mappings: [BaoziMapping()]);
        var mappingId = BaoziMappingIdOf(series);
        var chapter = AddLegacyChapter(series, mappingId, 0, "第1话");

        await Service().RunOnceAsync();
        using (var db = _db.NewContext())
        {
            Assert.Equal(1m, db.Chapters.Single(c => c.Id == chapter).Number);
        }

        var second = AddLegacyChapter(series, mappingId, 5, "第6话");
        await Service().RunOnceAsync();

        using var finalDb = _db.NewContext();
        Assert.Equal(1m, finalDb.Chapters.Single(c => c.Id == chapter).Number);
        Assert.Equal(5m, finalDb.Chapters.Single(c => c.Id == second).Number);
        Assert.Equal(1, finalDb.AppConfig.Count(c => c.Key == BaoziChapterRenumberRepairService.MarkerKey));
    }

    [Fact]
    public async Task Repair_is_idempotent_without_the_marker()
    {
        var series = _db.SeedSeries("Test Manhua", mappings: [BaoziMapping()]);
        var mappingId = BaoziMappingIdOf(series);
        AddLegacyChapter(series, mappingId, 0, "第1话");
        AddLegacyChapter(series, mappingId, 1, "第2话");

        await Service().RepairAsync();
        using (var db = _db.NewContext())
        {
            db.AppConfig.RemoveRange(db.AppConfig.Where(c => c.Key == BaoziChapterRenumberRepairService.MarkerKey));
            db.SaveChanges();
        }

        var (shifted, _, skipped) = await Service().RepairAsync();

        Assert.Equal(0, shifted);
        Assert.Equal(0, skipped);
        Assert.Equal([1m, 2m], NumbersOf(series));
    }
}
