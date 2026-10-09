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
}
