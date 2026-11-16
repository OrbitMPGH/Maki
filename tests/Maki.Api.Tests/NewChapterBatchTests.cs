using Maki.Api.Services;
using Maki.Core.Entities;

namespace Maki.Api.Tests;

/// <summary>
/// The one "N new" rule Home's Recently added rail and the Library rows share: unread files in the
/// series' latest batch.
/// </summary>
public class NewChapterBatchTests : IDisposable
{
    private static readonly DateTime Base = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static NewChapterBatch.File F(int id, DateTime at) => new(id, at);

    [Fact]
    public void Latest_keeps_only_the_newest_run()
    {
        var batch = NewChapterBatch.Latest(
        [
            F(1, Base),
            F(2, Base.AddDays(7)),
            F(3, Base.AddDays(7).AddMinutes(5)),
            F(4, Base.AddDays(7).AddMinutes(10)),
        ]);

        Assert.Equal([4, 3, 2], batch);
    }

    [Fact]
    public void Latest_joins_a_file_exactly_one_gap_apart_and_cuts_just_past_it()
    {
        var gap = NewChapterBatch.BatchGap;

        Assert.Equal([2, 1], NewChapterBatch.Latest([F(1, Base), F(2, Base + gap)]));
        Assert.Equal([2], NewChapterBatch.Latest([F(1, Base), F(2, Base + gap + TimeSpan.FromSeconds(1))]));
    }

    [Fact]
    public void Latest_chains_files_each_within_the_gap_of_the_next()
    {
        var step = NewChapterBatch.BatchGap - TimeSpan.FromHours(1);

        Assert.Equal([3, 2, 1], NewChapterBatch.Latest([F(1, Base), F(2, Base + step), F(3, Base + step * 2)]));
    }

    [Fact]
    public void Latest_of_no_files_is_empty() => Assert.Empty(NewChapterBatch.Latest([]));

    [Fact]
    public void UnreadFiles_keeps_a_volume_file_until_every_chapter_in_it_is_read()
    {
        var chapters = new Dictionary<int, int[]> { [10] = [100, 101], [11] = [102] };

        var unread = NewChapterBatch.UnreadFiles([10, 11], id => chapters[id], new HashSet<int> { 101 });

        Assert.Equal([10], unread);
    }

    [Fact]
    public void UnreadFiles_is_empty_when_nothing_is_left_to_read() =>
        Assert.Empty(NewChapterBatch.UnreadFiles([10], _ => [100], null));

    private int SeedChapter(int seriesId, decimal number, DateTime addedAt)
    {
        using var db = _db.NewContext();
        var file = new ChapterFile { SeriesId = seriesId, RelativePath = $"{seriesId}-{number}.cbz", DateAdded = addedAt };
        db.ChapterFiles.Add(file);
        db.SaveChanges();
        var chapter = new Chapter { SeriesId = seriesId, Number = number, ChapterFileId = file.Id };
        db.Chapters.Add(chapter);
        db.SaveChanges();
        return chapter.Id;
    }

    private void SeedRead(int seriesId, int chapterId, int userId = 1)
    {
        using var db = _db.NewContext();
        db.ChapterProgress.Add(new ChapterProgress
        {
            UserId = userId, SeriesId = seriesId, ChapterId = chapterId, PageCount = 20, Completed = true,
            StartedAt = Base, UpdatedAt = Base,
        });
        db.SaveChanges();
    }

    private async Task<Dictionary<int, int>> CountsAsync(int userId = 1)
    {
        using var db = _db.NewContext(userId);
        return await LibraryListRows.NewChapterCountsAsync(db, null, CancellationToken.None);
    }

    [Fact]
    public async Task Library_counts_only_the_unread_files_of_the_latest_batch()
    {
        var seriesId = _db.SeedSeries("Half read");
        SeedChapter(seriesId, 1, Base);
        SeedChapter(seriesId, 2, Base.AddDays(7));
        SeedChapter(seriesId, 3, Base.AddDays(7).AddMinutes(5));
        SeedRead(seriesId, SeedChapter(seriesId, 4, Base.AddDays(7).AddMinutes(10)));

        Assert.Equal(2, (await CountsAsync())[seriesId]);
    }

    [Fact]
    public async Task Library_reports_nothing_new_once_the_latest_batch_is_read()
    {
        var seriesId = _db.SeedSeries("Caught up on new");
        SeedChapter(seriesId, 1, Base);
        SeedRead(seriesId, SeedChapter(seriesId, 2, Base.AddDays(7)));

        Assert.False((await CountsAsync()).ContainsKey(seriesId));
    }

    [Fact]
    public async Task Library_counts_against_the_callers_own_reads()
    {
        var other = _db.SeedUser("other");
        var seriesId = _db.SeedSeries("Shared");
        SeedRead(seriesId, SeedChapter(seriesId, 1, Base), userId: other);

        Assert.Equal(1, (await CountsAsync())[seriesId]);
        Assert.False((await CountsAsync(other)).ContainsKey(seriesId));
    }

    private int SeedFile(int seriesId, DateTime addedAt, params (decimal? Number, int? Volume)[] chapters)
    {
        using var db = _db.NewContext();
        var file = new ChapterFile { SeriesId = seriesId, RelativePath = $"{seriesId}-{Guid.NewGuid()}.cbz", DateAdded = addedAt };
        db.ChapterFiles.Add(file);
        db.SaveChanges();
        db.Chapters.AddRange(chapters.Select(c => new Chapter
        {
            SeriesId = seriesId, Number = c.Number, Volume = c.Volume, ChapterFileId = file.Id,
        }));
        db.SaveChanges();
        return file.Id;
    }

    private int ChapterOf(int fileId, decimal? number)
    {
        using var db = _db.NewContext();
        return db.Chapters.Single(c => c.ChapterFileId == fileId && c.Number == number).Id;
    }

    /// <summary>The rule as first written: <see cref="ContinueReadingService"/> over every chapter.</summary>
    private async Task<Dictionary<int, int>> ReferenceCountsAsync(int userId)
    {
        using var db = _db.NewContext(userId);
        var batches = db.ChapterFiles.Where(ChapterFile.IsNewContent).ToList()
            .GroupBy(f => f.SeriesId)
            .ToDictionary(g => g.Key, g => NewChapterBatch.Latest(g.Select(f => new NewChapterBatch.File(f.Id, f.DateAdded))));
        var next = await new ContinueReadingService(db).NextForAsync(batches.Keys.ToList(), CancellationToken.None);
        var byFile = db.Chapters.Where(c => c.ChapterFileId != null).ToList().ToLookup(c => c.ChapterFileId!.Value, c => c.Id);
        return batches
            .Select(b => (b.Key, Count: NewChapterBatch.UnreadFiles(b.Value, id => byFile[id], next.GetValueOrDefault(b.Key)?.UnreadChapterIds).Count))
            .Where(x => x.Count > 0)
            .ToDictionary(x => x.Key, x => x.Count);
    }

    [Fact]
    public async Task Library_counts_match_the_continue_reading_rule_on_a_mixed_library()
    {
        var other = _db.SeedUser("other");
        var week = Base.AddDays(7);

        var partly = _db.SeedSeries("Partly read");
        SeedRead(partly, ChapterOf(SeedFile(partly, Base, (1, null)), 1));
        SeedFile(partly, week, (2, null));
        SeedRead(partly, ChapterOf(SeedFile(partly, week.AddMinutes(5), (3, null)), 3));
        var four = SeedFile(partly, week.AddMinutes(10), (4, null));
        SeedRead(partly, ChapterOf(four, 4), userId: other);

        var volumes = _db.SeedSeries("Volumes");
        var vol1 = SeedFile(volumes, week, (10, 1), (11, 1), (12, 1));
        SeedRead(volumes, ChapterOf(vol1, 10));
        SeedRead(volumes, ChapterOf(vol1, 11));
        var vol2 = SeedFile(volumes, week.AddMinutes(1), (13, 2), (14, 2));
        SeedRead(volumes, ChapterOf(vol2, 13));
        SeedRead(volumes, ChapterOf(vol2, 14));
        foreach (var n in new decimal[] { 10, 11, 12 })
        {
            SeedRead(volumes, ChapterOf(vol1, n), userId: other);
        }

        var languages = _db.SeedSeries("Two languages");
        SeedRead(languages, ChapterOf(SeedFile(languages, Base, (7, 1)), 7));
        SeedFile(languages, week, (7, 2));
        SeedFile(languages, week.AddMinutes(1), (8, 2));

        var extras = _db.SeedSeries("Volume extras");
        SeedRead(extras, ChapterOf(SeedFile(extras, Base, (0, 1)), 0));
        SeedRead(extras, ChapterOf(SeedFile(extras, Base.AddMinutes(1), (0, 3)), 0));
        SeedFile(extras, week, (0, 1));
        SeedFile(extras, week.AddMinutes(1), (0, 2));
        SeedFile(extras, week.AddMinutes(2), (0, 3));
        SeedFile(extras, week.AddMinutes(3), (null, null));

        var caughtUp = _db.SeedSeries("Caught up");
        SeedRead(caughtUp, ChapterOf(SeedFile(caughtUp, week, (1, null)), 1));

        var untouched = _db.SeedSeries("Untouched");
        SeedFile(untouched, Base, (1, null));
        SeedFile(untouched, week, (2, null), (3, null));
        SeedFile(untouched, week.AddHours(1), (4, null));

        var longRun = _db.SeedSeries("Long import");
        SeedFile(longRun, Base.AddDays(-30), (0.5m, null));
        for (var i = 0; i < 20; i++)
        {
            var file = SeedFile(longRun, Base.AddHours(11 * i), (i + 1, null));
            if (i is 0 or 19)
            {
                SeedRead(longRun, ChapterOf(file, i + 1));
            }
        }

        foreach (var user in new[] { 1, other })
        {
            var expected = await ReferenceCountsAsync(user);
            Assert.Equal(expected.OrderBy(x => x.Key), (await CountsAsync(user)).OrderBy(x => x.Key));
        }

        var mine = await CountsAsync();
        Assert.Equal(2, mine[partly]);
        Assert.Equal(1, mine[volumes]);
        Assert.Equal(1, mine[languages]);
        Assert.Equal(2, mine[extras]);
        Assert.False(mine.ContainsKey(caughtUp));
        Assert.Equal(2, mine[untouched]);
        Assert.Equal(18, mine[longRun]);

        var theirs = await CountsAsync(other);
        Assert.Equal(2, theirs[partly]);
        Assert.Equal(1, theirs[volumes]);
    }
}
