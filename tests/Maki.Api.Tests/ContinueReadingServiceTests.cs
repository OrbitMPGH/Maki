using Maki.Api.Services;
using Maki.Core.Entities;

namespace Maki.Api.Tests;

public class ContinueReadingServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private int Seed(int seriesId, decimal number, int? volume, bool downloaded = true, string language = "en")
    {
        using var db = _db.NewContext();
        ChapterFile? file = null;
        if (downloaded)
        {
            file = new ChapterFile { SeriesId = seriesId, RelativePath = $"{number}-{volume}-{language}.cbz" };
            db.ChapterFiles.Add(file);
            db.SaveChanges();
        }

        var chapter = new Chapter
        {
            SeriesId = seriesId, Number = number, Volume = volume, Language = language, ChapterFileId = file?.Id
        };
        db.Chapters.Add(chapter);
        db.SaveChanges();
        return chapter.Id;
    }

    private void Read(int seriesId, int chapterId)
    {
        using var db = _db.NewContext();
        db.ChapterProgress.Add(new ChapterProgress
        {
            UserId = 1, SeriesId = seriesId, ChapterId = chapterId, PageCount = 20, Completed = true
        });
        db.SaveChanges();
    }

    private async Task<int?> Next(int seriesId) =>
        (await new ContinueReadingService(_db.NewContext()).NextForAsync(seriesId, CancellationToken.None))?.ChapterId;

    [Fact]
    public async Task A_volumes_chapter_zero_is_not_offered_before_chapter_one()
    {
        var seriesId = _db.SeedSeries();
        Seed(seriesId, 0, 9);
        var first = Seed(seriesId, 1, 1);
        Seed(seriesId, 103, 9, downloaded: false);

        Assert.Equal(first, await Next(seriesId));
    }

    [Fact]
    public async Task A_volumes_chapter_zero_comes_after_the_previous_volume()
    {
        var seriesId = _db.SeedSeries();
        var zero = Seed(seriesId, 0, 9);
        Read(seriesId, Seed(seriesId, 1, 1));
        Read(seriesId, Seed(seriesId, 102, 8));
        Seed(seriesId, 103, 9);

        Assert.Equal(zero, await Next(seriesId));
    }

    [Fact]
    public async Task A_volume_only_entry_read_mid_series_is_not_the_readers_place()
    {
        var seriesId = _db.SeedSeries();
        var first = Seed(seriesId, 1, 1);
        Seed(seriesId, 2, 1);
        Seed(seriesId, 3, null);
        Seed(seriesId, 4, null);
        Seed(seriesId, 5, null);
        Seed(seriesId, 9, 3);
        Seed(seriesId, 10, 3);
        Read(seriesId, Seed(seriesId, 0, 2));

        Assert.Equal(first, await Next(seriesId));
    }

    [Fact]
    public async Task Another_languages_copy_of_the_last_read_chapter_is_not_next()
    {
        var seriesId = _db.SeedSeries();
        Read(seriesId, Seed(seriesId, 5, null));
        Seed(seriesId, 5, null, language: "es");
        var six = Seed(seriesId, 6, null);

        Assert.Equal(six, await Next(seriesId));
    }

    [Fact]
    public async Task A_series_read_through_in_one_language_is_finished_whatever_else_is_downloaded()
    {
        var seriesId = _db.SeedSeries();
        for (var n = 1; n <= 3; n++)
        {
            Read(seriesId, Seed(seriesId, n, null));
            Seed(seriesId, n, null, language: "es");
        }

        Assert.Null(await Next(seriesId));
    }

    [Fact]
    public async Task Another_languages_copies_do_not_count_as_unread()
    {
        var seriesId = _db.SeedSeries();
        Read(seriesId, Seed(seriesId, 1, null));
        Seed(seriesId, 1, null, language: "es");
        Seed(seriesId, 2, null);

        var next = await new ContinueReadingService(_db.NewContext()).NextForAsync(seriesId, CancellationToken.None);

        Assert.Equal(1, next!.UnreadChapters);
    }

    [Fact]
    public async Task Another_languages_copy_with_a_different_volume_still_counts_as_read()
    {
        var seriesId = _db.SeedSeries();
        Read(seriesId, Seed(seriesId, 5, 1));
        Seed(seriesId, 5, null, language: "es");

        Assert.Null(await Next(seriesId));
    }

    private int[] SeedRange(int seriesId, int from, int to) =>
        Enumerable.Range(from, to - from + 1).Select(n => Seed(seriesId, n, null)).ToArray();

    [Fact]
    public async Task A_stray_read_far_ahead_does_not_move_the_place()
    {
        var seriesId = _db.SeedSeries();
        foreach (var id in SeedRange(seriesId, 1, 10))
        {
            Read(seriesId, id);
        }

        var eleven = SeedRange(seriesId, 11, 49)[0];
        Read(seriesId, Seed(seriesId, 50, null));
        Seed(seriesId, 51, null);

        Assert.Equal(eleven, await Next(seriesId));
    }

    [Fact]
    public async Task Reading_on_from_a_jump_ahead_moves_the_place()
    {
        var seriesId = _db.SeedSeries();
        foreach (var id in SeedRange(seriesId, 1, 10))
        {
            Read(seriesId, id);
        }

        SeedRange(seriesId, 11, 49);
        Read(seriesId, Seed(seriesId, 50, null));
        Read(seriesId, Seed(seriesId, 51, null));
        var next = Seed(seriesId, 52, null);

        Assert.Equal(next, await Next(seriesId));
    }

    [Fact]
    public async Task A_bonus_chapter_read_does_not_move_the_place()
    {
        var seriesId = _db.SeedSeries();
        foreach (var id in SeedRange(seriesId, 1, 10))
        {
            Read(seriesId, id);
        }

        var eleven = SeedRange(seriesId, 11, 40)[0];
        Read(seriesId, Seed(seriesId, 37.5m, null));

        Assert.Equal(eleven, await Next(seriesId));
    }

    [Fact]
    public async Task A_bonus_chapter_between_reads_does_not_hold_the_place_back()
    {
        var seriesId = _db.SeedSeries();
        Read(seriesId, Seed(seriesId, 1, null));
        Seed(seriesId, 1.5m, null);
        Read(seriesId, Seed(seriesId, 2, null));
        var three = Seed(seriesId, 3, null);

        Assert.Equal(three, await Next(seriesId));
    }

    [Fact]
    public async Task Starting_mid_series_offers_the_chapter_after_it()
    {
        var seriesId = _db.SeedSeries();
        SeedRange(seriesId, 1, 99);
        Read(seriesId, Seed(seriesId, 100, null));
        var next = Seed(seriesId, 101, null);

        Assert.Equal(next, await Next(seriesId));
    }

    [Fact]
    public async Task A_skipped_chapter_is_offered_once_everything_after_it_is_read()
    {
        var seriesId = _db.SeedSeries();
        var chapters = SeedRange(seriesId, 1, 10);
        foreach (var id in chapters.Where((_, i) => i != 3))
        {
            Read(seriesId, id);
        }

        Assert.Equal(chapters[3], await Next(seriesId));
    }

    [Fact]
    public async Task Skipped_early_chapters_are_offered_once_the_rest_is_read()
    {
        var seriesId = _db.SeedSeries();
        var chapters = SeedRange(seriesId, 1, 10);
        foreach (var id in chapters.Skip(5))
        {
            Read(seriesId, id);
        }

        Assert.Equal(chapters[0], await Next(seriesId));
    }

    [Fact]
    public async Task A_skipped_chapter_does_not_pull_the_reader_back_while_more_is_ahead()
    {
        var seriesId = _db.SeedSeries();
        var chapters = SeedRange(seriesId, 1, 11);
        foreach (var id in chapters.Take(10).Where((_, i) => i != 3))
        {
            Read(seriesId, id);
        }

        Assert.Equal(chapters[10], await Next(seriesId));
    }
}
