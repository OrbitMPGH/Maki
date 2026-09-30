using System.IO.Compression;
using System.Text.Json;
using Maki.Api.Configuration;
using Maki.Api.Dtos;
using Maki.Api.Hubs;
using Maki.Api.Services;
using Maki.Core.Download;
using Maki.Core.Entities;
using Maki.Core.Kavita;
using Maki.Core.Quality;
using Maki.Core.Sources;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Maki.Api.Tests;

internal static class TestQuality
{
    public static ChapterFileQualityService Create(SourceRegistry? registry = null) =>
        new(registry ?? new SourceRegistry([]), TimeProvider.System, NullLogger<ChapterFileQualityService>.Instance);

    public static byte[] Png(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height);
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }

    public static void WriteCbz(string path, int pages, int width, int height)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        for (var i = 1; i <= pages; i++)
        {
            using var entry = zip.CreateEntry($"{i:000}.png").Open();
            entry.Write(Png(width, height));
        }
    }
}

public class ChapterFileMeasureServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "maki-measure-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private ChapterFileMeasureService Service(Maki.Data.MakiDbContext db) =>
        new(db, TestQuality.Create(), NullLogger<ChapterFileMeasureService>.Instance)
        {
            DelayBetweenFiles = TimeSpan.Zero
        };

    private int SeedSeries()
    {
        using var db = _db.NewContext();
        var root = new RootFolder { Path = _root };
        db.RootFolders.Add(root);
        db.SaveChanges();
        var series = new Series
        {
            Title = "Series", SortTitle = "series", FolderName = "Series", RootFolderId = root.Id,
            Added = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        db.Series.Add(series);
        db.SaveChanges();
        return series.Id;
    }

    private int SeedFile(int seriesId, string relativePath, string sourceName = "import", string? releaseName = null)
    {
        using var db = _db.NewContext();
        var file = new ChapterFile
        {
            SeriesId = seriesId, RelativePath = relativePath, SourceName = sourceName,
            ReleaseName = releaseName, DateAdded = DateTime.UtcNow
        };
        db.ChapterFiles.Add(file);
        db.SaveChanges();
        return file.Id;
    }

    private ChapterFile Load(int id)
    {
        using var db = _db.NewContext();
        return db.ChapterFiles.Single(f => f.Id == id);
    }

    [Fact]
    public async Task Measures_an_unmeasured_file_and_leaves_a_missing_one_for_later()
    {
        var seriesId = SeedSeries();
        TestQuality.WriteCbz(Path.Combine(_root, "Series", "Series v01 (Digital) (Oak).cbz"), 20, 40, 60);
        var present = SeedFile(seriesId, Path.Combine("Series", "Series v01 (Digital) (Oak).cbz"));
        var missing = SeedFile(seriesId, Path.Combine("Series", "Series v02.cbz"));

        using (var db = _db.NewContext())
        {
            // The count never stats files, so the missing one under a present root is pending too.
            Assert.Equal(2, await ChapterFileMeasureService.CountPendingAsync(db, CancellationToken.None));
            Assert.True(await Service(db).RunAsync(CancellationToken.None));
        }

        var measured = Load(present);
        Assert.NotNull(measured.MeasuredAtUtc);
        Assert.Equal(20, measured.PageCount);
        Assert.Equal(40, measured.MedianWidth);
        Assert.Equal(60, measured.MedianHeight);
        Assert.Equal("png", measured.ImageFormat);
        Assert.Equal(QualityTier.Volume, measured.Tier);
        Assert.Equal("Oak", measured.Group);

        var skipped = Load(missing);
        Assert.Null(skipped.MeasuredAtUtc);
        Assert.Null(skipped.PageCount);

        using (var db = _db.NewContext())
        {
            Assert.Equal(1, await ChapterFileMeasureService.CountPendingAsync(db, CancellationToken.None));
        }
    }

    [Fact]
    public async Task A_root_whose_folder_is_missing_adds_nothing_to_the_pending_count()
    {
        var seriesId = SeedSeries();
        SeedFile(seriesId, Path.Combine("Series", "Series 001.cbz"));
        int offlineSeries;
        using (var db = _db.NewContext())
        {
            var offline = new RootFolder { Path = Path.Combine(_root, "unmounted-share") };
            db.RootFolders.Add(offline);
            db.SaveChanges();
            var series = new Series
            {
                Title = "Offline", SortTitle = "offline", FolderName = "Offline", RootFolderId = offline.Id,
                Added = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            };
            db.Series.Add(series);
            db.SaveChanges();
            offlineSeries = series.Id;
        }

        SeedFile(offlineSeries, Path.Combine("Offline", "Offline 001.cbz"));
        SeedFile(offlineSeries, Path.Combine("Offline", "Offline 002.cbz"));
        Directory.CreateDirectory(_root);

        using (var db = _db.NewContext())
        {
            Assert.Equal(1, await ChapterFileMeasureService.CountPendingAsync(db, CancellationToken.None));
        }
    }

    [Fact]
    public async Task A_row_deleted_between_measuring_and_writing_is_skipped_and_the_run_continues()
    {
        var seriesId = SeedSeries();
        TestQuality.WriteCbz(Path.Combine(_root, "Series", "Series 001.cbz"), 2, 10, 10);
        TestQuality.WriteCbz(Path.Combine(_root, "Series", "Series 002.cbz"), 3, 20, 20);
        var doomed = SeedFile(seriesId, Path.Combine("Series", "Series 001.cbz"));
        var survivor = SeedFile(seriesId, Path.Combine("Series", "Series 002.cbz"));

        // Stamp reads the clock once it has measured and before the service writes, which is the
        // window a concurrent delete would land in.
        var clock = new DeletingClock(() =>
        {
            using var other = _db.NewContext();
            other.ChapterFiles.Where(f => f.Id == doomed).ExecuteDelete();
        });
        var quality = new ChapterFileQualityService(
            new SourceRegistry([]), clock, NullLogger<ChapterFileQualityService>.Instance);

        using (var db = _db.NewContext())
        {
            var service = new ChapterFileMeasureService(db, quality, NullLogger<ChapterFileMeasureService>.Instance)
            {
                DelayBetweenFiles = TimeSpan.Zero
            };
            Assert.True(await service.RunAsync(CancellationToken.None));
        }

        using (var db = _db.NewContext())
        {
            Assert.False(db.ChapterFiles.Any(f => f.Id == doomed));
        }

        var measured = Load(survivor);
        Assert.NotNull(measured.MeasuredAtUtc);
        Assert.Equal(3, measured.PageCount);
    }

    private sealed class DeletingClock(Action onFirstRead) : TimeProvider
    {
        private Action? _pending = onFirstRead;

        public override DateTimeOffset GetUtcNow()
        {
            var action = Interlocked.Exchange(ref _pending, null);
            action?.Invoke();
            return base.GetUtcNow();
        }
    }

    [Fact]
    public async Task A_corrupt_archive_is_stamped_unknown_so_it_is_not_retried()
    {
        var seriesId = SeedSeries();
        var path = Path.Combine(_root, "Series", "Series 001.cbz");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, "not a zip at all"u8.ToArray());
        var id = SeedFile(seriesId, Path.Combine("Series", "Series 001.cbz"));

        using (var db = _db.NewContext())
        {
            await Service(db).RunAsync(CancellationToken.None);
        }

        var file = Load(id);
        Assert.NotNull(file.MeasuredAtUtc);
        Assert.Equal(0, file.PageCount);
        Assert.Equal("unknown", file.ImageFormat);
    }

    [Fact]
    public async Task Group_comes_from_the_chapter_link_of_the_files_source()
    {
        var seriesId = SeedSeries();
        TestQuality.WriteCbz(Path.Combine(_root, "Series", "Series 001.cbz"), 2, 10, 10);
        var fileId = SeedFile(seriesId, Path.Combine("Series", "Series 001.cbz"), sourceName: "scan");
        using (var db = _db.NewContext())
        {
            var mapping = new SourceMapping { SeriesId = seriesId, SourceName = "scan", SourceSeriesId = "s" };
            db.SourceMappings.Add(mapping);
            var chapter = new Chapter { SeriesId = seriesId, Number = 1, ChapterFileId = fileId };
            db.Chapters.Add(chapter);
            db.SaveChanges();
            db.ChapterSourceLinks.Add(new ChapterSourceLink
            {
                ChapterId = chapter.Id, SourceMappingId = mapping.Id, SourceChapterId = "c1", Group = "Night Scans"
            });
            db.SaveChanges();
        }

        var registry = new SourceRegistry([new FakeSource { Name = "scan", Kind = SourceKind.Scanlator }]);
        using (var db = _db.NewContext())
        {
            await new ChapterFileMeasureService(db, TestQuality.Create(registry),
                NullLogger<ChapterFileMeasureService>.Instance) { DelayBetweenFiles = TimeSpan.Zero }
                .RunAsync(CancellationToken.None);
        }

        var file = Load(fileId);
        Assert.Equal(QualityTier.Scanlator, file.Tier);
        Assert.Equal("Night Scans", file.Group);
    }

    [Fact]
    public void Dto_serialises_the_tier_as_its_lowercase_name()
    {
        var options = new JsonOptions().JsonSerializerOptions;
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        var dto = ChapterFileQualityDto.From(new ChapterFile { Tier = QualityTier.Scanlator, Group = "Oak" });

        var json = JsonSerializer.Serialize(dto, options);

        Assert.Contains("\"tier\":\"scanlator\"", json);
        Assert.Contains("\"measured\":false", json);
    }
}

public class ChapterFileQualityServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("maki-quality-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void A_file_locked_by_another_handle_is_left_unmeasured_for_a_retry()
    {
        var path = Path.Combine(_root, "Series 001.cbz");
        TestQuality.WriteCbz(path, 2, 10, 10);
        var file = new ChapterFile { RelativePath = "Series 001.cbz", SourceName = "import" };

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            TestQuality.Create().Stamp(file, path, null, null, 0, CancellationToken.None);
        }

        Assert.Null(file.MeasuredAtUtc);
        Assert.Null(file.PageCount);
    }

    [Fact]
    public void A_zip_of_garbage_bytes_is_stamped_unknown()
    {
        var path = Path.Combine(_root, "Series 001.cbz");
        File.WriteAllBytes(path, Enumerable.Range(0, 4096).Select(i => (byte)(i * 37)).ToArray());
        var file = new ChapterFile { RelativePath = "Series 001.cbz", SourceName = "import" };

        TestQuality.Create().Stamp(file, path, null, null, 0, CancellationToken.None);

        Assert.NotNull(file.MeasuredAtUtc);
        Assert.Equal(0, file.PageCount);
        Assert.Equal("unknown", file.ImageFormat);
    }

    [Fact]
    public void Restamping_with_less_information_keeps_the_earlier_tier_and_group()
    {
        var file = new ChapterFile
        {
            RelativePath = "Series 001.cbz", SourceName = "gone-source", Tier = QualityTier.Scanlator, Group = "Oak"
        };

        ChapterFileQualityService.StampTierOnly(file, null, null);

        Assert.Equal(QualityTier.Scanlator, file.Tier);
        Assert.Equal("Oak", file.Group);
    }

    [Fact]
    public void Remeasuring_with_less_information_keeps_the_earlier_tier_and_group()
    {
        var path = Path.Combine(_root, "Series 001.cbz");
        TestQuality.WriteCbz(path, 2, 10, 10);
        var file = new ChapterFile
        {
            RelativePath = "Series 001.cbz", SourceName = "gone-source", Tier = QualityTier.Official, Group = "Publisher"
        };

        TestQuality.Create().Stamp(file, path, null, null, 0, CancellationToken.None);

        Assert.NotNull(file.MeasuredAtUtc);
        Assert.Equal(QualityTier.Official, file.Tier);
        Assert.Equal("Publisher", file.Group);
    }

    [Theory]
    [InlineData("import")]
    [InlineData("rescan")]
    [InlineData("Manual")]
    [InlineData("relink")]
    [InlineData("health")]
    public void A_sentinel_source_reads_its_tier_and_group_from_the_file_name(string sourceName)
    {
        var file = new ChapterFile
        {
            RelativePath = Path.Combine("Series", "Series v01 (2024) (Digital) (1r0n).cbz"), SourceName = sourceName
        };
        var quality = TestQuality.Create();

        var (kind, group) = quality.ResolveProvenance(file, null);
        ChapterFileQualityService.StampTierOnly(file, kind, group);

        Assert.Equal(QualityTier.Volume, file.Tier);
        Assert.Equal("1r0n", file.Group);
    }

    [Fact]
    public void A_scanlator_source_link_with_no_group_falls_back_to_the_site_name()
    {
        var registry = new SourceRegistry([new FakeSource { Name = "nightscans", Kind = SourceKind.Scanlator }]);
        var mapping = new SourceMapping { SourceName = "nightscans", SourceSeriesId = "s" };
        var chapter = new Chapter { Number = 1 };
        chapter.SourceLinks.Add(new ChapterSourceLink { SourceMapping = mapping, SourceChapterId = "c1" });
        var file = new ChapterFile { RelativePath = "Series 001.cbz", SourceName = "nightscans" };

        var (kind, group) = TestQuality.Create(registry).ResolveProvenance(file, chapter);

        Assert.Equal(SourceKind.Scanlator, kind);
        Assert.Equal("nightscans", group);
    }
}

/// <summary>
/// Runs a real download through <see cref="ChapterDownloadProcessor"/> with pages served inline, so
/// the file it writes is the one that gets measured.
/// </summary>
[Collection(ConfigDirCollection.Name)]
public class ChapterDownloadQualityTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeAppSettings _settings = new();
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "maki-dlquality-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _configDir;
    private readonly string? _priorEnv;

    public ChapterDownloadQualityTests()
    {
        _configDir = Path.Combine(_root, "config");
        _priorEnv = Environment.GetEnvironmentVariable("MAKI_CONFIG_DIR");
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _configDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _priorEnv);
        _db.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task A_downloaded_file_takes_its_tier_from_the_source_kind_and_is_measured()
    {
        var page = TestQuality.Png(30, 50);
        var source = new FakeSource
        {
            Name = "official",
            Kind = SourceKind.Official,
            OnGetPages = _ => new ChapterPages([new PageRequest("https://x.test/1.png", Data: page),
                new PageRequest("https://x.test/2.png", Data: page), new PageRequest("https://x.test/3.png", Data: page)])
        };
        var registry = new SourceRegistry([source]);

        int itemId;
        int chapterId;
        using (var db = _db.NewContext())
        {
            var root = new RootFolder { Path = Path.Combine(_root, "library") };
            Directory.CreateDirectory(root.Path); // the processor refuses to write into a missing root
            db.RootFolders.Add(root);
            db.SaveChanges();
            var series = new Series
            {
                Title = "Series", SortTitle = "series", FolderName = "Series", RootFolderId = root.Id,
                Added = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            };
            var mapping = new SourceMapping { SourceName = "official", SourceSeriesId = "s" };
            series.SourceMappings.Add(mapping);
            db.Series.Add(series);
            db.SaveChanges();
            var chapter = new Chapter { SeriesId = series.Id, Number = 1, NumberRaw = "1" };
            db.Chapters.Add(chapter);
            db.SaveChanges();
            db.ChapterSourceLinks.Add(new ChapterSourceLink
            {
                ChapterId = chapter.Id, SourceMappingId = mapping.Id, SourceChapterId = "c1", Group = "Publisher"
            });
            var item = new DownloadQueueItem
            {
                SeriesId = series.Id, ChapterId = chapter.Id, SourceMappingId = mapping.Id,
                SourceChapterId = "c1", QueuedAt = DateTime.UtcNow
            };
            db.DownloadQueue.Add(item);
            db.SaveChanges();
            itemId = item.Id;
            chapterId = chapter.Id;
        }

        var availability = new SourceAvailability(_settings, registry);
        var queue = new DownloadQueueService(
            _db.ScopeFactory(), TimeProvider.System, Sources.Resolver(registry, availability),
            NullLogger<DownloadQueueService>.Instance);
        using var batches = new DownloadBatchNotifier(
            new RecordingNotifications(), new RecordingInbox(), new TestLocalizer(),
            new TestUserLocaleResolver(), TimeProvider.System, NullLogger<DownloadBatchNotifier>.Instance);
        using (var db = _db.NewContext())
        {
            var processor = new ChapterDownloadProcessor(
                db, registry, Sources.Resolver(registry, availability),
                new PageDownloader(new StubHttpClientFactory(""), queue, TimeProvider.System, NullLogger<PageDownloader>.Instance),
                new EventBroadcaster(new NoopHubContext(), _db.ScopeFactory()),
                new AppPaths(),
                new KavitaScanService(new KavitaClient(new StubHttpClientFactory("{}")), _settings,
                    _db.ScopeFactory(), NullLogger<KavitaScanService>.Instance),
                queue, new RecordingInbox(), new StatsEventService(db), new RecordingNotifications(), batches,
                availability, new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance),
                new NamingService(_settings), TestQuality.Create(registry), new TestLocalizer(),
                new TestUserLocaleResolver(), NullLogger<ChapterDownloadProcessor>.Instance);

            Assert.Equal(DownloadOutcome.Settled, await processor.ProcessAsync(itemId, CancellationToken.None));
        }

        using (var db = _db.NewContext())
        {
            Assert.Equal(QueueStatus.Completed, db.DownloadQueue.Single(q => q.Id == itemId).Status);
            var file = db.Chapters.Include(c => c.ChapterFile).Single(c => c.Id == chapterId).ChapterFile!;
            Assert.Equal(QualityTier.Official, file.Tier);
            Assert.Equal("Publisher", file.Group);
            Assert.Equal(3, file.PageCount);
            Assert.Equal(30, file.MedianWidth);
            Assert.Equal(50, file.MedianHeight);
            Assert.Equal("png", file.ImageFormat);
            Assert.NotNull(file.MeasuredAtUtc);
        }
    }
}
