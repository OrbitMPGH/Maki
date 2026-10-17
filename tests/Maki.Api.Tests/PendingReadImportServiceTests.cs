using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Security;
using Maki.Core.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// Read marks from a Mihon backup that wait for a series' chapters, and the chapter-sync hook that
/// lands them.
/// </summary>
public sealed class PendingReadImportServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private PendingReadImportService Service() =>
        new(_db.ScopeFactory(), NullLogger<PendingReadImportService>.Instance);

    private int Reader(string name = "reader") => _db.SeedUser(name, MakiPermission.None);

    private Dictionary<decimal, int> AddChapters(int seriesId, params decimal?[] numbers)
    {
        using var db = _db.NewContext();
        var chapters = numbers.Select(n => new Chapter
        {
            SeriesId = seriesId, Number = n, Language = "en", Title = n is null ? "Special" : null,
        }).ToList();
        db.Chapters.AddRange(chapters);
        db.SaveChanges();
        return chapters.Where(c => c.Number is not null).ToDictionary(c => c.Number!.Value, c => c.Id);
    }

    private List<ChapterProgress> Progress()
    {
        using var db = _db.NewContext();
        return db.ChapterProgress.IgnoreQueryFilters().OrderBy(p => p.ChapterId).ToList();
    }

    private List<PendingReadImport> Pending()
    {
        using var db = _db.NewContext();
        return db.PendingReadImports.IgnoreQueryFilters().ToList();
    }

    [Fact]
    public async Task Marks_wait_for_chapters_and_land_after_a_sync_that_finds_them()
    {
        var user = Reader();
        var seriesId = _db.SeedSeries(mappings: new SourceMapping
        {
            SourceName = "fake", SourceSeriesId = "series", Url = "https://fake.test/series", Enabled = true,
        });
        var service = Service();

        await service.AddAsync(user, seriesId, [1m, 2m, 2.5m], default);
        await service.ApplyAsync(seriesId, default);
        Assert.Empty(Progress());
        Assert.Single(Pending());

        var fake = new FakeSource { Name = "fake" };
        var source = new FakeSource
        {
            Name = "fake",
            OnListChapters = _ => [fake.Chapter(1), fake.Chapter(2), fake.Chapter(2.5m), fake.Chapter(3)],
        };
        var sync = new ChapterSyncService(
            _db.NewContext(), new SourceRegistry([source]),
            new DownloadQueueService(null!, TimeProvider.System, null!, NullLogger<DownloadQueueService>.Instance),
            Sources.AllEnabled,
            new SourceChapterListCache(TimeProvider.System, NullLogger<SourceChapterListCache>.Instance),
            new FakeAppSettings(), NullLogger<ChapterSyncService>.Instance,
            pendingReadImports: service);
        await sync.SyncSeriesAsync(seriesId);

        using var db = _db.NewContext();
        var numbers = db.Chapters.Where(c => c.SeriesId == seriesId).ToDictionary(c => c.Id, c => c.Number);
        var rows = Progress();
        Assert.Equal([1m, 2m, 2.5m], rows.Select(r => numbers[r.ChapterId]).Order());
        Assert.All(rows, r =>
        {
            Assert.Equal(user, r.UserId);
            Assert.Equal(seriesId, r.SeriesId);
            Assert.True(r.Completed);
            Assert.True(r.BulkMarked);
            Assert.True(r.External);
            Assert.Equal(0, r.PageCount);
        });
        Assert.Empty(Pending());
        Assert.Empty(db.StatsEvents);
        Assert.Empty(db.UserNotifications.IgnoreQueryFilters());
    }

    [Fact]
    public async Task Rows_already_read_or_marked_unread_are_left_alone()
    {
        var user = Reader();
        var seriesId = _db.SeedSeries();
        var ids = AddChapters(seriesId, 1, 2, 3);
        var unreadAt = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        using (var db = _db.NewContext())
        {
            db.ChapterProgress.AddRange(
                new ChapterProgress
                {
                    UserId = user, SeriesId = seriesId, ChapterId = ids[1], UnreadAt = unreadAt,
                    StartedAt = unreadAt, UpdatedAt = unreadAt,
                },
                new ChapterProgress
                {
                    UserId = user, SeriesId = seriesId, ChapterId = ids[2], Completed = true, PageIndex = 9,
                    PageCount = 10, StartedAt = unreadAt, UpdatedAt = unreadAt,
                },
                new ChapterProgress
                {
                    UserId = user, SeriesId = seriesId, ChapterId = ids[3], PageIndex = 5, PageCount = 20,
                    StartedAt = unreadAt, UpdatedAt = unreadAt,
                });
            db.SaveChanges();
        }

        var service = Service();
        await service.AddAsync(user, seriesId, [1m, 2m, 3m], default);
        await service.ApplyAsync(seriesId, default);

        var rows = Progress().ToDictionary(r => r.ChapterId);
        Assert.False(rows[ids[1]].Completed);
        Assert.Equal(unreadAt, rows[ids[1]].UnreadAt);
        Assert.False(rows[ids[2]].External);
        Assert.False(rows[ids[2]].BulkMarked);
        Assert.Equal(10, rows[ids[2]].PageCount);
        Assert.True(rows[ids[3]].Completed);
        Assert.Equal(5, rows[ids[3]].PageIndex);
        Assert.Empty(Pending());
    }

    [Fact]
    public async Task A_second_import_merges_its_numbers_into_the_waiting_row()
    {
        var user = Reader();
        var seriesId = _db.SeedSeries();
        var service = Service();

        await service.AddAsync(user, seriesId, [1m, 3m], default);
        await service.AddAsync(user, seriesId, [2m, 3m], default);

        var row = Assert.Single(Pending());
        Assert.Equal([1m, 2m, 3m], PendingReadImportService.Parse(row.NumbersJson));
    }

    [Fact]
    public async Task Each_reader_gets_their_own_rows_under_an_unrestricted_scope()
    {
        var first = Reader("first");
        var second = Reader("second");
        var seriesId = _db.SeedSeries();
        var ids = AddChapters(seriesId, 1, 2);
        var service = Service();

        await service.AddAsync(first, seriesId, [1m], default);
        await service.AddAsync(second, seriesId, [2m], default);
        await service.ApplyAsync(seriesId, default);

        var rows = Progress();
        Assert.Equal(2, rows.Count);
        Assert.Equal(first, rows.Single(r => r.ChapterId == ids[1]).UserId);
        Assert.Equal(second, rows.Single(r => r.ChapterId == ids[2]).UserId);
        Assert.Empty(Pending());
    }

    [Fact]
    public async Task One_shots_and_numbers_with_no_chapter_are_not_marked()
    {
        var user = Reader();
        var seriesId = _db.SeedSeries();
        var ids = AddChapters(seriesId, null, 1);
        var service = Service();

        await service.AddAsync(user, seriesId, [0m, 1m, 7m], default);
        await service.ApplyAsync(seriesId, default);

        var row = Assert.Single(Progress());
        Assert.Equal(ids[1], row.ChapterId);
        Assert.Empty(Pending());
    }

    [Fact]
    public async Task Applying_twice_writes_nothing_new()
    {
        var user = Reader();
        var seriesId = _db.SeedSeries();
        AddChapters(seriesId, 1, 2);
        var service = Service();

        await service.AddAsync(user, seriesId, [1m, 2m], default);
        await service.ApplyAsync(seriesId, default);
        await service.AddAsync(user, seriesId, [1m, 2m], default);
        await service.ApplyAsync(seriesId, default);

        Assert.Equal(2, Progress().Count);
        Assert.Empty(Pending());
    }
}
