using System.IO.Compression;
using Maki.Api.Hubs;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Kavita;
using Maki.Core.Metadata;
using Maki.Core.Sources;
using Maki.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// A library import registers its files and hands the series to the background match; the link
/// stage of <see cref="SourceMatchWorkerHostedService"/> links them once chapters exist.
/// </summary>
public class LibraryImportDeferredLinkTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeAppSettings _settings = new();
    private readonly SourceMatchQueue _queue = new();
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "maki-deferred-link-" + Guid.NewGuid().ToString("N")[..8]);

    public LibraryImportDeferredLinkTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static void WriteComic(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry("001.png").Open());
        writer.Write("page");
    }

    private CbzLinkService LinkService(MakiDbContext db)
    {
        var registry = new SourceRegistry([]);
        return new CbzLinkService(
            db,
            registry,
            new KavitaScanService(
                new KavitaClient(new StubHttpClientFactory("{}")),
                _settings,
                _db.ScopeFactory(),
                NullLogger<KavitaScanService>.Instance),
            new StatsEventService(db),
            new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance),
            new SourceAvailability(_settings, registry),
            TestQuality.Create(registry),
            _settings,
            NullLogger<CbzLinkService>.Instance);
    }

    private LibraryImportService ImportService(MakiDbContext db) => new(
        db, [new FixedProvider()], null!, LinkService(db), _queue,
        new EventBroadcaster(new NoopHubContext(), _db.ScopeFactory()),
        _settings, new NamingService(_settings), new StatsEventService(db),
        new SeriesIdentityService(db, NullLogger<SeriesIdentityService>.Instance), new TestLocalizer(),
        new TestCurrentUser(1), NullLogger<LibraryImportService>.Instance);

    private SourceMatchWorkerHostedService Worker()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _db.NewContext());
        services.AddSingleton<EventBroadcaster>(
            sp => new EventBroadcaster(new NoopHubContext(), sp.GetRequiredService<IServiceScopeFactory>()));
        services.AddScoped(sp => LinkService(sp.GetRequiredService<MakiDbContext>()));
        var scopes = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new SourceMatchWorkerHostedService(_queue, scopes, NullLogger<SourceMatchWorkerHostedService>.Instance);
    }

    private int SeedRoot(string? path = null)
    {
        using var db = _db.NewContext();
        var root = new RootFolder { Path = path ?? _root };
        db.RootFolders.Add(root);
        db.SaveChanges();
        return root.Id;
    }

    /// <summary>A matched import series: chapters synced, files registered, link still owed.</summary>
    private int SeedOwedSeries(int rootId, PendingImportLink owed, params string[] files)
    {
        using var db = _db.NewContext();
        var series = new Series
        {
            Title = "Chainsaw Man", SortTitle = "Chainsaw Man", FolderName = "Chainsaw Man",
            RootFolderId = rootId, PendingImportLink = owed,
        };
        db.Series.Add(series);
        db.SaveChanges();
        db.Chapters.AddRange(Enumerable.Range(1, 2).Select(n => new Chapter { SeriesId = series.Id, Number = n, Language = "en" }));
        foreach (var file in files)
        {
            db.ChapterFiles.Add(new ChapterFile { SeriesId = series.Id, RelativePath = file, SourceName = "import" });
        }

        db.SaveChanges();
        return series.Id;
    }

    private List<int> MatchQueue()
    {
        var ids = new List<int>();
        while (_queue.TryReadMatch(out var id))
        {
            ids.Add(id);
        }

        return ids;
    }

    private List<int> LinkQueue()
    {
        var ids = new List<int>();
        while (_queue.LinkReader.TryRead(out var id))
        {
            ids.Add(id);
        }

        return ids;
    }

    [Fact]
    public void The_interactive_lane_is_read_before_the_background_lane()
    {
        _queue.Enqueue(1, SourceMatchLane.Background);
        _queue.Enqueue(2, SourceMatchLane.Background);
        _queue.Enqueue(3);

        Assert.Equal([3, 1, 2], MatchQueue());
    }

    [Theory]
    [InlineData(true, PendingImportLink.LinkAndComicInfo)]
    [InlineData(false, PendingImportLink.Link)]
    public async Task An_import_registers_its_files_and_queues_the_match_in_the_background_lane(
        bool updateComicInfo, PendingImportLink expected)
    {
        var rootId = SeedRoot();
        WriteComic(Path.Combine(_root, "Chainsaw Man", "Chainsaw Man c001.cbz"));
        WriteComic(Path.Combine(_root, "Chainsaw Man", "Chainsaw Man c002.cbz"));

        ImportResult result;
        await using (var db = _db.NewContext())
        {
            result = await ImportService(db).ImportAsync(
                rootId, new ImportRequestItem("Chainsaw Man", "42"), updateComicInfo);
        }

        Assert.True(result.Success, result.Error);
        Assert.True(result.LinkPending);
        Assert.Equal(2, result.FilesAdded);
        Assert.Null(result.Skipped);
        await using var check = _db.NewContext();
        var series = await check.Series.SingleAsync(s => s.MangaBakaId == 42);
        Assert.True(series.SourceMatchPending);
        Assert.Equal(expected, series.PendingImportLink);
        Assert.Equal(2, await check.ChapterFiles.CountAsync(f => f.SeriesId == series.Id));
        Assert.Equal([series.Id], MatchQueue());
        // Nothing jumped ahead of an interactive add.
        _queue.Enqueue(99);
        _queue.Enqueue(series.Id, SourceMatchLane.Background);
        Assert.Equal([99, series.Id], MatchQueue());
    }

    [Fact]
    public async Task Reimporting_into_a_series_with_no_chapters_defers_the_link_too()
    {
        var rootId = SeedRoot();
        int seriesId;
        await using (var db = _db.NewContext())
        {
            var existing = new Series
            {
                Title = "Chainsaw Man", SortTitle = "Chainsaw Man", FolderName = "Chainsaw Man",
                RootFolderId = rootId, MangaBakaId = 42, SourceMatchPending = true,
            };
            db.Series.Add(existing);
            db.SaveChanges();
            seriesId = existing.Id;
        }

        WriteComic(Path.Combine(_root, "Chainsaw Man", "Chainsaw Man c001.cbz"));

        ImportResult result;
        await using (var db = _db.NewContext())
        {
            result = await ImportService(db).ImportAsync(rootId, new ImportRequestItem("Chainsaw Man", "42"));
        }

        Assert.True(result.Success, result.Error);
        Assert.True(result.LinkPending);
        Assert.Equal(seriesId, result.SeriesId);
        await using var check = _db.NewContext();
        var series = await check.Series.SingleAsync(s => s.Id == seriesId);
        Assert.True(series.SourceMatchPending);
        Assert.Equal(PendingImportLink.LinkAndComicInfo, series.PendingImportLink);
        Assert.Equal([seriesId], MatchQueue());
    }

    [Fact]
    public async Task The_link_stage_links_the_registered_files_and_clears_the_marker()
    {
        var rootId = SeedRoot();
        WriteComic(Path.Combine(_root, "Chainsaw Man", "Chainsaw Man c001.cbz"));
        WriteComic(Path.Combine(_root, "Chainsaw Man", "Chainsaw Man c002.cbz"));
        var seriesId = SeedOwedSeries(rootId, PendingImportLink.Link,
            Path.Combine("Chainsaw Man", "Chainsaw Man c001.cbz"),
            Path.Combine("Chainsaw Man", "Chainsaw Man c002.cbz"));

        await Worker().LinkAsync(seriesId, default);

        await using var check = _db.NewContext();
        Assert.Equal(PendingImportLink.None, (await check.Series.SingleAsync(s => s.Id == seriesId)).PendingImportLink);
        Assert.Equal(2, await check.Chapters.CountAsync(c => c.SeriesId == seriesId && c.ChapterFileId != null));
        Assert.Equal(2, await check.ChapterFiles.CountAsync(f => f.SeriesId == seriesId));
    }

    [Fact]
    public async Task A_file_deleted_since_the_import_does_not_stop_the_rest_linking()
    {
        var rootId = SeedRoot();
        WriteComic(Path.Combine(_root, "Chainsaw Man", "Chainsaw Man c001.cbz"));
        var seriesId = SeedOwedSeries(rootId, PendingImportLink.Link,
            Path.Combine("Chainsaw Man", "Chainsaw Man c001.cbz"),
            Path.Combine("Chainsaw Man", "Chainsaw Man c002.cbz"));

        await Worker().LinkAsync(seriesId, default);

        await using var check = _db.NewContext();
        Assert.Equal(PendingImportLink.None, (await check.Series.SingleAsync(s => s.Id == seriesId)).PendingImportLink);
        var linked = await check.Chapters.Where(c => c.SeriesId == seriesId && c.ChapterFileId != null)
            .Select(c => c.Number).ToListAsync();
        Assert.Equal([1m], linked);
    }

    [Fact]
    public async Task An_unreachable_root_keeps_the_marker_for_the_next_start()
    {
        var rootId = SeedRoot(Path.Combine(_root, "unmounted"));
        var seriesId = SeedOwedSeries(rootId, PendingImportLink.LinkAndComicInfo,
            Path.Combine("Chainsaw Man", "Chainsaw Man c001.cbz"));

        await Worker().LinkAsync(seriesId, default);

        await using var check = _db.NewContext();
        Assert.Equal(PendingImportLink.LinkAndComicInfo,
            (await check.Series.SingleAsync(s => s.Id == seriesId)).PendingImportLink);
    }

    [Fact]
    public async Task A_series_still_matching_is_left_for_the_match_to_route()
    {
        var rootId = SeedRoot();
        WriteComic(Path.Combine(_root, "Chainsaw Man", "Chainsaw Man c001.cbz"));
        var seriesId = SeedOwedSeries(rootId, PendingImportLink.Link, Path.Combine("Chainsaw Man", "Chainsaw Man c001.cbz"));
        await using (var db = _db.NewContext())
        {
            await db.Series.Where(s => s.Id == seriesId).ExecuteUpdateAsync(u => u.SetProperty(s => s.SourceMatchPending, true));
        }

        await Worker().LinkAsync(seriesId, default);

        await using var check = _db.NewContext();
        Assert.Equal(PendingImportLink.Link, (await check.Series.SingleAsync(s => s.Id == seriesId)).PendingImportLink);
        Assert.Equal(0, await check.Chapters.CountAsync(c => c.SeriesId == seriesId && c.ChapterFileId != null));
    }

    [Fact]
    public async Task A_matched_series_that_still_owes_its_link_is_routed_to_the_link_stage()
    {
        var rootId = SeedRoot();
        var seriesId = SeedOwedSeries(rootId, PendingImportLink.Link);

        await Worker().MatchAsync(seriesId, default);

        Assert.Equal([seriesId], LinkQueue());
        Assert.Empty(MatchQueue());
    }

    private sealed class FixedProvider : IMetadataProvider
    {
        public string Name => "fake";

        public Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(
            string query, string maxContentRating, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MetadataSearchResult>>([]);

        public Task<SeriesMetadata?> GetAsync(string providerId, CancellationToken ct = default) =>
            Task.FromResult<SeriesMetadata?>(new SeriesMetadata
            {
                ProviderId = providerId, Title = "Chainsaw Man", MangaBakaId = 42,
            });
    }
}
