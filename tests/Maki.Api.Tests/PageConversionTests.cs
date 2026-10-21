using System.IO.Compression;
using System.Security.Cryptography;
using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Images;
using Maki.Core.Kavita;
using Maki.Core.Quality;
using Maki.Core.Sources;
using Maki.Core.Storage;
using Maki.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public sealed class PageConversionTests : IDisposable
{
    private readonly UpgradeWorld _world = new UpgradeWorld().Seed();
    private static readonly PageConversionSettings Webp = new(PageConversionFormat.Webp, 80);

    public void Dispose() => _world.Dispose();

    /// <summary>Gradients with fine grain, which a JPEG at 95 stores at several bits per pixel.</summary>
    private static byte[] ScanJpeg(int width, int height, int seed, int quality = 95)
    {
        var image = new Raster(width, height, bands: 3);
        var random = new Random(seed);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var tone = (byte)Math.Clamp((x + y) * 200 / (width + height) + random.Next(24), 0, 255);
            image[x, y] = new Rgba(tone, tone, tone);
        }

        return image.EncodeJpeg(quality);
    }

    private static void WriteJpegCbz(string path, int pages, int quality = 95)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("ComicInfo.xml").Open()))
        {
            writer.Write("<ComicInfo><Series>Series</Series></ComicInfo>");
        }

        for (var i = 0; i < pages; i++)
        {
            using var stream = zip.CreateEntry($"{i + 1:000}.jpg", CompressionLevel.NoCompression).Open();
            stream.Write(ScanJpeg(500, 750, i, quality));
        }
    }

    /// <summary>A chapter with a measured JPEG archive on disk, as a download would have left it.</summary>
    private (int ChapterId, int FileId, string Path) JpegChapter(decimal number, int quality = 95)
    {
        var (chapterId, fileId) = _world.Chapter(number, f => f.ImageFormat = "jpg");
        using var db = _world.Db.NewContext();
        var file = db.ChapterFiles.Single(f => f.Id == fileId);
        var path = Path.Combine(_world.Library, file.RelativePath);
        WriteJpegCbz(path, 3, quality);
        file.Size = new FileInfo(path).Length;
        file.PageCount = 3;
        file.MedianWidth = 500;
        file.MedianHeight = 750;
        db.SaveChanges();
        return (chapterId, fileId!.Value, path);
    }

    private PageConversionService Service(MakiDbContext db, Func<string, string, bool>? hardlink = null,
        Action<string, string>? moveOver = null,
        Func<string, string, PageConversionSettings, CancellationToken, Task<ArchiveConversionResult>>? converter = null) =>
        new(db, _world.Archives,
            new KavitaScanService(new KavitaClient(new StubHttpClientFactory("{}")), _world.Settings, _world.Db.ScopeFactory(),
                NullLogger<KavitaScanService>.Instance),
            NullLogger<PageConversionService>.Instance)
        {
            Hardlink = hardlink ?? FileLinker.TryHardlink,
            MoveOver = moveOver ?? ((from, to) => File.Move(from, to, overwrite: true)),
            Converter = converter ?? ArchivePageConverter.ConvertAsync
        };

    private string[] TmpFiles() =>
        Directory.Exists(Path.Combine(_world.Library, ".maki", "tmp"))
            ? Directory.GetFiles(Path.Combine(_world.Library, ".maki", "tmp"))
            : [];

    private async Task<FileConversion> ConvertAsync(int fileId)
    {
        using var db = _world.Db.NewContext();
        return await Service(db).ConvertAsync(fileId, Webp, CancellationToken.None);
    }

    private ChapterFile Row(int fileId)
    {
        using var db = _world.Db.NewContext();
        return db.ChapterFiles.AsNoTracking().Single(f => f.Id == fileId);
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private string[] TrashedOriginals() =>
        Directory.Exists(UpgradeTrash.SeriesFolder(_world.Library, _world.SeriesId))
            ? Directory.GetFiles(UpgradeTrash.SeriesFolder(_world.Library, _world.SeriesId), PageConversionService.TrashPrefix + "*")
            : [];

    private void EnableConversion(string format = "webp")
    {
        using var db = _world.Db.NewContext();
        db.AppConfig.Add(new AppConfigEntry { Key = SettingKeys.PageConversionFormat, Value = format });
        db.SaveChanges();
    }

    [Fact]
    public async Task A_library_file_is_converted_in_place_and_its_original_waits_in_the_upgrade_trash()
    {
        var (_, fileId, path) = JpegChapter(1);
        var before = Row(fileId);
        var originalHash = Hash(path);

        var result = await ConvertAsync(fileId);

        Assert.Equal(FileConversionStatus.Converted, result.Status);
        var after = Row(fileId);
        Assert.Equal(new FileInfo(path).Length, after.Size);
        Assert.True(after.Size < before.Size);
        Assert.Equal(before.Size - after.Size, after.ConversionSavedBytes);
        Assert.Equal(before.Size, after.QualitySize);
        Assert.Equal(("jpg", "webp"), (after.OriginalImageFormat, after.ImageFormat));
        Assert.Equal("jpg", after.QualityImageFormat);
        Assert.NotNull(after.ConversionCheckedAtUtc);

        using (var zip = ZipFile.OpenRead(path))
        {
            Assert.Equal(["ComicInfo.xml", "001.webp", "002.webp", "003.webp"], zip.Entries.Select(e => e.FullName));
        }

        var trashed = Assert.Single(TrashedOriginals());
        Assert.StartsWith($"{PageConversionService.TrashPrefix}{fileId}-", Path.GetFileName(trashed));
        Assert.Equal(originalHash, Hash(trashed));
        Assert.Equal(1, HardLinkCount(path));

        Assert.Equal(FileConversionStatus.Unchanged, (await ConvertAsync(fileId)).Status);
        Assert.Single(TrashedOriginals());
    }

    private static int HardLinkCount(string path) => Maki.Core.Paths.HardLinks.Count(path) ?? 1;

    [Fact]
    public async Task A_converted_file_scores_exactly_as_it_did_before()
    {
        var (_, fileId, _) = JpegChapter(1);
        using (var db = _world.Db.NewContext())
        {
            var profile = db.UpgradeProfiles.Single();
            profile.CompressionWeight = 20;
            profile.ResolutionWeight = 10;
            db.SaveChanges();
        }

        async Task<int> ScoreAsync()
        {
            using var db = _world.Db.NewContext();
            var evaluator = await new UpgradeEvaluationService(db, TestQuality.Create(_world.Registry))
                .ForSeriesAsync(_world.SeriesId, CancellationToken.None);
            return evaluator!.Evaluate(db.ChapterFiles.Single(f => f.Id == fileId), "en")!.Value.Score.Score;
        }

        var before = await ScoreAsync();
        Assert.Equal(FileConversionStatus.Converted, (await ConvertAsync(fileId)).Status);

        Assert.Equal(before, await ScoreAsync());

        var row = Row(fileId);
        var snapshot = UpgradeEvaluator.Snapshot(row, 0);
        Assert.Equal(("jpg", row.QualitySize), (snapshot.ImageFormat, snapshot.SizeBytes));
        Assert.Equal(("webp", row.ConversionSavedBytes), (snapshot.StoredImageFormat, snapshot.ConversionSavedBytes));
    }

    [Fact]
    public void A_revert_puts_a_converted_files_record_back_with_its_bytes()
    {
        var snapshot = new QualitySnapshot { ImageFormat = "jpg", SizeBytes = 900, StoredImageFormat = "webp", ConversionSavedBytes = 400 };
        var file = new ChapterFile { ImageFormat = "png", OriginalImageFormat = "png", ConversionSavedBytes = 7 };

        snapshot.RestoreFormatOnto(file, DateTime.UtcNow);

        Assert.Equal(("webp", "jpg", 400L), (file.ImageFormat, file.OriginalImageFormat, file.ConversionSavedBytes));
        Assert.NotNull(file.ConversionCheckedAtUtc);

        new QualitySnapshot { ImageFormat = "png", SizeBytes = 900 }.RestoreFormatOnto(file, DateTime.UtcNow);

        Assert.Equal(("png", null, 0L), (file.ImageFormat, file.OriginalImageFormat, file.ConversionSavedBytes));
        Assert.Null(file.ConversionCheckedAtUtc);
    }

    [Fact]
    public async Task A_file_shared_with_a_torrent_clients_copy_is_left_alone()
    {
        var (_, fileId, path) = JpegChapter(1);
        var seeding = Path.Combine(_world.Root, "seeding.cbz");
        Assert.True(FileLinker.TryHardlink(path, seeding), "this filesystem cannot hardlink");
        var hash = Hash(path);

        var result = await ConvertAsync(fileId);

        Assert.Equal(FileConversionStatus.Hardlinked, result.Status);
        Assert.Equal(hash, Hash(path));
        Assert.Null(Row(fileId).OriginalImageFormat);
        Assert.NotNull(Row(fileId).ConversionCheckedAtUtc);
        Assert.Empty(TrashedOriginals());
    }

    [Fact]
    public async Task A_file_that_would_not_shrink_is_marked_checked_and_left_untouched()
    {
        var (_, fileId, path) = JpegChapter(1, quality: 35);
        var hash = Hash(path);

        using (var db = _world.Db.NewContext())
        {
            var result = await Service(db).ConvertAsync(fileId, new PageConversionSettings(PageConversionFormat.Webp, 100),
                CancellationToken.None);
            Assert.Equal((FileConversionStatus.Unchanged, ArchiveConversionOutcome.NotSmaller), (result.Status, result.Outcome));
        }

        Assert.Equal(hash, Hash(path));
        Assert.NotNull(Row(fileId).ConversionCheckedAtUtc);
        Assert.Equal(0, Row(fileId).ConversionSavedBytes);
        Assert.Empty(TrashedOriginals());
    }

    [Fact]
    public async Task A_series_with_a_download_in_flight_is_passed_over_for_now()
    {
        var (chapterId, fileId, path) = JpegChapter(1);
        using (var db = _world.Db.NewContext())
        {
            db.DownloadQueue.Add(new DownloadQueueItem
            {
                SeriesId = _world.SeriesId, ChapterId = chapterId, Status = QueueStatus.Downloading,
                QueuedAt = DateTime.UtcNow, SourceMappingId = _world.AggMappingId
            });
            db.SaveChanges();
        }

        var hash = Hash(path);
        Assert.Equal(FileConversionStatus.Busy, (await ConvertAsync(fileId)).Status);
        Assert.Equal(hash, Hash(path));
        Assert.Null(Row(fileId).ConversionCheckedAtUtc);
    }

    [Fact]
    public async Task A_new_download_is_converted_and_recorded_as_the_jpeg_the_source_served()
    {
        EnableConversion();
        Directory.CreateDirectory(_world.Library);
        var (chapterId, _) = _world.Chapter(1, withFile: false);
        var jpegs = Enumerable.Range(0, 3).Select(i => ScanJpeg(500, 750, i)).ToList();
        _world.OfficialPages = c => new ChapterPages(
            [.. jpegs.Select((bytes, i) => new PageRequest($"https://official.test/{c.SourceChapterId}/{i}.jpg", Data: bytes))]);
        int itemId;
        using (var db = _world.Db.NewContext())
        {
            var item = new DownloadQueueItem
            {
                SeriesId = _world.SeriesId, ChapterId = chapterId, SourceMappingId = _world.OfficialMappingId,
                SourceChapterId = "o1", QueuedAt = DateTime.UtcNow, Origin = DownloadOrigin.Manual
            };
            db.DownloadQueue.Add(item);
            db.SaveChanges();
            itemId = item.Id;
        }

        await _world.ProcessAsync(itemId);

        using var check = _world.Db.NewContext();
        Assert.Equal(QueueStatus.Completed, check.DownloadQueue.Single(q => q.Id == itemId).Status);
        var file = check.Chapters.Include(c => c.ChapterFile).Single(c => c.Id == chapterId).ChapterFile!;
        Assert.Equal(("jpg", "webp"), (file.OriginalImageFormat, file.ImageFormat));
        Assert.True(file.ConversionSavedBytes > 0);
        Assert.Equal(new FileInfo(Path.Combine(_world.Library, file.RelativePath)).Length, file.Size);
        var sample = check.SourceQualitySamples.Single(s => s.ChapterId == chapterId);
        Assert.Equal(("jpg", file.QualitySize), (sample.ImageFormat, sample.SizeBytes));
        Assert.Empty(TrashedOriginals());
        Assert.False(File.Exists(Path.Combine(_world.Library, ".maki", "tmp", $"{itemId}.cbz.converted")));
    }

    [Fact]
    public async Task An_upgrade_is_judged_on_the_pages_as_downloaded_and_stored_converted()
    {
        EnableConversion();
        var (chapterId, fileId) = _world.Chapter(1, onDisk: true);
        var jpegs = Enumerable.Range(0, 20).Select(i => ScanJpeg(1500, 2250, i, quality: 92)).ToList();
        _world.OfficialPages = c => new ChapterPages(
            [.. jpegs.Select((bytes, i) => new PageRequest($"https://official.test/{c.SourceChapterId}/{i}.jpg", Data: bytes))]);
        var itemId = _world.QueueUpgrade(chapterId, fileId!.Value);

        await _world.ProcessAsync(itemId);

        using var check = _world.Db.NewContext();
        Assert.Equal(QueueStatus.Completed, check.DownloadQueue.Single(q => q.Id == itemId).Status);
        var file = check.ChapterFiles.Single(f => f.Id == fileId);
        Assert.Equal(("jpg", "webp"), (file.OriginalImageFormat, file.ImageFormat));
        var history = check.UpgradeHistory.Single();
        var after = QualitySnapshot.Parse(history.AfterJson)!;
        Assert.Equal(("jpg", file.QualitySize), (after.ImageFormat, after.SizeBytes));
        using var zip = ZipFile.OpenRead(Path.Combine(_world.Library, file.RelativePath));
        Assert.All(Maki.Core.Reading.CbzReader.PageNames(zip), n => Assert.EndsWith(".webp", n));
    }

    [Fact]
    public async Task The_library_pass_converts_what_is_pending_and_records_its_totals()
    {
        EnableConversion();
        var (_, convertible, _) = JpegChapter(1);
        var (_, stubborn, _) = JpegChapter(2, quality: 35);
        var settings = new SettingsService(_world.Db.ScopeFactory());
        var services = new ServiceCollection();
        services.AddScoped(_ => _world.Db.NewContext());
        services.AddScoped(sp => Service(sp.GetRequiredService<MakiDbContext>()));
        var worker = new PageConversionWorker(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            settings, NullLogger<PageConversionWorker>.Instance) { DelayBetweenFiles = TimeSpan.Zero };

        await worker.StartPassAsync(CancellationToken.None);
        await worker.RunPassAsync(CancellationToken.None);

        Assert.NotNull(Row(convertible).OriginalImageFormat);
        Assert.NotNull(Row(stubborn).ConversionCheckedAtUtc);
        var snapshot = await worker.SnapshotAsync(CancellationToken.None);
        Assert.Equal(PageConversionPassState.Idle, snapshot.State);
        Assert.NotNull(snapshot.Pass!.FinishedAtUtc);
        Assert.True(snapshot.Pass.Converted >= 1);
        Assert.True(snapshot.Pass.SavedBytes > 0);
        using var db = _world.Db.NewContext();
        Assert.Equal(0, await PageConversionWorker.PendingQuery(db).CountAsync());
    }

    [Fact]
    public async Task A_paused_pass_stops_before_the_next_file()
    {
        EnableConversion();
        var (_, fileId, _) = JpegChapter(1);
        var settings = new SettingsService(_world.Db.ScopeFactory());
        var worker = new PageConversionWorker(_world.Db.ScopeFactory(), settings, NullLogger<PageConversionWorker>.Instance);

        await worker.StartPassAsync(CancellationToken.None);
        await worker.PauseAsync(CancellationToken.None);
        await worker.RunPassAsync(CancellationToken.None);

        Assert.Null(Row(fileId).ConversionCheckedAtUtc);
        Assert.Equal(PageConversionPassState.Paused, (await worker.SnapshotAsync(CancellationToken.None)).State);
    }

    private PageConversionController Controller(MakiDbContext db) => new(db, new SettingsService(_world.Db.ScopeFactory()),
        new PageConversionWorker(_world.Db.ScopeFactory(), new SettingsService(_world.Db.ScopeFactory()),
            NullLogger<PageConversionWorker>.Instance),
        new TestLocalizer());

    [Fact]
    public async Task Changing_the_format_puts_files_left_as_they_were_back_in_line_but_not_converted_ones()
    {
        var (_, converted, _) = JpegChapter(1);
        var (_, untouched, _) = JpegChapter(2);
        using (var db = _world.Db.NewContext())
        {
            var now = DateTime.UtcNow;
            var a = db.ChapterFiles.Single(f => f.Id == converted);
            (a.ConversionCheckedAtUtc, a.OriginalImageFormat, a.ConversionSavedBytes) = (now, "jpg", 10);
            db.ChapterFiles.Single(f => f.Id == untouched).ConversionCheckedAtUtc = now;
            db.SaveChanges();
        }

        using (var db = _world.Db.NewContext())
        {
            var result = await Controller(db).Save(new PageConversionController.SettingsDto("avif", 80, 60), CancellationToken.None);
            var status = Assert.IsType<PageConversionController.StatusDto>(Assert.IsType<OkObjectResult>(result).Value);
            Assert.Equal(new PageConversionController.SettingsDto("avif", 80, 60), status.Settings);
            Assert.Equal(1, status.Library.ConvertedFiles);
        }

        Assert.NotNull(Row(converted).ConversionCheckedAtUtc);
        Assert.Null(Row(untouched).ConversionCheckedAtUtc);
    }

    [Fact]
    public async Task Bad_settings_and_a_pass_with_conversion_off_are_refused()
    {
        using var db = _world.Db.NewContext();
        var controller = Controller(db);

        Assert.IsType<BadRequestObjectResult>(
            await controller.Save(new PageConversionController.SettingsDto("jxl", 80, 55), CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(
            await controller.Save(new PageConversionController.SettingsDto("webp", 0, 55), CancellationToken.None));
        Assert.IsType<ConflictObjectResult>(await controller.Start(CancellationToken.None));
    }

    [Fact]
    public async Task New_bytes_from_an_upgrade_forget_the_old_conversion()
    {
        var (chapterId, fileId, _) = JpegChapter(1);
        Assert.Equal(FileConversionStatus.Converted, (await ConvertAsync(fileId)).Status);
        var pngs = Enumerable.Range(0, 20).Select(_ => TestQuality.Png(1500, 2250)).ToList();
        _world.OfficialPages = c => new ChapterPages(
            [.. pngs.Select((bytes, i) => new PageRequest($"https://official.test/{c.SourceChapterId}/{i}.png", Data: bytes))]);
        using (var db = _world.Db.NewContext())
        {
            db.ChapterFiles.Single(f => f.Id == fileId).PageCount = 20;
            db.SaveChanges();
        }

        await _world.ProcessAsync(_world.QueueUpgrade(chapterId, fileId));

        var row = Row(fileId);
        Assert.Equal(UpgradeWorld.Official, row.SourceName);
        Assert.Equal((null, 0L, (DateTime?)null), (row.OriginalImageFormat, row.ConversionSavedBytes, row.ConversionCheckedAtUtc));
        var before = QualitySnapshot.Parse(Assert.Single(_world.Db.NewContext().UpgradeHistory.ToList()).BeforeJson)!;
        Assert.Equal(("jpg", "webp"), (before.ImageFormat, before.StoredImageFormat));
    }

    [Fact]
    public async Task Without_hardlinks_the_original_is_copied_into_the_trash_and_the_copy_renamed_over_it()
    {
        var (_, fileId, path) = JpegChapter(1);
        var originalHash = Hash(path);

        using (var db = _world.Db.NewContext())
        {
            var result = await Service(db, hardlink: (_, _) => false).ConvertAsync(fileId, Webp, CancellationToken.None);
            Assert.Equal(FileConversionStatus.Converted, result.Status);
        }

        Assert.Equal(originalHash, Hash(Assert.Single(TrashedOriginals())));
        Assert.Equal(new FileInfo(path).Length, Row(fileId).Size);
        using (var zip = ZipFile.OpenRead(path))
        {
            Assert.All(Maki.Core.Reading.CbzReader.PageNames(zip), n => Assert.EndsWith(".webp", n));
        }

        Assert.Empty(TmpFiles());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_rename_that_fails_leaves_the_original_in_place_and_nothing_in_the_trash(bool hardlinks)
    {
        var (_, fileId, path) = JpegChapter(1);
        var before = Row(fileId);
        var originalHash = Hash(path);

        using (var db = _world.Db.NewContext())
        {
            var service = Service(db, hardlink: hardlinks ? null : (_, _) => false,
                moveOver: (_, _) => throw new IOException("the share went away"));
            Assert.Equal(FileConversionStatus.Failed, (await service.ConvertAsync(fileId, Webp, CancellationToken.None)).Status);
        }

        Assert.Equal(originalHash, Hash(path));
        Assert.Empty(TrashedOriginals());
        var after = Row(fileId);
        Assert.Equal((before.Size, before.ImageFormat), (after.Size, after.ImageFormat));
        Assert.Null(after.ConversionCheckedAtUtc);
        Assert.Empty(TmpFiles());
    }

    [Fact]
    public async Task A_rename_that_reports_a_failure_but_happened_is_recorded_as_converted()
    {
        var (_, fileId, path) = JpegChapter(1);

        using (var db = _world.Db.NewContext())
        {
            var service = Service(db, hardlink: (_, _) => false, moveOver: (from, to) =>
            {
                File.Move(from, to, overwrite: true);
                throw new IOException("a retransmitted rename");
            });
            Assert.Equal(FileConversionStatus.Converted, (await service.ConvertAsync(fileId, Webp, CancellationToken.None)).Status);
        }

        var row = Row(fileId);
        Assert.Equal(new FileInfo(path).Length, row.Size);
        Assert.Equal("webp", row.ImageFormat);
        Assert.Single(TrashedOriginals());
    }

    [Fact]
    public async Task The_library_file_can_be_renamed_while_its_pages_are_being_converted()
    {
        var (_, fileId, path) = JpegChapter(1);

        using (var db = _world.Db.NewContext())
        {
            var service = Service(db, converter: async (source, target, settings, ct) =>
            {
                using (ZipFile.OpenRead(source))
                {
                    File.Move(path, path + ".renamed");
                    File.Move(path + ".renamed", path);
                }

                return await ArchivePageConverter.ConvertAsync(source, target, settings, ct);
            });
            Assert.Equal(FileConversionStatus.Converted, (await service.ConvertAsync(fileId, Webp, CancellationToken.None)).Status);
        }

        Assert.Empty(TmpFiles());
    }
}
