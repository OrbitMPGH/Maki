using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public sealed class ChapterFoldServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeSource _parts = new() { Name = "parts" };
    private readonly FakeSource _whole = new() { Name = "whole" };

    public void Dispose() => _db.Dispose();

    private static SourceMapping Mapping(string source) => new()
    {
        SourceName = source, SourceSeriesId = "series", Url = $"https://{source}.test/series", Priority = 1, Enabled = true,
    };

    private static List<ulong> Hashes(int from, int count) => [.. Enumerable.Range(from, count).Select(seed =>
    {
        var random = new Random(seed);
        ulong hash;
        do
        {
            hash = (ulong)random.NextInt64();
        }
        while (System.Numerics.BitOperations.PopCount(hash) is < 24 or > 40);

        return hash;
    })];

    private static readonly List<ulong> Whole = Hashes(0, 39);

    /// <summary>
    /// Chapter 3 whole on "whole", 3.1 (13 pages) and 3.2 (26) on "parts", their pages the whole's; 4 on both.
    /// The counts nominate the fold and the hashes confirm it.
    /// </summary>
    private int Seed(Action<Microsoft.EntityFrameworkCore.DbContext, Dictionary<decimal, Chapter>>? more = null)
    {
        var seriesId = _db.SeedSeries(mappings: [Mapping("parts"), Mapping("whole")]);
        using var db = _db.NewContext();
        var mappings = db.SourceMappings.Where(m => m.SeriesId == seriesId).ToDictionary(m => m.SourceName);
        var rows = new Dictionary<decimal, Chapter>();
        void Link(FakeSource source, decimal number, int pages, IEnumerable<ulong>? hashes)
        {
            if (!rows.TryGetValue(number, out var chapter))
            {
                chapter = db.Chapters.Add(new Chapter { SeriesId = seriesId, Number = number, Language = "en", Wanted = true }).Entity;
                rows[number] = chapter;
            }

            var sc = source.Chapter(number);
            db.ChapterSourceLinks.Add(new ChapterSourceLink
            {
                Chapter = chapter, SourceMappingId = mappings[source.Name].Id, SourceChapterId = sc.SourceChapterId,
                NumberRaw = sc.NumberRaw, PageCount = pages,
                PageHashes = hashes is null ? null : ChapterPageMatch.Format(hashes),
            });
        }

        Link(_parts, 3.1m, 13, Whole[..13]);
        Link(_parts, 3.2m, 26, Whole[13..]);
        Link(_parts, 4, 40, null);
        Link(_whole, 3, 39, Whole);
        Link(_whole, 4, 40, null);
        db.SaveChanges();
        more?.Invoke(db, rows);
        db.SaveChanges();
        return seriesId;
    }

    private ChapterFoldService Folds()
    {
        var db = _db.NewContext();
        return new ChapterFoldService(
            db,
            new ChapterFoldPreviewService(db, Sources.AllEnabled),
            new ChapterFileDeletion(db, new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance), TimeProvider.System,
                TestRecycleBin.For(db), NullLogger<ChapterFileDeletion>.Instance),
            TimeProvider.System,
            NullLogger<ChapterFoldService>.Instance);
    }

    private ChapterSyncService Sync() => new(
        _db.NewContext(),
        new SourceRegistry([_parts, _whole]),
        new DownloadQueueService(null!, TimeProvider.System, null!, NullLogger<DownloadQueueService>.Instance),
        Sources.AllEnabled,
        new SourceChapterListCache(TimeProvider.System, NullLogger<SourceChapterListCache>.Instance),
        new FakeAppSettings(),
        NullLogger<ChapterSyncService>.Instance);

    private List<Chapter> Chapters(int seriesId)
    {
        using var db = _db.NewContext();
        return [.. db.Chapters.Include(c => c.SourceLinks).ThenInclude(l => l.SourceMapping)
            .Where(c => c.SeriesId == seriesId).AsEnumerable().OrderBy(c => c.Number)];
    }

    [Fact]
    public async Task Confirmed_parts_become_one_link_on_the_whole_chapter()
    {
        var seriesId = Seed();

        Assert.Equal(1, await Folds().ApplyAsync(seriesId, CancellationToken.None));

        var chapters = Chapters(seriesId);
        Assert.Equal([3m, 4m], chapters.Select(c => c.Number!.Value));
        var link = chapters[0].SourceLinks.Single(l => l.SourceMapping!.SourceName == "parts");
        Assert.Equal(_parts.Chapter(3.1m).SourceChapterId, link.SourceChapterId);
        Assert.Equal([3.1m, 3.2m], ChapterFoldPart.Parse(link.Parts).Select(p => p.Number));
        Assert.Equal(39, link.PageCount);
        Assert.Equal(Whole, ChapterPageMatch.Parse(link.PageHashes));
        using var db = _db.NewContext();
        Assert.Equal(3m, db.ChapterFolds.Single(f => f.SeriesId == seriesId).Floor);
    }

    [Fact]
    public async Task A_sync_keeps_a_fold_while_the_source_lists_the_same_parts()
    {
        var seriesId = Seed();
        await Folds().ApplyAsync(seriesId, CancellationToken.None);
        var parts = new FakeSource { Name = "parts", OnListChapters = _ => [_parts.Chapter(3.1m), _parts.Chapter(3.2m), _parts.Chapter(4)] };
        var whole = new FakeSource { Name = "whole", OnListChapters = _ => [_whole.Chapter(3), _whole.Chapter(4)] };

        var added = await new ChapterSyncService(
            _db.NewContext(), new SourceRegistry([parts, whole]),
            new DownloadQueueService(null!, TimeProvider.System, null!, NullLogger<DownloadQueueService>.Instance),
            Sources.AllEnabled, new SourceChapterListCache(TimeProvider.System, NullLogger<SourceChapterListCache>.Instance),
            new FakeAppSettings(), NullLogger<ChapterSyncService>.Instance).SyncSeriesAsync(seriesId, CancellationToken.None);

        Assert.Empty(added);
        var chapters = Chapters(seriesId);
        Assert.Equal([3m, 4m], chapters.Select(c => c.Number!.Value));
        Assert.NotNull(chapters[0].SourceLinks.Single(l => l.SourceMapping!.SourceName == "parts").Parts);
        using var db = _db.NewContext();
        Assert.Null(db.Series.Single(s => s.Id == seriesId).NumberingClash);
    }

    [Fact]
    public async Task A_part_the_source_no_longer_lists_unfolds_the_chapter()
    {
        var seriesId = Seed();
        await Folds().ApplyAsync(seriesId, CancellationToken.None);
        var parts = new FakeSource { Name = "parts", OnListChapters = _ => [_parts.Chapter(3.1m), _parts.Chapter(4)] };
        var whole = new FakeSource { Name = "whole", OnListChapters = _ => [_whole.Chapter(3), _whole.Chapter(4)] };

        await new ChapterSyncService(
            _db.NewContext(), new SourceRegistry([parts, whole]),
            new DownloadQueueService(null!, TimeProvider.System, null!, NullLogger<DownloadQueueService>.Instance),
            Sources.AllEnabled, new SourceChapterListCache(TimeProvider.System, NullLogger<SourceChapterListCache>.Instance),
            new FakeAppSettings(), NullLogger<ChapterSyncService>.Instance).SyncSeriesAsync(seriesId, CancellationToken.None);

        var chapters = Chapters(seriesId);
        Assert.Equal([3m, 3.1m, 4m], chapters.Select(c => c.Number!.Value));
        Assert.DoesNotContain(chapters[0].SourceLinks, l => l.SourceMapping!.SourceName == "parts");
        using var db = _db.NewContext();
        Assert.Empty(db.ChapterFolds.Where(f => f.SeriesId == seriesId));
    }

    [Fact]
    public async Task Reads_on_the_parts_move_onto_the_whole_at_the_same_page()
    {
        var reader = _db.SeedUser();
        var seriesId = Seed((db, rows) =>
        {
            var now = DateTime.UtcNow;
            db.Add(new ChapterProgress
            {
                UserId = reader, SeriesId = rows[3.1m].SeriesId, ChapterId = rows[3.1m].Id, PageIndex = 12, PageCount = 13,
                Completed = true, CompletedAt = now.AddHours(-1), ReadSeconds = 100, ReportedSeconds = 100, StartedAt = now.AddHours(-2), UpdatedAt = now.AddHours(-1),
            });
            db.Add(new ChapterProgress
            {
                UserId = reader, SeriesId = rows[3.2m].SeriesId, ChapterId = rows[3.2m].Id, PageIndex = 5, PageCount = 26,
                ReadSeconds = 40, ReportedSeconds = 20, StartedAt = now.AddMinutes(-30), UpdatedAt = now,
            });
            db.Add(new ReaderBookmark { UserId = reader, SeriesId = rows[3.2m].SeriesId, ChapterId = rows[3.2m].Id, PageIndex = 2, CreatedAt = now });
        });

        await Folds().ApplyAsync(seriesId, CancellationToken.None);

        using var check = _db.NewContext();
        var three = check.Chapters.Single(c => c.SeriesId == seriesId && c.Number == 3m).Id;
        var progress = check.ChapterProgress.IgnoreQueryFilters().Single(p => p.SeriesId == seriesId);
        Assert.Equal(three, progress.ChapterId);
        Assert.False(progress.Completed);
        Assert.Equal(13 + 5, progress.PageIndex);
        Assert.Equal(39, progress.PageCount);
        Assert.Equal(140, progress.ReadSeconds);
        Assert.Equal(120, progress.ReportedSeconds);
        var bookmark = check.ReaderBookmarks.IgnoreQueryFilters().Single(b => b.SeriesId == seriesId);
        Assert.Equal((three, 15), (bookmark.ChapterId, bookmark.PageIndex));
    }

    [Fact]
    public async Task A_part_on_disk_waits_until_the_whole_is_downloaded()
    {
        var seriesId = Seed((db, rows) =>
        {
            var file = new ChapterFile { SeriesId = rows[3.1m].SeriesId, RelativePath = "Test Series/3.1.cbz" };
            db.Add(file);
            db.SaveChanges();
            rows[3.1m].ChapterFileId = file.Id;
        });

        Assert.Equal(0, await Folds().ApplyAsync(seriesId, CancellationToken.None));

        Assert.Equal([3m, 3.1m, 3.2m, 4m], Chapters(seriesId).Select(c => c.Number!.Value));
    }

    private sealed class PagesOf(IReadOnlyDictionary<string, List<ulong>> pages) : IChapterPageHasher
    {
        public Task<PageEvidence?> HashAsync(ISource source, SourceChapter chapter, CancellationToken ct) =>
            Task.FromResult(pages.GetValueOrDefault(chapter.SourceChapterId) is { } found ? new PageEvidence(found, found) : null);
    }

    [Fact]
    public async Task The_check_after_a_match_counts_hashes_and_folds_the_series()
    {
        // As a freshly matched series has it: nothing counted or hashed yet.
        var seriesId = Seed();
        using (var db = _db.NewContext())
        {
            foreach (var link in db.ChapterSourceLinks.Include(l => l.SourceMapping).Where(l => l.Chapter!.SeriesId == seriesId))
            {
                link.PageHashes = null;
                if (link.SourceMapping!.SourceName == "whole")
                {
                    link.PageCount = null;
                }
            }

            db.SaveChanges();
        }

        var whole = new FakeSource
        {
            Name = "whole",
            OnGetPages = c => new ChapterPages([.. Enumerable.Range(0, c.Number == 3 ? 39 : 40).Select(i => new PageRequest($"https://whole.test/{i}"))]),
        };
        var services = new ServiceCollection();
        services.AddScoped(_ => _db.NewContext());
        services.AddSingleton(new SourceRegistry([_parts, whole]));
        services.AddSingleton(Sources.AllEnabled);
        services.AddSingleton(new DownloadQueueService(null!, TimeProvider.System, null!, NullLogger<DownloadQueueService>.Instance));
        services.AddSingleton(new PageCountFailures());
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IChapterPageHasher>(new PagesOf(new Dictionary<string, List<ulong>>
        {
            [_whole.Chapter(3).SourceChapterId] = Whole,
            [_parts.Chapter(3.1m).SourceChapterId] = Whole[..13],
            [_parts.Chapter(3.2m).SourceChapterId] = Whole[13..],
        }));
        services.AddScoped<ChapterFoldPreviewService>();
        services.AddScoped<ChapterPageCountService>();
        services.AddScoped<ChapterPageHashService>();
        services.AddScoped(sp => new ChapterFileDeletion(sp.GetRequiredService<Maki.Data.MakiDbContext>(),
            new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance), TimeProvider.System,
            TestRecycleBin.For(sp.GetRequiredService<Maki.Data.MakiDbContext>()), NullLogger<ChapterFileDeletion>.Instance));
        services.AddScoped<ChapterFoldService>();
        using var provider = services.BuildServiceProvider();

        await new ChapterFoldChecks(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<ChapterFoldChecks>.Instance)
            .CheckAsync(seriesId, CancellationToken.None);

        Assert.Equal([3m, 4m], Chapters(seriesId).Select(c => c.Number!.Value));
    }

    [Fact]
    public async Task A_part_being_downloaded_is_not_folded_under_it()
    {
        var seriesId = Seed((db, rows) => db.Add(new DownloadQueueItem
        {
            SeriesId = rows[3.2m].SeriesId, ChapterId = rows[3.2m].Id, Status = QueueStatus.Downloading, QueuedAt = DateTime.UtcNow,
        }));

        Assert.Equal(0, await Folds().ApplyAsync(seriesId, CancellationToken.None));
    }
}
