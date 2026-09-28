using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Security;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.Extensions.Caching.Memory;

namespace Maki.Api.Tests;

/// <summary>
/// <see cref="LibraryCompositionService"/>: what the collection is made of, and the fact that it
/// only ever answers about root folders the caller can see.
/// </summary>
public sealed class LibraryCompositionTests : IDisposable
{
    private const int Owner = 1;

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static readonly DateTime T0 = new(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc);

    private LibraryCompositionService Service(
        MakiDbContext db, int userId = Owner, MakiPermission permissions = MakiPermission.Admin) =>
        new(db, new TestCurrentUser(userId, permissions: permissions), new MemoryCache(new MemoryCacheOptions()));

    /// <summary>Adds a downloaded chapter with a backing file, and returns the file's series.</summary>
    private void SeedFile(int seriesId, string source, long size, DateTime? added = null)
    {
        using var db = _db.NewContext();
        var file = new ChapterFile
        {
            SeriesId = seriesId,
            RelativePath = $"{Guid.NewGuid()}.cbz",
            Size = size,
            SourceName = source,
            DateAdded = added ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        db.ChapterFiles.Add(file);
        db.SaveChanges();

        db.Chapters.Add(new Chapter { SeriesId = seriesId, Number = 1, ChapterFileId = file.Id });
        db.SaveChanges();
    }

    [Fact]
    public async Task TotalsCountChaptersFilesAndBytes()
    {
        var a = _db.SeedSeries("A");
        var b = _db.SeedSeries("B", monitor: NewChapterMonitorMode.None);
        SeedFile(a, "MangaDex", 1_000);
        SeedFile(a, "MangaDex", 2_000);
        SeedFile(b, "Asura", 500);

        using var db = _db.NewContext(Owner);
        var stats = await Service(db).GetAsync(CancellationToken.None);

        Assert.Equal(2, stats.Totals.SeriesCount);
        // "None" is the only mode that means unmonitored; the other three all watch something.
        Assert.Equal(1, stats.Totals.MonitoredCount);
        Assert.Equal(3, stats.Totals.ChapterCount);
        Assert.Equal(3, stats.Totals.DownloadedChapterCount);
        Assert.Equal(3, stats.Totals.FileCount);
        Assert.Equal(3_500, stats.Totals.TotalBytes);
    }

    [Fact]
    public async Task EmptyLibraryReportsZeroRatherThanFailingOnANullSum()
    {
        using var db = _db.NewContext(Owner);
        var stats = await Service(db).GetAsync(CancellationToken.None);

        Assert.Equal(0, stats.Totals.SeriesCount);
        Assert.Equal(0, stats.Totals.TotalBytes);
        Assert.Empty(stats.BySource);
        Assert.Empty(stats.Growth);
    }

    [Fact]
    public async Task SourcesRankByBytesAndCarryTheirFileCount()
    {
        var a = _db.SeedSeries("A");
        SeedFile(a, "MangaDex", 100);
        SeedFile(a, "MangaDex", 100);
        SeedFile(a, "Asura", 5_000);

        using var db = _db.NewContext(Owner);
        var stats = await Service(db).GetAsync(CancellationToken.None);

        Assert.Equal("Asura", stats.BySource[0].Name);
        Assert.Equal(5_000, stats.BySource[0].Bytes);
        Assert.Equal(1, stats.BySource[0].Files);
        Assert.Equal(200, stats.BySource[1].Bytes);
        Assert.Equal(2, stats.BySource[1].Files);
    }

    [Fact]
    public async Task TypeAndStatusGroupWithUnknownNamed()
    {
        _db.SeedSeries("A", configure: s => { s.Type = "manga"; s.Status = SeriesStatus.Ongoing; });
        _db.SeedSeries("B", configure: s => { s.Type = "manga"; s.Status = SeriesStatus.Completed; });
        // Never refreshed since the Type column landed, so it is null rather than absent.
        _db.SeedSeries("C", configure: s => s.Status = SeriesStatus.Ongoing);

        using var db = _db.NewContext(Owner);
        var stats = await Service(db).GetAsync(CancellationToken.None);

        Assert.Equal(2, stats.ByType.Single(t => t.Name == "manga").Count);
        Assert.Equal(1, stats.ByType.Single(t => t.Name == "Unknown").Count);
        Assert.Equal(2, stats.ByStatus.Single(s => s.Name == "Ongoing").Count);
        Assert.Equal(1, stats.ByStatus.Single(s => s.Name == "Completed").Count);
    }

    [Fact]
    public async Task GrowthAccumulatesMonthByMonth()
    {
        _db.SeedSeries("A", configure: s => s.Added = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc));
        _db.SeedSeries("B", configure: s => s.Added = new DateTime(2026, 1, 20, 0, 0, 0, DateTimeKind.Utc));
        _db.SeedSeries("C", configure: s => s.Added = new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc));

        using var db = _db.NewContext(Owner);
        var stats = await Service(db).GetAsync(CancellationToken.None);

        Assert.Equal(["2026-01", "2026-03"], stats.Growth.Select(g => g.Bucket));
        Assert.Equal(2, stats.Growth[0].SeriesAdded);
        Assert.Equal(2, stats.Growth[0].Cumulative);
        Assert.Equal(1, stats.Growth[1].SeriesAdded);
        Assert.Equal(3, stats.Growth[1].Cumulative);
    }

    [Fact]
    public async Task GenresAreCountedPerSeriesNotPerChapter()
    {
        var a = _db.SeedSeries("A", configure: s => s.Genres = ["Action", "Comedy"]);
        _db.SeedSeries("B", configure: s => s.Genres = ["Action"]);
        // Several files on one series must not multiply its genres.
        SeedFile(a, "MangaDex", 10);
        SeedFile(a, "MangaDex", 10);

        using var db = _db.NewContext(Owner);
        var stats = await Service(db).GetAsync(CancellationToken.None);

        Assert.Equal(2, stats.TopGenres.Single(g => g.Name == "Action").Count);
        Assert.Equal(1, stats.TopGenres.Single(g => g.Name == "Comedy").Count);
    }

    [Fact]
    public async Task LargestSeriesRankByTotalBytes()
    {
        var small = _db.SeedSeries("Small");
        var big = _db.SeedSeries("Big", configure: s => s.CoverPath = "covers/big.jpg");
        SeedFile(small, "MangaDex", 1_000);
        SeedFile(big, "MangaDex", 4_000);
        SeedFile(big, "MangaDex", 4_000);

        using var db = _db.NewContext(Owner);
        var stats = await Service(db).GetAsync(CancellationToken.None);

        Assert.Equal("Big", stats.Largest[0].Title);
        Assert.Equal(8_000, stats.Largest[0].Bytes);
        Assert.Equal(2, stats.Largest[0].Files);
        Assert.NotNull(stats.Largest[0].CoverUrl);
        Assert.Equal("Small", stats.Largest[1].Title);
        Assert.Null(stats.Largest[1].CoverUrl);
    }

    [Fact]
    public async Task RootFolderVisibilityHidesSeriesAndTheirBytes()
    {
        var mine = _db.SeedSeries("Mine", configure: s => s.Genres = ["Action"]);
        var hidden = _db.SeedSeries("Hidden", configure: s => s.Genres = ["Horror"]);
        SeedFile(mine, "MangaDex", 1_000);
        SeedFile(hidden, "Asura", 9_000);

        var restricted = _db.SeedUser("restricted", allRootFolders: false);
        using (var seed = _db.NewContext())
        {
            var myRoot = seed.Series.Single(s => s.Id == mine).RootFolderId;
            seed.Set<UserRootFolder>().Add(new UserRootFolder { UserId = restricted, RootFolderId = myRoot });
            seed.SaveChanges();
        }

        using var db = _db.NewContext(restricted, allRootFolders: false);
        var stats = await Service(db, restricted).GetAsync(CancellationToken.None);

        Assert.Equal(1, stats.Totals.SeriesCount);
        Assert.Equal(1_000, stats.Totals.TotalBytes);
        Assert.Equal(1, stats.Totals.FileCount);
        Assert.Single(stats.BySource, s => s.Name == "MangaDex");
        Assert.DoesNotContain(stats.TopGenres, g => g.Name == "Horror");
        Assert.DoesNotContain(stats.Largest, s => s.Title == "Hidden");
    }

    [Fact]
    public async Task ContentRatingGroupsWithUnknownNamedForNullOrBlank()
    {
        _db.SeedSeries("A", configure: s => s.ContentRating = "safe");
        _db.SeedSeries("B", configure: s => s.ContentRating = "safe");
        _db.SeedSeries("C", configure: s => s.ContentRating = null);
        _db.SeedSeries("D", configure: s => s.ContentRating = "");

        using var db = _db.NewContext(Owner);
        var stats = await Service(db).GetAsync(CancellationToken.None);

        Assert.Equal(2, stats.ByContentRating.Single(r => r.Name == "safe").Count);
        Assert.Equal(2, stats.ByContentRating.Single(r => r.Name == "unknown").Count);
    }

    private void SeedQueueItem(
        int seriesId, QueueStatus status, DateTime queuedAt, DateTime? completedAt = null,
        int? sourceMappingId = null, AcquisitionProtocol protocol = AcquisitionProtocol.Scraper,
        DownloadOrigin origin = DownloadOrigin.Manual)
    {
        using var db = _db.NewContext();
        db.DownloadQueue.Add(new DownloadQueueItem
        {
            SeriesId = seriesId,
            Status = status,
            QueuedAt = queuedAt,
            CompletedAt = completedAt,
            SourceMappingId = sourceMappingId,
            Protocol = protocol,
            Origin = origin
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task SourceReliabilityCountsCompletedAndFailedWithMedianDuration()
    {
        var series = _db.SeedSeries("A", mappings:
        [
            new SourceMapping { SourceName = "MangaDex", SourceSeriesId = "s", Url = "u" }
        ]);
        int mappingId;
        using (var db = _db.NewContext())
        {
            mappingId = db.SourceMappings.Single().Id;
        }

        // Completed after 10, 20, 30 minutes -> median 20 minutes = 1200s.
        SeedQueueItem(series, QueueStatus.Completed, T0, T0.AddMinutes(10), mappingId);
        SeedQueueItem(series, QueueStatus.Completed, T0, T0.AddMinutes(20), mappingId);
        SeedQueueItem(series, QueueStatus.Completed, T0, T0.AddMinutes(30), mappingId);
        SeedQueueItem(series, QueueStatus.Failed, T0, sourceMappingId: mappingId);

        using var check = _db.NewContext(Owner);
        var stats = await Service(check).GetAsync(CancellationToken.None);

        var reliability = stats.SourceReliability.Single(r => r.Name == "MangaDex");
        Assert.Equal(3, reliability.Completed);
        Assert.Equal(1, reliability.Failed);
        Assert.Equal(1200, reliability.MedianSecondsToComplete);
    }

    [Fact]
    public async Task SourceReliabilityExcludesRowsOlderThan30Days()
    {
        var series = _db.SeedSeries("A");
        // The service windows against DateTime.UtcNow, so seed relative to "now".
        var now = DateTime.UtcNow;
        SeedQueueItem(series, QueueStatus.Completed, now.AddDays(-31), now.AddDays(-31).AddMinutes(5));
        SeedQueueItem(series, QueueStatus.Completed, now.AddDays(-1), now.AddDays(-1).AddMinutes(5));

        using var check = _db.NewContext(Owner);
        var stats = await Service(check).GetAsync(CancellationToken.None);

        var reliability = stats.SourceReliability.Single();
        Assert.Equal(1, reliability.Completed);
    }

    [Fact]
    public async Task SourceReliabilityFallsBackToProtocolNameWithNoMapping()
    {
        var series = _db.SeedSeries("A");
        var now = DateTime.UtcNow;
        SeedQueueItem(series, QueueStatus.Completed, now, now.AddMinutes(1), protocol: AcquisitionProtocol.Torrent);

        using var check = _db.NewContext(Owner);
        var stats = await Service(check).GetAsync(CancellationToken.None);

        Assert.Equal("torrent", stats.SourceReliability.Single().Name);
    }

    [Fact]
    public async Task MonitorCatchesCountsOnlyCompletedMonitorRefreshWithin30Days()
    {
        var series = _db.SeedSeries("A");
        var now = DateTime.UtcNow;

        SeedQueueItem(series, QueueStatus.Completed, now, now, origin: DownloadOrigin.MonitorRefresh);
        // Manual completion in the window: should not count.
        SeedQueueItem(series, QueueStatus.Completed, now, now, origin: DownloadOrigin.Manual);
        // Monitor refresh but failed: should not count.
        SeedQueueItem(series, QueueStatus.Failed, now, origin: DownloadOrigin.MonitorRefresh);
        // Monitor refresh completed, but outside the window: should not count.
        SeedQueueItem(series, QueueStatus.Completed, now.AddDays(-40), now.AddDays(-40), origin: DownloadOrigin.MonitorRefresh);
        // Queued before the window, finished inside it: windowed on QueuedAt like housekeeping, so out.
        SeedQueueItem(series, QueueStatus.Completed, now.AddDays(-31), now.AddDays(-29), origin: DownloadOrigin.MonitorRefresh);

        using var check = _db.NewContext(Owner);
        var stats = await Service(check).GetAsync(CancellationToken.None);

        Assert.Equal(1, stats.MonitorCatches);
    }

    [Fact]
    public async Task RequestsScopeToOwnerForNonAdminAndEveryoneForAdmin()
    {
        var reader = _db.SeedUser("reader", MakiPermission.None);
        var admin = _db.SeedUser("admin", MakiPermission.Admin);

        using (var db = _db.NewContext())
        {
            db.SeriesRequests.Add(new SeriesRequest
            {
                UserId = reader, Kind = SeriesRequestKind.NewSeries, MetadataProviderId = "1",
                Title = "Mine", Status = SeriesRequestStatus.Pending, Created = T0
            });
            db.SeriesRequests.Add(new SeriesRequest
            {
                UserId = admin, Kind = SeriesRequestKind.NewSeries, MetadataProviderId = "2",
                Title = "Somebody else's", Status = SeriesRequestStatus.Pending, Created = T0
            });
            db.SaveChanges();
        }

        using var asReader = _db.NewContext(reader);
        var readerStats = await Service(asReader, reader, MakiPermission.None).GetAsync(CancellationToken.None);
        Assert.False(readerStats.Requests.AllUsers);
        Assert.Equal(1, readerStats.Requests.Open);

        using var asAdmin = _db.NewContext(admin);
        var adminStats = await Service(asAdmin, admin, MakiPermission.Admin).GetAsync(CancellationToken.None);
        Assert.True(adminStats.Requests.AllUsers);
        Assert.Equal(2, adminStats.Requests.Open);
    }

    [Fact]
    public async Task RequestsOpenCountsPendingAndProcessingOnly()
    {
        var admin = _db.SeedUser("admin", MakiPermission.Admin);
        using (var db = _db.NewContext())
        {
            db.SeriesRequests.Add(new SeriesRequest
            {
                UserId = admin, Kind = SeriesRequestKind.NewSeries, MetadataProviderId = "1",
                Title = "Pending", Status = SeriesRequestStatus.Pending, Created = T0
            });
            db.SeriesRequests.Add(new SeriesRequest
            {
                UserId = admin, Kind = SeriesRequestKind.NewSeries, MetadataProviderId = "2",
                Title = "Processing", Status = SeriesRequestStatus.Processing, Created = T0
            });
            db.SeriesRequests.Add(new SeriesRequest
            {
                UserId = admin, Kind = SeriesRequestKind.NewSeries, MetadataProviderId = "3",
                Title = "Rejected", Status = SeriesRequestStatus.Rejected, Created = T0
            });
            db.SaveChanges();
        }

        using var check = _db.NewContext(admin);
        var stats = await Service(check, admin, MakiPermission.Admin).GetAsync(CancellationToken.None);

        Assert.Equal(2, stats.Requests.Open);
    }

    [Fact]
    public async Task RequestsMedianResolveHoursOverLast90DaysOnly()
    {
        var admin = _db.SeedUser("admin", MakiPermission.Admin);
        var now = DateTime.UtcNow;

        using (var db = _db.NewContext())
        {
            // Resolved in 10h and 20h within the window -> median 15h.
            db.SeriesRequests.Add(new SeriesRequest
            {
                UserId = admin, Kind = SeriesRequestKind.NewSeries, MetadataProviderId = "1",
                Title = "Fast", Status = SeriesRequestStatus.Approved,
                Created = now.AddDays(-10), ResolvedAt = now.AddDays(-10).AddHours(10)
            });
            db.SeriesRequests.Add(new SeriesRequest
            {
                UserId = admin, Kind = SeriesRequestKind.NewSeries, MetadataProviderId = "2",
                Title = "Slow", Status = SeriesRequestStatus.Approved,
                Created = now.AddDays(-10), ResolvedAt = now.AddDays(-10).AddHours(20)
            });
            // Resolved outside the 90-day window: excluded.
            db.SeriesRequests.Add(new SeriesRequest
            {
                UserId = admin, Kind = SeriesRequestKind.NewSeries, MetadataProviderId = "3",
                Title = "Old", Status = SeriesRequestStatus.Approved,
                Created = now.AddDays(-100), ResolvedAt = now.AddDays(-91)
            });
            db.SaveChanges();
        }

        using var check = _db.NewContext(admin);
        var stats = await Service(check, admin, MakiPermission.Admin).GetAsync(CancellationToken.None);

        Assert.Equal(2, stats.Requests.Resolved90d);
        Assert.NotNull(stats.Requests.MedianResolveHours);
        Assert.Equal(15, stats.Requests.MedianResolveHours!.Value, 3);
    }
}
