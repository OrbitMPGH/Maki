using Maki.Api.Hubs;
using Maki.Api.Services;
using Maki.Core.Entities;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public class ReadingAwareQueueBumpTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly TestDb _db = new();
    private readonly StoppedClock _clock = new(T0);

    public void Dispose() => _db.Dispose();

    private ReadingAwareQueueBump Bump() => new(
        _db.ScopeFactory(), new EventBroadcaster(new NoopHubContext(), _db.ScopeFactory()), _clock,
        NullLogger<ReadingAwareQueueBump>.Instance);

    private int SeedSeries() => _db.SeedSeries();

    /// <summary>One chapter per number; those in <paramref name="onDisk"/> get a file.</summary>
    private Dictionary<decimal, int> SeedChapters(int seriesId, int count, params int[] onDisk)
    {
        using var db = _db.NewContext();
        var ids = new Dictionary<decimal, int>();
        foreach (var number in Enumerable.Range(1, count))
        {
            var chapter = new Chapter { SeriesId = seriesId, Number = number, Language = "en" };
            if (onDisk.Contains(number))
            {
                var file = new ChapterFile
                {
                    SeriesId = seriesId, RelativePath = $"{number}.cbz", Size = 1, SourceName = "Test",
                    DateAdded = T0.UtcDateTime
                };
                db.ChapterFiles.Add(file);
                db.SaveChanges();
                chapter.ChapterFileId = file.Id;
            }

            db.Chapters.Add(chapter);
            db.SaveChanges();
            ids[number] = chapter.Id;
        }

        return ids;
    }

    private int Queue(int seriesId, int chapterId, int sortOrder, QueueStatus status = QueueStatus.Queued,
        AcquisitionProtocol protocol = AcquisitionProtocol.Scraper)
    {
        using var db = _db.NewContext();
        var item = new DownloadQueueItem
        {
            SeriesId = seriesId, ChapterId = chapterId, Protocol = protocol, Status = status,
            SortOrder = sortOrder, QueuedAt = T0.UtcDateTime
        };
        db.DownloadQueue.Add(item);
        db.SaveChanges();
        return item.Id;
    }

    private int OrderOf(int id)
    {
        using var db = _db.NewContext();
        return db.DownloadQueue.Single(q => q.Id == id).SortOrder;
    }

    private List<int> Dispatch()
    {
        using var db = _db.NewContext();
        return db.DownloadQueue
            .Where(q => q.Status == QueueStatus.Queued || q.Status == QueueStatus.RateLimited)
            .OrderBy(q => q.SortOrder).ThenBy(q => q.QueuedAt)
            .Select(q => q.Id).ToList();
    }

    [Fact]
    public async Task Reading_the_last_downloaded_chapter_moves_the_next_three_to_the_front()
    {
        var series = SeedSeries();
        var ch = SeedChapters(series, 10, onDisk: [1, 2, 3]);
        var other = SeedSeries();
        var otherCh = SeedChapters(other, 1);
        var foreign = Queue(other, otherCh[1], sortOrder: 0);
        var q4 = Queue(series, ch[4], 10);
        var q5 = Queue(series, ch[5], 11);
        var q6 = Queue(series, ch[6], 12);
        var q7 = Queue(series, ch[7], 13);

        var moved = await Bump().BumpAsync(series, ch[3], default);

        Assert.Equal(3, moved);
        Assert.Equal([q4, q5, q6, foreign, q7], Dispatch());
        Assert.Equal(0, OrderOf(foreign));
    }

    [Fact]
    public async Task Reading_the_second_to_last_downloaded_chapter_also_bumps()
    {
        var series = SeedSeries();
        var ch = SeedChapters(series, 6, onDisk: [1, 2, 3]);
        var foreign = Queue(_db.SeedSeries(), Seed(1), 0);
        var q4 = Queue(series, ch[4], 5);

        Assert.Equal(1, await Bump().BumpAsync(series, ch[2], default));
        Assert.Equal([q4, foreign], Dispatch());

        int Seed(decimal n) => SeedChapters(_db.SeedSeries(), 1)[n];
    }

    [Fact]
    public async Task Mid_series_reading_leaves_the_queue_alone()
    {
        var series = SeedSeries();
        var ch = SeedChapters(series, 10, onDisk: [1, 2, 3, 4, 5]);
        var q6 = Queue(series, ch[6], 9);
        var foreign = Queue(_db.SeedSeries(), SeedChapters(_db.SeedSeries(), 1)[1], 0);

        Assert.Equal(0, await Bump().BumpAsync(series, ch[2], default));
        Assert.Equal([foreign, q6], Dispatch());
    }

    [Fact]
    public async Task Rows_already_leading_the_queue_are_not_written_again()
    {
        var series = SeedSeries();
        var ch = SeedChapters(series, 6, onDisk: [1, 2, 3]);
        var q4 = Queue(series, ch[4], 1);
        var q5 = Queue(series, ch[5], 2);
        Queue(_db.SeedSeries(), SeedChapters(_db.SeedSeries(), 1)[1], 5);

        Assert.Equal(0, await Bump().BumpAsync(series, ch[3], default));
        Assert.Equal(1, OrderOf(q4));
        Assert.Equal(2, OrderOf(q5));
    }

    [Fact]
    public async Task A_second_bump_after_the_first_changes_nothing()
    {
        var series = SeedSeries();
        var ch = SeedChapters(series, 8, onDisk: [1, 2]);
        var q3 = Queue(series, ch[3], 4);
        var q4 = Queue(series, ch[4], 5);
        Queue(_db.SeedSeries(), SeedChapters(_db.SeedSeries(), 1)[1], 0);
        var bump = Bump();

        Assert.Equal(2, await bump.BumpAsync(series, ch[2], default));
        var first = (OrderOf(q3), OrderOf(q4));
        Assert.Equal(0, await bump.BumpAsync(series, ch[2], default));
        Assert.Equal(first, (OrderOf(q3), OrderOf(q4)));
    }

    [Fact]
    public void A_series_is_looked_at_once_per_window()
    {
        var bump = Bump();

        Assert.True(bump.Due(1));
        Assert.False(bump.Due(1));
        Assert.True(bump.Due(2));

        _clock.Now = T0 + ReadingAwareQueueBump.Window - TimeSpan.FromSeconds(1);
        Assert.False(bump.Due(1));

        _clock.Now = T0 + ReadingAwareQueueBump.Window;
        Assert.True(bump.Due(1));
        Assert.False(bump.Due(1));
    }

    [Theory]
    [InlineData(QueueStatus.FetchingPages)]
    [InlineData(QueueStatus.Downloading)]
    [InlineData(QueueStatus.Resolving)]
    [InlineData(QueueStatus.Waiting)]
    [InlineData(QueueStatus.Failed)]
    [InlineData(QueueStatus.AwaitingImport)]
    public async Task Rows_that_are_in_flight_waiting_or_settled_keep_their_place(QueueStatus status)
    {
        var series = SeedSeries();
        var ch = SeedChapters(series, 6, onDisk: [1, 2, 3]);
        var held = Queue(series, ch[4], 50, status);
        var foreign = Queue(_db.SeedSeries(), SeedChapters(_db.SeedSeries(), 1)[1], 0);

        Assert.Equal(0, await Bump().BumpAsync(series, ch[3], default));
        Assert.Equal(50, OrderOf(held));
        Assert.Equal([foreign], Dispatch());
    }

    [Fact]
    public async Task A_waiting_chapter_does_not_stop_the_ones_after_it_moving()
    {
        var series = SeedSeries();
        var ch = SeedChapters(series, 6, onDisk: [1, 2, 3]);
        var waiting = Queue(series, ch[4], 50, QueueStatus.Waiting);
        var q5 = Queue(series, ch[5], 51);
        Queue(_db.SeedSeries(), SeedChapters(_db.SeedSeries(), 1)[1], 0);

        Assert.Equal(1, await Bump().BumpAsync(series, ch[3], default));
        Assert.Equal(q5, Dispatch()[0]);
        Assert.Equal(50, OrderOf(waiting));
    }

    [Fact]
    public async Task Torrent_rows_and_chapters_behind_the_reader_are_ignored()
    {
        var series = SeedSeries();
        var ch = SeedChapters(series, 6, onDisk: [2, 3]);
        var torrent = Queue(series, ch[4], 40, protocol: AcquisitionProtocol.Torrent);
        var behind = Queue(series, ch[1], 41);
        var foreign = Queue(_db.SeedSeries(), SeedChapters(_db.SeedSeries(), 1)[1], 0);

        Assert.Equal(0, await Bump().BumpAsync(series, ch[3], default));
        Assert.Equal(40, OrderOf(torrent));
        Assert.Equal(41, OrderOf(behind));
        Assert.Equal(0, OrderOf(foreign));
    }

    [Fact]
    public async Task Only_the_reading_series_moves()
    {
        var series = SeedSeries();
        var ch = SeedChapters(series, 6, onDisk: [1, 2, 3]);
        var other = SeedSeries();
        var otherCh = SeedChapters(other, 6, onDisk: [1]);
        var q4 = Queue(series, ch[4], 20);
        var otherQ = Queue(other, otherCh[2], 21);
        Queue(_db.SeedSeries(), SeedChapters(_db.SeedSeries(), 1)[1], 5);

        await Bump().BumpAsync(series, ch[3], default);

        Assert.Equal(4, OrderOf(q4));
        Assert.Equal(21, OrderOf(otherQ));
    }

    [Fact]
    public async Task A_second_language_copy_does_not_count_as_another_chapter_ahead()
    {
        var series = SeedSeries();
        var ch = SeedChapters(series, 5, onDisk: [1, 2]);
        using (var db = _db.NewContext())
        {
            var file = new ChapterFile
            {
                SeriesId = series, RelativePath = "2-es.cbz", Size = 1, SourceName = "Test", DateAdded = T0.UtcDateTime
            };
            db.ChapterFiles.Add(file);
            db.SaveChanges();
            db.Chapters.Add(new Chapter { SeriesId = series, Number = 2, Language = "es", ChapterFileId = file.Id });
            db.SaveChanges();
        }

        var q3 = Queue(series, ch[3], 9);
        Queue(_db.SeedSeries(), SeedChapters(_db.SeedSeries(), 1)[1], 0);

        Assert.Equal(1, await Bump().BumpAsync(series, ch[1], default));
        Assert.Equal(q3, Dispatch()[0]);
    }
}
