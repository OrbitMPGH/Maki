using System.IO.Compression;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// <see cref="OpdsProgressWriter"/>: fetches for one chapter fold into one write, folding keeps the
/// rule about prefetched last pages, and nothing queued or in flight is lost at shutdown.
/// </summary>
public sealed class OpdsProgressWriterTests : IDisposable
{
    private const int Pages = 5;
    private readonly TestDb _db = new();
    private readonly ReadingProgressGate _gate = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maki-opds-writer-tests", Guid.NewGuid().ToString("N"));

    public OpdsProgressWriterTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private OpdsProgressWriter Writer(params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<MakiDbContext>(_db.Options).AddInterceptors(interceptors).Options;
        var archives = new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance);
        var services = new ServiceCollection();
        services.AddScoped<DataScope>();
        services.AddScoped(sp => new MakiDbContext(options, sp.GetRequiredService<DataScope>()));
        services.AddScoped(sp =>
        {
            var context = sp.GetRequiredService<MakiDbContext>();
            return new ReaderService(context, archives,
                new ReadingProgressService(context, _gate, NullLogger<ReadingProgressService>.Instance),
                InertKavitaPusher.For(_db.ScopeFactory()), new ReadingSessionService(context),
                NullLogger<ReaderService>.Instance);
        });
        return new OpdsProgressWriter(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OpdsProgressWriter>.Instance);
    }

    private int SeedChapter(string title = "Fetched")
    {
        var seriesId = _db.SeedSeries(title);
        var path = Path.Combine(_root, $"{title}.cbz");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            for (var i = 1; i <= Pages; i++)
            {
                using var stream = archive.CreateEntry($"{i:000}.jpg").Open();
                stream.WriteByte(0xFF);
            }
        }

        using var db = _db.NewContext();
        var series = db.Series.Include(s => s.RootFolder).Single(s => s.Id == seriesId);
        series.RootFolder!.Path = _root;
        series.FolderName = "";
        var file = new ChapterFile
        {
            SeriesId = seriesId,
            RelativePath = Path.GetFileName(path),
            Size = new FileInfo(path).Length,
            SourceName = "Test",
            DateAdded = DateTime.UtcNow,
        };
        db.ChapterFiles.Add(file);
        db.SaveChanges();
        var chapter = new Chapter { SeriesId = seriesId, Number = 1, Language = "en", ChapterFileId = file.Id };
        db.Chapters.Add(chapter);
        db.SaveChanges();
        return chapter.Id;
    }

    private ChapterProgress? Progress(int chapterId)
    {
        using var db = _db.NewContext();
        return db.ChapterProgress.IgnoreQueryFilters().AsNoTracking().SingleOrDefault(p => p.ChapterId == chapterId);
    }

    [Fact]
    public async Task Fetches_for_one_chapter_fold_into_the_highest_page()
    {
        var userId = _db.SeedUser("reader");
        var chapterId = SeedChapter();
        var writer = Writer();

        writer.Enqueue(userId, true, chapterId, page: 1, pageCount: Pages);
        writer.Enqueue(userId, true, chapterId, page: 3, pageCount: Pages);
        writer.Enqueue(userId, true, chapterId, page: 2, pageCount: Pages);
        await writer.FlushAsync(default);

        // Written one by one, the last write (page 2) would have moved the resume point back.
        var row = Progress(chapterId);
        Assert.NotNull(row);
        Assert.Equal(3, row.PageIndex);
        Assert.False(row.Completed);
    }

    [Fact]
    public async Task A_lone_last_page_prefetch_does_not_complete_the_chapter()
    {
        var userId = _db.SeedUser("reader");
        var chapterId = SeedChapter();
        var writer = Writer();

        writer.Enqueue(userId, true, chapterId, page: Pages - 1, pageCount: Pages);
        await writer.FlushAsync(default);

        Assert.False(Progress(chapterId)!.Completed);
    }

    [Fact]
    public async Task A_last_page_folded_after_an_earlier_fetch_completes_the_chapter()
    {
        var userId = _db.SeedUser("reader");
        var chapterId = SeedChapter();
        var writer = Writer();

        writer.Enqueue(userId, true, chapterId, page: 0, pageCount: Pages);
        writer.Enqueue(userId, true, chapterId, page: Pages - 1, pageCount: Pages);
        await writer.FlushAsync(default);

        Assert.True(Progress(chapterId)!.Completed);
    }

    [Fact]
    public async Task Whatever_is_queued_at_shutdown_is_still_written()
    {
        var userId = _db.SeedUser("reader");
        var first = SeedChapter("First");
        var last = SeedChapter("Last");
        var writer = Writer();

        // Wait for the loop to be running, so stopping exercises it rather than racing its start.
        writer.Enqueue(userId, true, first, page: 1, pageCount: Pages);
        await writer.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (Progress(first) is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        // Whether the loop or the final flush picks this one up, it lands.
        writer.Enqueue(userId, true, last, page: 2, pageCount: Pages);
        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(2, Progress(last)!.PageIndex);
    }

    [Fact]
    public async Task A_write_in_flight_when_shutdown_begins_is_not_lost()
    {
        var userId = _db.SeedUser("reader");
        var chapterId = SeedChapter();
        using var stopping = new CancellationTokenSource();
        var writer = Writer(new CancelOnSave(stopping));

        writer.Enqueue(userId, true, chapterId, page: 2, pageCount: Pages);
        await writer.StartAsync(stopping.Token);
        await writer.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30));
        await writer.StopAsync(CancellationToken.None);

        // Shutdown lands mid-save. The entry had already left the pending map, so a cancelled save
        // would have been dropped and the final flush would never have seen it.
        Assert.True(stopping.IsCancellationRequested);
        Assert.Equal(2, Progress(chapterId)!.PageIndex);
    }

    private sealed class CancelOnSave(CancellationTokenSource stopping) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            stopping.Cancel();
            return ValueTask.FromResult(result);
        }
    }
}
