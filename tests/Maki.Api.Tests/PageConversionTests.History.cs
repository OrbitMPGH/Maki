using System.IO.Compression;
using Maki.Api.Controllers;
using Maki.Api.Jobs;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Images;
using Maki.Core.Kavita;
using Maki.Core.Quality;
using Maki.Core.Storage;
using Maki.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public sealed partial class PageConversionTests
{
    private static readonly PageConversionSettings High = new(PageConversionFormat.Webp, PageConversionStop.High, false);

    /// <summary>A measured JPEG chapter of <paramref name="pages"/> pages, so the middle third holds three.</summary>
    private (int ChapterId, int FileId, string Path) JpegFile(decimal number, int pages, Action<ChapterFile>? file = null,
        int seed = 0)
    {
        var (chapterId, fileId) = _world.Chapter(number, f =>
        {
            f.ImageFormat = "jpg";
            file?.Invoke(f);
        });
        using var db = _world.Db.NewContext();
        var row = db.ChapterFiles.Single(f => f.Id == fileId);
        var path = Path.Combine(_world.Library, row.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            for (var i = 0; i < pages; i++)
            {
                using var stream = zip.CreateEntry($"{i + 1:000}.jpg", CompressionLevel.NoCompression).Open();
                stream.Write(ScanJpeg(500, 750, seed * 100 + i));
            }
        }

        row.Size = new FileInfo(path).Length;
        row.PageCount = pages;
        row.MedianWidth = 500;
        row.MedianHeight = 750;
        db.SaveChanges();
        return (chapterId, fileId!.Value, path);
    }

    private async Task ConvertAsync(int fileId, string? passId)
    {
        using var db = _world.Db.NewContext();
        var result = await Service(db).ConvertAsync(fileId, PageConversionPlan.For(Webp), new PageConversionPassCache { PassId = passId },
            CancellationToken.None);
        Assert.Equal(FileConversionStatus.Converted, result.Status);
    }

    private PageConversionHistory History(int fileId)
    {
        using var db = _world.Db.NewContext();
        return db.PageConversionHistory.AsNoTracking().Where(h => h.ChapterFileId == fileId).OrderBy(h => h.Id).Last();
    }

    [Fact]
    public async Task A_converted_file_records_its_original_in_a_history_row()
    {
        var (chapterId, fileId, path) = JpegChapter(1);
        var before = Row(fileId).Size;

        await ConvertAsync(fileId, "2026-10-26T10:00:00.0000000Z");

        var row = History(fileId);
        var after = Row(fileId);
        Assert.Equal((_world.SeriesId, (int?)chapterId, "2026-10-26T10:00:00.0000000Z"), (row.SeriesId, row.ChapterId, row.PassId));
        Assert.Equal(Path.GetFullPath(Assert.Single(TrashedOriginals())), Path.GetFullPath(Path.Combine(_world.Library, row.TrashPath!)));
        Assert.StartsWith($".maki-trash/{_world.SeriesId}/conv-{fileId}-", row.TrashPath);
        Assert.Equal((before, before, before), (row.TrashBytes!.Value, row.BytesBefore, new FileInfo(Path.Combine(_world.Library, row.TrashPath!)).Length));
        Assert.Equal((after.Size, new FileInfo(path).Length), (row.BytesAfter, row.BytesAfter));
        Assert.Equal((Webp.Name(), "jpg", "webp"), (row.Action, row.SourceFormat, row.ResultFormat));
        Assert.Null(row.UndoneAtUtc);

        using var db = _world.Db.NewContext();
        var library = Saved(await Controller(db).Status(CancellationToken.None)).Library;
        Assert.Equal((1, before), (library.TrashFiles, library.TrashBytes));
    }

    [Fact]
    public async Task The_trash_purge_ages_conversion_rows_and_keeps_originals_a_row_still_names()
    {
        var (_, oldId, _) = JpegChapter(1);
        var (_, freshId, _) = JpegChapter(2);
        await ConvertAsync(oldId, null);
        await ConvertAsync(freshId, null);
        var oldTrash = Path.Combine(_world.Library, History(oldId).TrashPath!);
        var freshTrash = Path.Combine(_world.Library, History(freshId).TrashPath!);
        var stray = Path.Combine(UpgradeTrash.SeriesFolder(_world.Library, _world.SeriesId), "conv-999-stray.cbz");
        File.WriteAllBytes(stray, [1, 2, 3]);
        foreach (var file in new[] { oldTrash, freshTrash, stray })
        {
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-40));
        }

        using (var db = _world.Db.NewContext())
        {
            db.PageConversionHistory.Single(h => h.ChapterFileId == oldId).CreatedAtUtc = DateTime.UtcNow.AddDays(-40);
            db.SaveChanges();
        }

        using (var db = _world.Db.NewContext())
        {
            await new UpgradeTrashService(db, _world.Settings, NullLogger<UpgradeTrashService>.Instance).PurgeAsync(CancellationToken.None);
        }

        var aged = History(oldId);
        Assert.Equal((null, null), (aged.TrashPath, aged.TrashBytes));
        Assert.False(File.Exists(oldTrash));
        Assert.NotNull(History(freshId).TrashPath);
        Assert.True(File.Exists(freshTrash));
        Assert.False(File.Exists(stray));
    }

    private sealed class StubLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => _stopping.Cancel();
    }

    private PageConversionEstimateService Estimates(Func<CancellationToken, Task>? beforeFile = null,
        IHostApplicationLifetime? lifetime = null) =>
        new(_world.Db.ScopeFactory(), _world.Settings, lifetime ?? new StubLifetime(),
            NullLogger<PageConversionEstimateService>.Instance)
        {
            BeforeFile = beforeFile ?? (_ => Task.CompletedTask)
        };

    private static PageConversionController.PlanDto EstimatePlan(string stop = "high") => new(
        new PageConversionController.SettingsDto("webp", stop, false),
        [
            new PageConversionController.RuleDto(null, "Official", new PageConversionConditions(Tiers: ["official"]), "skip", null),
            new PageConversionController.RuleDto(null, "Volumes", new PageConversionConditions(Tiers: ["volume"]), "convert", "balanced")
        ]);

    private static PageConversionEstimateService.EstimateDto Estimate(IActionResult result) =>
        Assert.IsType<PageConversionEstimateService.EstimateDto>(Assert.IsType<OkObjectResult>(result).Value);

    private int OtherSeries(string title, Action<Series>? series = null, long size = 4000, string format = "jpg")
    {
        using var db = _world.Db.NewContext();
        var row = new Series
        {
            Title = title, SortTitle = title.ToLowerInvariant(), FolderName = title,
            RootFolderId = db.Series.Single(s => s.Id == _world.SeriesId).RootFolderId
        };
        series?.Invoke(row);
        db.Series.Add(row);
        db.SaveChanges();
        db.ChapterFiles.Add(new ChapterFile
        {
            SeriesId = row.Id, RelativePath = $"{title}/{title} 001.cbz", SourceName = "agg", Size = size, ImageFormat = format
        });
        db.SaveChanges();
        return row.Id;
    }

    [Fact]
    public async Task An_estimate_samples_each_bucket_and_counts_what_the_pass_would_leave()
    {
        JpegFile(1, 9, f => f.Tier = QualityTier.Official, seed: 1);
        JpegFile(2, 9, f => f.Tier = QualityTier.Official, seed: 2);
        var volumes = new[] { JpegFile(3, 9, f => f.Tier = QualityTier.Volume, seed: 3), JpegFile(4, 9, f => f.Tier = QualityTier.Volume, seed: 4) };
        var (_, seeded, seededPath) = JpegFile(5, 9, seed: 5);
        JpegFile(6, 9, seed: 6);
        Assert.True(FileLinker.TryHardlink(seededPath, seededPath + ".seed"));
        _world.Chapter(7, f => f.ImageFormat = "webp");
        _world.Chapter(8, f => (f.ConversionCheckedAtUtc, f.CheckedWith, f.ImageFormat) = (DateTime.UtcNow, PlanAction.UndoneName, "jpg"));
        _world.Chapter(9, f => (f.OriginalImageFormat, f.ImageFormat, f.CheckedWith, f.ConversionSavedBytes) = ("jpg", "webp", "webp:high", 500));
        _world.Chapter(10, f => (f.OriginalImageFormat, f.ImageFormat, f.ConversionSavedBytes) = ("png", "webp", 300));
        _world.Chapter(11, f => (f.OriginalImageFormat, f.ImageFormat, f.CheckedWith, f.ConversionSavedBytes) = ("jpg", "webp", "webp:high", 700));
        OtherSeries("Never", s => s.PageConversion = PageConversionOverride.Never, size: 6000);
        OtherSeries("Importing", s => s.PendingImportLink = PendingImportLink.Link);
        var busy = OtherSeries("Busy");
        using (var db = _world.Db.NewContext())
        {
            db.DownloadQueue.Add(new DownloadQueueItem { SeriesId = busy, Status = QueueStatus.Downloading, QueuedAt = DateTime.UtcNow });
            db.PageConversionHistory.AddRange(
                new PageConversionHistory { SeriesId = _world.SeriesId, ChapterFileId = 9001, TrashPath = ".maki-trash/x", Action = "webp:high" },
                new PageConversionHistory
                {
                    SeriesId = _world.SeriesId, ChapterFileId = 9002, Action = "webp:high", UndoneAtUtc = DateTime.UtcNow
                });
            db.SaveChanges();
        }

        var service = Estimates();
        using (var db = _world.Db.NewContext())
        {
            var started = Estimate(Controller(db).StartEstimate(EstimatePlan(), service));
            Assert.Equal("running", started.State);
        }

        await service.Running;
        var estimate = service.Current;
        Assert.Equal(("done", 4, 4), (estimate.State, estimate.Done, estimate.Total));
        var result = estimate.Result!;

        Assert.Equal(2, result.Rules.Count);
        var skipped = result.Rules[0];
        Assert.True(skipped.Skip);
        Assert.Equal((2, 0), (skipped.Files, skipped.SampledFiles));

        var volume = result.Rules[1];
        Assert.False(volume.Skip);
        Assert.Equal((2, 2, 0L), (volume.Files, volume.SampledFiles, volume.HardlinkedBytes));
        Assert.True(volume.ExpectedLow <= volume.ExpectedHigh && volume.ExpectedHigh < volume.Bytes);
        Assert.True(volume.SecondsLow <= volume.SecondsHigh && volume.SecondsHigh > 0);
        long real = 0;
        foreach (var (_, _, path) in volumes)
        {
            var target = Path.Combine(_world.Root, $"real-{Path.GetFileName(path)}");
            var converted = await ArchivePageConverter.ConvertAsync(path, target,
                new PageConversionSettings(PageConversionFormat.Webp, PageConversionStop.Balanced, false), CancellationToken.None);
            real += converted.Outcome == ArchiveConversionOutcome.Converted ? converted.ResultBytes : converted.SourceBytes;
        }

        Assert.InRange(real, volume.ExpectedLow * 0.9, volume.ExpectedHigh * 1.1);

        var fallthrough = result.Default;
        Assert.Equal((2, 2), (fallthrough.Files, fallthrough.SampledFiles));
        Assert.Equal(fallthrough.Bytes, fallthrough.ExpectedHigh);
        Assert.InRange(fallthrough.HardlinkedBytes, fallthrough.Bytes / 3, fallthrough.Bytes * 2 / 3);
        Assert.Equal((0, 0, 0L), (result.Overrides.Files, result.Overrides.SampledFiles, result.Overrides.ExpectedLow));

        var over = result.PassedOver;
        Assert.Equal((1, fallthrough.HardlinkedBytes), (over.HardlinkedFiles, over.HardlinkedBytes));
        Assert.Equal((0, 1, 1, 1), (over.CleanupDue, over.AlreadyCompact, over.DownloadInFlight, over.PendingImport));
        Assert.Equal((2, skipped.Bytes), (over.SkippedByRule, over.SkippedByRuleBytes));
        Assert.Equal((1, 1, 6000L, 1), (over.NeverSeries, over.NeverFiles, over.NeverBytes, over.Undone));
        Assert.Equal((0, 0L), (over.AlreadyChecked, over.AlreadyCheckedBytes));

        Assert.Equal(
        [
            new PageConversionEstimateService.ConvertedEarlier("webp:high", 2, 1200),
            new PageConversionEstimateService.ConvertedEarlier("unknown", 1, 300)
        ], result.ConvertedEarlier);
        Assert.Equal(0, result.ConvertedWithOriginalInTrash);
        Assert.True(result.MsPerMegapixel > 0);
        Assert.True(HardLinkCount(seededPath) > 1);
        Assert.Null(Row(seeded).ConversionCheckedAtUtc);
    }

    [Fact]
    public async Task A_second_estimate_cancels_the_first_and_the_same_plan_hashes_the_same()
    {
        JpegFile(1, 9, seed: 1);
        var held = new TaskCompletionSource();
        var first = 1;
        var service = Estimates(async ct =>
        {
            if (Interlocked.Exchange(ref first, 0) == 1)
            {
                held.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
        });

        using var db = _world.Db.NewContext();
        var controller = Controller(db);
        var a = Estimate(controller.StartEstimate(EstimatePlan("high"), service));
        await held.Task;
        var firstRun = service.Running;
        Assert.Equal(a.PlanHash, Estimate(controller.StartEstimate(EstimatePlan("high"), service)).PlanHash);
        Assert.Same(firstRun, service.Running);

        var b = Estimate(controller.StartEstimate(EstimatePlan("compact"), service));
        Assert.NotEqual(a.PlanHash, b.PlanHash);
        await firstRun.WaitAsync(TimeSpan.FromSeconds(30));
        await service.Running.WaitAsync(TimeSpan.FromSeconds(60));

        var done = Estimate(controller.Estimate(service));
        Assert.Equal(("done", b.PlanHash, 1), (done.State, done.PlanHash, done.Total));
        Assert.Equal(PageConversionEstimateService.Hash(new PageConversionPlan(PageConversionFormat.Webp, PageConversionStop.Compact, false,
                [
                    new PlanRule(0, "Renamed", new PageConversionConditions(Tiers: ["official"]), true, null),
                    new PlanRule(1, "", new PageConversionConditions(Tiers: ["volume"]), false, PageConversionStop.Balanced)
                ])),
            done.PlanHash);

        Assert.IsType<BadRequestObjectResult>(controller.StartEstimate(
            new PageConversionController.PlanDto(new PageConversionController.SettingsDto("webp", "best", false), null), service));
        Assert.Equal(b.PlanHash, Estimate(controller.Estimate(service)).PlanHash);
    }

    [Fact]
    public async Task An_estimate_leaves_out_files_the_pass_already_decided_under_the_same_answer()
    {
        var checkedAt = DateTime.UtcNow.AddDays(-3);
        JpegFile(1, 9, seed: 1);
        _world.Chapter(2, f => (f.ImageFormat, f.ConversionCheckedAtUtc, f.CheckedWith) = ("jpg", checkedAt, High.Name()));
        _world.Chapter(3, f => (f.ImageFormat, f.ConversionCheckedAtUtc, f.CheckedWith) = ("jpg", checkedAt, PlanAction.HardSkipName));
        _world.Chapter(4, f => (f.ImageFormat, f.ConversionCheckedAtUtc, f.CheckedWith) = ("jpg", checkedAt, "webp:compact"));
        _world.Chapter(5, f => (f.ImageFormat, f.ConversionCheckedAtUtc, f.CheckedWith, f.Tier) =
            ("jpg", checkedAt, High.Name(), QualityTier.Official));

        var service = Estimates();
        using (var db = _world.Db.NewContext())
        {
            Estimate(Controller(db).StartEstimate(EstimatePlan(), service));
        }

        await service.Running;
        var result = service.Current.Result!;
        Assert.Equal((2, 1), (result.Default.Files, result.Default.SampledFiles));
        Assert.Equal((2, 2000L), (result.PassedOver.AlreadyChecked, result.PassedOver.AlreadyCheckedBytes));
        Assert.Equal(1, result.PassedOver.SkippedByRule);

        using var scope = _world.Db.NewContext();
        var scoped = await new PageConversionPlanService(scope).ScopeAsync(
            new PageConversionPlan(PageConversionFormat.Webp, PageConversionStop.High, false,
            [
                new PlanRule(0, "Official", new PageConversionConditions(Tiers: ["official"]), true, null),
                new PlanRule(1, "Volumes", new PageConversionConditions(Tiers: ["volume"]), false, PageConversionStop.Balanced)
            ]), CancellationToken.None);
        Assert.Equal(4, scoped.Default.Files);
    }

    [Fact]
    public async Task Shutting_down_cancels_a_running_estimate()
    {
        JpegFile(1, 9, seed: 1);
        var held = new TaskCompletionSource();
        var lifetime = new StubLifetime();
        var service = Estimates(async ct =>
        {
            held.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }, lifetime);

        using (var db = _world.Db.NewContext())
        {
            Estimate(Controller(db).StartEstimate(EstimatePlan(), service));
        }

        await held.Task;
        Assert.Equal(1, service.Current.Total);
        lifetime.StopApplication();
        await service.Running.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("cancelled", service.Current.State);
        Assert.Null(service.Current.Error);
    }

    private (PageConversionUndoService Undo, KavitaScanService Kavita, PageConversionWorker Worker) Undo(MakiDbContext db,
        Func<string, string, bool>? move = null)
    {
        var kavita = new KavitaScanService(new KavitaClient(new StubHttpClientFactory("{}")), _world.Settings,
            _world.Db.ScopeFactory(), NullLogger<KavitaScanService>.Instance);
        var worker = new PageConversionWorker(_world.Db.ScopeFactory(), new SettingsService(_world.Db.ScopeFactory()),
            NullLogger<PageConversionWorker>.Instance);
        return (new PageConversionUndoService(db, _world.Archives, kavita, worker, new PageConversionPlanService(db),
            new TestLocalizer(), NullLogger<PageConversionUndoService>.Instance) { MoveFile = move }, kavita, worker);
    }

    [Fact]
    public async Task Undo_puts_the_original_back_and_stamps_the_file_undone()
    {
        var (_, fileId, path) = JpegChapter(1);
        var original = File.ReadAllBytes(path);
        var before = Row(fileId);
        await ConvertAsync(fileId, null);
        var converted = Row(fileId);
        await _world.Archives.GetAsync(fileId, converted.Size, path);
        var loads = _world.Archives.Loads;

        using var db = _world.Db.NewContext();
        var (undo, kavita, _) = Undo(db);
        var result = await undo.UndoAsync(_world.SeriesId, setNever: false, convertAgain: false, CancellationToken.None);

        Assert.Equal((1, 0), (result!.Restored, result.Skipped));
        Assert.Empty(result.Reasons);
        Assert.Equal(original, File.ReadAllBytes(path));
        var row = Row(fileId);
        Assert.Equal(("jpg", (string?)null, 0L, before.Size), (row.ImageFormat, row.OriginalImageFormat, row.ConversionSavedBytes, row.Size));
        Assert.Equal(PlanAction.UndoneName, row.CheckedWith);
        Assert.NotNull(row.ConversionCheckedAtUtc);
        Assert.Equal(before.MeasuredAtUtc, row.MeasuredAtUtc);
        var history = History(fileId);
        Assert.NotNull(history.UndoneAtUtc);
        Assert.Equal((null, null), (history.TrashPath, history.TrashBytes));
        Assert.Empty(TrashedOriginals());
        Assert.DoesNotContain(TmpFiles(), f => Path.GetFileName(f).StartsWith(PageConversionUndoService.AsidePrefix));
        Assert.Contains(Path.Combine(_world.Library, "Series"), kavita.PendingScans);
        await _world.Archives.GetAsync(fileId, converted.Size, path);
        Assert.Equal(loads + 1, _world.Archives.Loads);

        using var status = _world.Db.NewContext();
        Assert.Equal(0, Saved(await Controller(status).Status(CancellationToken.None)).Library.TrashFiles);
        Assert.Equal(PageConversionOverride.Default, status.Series.Single(s => s.Id == _world.SeriesId).PageConversion);
    }

    [Fact]
    public async Task Undo_passes_over_a_changed_file_a_missing_original_and_a_series_with_a_download_in_flight()
    {
        var (_, changed, changedPath) = JpegChapter(1);
        var (_, gone, _) = JpegChapter(2);
        var (_, kept, keptPath) = JpegChapter(3);
        foreach (var id in new[] { changed, gone, kept })
        {
            await ConvertAsync(id, null);
        }

        await File.AppendAllTextAsync(changedPath, "more");
        File.Delete(Path.Combine(_world.Library, History(gone).TrashPath!));
        var keptConverted = File.ReadAllBytes(keptPath);

        using (var db = _world.Db.NewContext())
        {
            db.DownloadQueue.Add(new DownloadQueueItem { SeriesId = _world.SeriesId, Status = QueueStatus.Packaging, QueuedAt = DateTime.UtcNow });
            db.SaveChanges();
            var blocked = await Undo(db).Undo.UndoAsync(_world.SeriesId, false, false, CancellationToken.None);
            Assert.Equal((0, 3), (blocked!.Restored, blocked.Skipped));
            Assert.Equal(["pageConversion.undo.downloadInFlight"], blocked.Reasons);
            Assert.Equal(keptConverted, File.ReadAllBytes(keptPath));
            db.DownloadQueue.RemoveRange(db.DownloadQueue.IgnoreQueryFilters());
            db.SaveChanges();
        }

        using (var db = _world.Db.NewContext())
        {
            var result = await Undo(db).Undo.UndoAsync(_world.SeriesId, false, false, CancellationToken.None);
            Assert.Equal((1, 2), (result!.Restored, result.Skipped));
            Assert.Contains(result.Reasons, r => r.StartsWith("pageConversion.undo.fileChanged") && r.Contains("Series 001.cbz"));
            Assert.Contains(result.Reasons, r => r.StartsWith("pageConversion.undo.trashGone") && r.Contains("Series 002.cbz"));
        }

        Assert.Equal(PlanAction.UndoneName, Row(kept).CheckedWith);
        Assert.Equal("webp", Row(changed).ImageFormat);
        Assert.NotNull(History(changed).TrashPath);
        Assert.Null(History(changed).UndoneAtUtc);
    }

    [Fact]
    public async Task Undo_moves_the_converted_copy_back_when_the_original_cannot_come_back()
    {
        var (_, fileId, path) = JpegChapter(1);
        await ConvertAsync(fileId, null);
        var converted = File.ReadAllBytes(path);
        var trash = Path.GetFullPath(Path.Combine(_world.Library, History(fileId).TrashPath!));

        using var db = _world.Db.NewContext();
        var (undo, _, _) = Undo(db, (from, to) =>
        {
            if (Path.GetFullPath(from) == trash)
            {
                return false;
            }

            File.Move(from, to);
            return true;
        });
        var result = await undo.UndoAsync(_world.SeriesId, false, false, CancellationToken.None);

        Assert.Equal((0, 1), (result!.Restored, result.Skipped));
        Assert.StartsWith("pageConversion.undo.moveFailed", Assert.Single(result.Reasons));
        Assert.Equal(converted, File.ReadAllBytes(path));
        Assert.True(File.Exists(trash));
        Assert.DoesNotContain(TmpFiles(), f => Path.GetFileName(f).StartsWith(PageConversionUndoService.AsidePrefix));
        Assert.Equal("webp", Row(fileId).ImageFormat);
        Assert.Null(History(fileId).UndoneAtUtc);
    }

    [Fact]
    public async Task Undo_can_set_the_series_to_never()
    {
        var (_, fileId, _) = JpegChapter(1);
        await ConvertAsync(fileId, null);

        using var db = _world.Db.NewContext();
        var result = await Undo(db).Undo.UndoAsync(_world.SeriesId, setNever: true, convertAgain: false, CancellationToken.None);

        Assert.Equal(1, result!.Restored);
        using var check = _world.Db.NewContext();
        Assert.Equal(PageConversionOverride.Never, check.Series.Single(s => s.Id == _world.SeriesId).PageConversion);
        Assert.Equal(PlanAction.UndoneName, Row(fileId).CheckedWith);
    }

    [Fact]
    public async Task Convert_again_clears_the_stamps_and_starts_a_pass()
    {
        EnableConversion();
        var (_, fileId, _) = JpegChapter(1);
        await ConvertAsync(fileId, null);

        using var db = _world.Db.NewContext();
        var (undo, _, worker) = Undo(db);
        var result = await undo.UndoAsync(_world.SeriesId, setNever: false, convertAgain: true, CancellationToken.None);

        Assert.Equal(1, result!.Restored);
        Assert.Equal(["pageConversion.undo.queued(count=1)"], result.Reasons);
        var row = Row(fileId);
        Assert.Equal((null, null, "jpg"), (row.ConversionCheckedAtUtc, row.CheckedWith, row.ImageFormat));
        Assert.Equal(PageConversionPassState.Running, await worker.StateAsync(CancellationToken.None));
        using var pending = _world.Db.NewContext();
        Assert.Contains(fileId, await PageConversionWorker.PendingQuery(pending).Select(f => f.Id).ToListAsync());
    }

    [Fact]
    public async Task Undo_refuses_convert_again_together_with_never()
    {
        using var db = _world.Db.NewContext();
        var result = await SeriesController(db).UndoPageConversion(_world.SeriesId,
            new SeriesController.PageConversionUndoRequest(SetNever: true, ConvertAgain: true), Undo(db).Undo, CancellationToken.None);

        var error = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("error.pageConversion.undoConflict", System.Text.Json.JsonSerializer.Serialize(error.Value));
    }

    [Fact]
    public async Task A_recheck_leaves_an_undone_file_alone()
    {
        EnableConversion();
        var (_, fileId, _) = JpegChapter(1);
        Stamp(fileId, PlanAction.UndoneName);

        using var db = _world.Db.NewContext();
        var plans = new PageConversionPlanService(db);
        var plan = await PageConversionOptions.LoadPlanAsync(db, CancellationToken.None);
        Assert.Equal(0, await plans.RecheckAsync(plan, null, CancellationToken.None));
        Assert.Equal(0, await plans.RecheckAsync(plan, _world.SeriesId, CancellationToken.None));
        Assert.Equal(0, await plans.RecheckFileAsync(plan, fileId, CancellationToken.None));

        Assert.Equal(PlanAction.UndoneName, Row(fileId).CheckedWith);
        Assert.NotNull(Row(fileId).ConversionCheckedAtUtc);
    }

    [Fact]
    public async Task The_series_history_counts_what_an_undo_can_still_restore()
    {
        var (_, first, _) = JpegChapter(1);
        var (_, second, _) = JpegChapter(2);
        var (_, upgraded, _) = JpegChapter(4);
        await ConvertAsync(first, null);
        await ConvertAsync(second, null);
        await ConvertAsync(upgraded, null);
        // The history never looks at the disk; the undo finds the missing original when it gets there.
        File.Delete(Path.Combine(_world.Library, History(second).TrashPath!));
        _world.Chapter(3, f => (f.OriginalImageFormat, f.ImageFormat, f.ConversionSavedBytes) = ("jpg", "webp", 100));
        using (var db = _world.Db.NewContext())
        {
            // Upgraded since, and the download converted the new copy before placement, so no new history row.
            var row = db.ChapterFiles.Single(f => f.Id == upgraded);
            row.Size += 1234;
            row.ConversionSavedBytes = 50;
            db.SaveChanges();
        }

        using var dbc = _world.Db.NewContext();
        var result = await SeriesController(dbc).PageConversionHistory(_world.SeriesId, Undo(dbc).Undo, CancellationToken.None);

        var history = Assert.IsType<SeriesConversionDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(4, history.ConvertedFiles);
        Assert.Equal(Row(first).ConversionSavedBytes + Row(second).ConversionSavedBytes + 150, history.SavedBytes);
        Assert.Equal((2, History(first).TrashBytes!.Value + History(second).TrashBytes!.Value),
            (history.RestorableFiles, history.RestorableBytes));
        Assert.Equal([Webp.Name()], history.CheckedWith);
        Assert.IsType<NotFoundResult>(await SeriesController(dbc).PageConversionHistory(9999, Undo(dbc).Undo, CancellationToken.None));

        var undone = await Undo(dbc).Undo.UndoAsync(_world.SeriesId, false, false, CancellationToken.None);
        Assert.Equal((1, 1), (undone!.Restored, undone.Skipped));
        Assert.Equal(2, undone.Reasons.Count);
        Assert.StartsWith("pageConversion.undo.trashGone", undone.Reasons[0]);
        Assert.Equal("pageConversion.undo.stale(count=1)", undone.Reasons[1]);
        Assert.Equal("webp", Row(upgraded).ImageFormat);
    }

    [Fact]
    public async Task Undo_sums_up_conversions_that_no_longer_apply_in_one_line()
    {
        var (_, reset, _) = JpegChapter(1);
        var (_, removed, _) = JpegChapter(2);
        var (_, reconverted, _) = JpegChapter(3);
        var (_, kept, _) = JpegChapter(4);
        foreach (var id in new[] { reset, removed, reconverted })
        {
            await ConvertAsync(id, null);
        }

        using (var db = _world.Db.NewContext())
        {
            var row = db.ChapterFiles.Single(f => f.Id == reset);
            row.ResetConversion();
            row.Size += 10;
            db.ChapterFiles.Single(f => f.Id == reconverted).Size += 10;
            db.SaveChanges();
            await db.ChapterFiles.Where(f => f.Id == removed).ExecuteDeleteAsync();
            db.DownloadQueue.Add(new DownloadQueueItem { SeriesId = _world.SeriesId, Status = QueueStatus.Downloading, QueuedAt = DateTime.UtcNow });
            db.SaveChanges();
        }

        using (var db = _world.Db.NewContext())
        {
            Assert.Equal(0, (await Undo(db).Undo.HistoryAsync(_world.SeriesId, CancellationToken.None))!.RestorableFiles);
            var nothing = await Undo(db).Undo.UndoAsync(_world.SeriesId, false, false, CancellationToken.None);
            Assert.Equal((0, 0), (nothing!.Restored, nothing.Skipped));
            Assert.Equal(["pageConversion.undo.stale(count=3)"], nothing.Reasons);
            db.DownloadQueue.RemoveRange(db.DownloadQueue.IgnoreQueryFilters());
            db.SaveChanges();
        }

        await ConvertAsync(kept, null);
        using (var db = _world.Db.NewContext())
        {
            var result = await Undo(db).Undo.UndoAsync(_world.SeriesId, false, false, CancellationToken.None);
            Assert.Equal((1, 0), (result!.Restored, result.Skipped));
            Assert.Equal(["pageConversion.undo.stale(count=3)"], result.Reasons);
        }

        Assert.Equal(PlanAction.UndoneName, Row(kept).CheckedWith);
        Assert.Null(History(reconverted).UndoneAtUtc);
    }

    [Fact]
    public async Task A_converted_copy_a_failed_rollback_left_aside_survives_the_sweep_and_goes_back_on_the_next_undo()
    {
        var (_, fileId, path) = JpegChapter(1);
        var original = File.ReadAllBytes(path);
        await ConvertAsync(fileId, null);
        var converted = File.ReadAllBytes(path);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-40));
        var trash = Path.GetFullPath(Path.Combine(_world.Library, History(fileId).TrashPath!));
        var aside = PageConversionUndoService.AsidePath(_world.Library, fileId);

        using (var db = _world.Db.NewContext())
        {
            var (undo, _, _) = Undo(db, (from, to) =>
            {
                if (Path.GetFullPath(from) == trash || Path.GetFullPath(from) == Path.GetFullPath(aside))
                {
                    return false;
                }

                File.Move(from, to);
                return true;
            });
            var failed = await undo.UndoAsync(_world.SeriesId, false, false, CancellationToken.None);
            Assert.Equal((0, 1), (failed!.Restored, failed.Skipped));
            Assert.StartsWith("pageConversion.undo.moveFailed", Assert.Single(failed.Reasons));
        }

        Assert.False(File.Exists(path));
        Assert.Equal(converted, File.ReadAllBytes(aside));
        Assert.EndsWith(".cbz.aside", aside);
        Assert.True(File.GetLastWriteTimeUtc(aside) > DateTime.UtcNow.AddMinutes(-5));

        var tmp = Path.GetDirectoryName(aside)!;
        var leftover = Path.Combine(tmp, "123.cbz");
        var oldAside = Path.Combine(tmp, $"{PageConversionUndoService.AsidePrefix}999{PageConversionUndoService.AsideExtension}");
        File.WriteAllBytes(leftover, [1]);
        File.WriteAllBytes(oldAside, [1]);
        File.SetLastWriteTimeUtc(oldAside, DateTime.UtcNow.AddDays(-31));
        Assert.True(HousekeepingJob.SweepTmp(tmp, new HashSet<string>(), DateTime.UtcNow.AddDays(2),
            NullLogger.Instance, CancellationToken.None));
        Assert.False(File.Exists(leftover));
        Assert.False(File.Exists(oldAside));
        Assert.True(File.Exists(aside));

        using (var db = _world.Db.NewContext())
        {
            Assert.Equal(1, (await Undo(db).Undo.HistoryAsync(_world.SeriesId, CancellationToken.None))!.RestorableFiles);
            var result = await Undo(db).Undo.UndoAsync(_world.SeriesId, false, false, CancellationToken.None);
            Assert.Equal((1, 0), (result!.Restored, result.Skipped));
        }

        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(File.Exists(aside));
        Assert.False(File.Exists(trash));
        Assert.Equal(PlanAction.UndoneName, Row(fileId).CheckedWith);
    }

    [Fact]
    public async Task The_trash_size_counts_originals_conversion_left_there()
    {
        var (_, fileId, _) = JpegChapter(1);
        await ConvertAsync(fileId, null);
        var bytes = History(fileId).TrashBytes!.Value;

        using var db = _world.Db.NewContext();
        Assert.Equal((bytes, 1), await new UpgradeTrashService(db, _world.Settings, NullLogger<UpgradeTrashService>.Instance)
            .SizeAsync(CancellationToken.None));

        await Undo(db).Undo.UndoAsync(_world.SeriesId, false, false, CancellationToken.None);
        Assert.Equal((0L, 0), await UpgradeTrashService.SizeAsync(db, CancellationToken.None));
    }
}
