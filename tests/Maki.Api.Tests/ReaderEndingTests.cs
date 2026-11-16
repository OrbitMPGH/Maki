using System.IO.Compression;
using System.Text.Json;
using Maki.Api.Configuration;
using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Api.Services.Upscaling;
using Maki.Core.Entities;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// What the reader's manifest and its live ending read tell the end screen: whether finishing this
/// chapter finishes the series, the reader's own pace, and the next chapter once it lands.
/// </summary>
public sealed class ReaderEndingTests : IDisposable
{
    private const int UserId = 1;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly DateTime Now = new(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);

    private readonly TestDb _db = new();
    private readonly string _configDir = Path.Combine(Path.GetTempPath(), "maki-ending-" + Guid.NewGuid().ToString("N"));
    private readonly ReaderArchiveCache _archives = new(NullLogger<ReaderArchiveCache>.Instance);

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            Directory.Delete(_configDir, true);
        }
        catch (IOException)
        {
        }
    }

    private int SeedSeries(SeriesStatus status, int? total = null)
    {
        using var db = _db.NewContext();
        var folder = new RootFolder { Path = Path.Combine(_configDir, "library") };
        db.RootFolders.Add(folder);
        db.SaveChanges();
        var series = new Series
        {
            Title = "S", SortTitle = "s", FolderName = "S", RootFolderId = folder.Id, Status = status, TotalChapters = total,
        };
        db.Series.Add(series);
        db.SaveChanges();
        return series.Id;
    }

    /// <summary>A chapter with a real archive on disk, or a wanted one with none yet.</summary>
    private int AddChapter(int seriesId, decimal number, bool onDisk = true)
    {
        using var db = _db.NewContext();
        int? fileId = null;
        if (onDisk)
        {
            var name = $"c{number}.cbz";
            var path = Path.Combine(_configDir, "library", "S", name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                for (var page = 1; page <= 3; page++)
                {
                    using var entry = zip.CreateEntry($"{page:000}.png").Open();
                    entry.Write([0x89, 0x50, 0x4E, 0x47]);
                }
            }

            var file = new ChapterFile
            {
                SeriesId = seriesId, RelativePath = $"S/{name}", Size = new FileInfo(path).Length,
                SourceName = "test", DateAdded = Now,
            };
            db.ChapterFiles.Add(file);
            db.SaveChanges();
            fileId = file.Id;
        }

        var chapter = new Chapter { SeriesId = seriesId, Number = number, ChapterFileId = fileId, Wanted = true };
        db.Chapters.Add(chapter);
        db.SaveChanges();
        return chapter.Id;
    }

    private void Complete(int seriesId, int chapterId, int seconds = 0)
    {
        using var db = _db.NewContext();
        db.ChapterProgress.Add(new ChapterProgress
        {
            UserId = UserId, SeriesId = seriesId, ChapterId = chapterId, PageCount = 3, Completed = true,
            ReadSeconds = seconds, StartedAt = Now, UpdatedAt = Now,
        });
        db.SaveChanges();
    }

    private void SeedFinished(int seriesId)
    {
        using var db = _db.NewContext();
        db.ReadingStates.Add(new ReadingState
        {
            UserId = UserId, SeriesId = seriesId, Title = "S", Finished = true, MaxChapter = 2,
            LastProgressAt = Now, UpdatedAt = Now,
        });
        db.SaveChanges();
    }

    private ReaderController Controller(MakiDbContext db) => new(
        new TestLocalizer(), db,
        new ReaderService(db, _archives, null!, null!, new ReadingSessionService(db), NullLogger<ReaderService>.Instance),
        null!, null!, new ReadingProfileService(db, new UserSettingsService(db, new TestCurrentUser(UserId))),
        null!, null!, new AppPaths(_configDir), new TestCurrentUser(UserId), null!, new FakeAppSettings())
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
    };

    private async Task<JsonElement> ManifestAsync(int chapterId)
    {
        using var db = _db.NewContext(UserId);
        var result = await Controller(db).Manifest(
            chapterId, new EnhanceAvailability(new FakeAppSettings(), null!),
            new CreditStore(_db.ScopeFactory(), new Maki.Api.Services.Ocr.PageTextReader(NullLogger<Maki.Api.Services.Ocr.PageTextReader>.Instance, "missing"), NullLogger<CreditStore>.Instance),
            new ReadingTimeEstimateService(db), default);
        return JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value, Web);
    }

    private async Task<JsonElement> EndingAsync(int chapterId)
    {
        using var db = _db.NewContext(UserId);
        var result = await Controller(db).Ending(chapterId, default);
        return JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value, Web);
    }

    [Theory]
    [InlineData(SeriesStatus.Completed, 10, 10, true)]
    [InlineData(SeriesStatus.Completed, 10.5, 10, true)]
    [InlineData(SeriesStatus.Completed, 9, 10, false)]
    [InlineData(SeriesStatus.Completed, 0, 10, false)]
    [InlineData(SeriesStatus.Ongoing, 10, 10, false)]
    public void A_mark_closes_a_completed_series_at_its_last_numbered_chapter(
        SeriesStatus status, double mark, int highest, bool closes) =>
        Assert.Equal(closes, SeriesFinishRule.Closes(status, mark, highest));

    [Fact]
    public void A_series_with_no_numbered_chapter_never_closes() =>
        Assert.False(SeriesFinishRule.Closes(SeriesStatus.Completed, 5, null));

    [Fact]
    public async Task The_last_chapter_of_a_completed_series_finishes_it_and_an_earlier_one_does_not()
    {
        var series = SeedSeries(SeriesStatus.Completed);
        var first = AddChapter(series, 1);
        var last = AddChapter(series, 2);

        Assert.False((await ManifestAsync(first)).GetProperty("finishesSeries").GetBoolean());
        var manifest = await ManifestAsync(last);
        Assert.True(manifest.GetProperty("finishesSeries").GetBoolean());
        Assert.False(manifest.GetProperty("seriesFinished").GetBoolean());
    }

    [Fact]
    public async Task An_ongoing_series_is_never_finished_by_its_last_chapter()
    {
        var series = SeedSeries(SeriesStatus.Ongoing);
        AddChapter(series, 1);
        var last = AddChapter(series, 2);

        Assert.False((await ManifestAsync(last)).GetProperty("finishesSeries").GetBoolean());
    }

    [Fact]
    public async Task A_finished_series_says_so_in_the_manifest()
    {
        var series = SeedSeries(SeriesStatus.Completed);
        AddChapter(series, 1);
        var last = AddChapter(series, 2);
        SeedFinished(series);

        Assert.True((await ManifestAsync(last)).GetProperty("seriesFinished").GetBoolean());
    }

    [Fact]
    public async Task The_manifest_carries_the_readers_pace_once_three_chapters_are_timed()
    {
        var series = SeedSeries(SeriesStatus.Ongoing, total: 20);
        var timed = new[] { 1, 2, 3 }.Select(n => AddChapter(series, n)).ToArray();
        var open = AddChapter(series, 4);

        Assert.Equal(JsonValueKind.Null, (await ManifestAsync(open)).GetProperty("readingPace").ValueKind);

        Complete(series, timed[0], 300);
        Complete(series, timed[1], 600);
        Complete(series, timed[2], 900);
        var pace = (await ManifestAsync(open)).GetProperty("readingPace");

        Assert.Equal(600, pace.GetProperty("chapterSeconds").GetInt32());
        Assert.Equal(20, pace.GetProperty("totalChapters").GetInt32());
        Assert.Equal("paged", pace.GetProperty("style").GetString());
    }

    [Fact]
    public async Task The_ending_names_a_next_chapter_the_moment_one_lands()
    {
        var series = SeedSeries(SeriesStatus.Ongoing);
        var current = AddChapter(series, 1);
        var upcoming = AddChapter(series, 2, onDisk: false);

        var before = await EndingAsync(current);
        Assert.Equal(JsonValueKind.Null, before.GetProperty("nextChapterId").ValueKind);
        Assert.Equal(2, before.GetProperty("seriesWantedCount").GetInt32());
        Assert.Equal(1, before.GetProperty("seriesChapterCount").GetInt32());

        using (var db = _db.NewContext())
        {
            var file = new ChapterFile
            {
                SeriesId = series, RelativePath = "S/c2.cbz", Size = 1, SourceName = "test", DateAdded = Now,
            };
            db.ChapterFiles.Add(file);
            db.SaveChanges();
            db.Chapters.Single(c => c.Id == upcoming).ChapterFileId = file.Id;
            db.SaveChanges();
        }

        var after = await EndingAsync(current);
        Assert.Equal(upcoming, after.GetProperty("nextChapterId").GetInt32());
        Assert.Equal(2, after.GetProperty("nextChapterNumber").GetDecimal());
        Assert.Equal(2, after.GetProperty("seriesChapterCount").GetInt32());
    }

    [Fact]
    public async Task The_ending_counts_this_chapter_once_its_completing_write_has_landed()
    {
        var series = SeedSeries(SeriesStatus.Ongoing);
        var current = AddChapter(series, 1);
        AddChapter(series, 2);

        var before = await EndingAsync(current);
        Assert.False(before.GetProperty("completed").GetBoolean());
        Assert.Equal(0, before.GetProperty("seriesReadCount").GetInt32());

        Complete(series, current);
        var after = await EndingAsync(current);
        Assert.True(after.GetProperty("completed").GetBoolean());
        Assert.Equal(1, after.GetProperty("seriesReadCount").GetInt32());
    }

    [Fact]
    public async Task The_ending_reports_where_the_final_rating_stands()
    {
        var series = SeedSeries(SeriesStatus.Completed);
        var last = AddChapter(series, 1);
        SeedFinished(series);

        var asked = await EndingAsync(last);
        Assert.True(asked.GetProperty("finished").GetBoolean());
        Assert.Equal(JsonValueKind.Null, asked.GetProperty("rating").ValueKind);
        Assert.False(asked.GetProperty("rateHintDismissed").GetBoolean());

        using (var db = _db.NewContext())
        {
            db.UserSeriesStates.Add(new UserSeriesState
            {
                UserId = UserId, SeriesId = series, Rating = 9, RateHintDismissedAt = Now, UpdatedAt = Now,
            });
            db.SaveChanges();
        }

        var answered = await EndingAsync(last);
        Assert.Equal(9, answered.GetProperty("rating").GetInt32());
        Assert.True(answered.GetProperty("rateHintDismissed").GetBoolean());
    }

    [Fact]
    public async Task The_ending_hides_a_chapter_in_a_folder_the_reader_has_no_grant_for()
    {
        var series = SeedSeries(SeriesStatus.Ongoing);
        var chapter = AddChapter(series, 1);

        using var db = _db.NewContext(UserId, allRootFolders: false);
        var result = await Controller(db).Ending(chapter, default);

        Assert.IsNotType<OkObjectResult>(result);
    }
}
