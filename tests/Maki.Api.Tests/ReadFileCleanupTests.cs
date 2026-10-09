using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public sealed class ReadFileCleanupTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
    private readonly TestDb _db = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"maki-cleanup-{Guid.NewGuid():N}");
    private int _bob;

    public ReadFileCleanupTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Series"));
        _bob = _db.SeedUser("bob");
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    private sealed class StoppedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private ReadFileCleanupService Service(Maki.Data.MakiDbContext db)
    {
        var clock = new StoppedClock(Now);
        return new ReadFileCleanupService(
            db, new SettingsService(_db.ScopeFactory()),
            new ChapterFileDeletion(db, new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance), clock,
                NullLogger<ChapterFileDeletion>.Instance),
            clock, NullLogger<ReadFileCleanupService>.Instance);
    }

    /// <summary>A series under the temp root with one file per entry in <paramref name="files"/>, each backing the chapter numbers listed.</summary>
    private (int SeriesId, Dictionary<decimal, int> Chapters) Seed(ReadFileCleanup mode, params decimal[][] files)
    {
        var seriesId = _db.SeedSeries(configure: s => s.ReadFileCleanup = mode);
        using var db = _db.NewContext();
        db.RootFolders.First(r => r.Id == db.Series.First(s => s.Id == seriesId).RootFolderId).Path = _root;
        db.SaveChanges();

        var ids = new Dictionary<decimal, int>();
        for (var i = 0; i < files.Length; i++)
        {
            var relative = Path.Combine("Series", $"f{i}.cbz");
            File.WriteAllText(Path.Combine(_root, relative), "cbz");
            var file = new ChapterFile { SeriesId = seriesId, RelativePath = relative, SourceName = "Test", DateAdded = Now };
            db.ChapterFiles.Add(file);
            db.SaveChanges();
            foreach (var n in files[i])
            {
                var chapter = new Chapter { SeriesId = seriesId, Number = n, ChapterFileId = file.Id };
                db.Chapters.Add(chapter);
                db.SaveChanges();
                ids[n] = chapter.Id;
            }
        }

        return (seriesId, ids);
    }

    private void Finish(int userId, int seriesId, int chapterId, int daysAgo, bool watched = false)
    {
        using var db = _db.NewContext();
        db.ChapterProgress.Add(new ChapterProgress
        {
            UserId = userId, SeriesId = seriesId, ChapterId = chapterId, Completed = true, Watched = watched,
            StartedAt = Now, UpdatedAt = Now, CompletedAt = Now.AddDays(-daysAgo),
        });
        db.SaveChanges();
    }

    private static readonly ReadFileCleanupService.Options On7 = new(true, 7, KeepLast: false);

    [Fact]
    public async Task A_chapter_is_due_only_once_every_reader_has_finished_it()
    {
        var (seriesId, ch) = Seed(ReadFileCleanup.Default, [1m], [2m], [3m]);
        Finish(1, seriesId, ch[1m], daysAgo: 10);
        Finish(_bob, seriesId, ch[1m], daysAgo: 3);
        Finish(1, seriesId, ch[2m], daysAgo: 10);

        using var db = _db.NewContext();
        var due = await Service(db).ScheduleAsync(seriesId, On7, default);

        // Due a week after the slower reader finished; chapter 2 waits on bob, chapter 3 on everyone.
        Assert.Equal(Now.AddDays(-3).AddDays(7), Assert.Single(due).Value);
        Assert.Equal(ch[1m], due.Keys.Single());
    }

    [Fact]
    public async Task Keep_last_holds_back_each_readers_most_recent_finish()
    {
        var (seriesId, ch) = Seed(ReadFileCleanup.Default, [1m], [2m], [3m]);
        Finish(1, seriesId, ch[1m], daysAgo: 30);
        Finish(1, seriesId, ch[2m], daysAgo: 20);
        Finish(1, seriesId, ch[3m], daysAgo: 10);

        using var db = _db.NewContext();
        var due = await Service(db).ScheduleAsync(seriesId, On7 with { KeepLast = true }, default);

        Assert.Equal([ch[1m], ch[2m]], due.Keys.Order());
    }

    [Fact]
    public async Task A_watched_tick_does_not_make_a_file_due()
    {
        var (seriesId, ch) = Seed(ReadFileCleanup.Default, [1m], [2m]);
        Finish(1, seriesId, ch[1m], daysAgo: 30, watched: true);
        Finish(1, seriesId, ch[2m], daysAgo: 30);

        using var db = _db.NewContext();
        var due = await Service(db).ScheduleAsync(seriesId, On7, default);

        Assert.Equal(ch[2m], Assert.Single(due).Key);
    }

    [Fact]
    public async Task Keep_last_breaks_a_tied_finish_on_chapter_number()
    {
        var (seriesId, ch) = Seed(ReadFileCleanup.Default, [1m], [2m], [3m]);
        Finish(1, seriesId, ch[1m], daysAgo: 30);
        Finish(1, seriesId, ch[3m], daysAgo: 30);
        Finish(1, seriesId, ch[2m], daysAgo: 30);

        using var db = _db.NewContext();
        var due = await Service(db).ScheduleAsync(seriesId, On7 with { KeepLast = true }, default);

        Assert.Equal([ch[1m], ch[2m]], due.Keys.Order());
    }

    [Fact]
    public async Task A_volume_file_waits_for_every_chapter_on_it()
    {
        var (seriesId, ch) = Seed(ReadFileCleanup.Default, [1m, 2m]);
        Finish(1, seriesId, ch[1m], daysAgo: 30);

        using var db = _db.NewContext();
        Assert.Empty(await Service(db).ScheduleAsync(seriesId, On7, default));

        Finish(1, seriesId, ch[2m], daysAgo: 10);
        using var again = _db.NewContext();
        var due = await Service(again).ScheduleAsync(seriesId, On7, default);
        Assert.Equal(2, due.Count);
        Assert.All(due.Values, at => Assert.Equal(Now.AddDays(-10).AddDays(7), at));
    }

    [Theory]
    [InlineData(false, ReadFileCleanup.Default, false)]
    [InlineData(true, ReadFileCleanup.Default, true)]
    [InlineData(false, ReadFileCleanup.On, true)]
    [InlineData(true, ReadFileCleanup.Off, false)]
    public async Task Run_follows_the_switch_and_the_series_choice(bool globalOn, ReadFileCleanup mode, bool deletes)
    {
        _db.SetConfig((SettingKeys.ReadFileCleanupEnabled, globalOn ? "true" : "false"),
            (SettingKeys.ReadFileCleanupDays, "7"), (SettingKeys.ReadFileCleanupKeepLast, "false"));
        var (seriesId, ch) = Seed(mode, [1m]);
        Finish(1, seriesId, ch[1m], daysAgo: 8);

        using (var db = _db.NewContext())
        {
            Assert.Equal(deletes ? 1 : 0, await Service(db).RunAsync(default));
        }

        Assert.Equal(!deletes, File.Exists(Path.Combine(_root, "Series", "f0.cbz")));
        using var check = _db.NewContext();
        var chapter = await check.Chapters.SingleAsync();
        Assert.Equal(deletes, chapter.FileRemovedAt != null);
        Assert.True((await check.ChapterProgress.SingleAsync()).Completed);
    }

    [Fact]
    public async Task Run_leaves_a_file_not_yet_due()
    {
        _db.SetConfig((SettingKeys.ReadFileCleanupEnabled, "true"), (SettingKeys.ReadFileCleanupDays, "7"));
        var (seriesId, ch) = Seed(ReadFileCleanup.Default, [1m]);
        Finish(1, seriesId, ch[1m], daysAgo: 6);

        using var db = _db.NewContext();
        Assert.Equal(0, await Service(db).RunAsync(default));
        Assert.True(File.Exists(Path.Combine(_root, "Series", "f0.cbz")));
    }
}
