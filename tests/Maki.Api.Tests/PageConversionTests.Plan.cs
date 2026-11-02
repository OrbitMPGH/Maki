using System.Text.Json;
using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Images;
using Maki.Core.Quality;
using Maki.Core.Sources;
using Maki.Core.Storage;
using Maki.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>The conversion plan: rules, series overrides and the answers recorded on each checked file.</summary>
public sealed partial class PageConversionTests
{
    private static readonly PageConversionPlan HighPlan =
        PageConversionPlan.For(new PageConversionSettings(PageConversionFormat.Webp, PageConversionStop.High, false));

    private void Stamp(int fileId, string? checkedWith, string? originalFormat = null)
    {
        using var db = _world.Db.NewContext();
        var file = db.ChapterFiles.Single(f => f.Id == fileId);
        file.ConversionCheckedAtUtc = DateTime.UtcNow;
        file.CheckedWith = checkedWith;
        file.OriginalImageFormat = originalFormat;
        db.SaveChanges();
    }

    private void SetSeries(Action<Series> change)
    {
        using var db = _world.Db.NewContext();
        change(db.Series.Single(s => s.Id == _world.SeriesId));
        db.SaveChanges();
    }

    private void AddRule(PageConversionConditions conditions, bool skip, PageConversionStop? stop = null, int order = 0)
    {
        using var db = _world.Db.NewContext();
        db.PageConversionRules.Add(new PageConversionRule
        {
            Order = order, ConditionsJson = conditions.ToJson(), Skip = skip, Stop = stop
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task A_recheck_clears_only_the_answers_the_plan_now_gives_differently()
    {
        var (_, same, _) = JpegChapter(1);
        var (_, differs, _) = JpegChapter(2);
        var (_, unknown, _) = JpegChapter(3);
        var (_, converted, _) = JpegChapter(4);
        var (_, hard, _) = JpegChapter(5);
        Stamp(same, "webp:high");
        Stamp(differs, "skip");
        Stamp(unknown, null);
        Stamp(converted, "webp:balanced", originalFormat: "jpg");
        Stamp(hard, PlanAction.HardSkipName);

        using (var db = _world.Db.NewContext())
        {
            Assert.Equal(2, await new PageConversionPlanService(db).RecheckAsync(HighPlan, null, CancellationToken.None));
        }

        Assert.Equal("webp:high", Row(same).CheckedWith);
        Assert.NotNull(Row(same).ConversionCheckedAtUtc);
        Assert.Equal((null, (DateTime?)null), (Row(differs).CheckedWith, Row(differs).ConversionCheckedAtUtc));
        Assert.Null(Row(unknown).ConversionCheckedAtUtc);
        Assert.Equal("webp:balanced", Row(converted).CheckedWith);
        Assert.NotNull(Row(converted).ConversionCheckedAtUtc);
        Assert.Equal("skip:hard", Row(hard).CheckedWith);
        Assert.NotNull(Row(hard).ConversionCheckedAtUtc);

        Stamp(differs, "skip");
        using (var db = _world.Db.NewContext())
        {
            Assert.Equal(0, await new PageConversionPlanService(db).RecheckAsync(null, null, CancellationToken.None));
        }

        Assert.NotNull(Row(differs).ConversionCheckedAtUtc);
    }

    [Fact]
    public async Task The_pass_records_its_answer_on_converted_skipped_and_hardlinked_files()
    {
        var (_, converted, _) = JpegChapter(1);
        var (_, seeding, seedingPath) = JpegChapter(2);
        Assert.True(FileLinker.TryHardlink(seedingPath, Path.Combine(_world.Root, "seeding.cbz")), "this filesystem cannot hardlink");

        using (var db = _world.Db.NewContext())
        {
            Assert.Equal(FileConversionStatus.Converted,
                (await Service(db).ConvertAsync(converted, HighPlan, new PageConversionPassCache(), CancellationToken.None)).Status);
            Assert.Equal(FileConversionStatus.Hardlinked,
                (await Service(db).ConvertAsync(seeding, HighPlan, new PageConversionPassCache(), CancellationToken.None)).Status);
        }

        Assert.Equal("webp:high", Row(converted).CheckedWith);
        Assert.Equal("skip:hard", Row(seeding).CheckedWith);
        Assert.NotNull(Row(seeding).ConversionCheckedAtUtc);
    }

    [Fact]
    public async Task A_file_the_plan_skips_is_checked_as_skip_and_left_untouched()
    {
        var (_, fileId, path) = JpegChapter(1);
        var hash = Hash(path);
        AddRule(new PageConversionConditions(Tiers: ["aggregator"]), skip: true);

        using (var db = _world.Db.NewContext())
        {
            Assert.Null(await PageConversionOptions.LoadPlanAsync(db, CancellationToken.None));
        }

        EnableConversion();
        using (var db = _world.Db.NewContext())
        {
            var plan = (await PageConversionOptions.LoadPlanAsync(db, CancellationToken.None))!;
            var result = await Service(db).ConvertAsync(fileId, plan, new PageConversionPassCache(), CancellationToken.None);
            Assert.Equal((FileConversionStatus.Unchanged, (ArchiveConversionOutcome?)null), (result.Status, result.Outcome));
        }

        Assert.Equal(hash, Hash(path));
        Assert.Equal("skip", Row(fileId).CheckedWith);
        Assert.NotNull(Row(fileId).ConversionCheckedAtUtc);
        Assert.Empty(TrashedOriginals());
    }

    [Fact]
    public async Task A_series_set_to_never_is_skipped_and_one_set_to_a_stop_converts_at_it()
    {
        var (_, fileId, _) = JpegChapter(1);
        SetSeries(s => s.PageConversion = PageConversionOverride.Never);
        using (var db = _world.Db.NewContext())
        {
            await Service(db).ConvertAsync(fileId, HighPlan, new PageConversionPassCache(), CancellationToken.None);
        }

        Assert.Equal("skip", Row(fileId).CheckedWith);

        SetSeries(s => s.PageConversion = PageConversionOverride.Balanced);
        using (var db = _world.Db.NewContext())
        {
            await new PageConversionPlanService(db).RecheckAsync(HighPlan, _world.SeriesId, CancellationToken.None);
            Assert.Equal(FileConversionStatus.Converted,
                (await Service(db).ConvertAsync(fileId, HighPlan, new PageConversionPassCache(), CancellationToken.None)).Status);
        }

        Assert.Equal("webp:balanced", Row(fileId).CheckedWith);
    }

    [Fact]
    public async Task A_file_whose_middle_pages_do_not_shrink_is_checked_without_a_rewrite()
    {
        var (_, fileId, path) = JpegChapter(1, quality: 35);
        var hash = Hash(path);
        var encoded = false;

        using (var db = _world.Db.NewContext())
        {
            var service = Service(db, converter: (source, target, settings, ct) =>
            {
                encoded = true;
                return ArchivePageConverter.ConvertAsync(source, target, settings, ct);
            });
            var result = await service.ConvertAsync(fileId, HighPlan, new PageConversionPassCache(), CancellationToken.None);
            Assert.Equal((FileConversionStatus.Unchanged, ArchiveConversionOutcome.NotSmaller), (result.Status, result.Outcome));
        }

        Assert.False(encoded);
        Assert.Equal(hash, Hash(path));
        Assert.Equal("webp:high", Row(fileId).CheckedWith);
        Assert.NotNull(Row(fileId).ConversionCheckedAtUtc);
        Assert.Empty(TmpFiles());
    }

    [Fact]
    public async Task The_probe_lets_a_file_whose_pages_shrink_through_to_the_full_conversion()
    {
        var (_, fileId, path) = JpegChapter(1);

        Assert.True(await ArchivePageConverter.ProbeAsync(path, HighPlan.Settings(PageConversionStop.High), CancellationToken.None));
        Assert.False(await ArchivePageConverter.ProbeAsync(path, HighPlan.Settings(PageConversionStop.Lossless), CancellationToken.None));
        Assert.Equal(FileConversionStatus.Converted, (await ConvertAsync(fileId)).Status);
    }

    [Theory]
    [InlineData(ReadFileCleanup.On, 3, false, false)]
    [InlineData(ReadFileCleanup.On, -20, true, false)]
    [InlineData(ReadFileCleanup.Off, 3, true, false)]
    [InlineData(ReadFileCleanup.On, 3, false, true)]
    public async Task A_file_read_file_cleanup_deletes_within_a_week_is_passed_over_unchecked(ReadFileCleanup mode,
        int readDaysAgo, bool converts, bool never)
    {
        var (chapterId, fileId, path) = JpegChapter(1);
        var hash = Hash(path);
        SetSeries(s => (s.ReadFileCleanup, s.PageConversion) =
            (mode, never ? PageConversionOverride.Never : PageConversionOverride.Default));
        await _world.Settings.SetAsync(SettingKeys.ReadFileCleanupKeepLast, "false");
        await _world.Settings.SetAsync(SettingKeys.ReadFileCleanupDays, "7");
        var reader = _world.Db.SeedUser("cleanup-reader");
        using (var db = _world.Db.NewContext())
        {
            var finished = DateTime.UtcNow.AddDays(-Math.Abs(readDaysAgo));
            if (readDaysAgo < 0)
            {
                // Read long ago, but the file arrived since: due a week after it arrived, not now.
                db.ChapterFiles.Single(f => f.Id == fileId).DateAdded = DateTime.UtcNow.AddDays(30);
            }

            db.ChapterProgress.Add(new ChapterProgress
            {
                UserId = reader, SeriesId = _world.SeriesId, ChapterId = chapterId, Completed = true,
                StartedAt = finished, UpdatedAt = finished, CompletedAt = finished
            });
            db.SaveChanges();
        }

        var result = await ConvertAsync(fileId);

        Assert.Equal(converts ? FileConversionStatus.Converted : FileConversionStatus.Unchanged, result.Status);
        Assert.Equal(converts || never, Row(fileId).ConversionCheckedAtUtc is not null);
        Assert.Equal(converts, Hash(path) != hash);
        if (never)
        {
            Assert.Equal("skip", Row(fileId).CheckedWith);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_download_is_converted_or_left_as_the_plan_says_and_records_the_answer(bool skipOfficial)
    {
        EnableConversion();
        if (skipOfficial)
        {
            AddRule(new PageConversionConditions(Tiers: ["official"]), skip: true);
        }

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
        Assert.NotNull(file.ConversionCheckedAtUtc);
        if (skipOfficial)
        {
            Assert.Equal(("skip", "jpg", (string?)null), (file.CheckedWith, file.ImageFormat, file.OriginalImageFormat));
            Assert.Equal(0, file.ConversionSavedBytes);
        }
        else
        {
            Assert.Equal(("webp:high", "webp", "jpg"), (file.CheckedWith, file.ImageFormat, file.OriginalImageFormat));
        }
    }

    [Fact]
    public async Task The_scope_counts_each_file_once_where_the_plan_puts_it()
    {
        _world.Chapter(1, f => (f.Size, f.ImageFormat) = (100, "jpg"));
        _world.Chapter(2, f => (f.Size, f.ImageFormat, f.Tier) = (200, "png", QualityTier.Official));
        _world.Chapter(3, f => (f.Size, f.ImageFormat, f.Tier) = (400, "jpg", QualityTier.Official));
        _world.Chapter(4, f => (f.Size, f.ImageFormat) = (800, "webp"));
        _world.Chapter(5, f => (f.Size, f.ImageFormat, f.OriginalImageFormat) = (1600, "webp", "jpg"));
        _world.Chapter(6, f => f.Size = 3200, extension: "pdf");
        using (var db = _world.Db.NewContext())
        {
            var other = new Series
            {
                Title = "Other", SortTitle = "other", FolderName = "Other", PageConversion = PageConversionOverride.Never,
                RootFolderId = db.Series.Single(s => s.Id == _world.SeriesId).RootFolderId
            };
            db.Series.Add(other);
            db.SaveChanges();
            db.ChapterFiles.Add(new ChapterFile
            {
                SeriesId = other.Id, RelativePath = "Other/Other 001.cbz", SourceName = "agg", Size = 6400, ImageFormat = "jpg"
            });
            var importing = new Series
            {
                Title = "Importing", SortTitle = "importing", FolderName = "Importing", PendingImportLink = PendingImportLink.Link,
                RootFolderId = other.RootFolderId
            };
            db.Series.Add(importing);
            db.SaveChanges();
            db.ChapterFiles.Add(new ChapterFile
            {
                SeriesId = importing.Id, RelativePath = "Importing/Importing 001.cbz", SourceName = "agg", Size = 12800,
                ImageFormat = "jpg"
            });
            db.SaveChanges();
        }

        using var context = _world.Db.NewContext();
        var result = await Controller(context).Scope(new PageConversionController.ScopeRequest("webp", "high", false,
        [
            new PageConversionController.RuleDto(null, "Scrapes", new PageConversionConditions(Tiers: ["aggregator"]), "skip", null),
            new PageConversionController.RuleDto(null, "PNG", new PageConversionConditions(Formats: ["png", "jpg"]), "convert", "balanced"),
            new PageConversionController.RuleDto(null, "Never reached", new PageConversionConditions(Formats: ["png"]), "convert", "compact")
        ]), CancellationToken.None);

        var scope = Assert.IsType<PageConversionPlanService.Scope>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(
        [
            new PageConversionPlanService.ScopeBucket(1, 100), new PageConversionPlanService.ScopeBucket(2, 600),
            new PageConversionPlanService.ScopeBucket(0, 0)
        ], scope.Rules);
        Assert.Equal(new PageConversionPlanService.ScopeBucket(0, 0), scope.Default);
        Assert.Equal(new PageConversionPlanService.ScopeBucket(1, 6400), scope.Overrides);
        Assert.Equal(new PageConversionPlanService.ScopeBucket(1, 800), scope.AlreadyCompact);
        Assert.Equal(new PageConversionPlanService.ScopeBucket(1, 1600), scope.ConvertedEarlier);

        var noRules = Assert.IsType<PageConversionPlanService.Scope>(Assert.IsType<OkObjectResult>(
            await Controller(context).Scope(new PageConversionController.ScopeRequest(null, "high", false, null),
                CancellationToken.None)).Value);
        Assert.Empty(noRules.Rules);
        Assert.Equal(new PageConversionPlanService.ScopeBucket(3, 700), noRules.Default);
    }

    public static TheoryData<PageConversionController.RuleDto> InvalidRules => new()
    {
        new PageConversionController.RuleDto(null, "", null, "delete", null),
        new PageConversionController.RuleDto(null, "", null, "convert", null),
        new PageConversionController.RuleDto(null, "", null, "convert", "best"),
        new PageConversionController.RuleDto(null, "", new PageConversionConditions(Tiers: ["gold"]), "skip", null),
        new PageConversionController.RuleDto(null, "", new PageConversionConditions(Formats: ["jxl"]), "skip", null),
        new PageConversionController.RuleDto(null, "", new PageConversionConditions(BppUnder: 0), "skip", null),
        new PageConversionController.RuleDto(null, "", new PageConversionConditions(BppOver: -1), "skip", null),
        new PageConversionController.RuleDto(null, "", new PageConversionConditions(SizeOverBytes: 0), "skip", null),
        new PageConversionController.RuleDto(null, "", new PageConversionConditions(SizeUnderBytes: -5), "skip", null)
    };

    [Theory]
    [MemberData(nameof(InvalidRules))]
    public async Task An_invalid_rule_is_refused_and_nothing_is_saved(PageConversionController.RuleDto rule)
    {
        using var db = _world.Db.NewContext();
        var valid = new PageConversionController.RuleDto(null, "Fine", null, "skip", null);

        Assert.IsType<BadRequestObjectResult>(
            await Controller(db).SaveRules(new PageConversionController.RulesDto([valid, rule]), CancellationToken.None));
        Assert.Empty(_world.Db.NewContext().PageConversionRules);
    }

    [Fact]
    public async Task More_than_fifty_rules_are_refused()
    {
        using var db = _world.Db.NewContext();
        var rules = Enumerable.Range(0, PageConversionController.MaxRules + 1)
            .Select(i => new PageConversionController.RuleDto(null, $"Rule {i}", null, "skip", null))
            .ToList();

        Assert.IsType<BadRequestObjectResult>(
            await Controller(db).SaveRules(new PageConversionController.RulesDto(rules), CancellationToken.None));
    }

    [Fact]
    public async Task Saved_rules_replace_the_list_and_come_back_in_the_order_sent()
    {
        PageConversionController.RulesDto Read(IActionResult result) =>
            Assert.IsType<PageConversionController.RulesDto>(Assert.IsType<OkObjectResult>(result).Value);

        using var db = _world.Db.NewContext();
        var controller = Controller(db);
        Read(await controller.SaveRules(new PageConversionController.RulesDto(
            [new PageConversionController.RuleDto(null, "Old", null, "skip", null)]), CancellationToken.None));

        var saved = Read(await controller.SaveRules(new PageConversionController.RulesDto(
        [
            new PageConversionController.RuleDto(null, " Volumes ", new PageConversionConditions(Tiers: ["Volume"]), "convert", "Balanced"),
            new PageConversionController.RuleDto(7, "Tagged", new PageConversionConditions(Tags: ["keep-original"]), "skip", "high")
        ]), CancellationToken.None));

        Assert.Equal(["Volumes", "Tagged"], saved.Rules!.Select(r => r.Name));
        Assert.All(saved.Rules!, r => Assert.NotNull(r.Id));
        Assert.Equal(("convert", "balanced"), (saved.Rules![0].Action, saved.Rules[0].Stop));
        Assert.Equal(("skip", (string?)null), (saved.Rules[1].Action, saved.Rules[1].Stop));
        Assert.Equal(new[] { "volume" }, saved.Rules[0].Conditions!.Tiers!);
        Assert.Equal(JsonSerializer.Serialize(saved), JsonSerializer.Serialize(Read(await controller.Rules(CancellationToken.None))));
        Assert.Equal([0, 1], _world.Db.NewContext().PageConversionRules.OrderBy(r => r.Id).Select(r => r.Order).ToList());

        var json = JsonSerializer.Serialize(saved.Rules[1], new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"tags\":[\"keep-original\"]", json);
        Assert.DoesNotContain("tiers", json);
        Assert.DoesNotContain("bppUnder", json);

        using (var raw = _world.Db.NewContext())
        {
            raw.PageConversionRules.Add(new PageConversionRule { Order = 2, ConditionsJson = "{not json", Skip = true });
            raw.SaveChanges();
        }

        var status = Saved(await controller.Status(CancellationToken.None));
        Assert.Equal(2, status.Library.RuleCount);
    }

    [Fact]
    public async Task Saving_rules_puts_files_back_in_line_where_their_answer_changed()
    {
        EnableConversion();
        var (_, fileId, _) = JpegChapter(1);
        Stamp(fileId, "webp:high");

        using var db = _world.Db.NewContext();
        await Controller(db).SaveRules(new PageConversionController.RulesDto(
            [new PageConversionController.RuleDto(null, "", new PageConversionConditions(Tiers: ["official"]), "skip", null)]),
            CancellationToken.None);
        Assert.NotNull(Row(fileId).ConversionCheckedAtUtc);

        await Controller(db).SaveRules(new PageConversionController.RulesDto(
            [new PageConversionController.RuleDto(null, "", new PageConversionConditions(Tiers: ["aggregator"]), "skip", null)]),
            CancellationToken.None);
        Assert.Null(Row(fileId).ConversionCheckedAtUtc);
    }

    private SeriesController SeriesController(MakiDbContext db) =>
        new(new TestLocalizer(), db, null!, null!, null!, null!, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            new SourceAvailability(_world.Settings, new SourceRegistry([])), null!, null!, null!, null!,
            NullLogger<SeriesController>.Instance);

    [Fact]
    public async Task A_series_override_puts_its_checked_files_back_in_line_when_it_changes_their_answer()
    {
        EnableConversion();
        var (_, fileId, _) = JpegChapter(1);
        Stamp(fileId, "webp:high");

        using var db = _world.Db.NewContext();
        var plans = new PageConversionPlanService(db);
        Assert.IsType<BadRequestObjectResult>(await SeriesController(db).SetPageConversion(_world.SeriesId,
            new SeriesController.PageConversionRequest("Sometimes"), plans, CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await SeriesController(db).SetPageConversion(_world.SeriesId,
            new SeriesController.PageConversionRequest("3"), plans, CancellationToken.None));

        var high = await SeriesController(db).SetPageConversion(_world.SeriesId,
            new SeriesController.PageConversionRequest("high"), plans, CancellationToken.None);
        Assert.Contains("High", JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(high).Value));
        Assert.NotNull(Row(fileId).ConversionCheckedAtUtc);

        await SeriesController(db).SetPageConversion(_world.SeriesId,
            new SeriesController.PageConversionRequest("Never"), plans, CancellationToken.None);
        Assert.Null(Row(fileId).ConversionCheckedAtUtc);
        using var check = _world.Db.NewContext();
        Assert.Equal(PageConversionOverride.Never, check.Series.Single(s => s.Id == _world.SeriesId).PageConversion);
        Assert.Equal(1, Saved(await Controller(check).Status(CancellationToken.None)).Library.OverrideSeries);
    }

    [Fact]
    public async Task Saving_new_settings_pauses_a_running_pass_and_saving_the_same_ones_does_not()
    {
        var worker = new PageConversionWorker(_world.Db.ScopeFactory(), new SettingsService(_world.Db.ScopeFactory()),
            NullLogger<PageConversionWorker>.Instance);
        using var db = _world.Db.NewContext();
        var controller = Controller(db, worker);
        await controller.Save(new PageConversionController.SettingsDto("webp", "high", false), CancellationToken.None);

        await worker.StartPassAsync(CancellationToken.None);
        await controller.Save(new PageConversionController.SettingsDto("webp", "high", false), CancellationToken.None);
        Assert.Equal(PageConversionPassState.Running, await worker.StateAsync(CancellationToken.None));

        var status = Saved(await controller.Save(new PageConversionController.SettingsDto("avif", "high", false), CancellationToken.None));
        Assert.Equal(("paused", "settingsChanged"), (status.Pass.State, status.Pass.Hold));

        await worker.StartPassAsync(CancellationToken.None);
        await controller.SaveRules(new PageConversionController.RulesDto(
            [new PageConversionController.RuleDto(null, "", null, "skip", null)]), CancellationToken.None);
        var snapshot = await worker.SnapshotAsync(CancellationToken.None);
        Assert.Equal((PageConversionPassState.Paused, PageConversionHold.SettingsChanged), (snapshot.State, snapshot.Hold));
    }

    [Fact]
    public void A_revert_puts_back_what_the_plan_answered()
    {
        var checkedFile = new ChapterFile { ImageFormat = "jpg", ConversionCheckedAtUtc = DateTime.UtcNow, CheckedWith = "skip" };
        var snapshot = UpgradeEvaluator.Snapshot(checkedFile, 0);
        Assert.Equal("skip", snapshot.CheckedWith);

        var file = new ChapterFile { ImageFormat = "png" };
        QualitySnapshot.Parse(snapshot.Serialize())!.RestoreFormatOnto(file, DateTime.UtcNow);
        Assert.Equal(("jpg", "skip", (string?)null), (file.ImageFormat, file.CheckedWith, file.OriginalImageFormat));
        Assert.NotNull(file.ConversionCheckedAtUtc);

        new QualitySnapshot { ImageFormat = "jpg", StoredImageFormat = "webp", ConversionSavedBytes = 9, CheckedWith = "webp:high" }
            .RestoreFormatOnto(file, DateTime.UtcNow);
        Assert.Equal(("webp", "jpg", "webp:high"), (file.ImageFormat, file.OriginalImageFormat, file.CheckedWith));

        Assert.Null(UpgradeEvaluator.Snapshot(new ChapterFile { CheckedWith = "skip" }, 0).CheckedWith);
        file.ResetConversion();
        Assert.Null(file.CheckedWith);
    }

    [Fact]
    public async Task Turning_conversion_off_during_a_pass_holds_it_as_conversion_off()
    {
        var worker = new PageConversionWorker(_world.Db.ScopeFactory(), new SettingsService(_world.Db.ScopeFactory()),
            NullLogger<PageConversionWorker>.Instance);
        using var db = _world.Db.NewContext();
        var controller = Controller(db, worker);
        await controller.Save(new PageConversionController.SettingsDto("webp", "high", false), CancellationToken.None);
        await worker.StartPassAsync(CancellationToken.None);

        var status = Saved(await controller.Save(new PageConversionController.SettingsDto(null, "high", false), CancellationToken.None));

        Assert.Equal(("paused", "conversionOff"), (status.Pass.State, status.Pass.Hold));
    }

    [Fact]
    public async Task A_pass_restarted_while_a_file_is_in_flight_decides_the_files_after_it_by_the_new_plan()
    {
        EnableConversion();
        var (_, first, _) = JpegChapter(1);
        var (_, second, _) = JpegChapter(2);
        var (_, third, _) = JpegChapter(3);
        PageConversionWorker worker = null!;
        var restarted = false;
        var services = new ServiceCollection();
        services.AddScoped(_ => _world.Db.NewContext());
        services.AddScoped(sp => Service(sp.GetRequiredService<MakiDbContext>(), converter: async (source, target, settings, ct) =>
        {
            if (!restarted)
            {
                // The wizard's Restart, while the worker is busy on the first file.
                restarted = true;
                await worker.PauseAsync(ct);
                using var db = _world.Db.NewContext();
                await Controller(db, worker).Save(new PageConversionController.SettingsDto("webp", "balanced", false), ct);
                await worker.StartPassAsync(ct);
            }

            return await ArchivePageConverter.ConvertAsync(source, target, settings, ct);
        }));
        worker = new PageConversionWorker(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new SettingsService(_world.Db.ScopeFactory()), NullLogger<PageConversionWorker>.Instance)
        {
            DelayBetweenFiles = TimeSpan.Zero
        };

        await worker.StartPassAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await worker.StartAsync(timeout.Token);
        try
        {
            while (await worker.StateAsync(timeout.Token) != PageConversionPassState.Idle)
            {
                await Task.Delay(50, timeout.Token);
            }
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.True(restarted);
        Assert.Equal("webp:high", Row(first).CheckedWith);
        Assert.Equal("webp:balanced", Row(second).CheckedWith);
        Assert.Equal("webp:balanced", Row(third).CheckedWith);
    }

    [Fact]
    public async Task Saving_the_plan_stores_settings_and_rules_together_and_rechecks_against_both()
    {
        EnableConversion();
        var (_, fileId, _) = JpegChapter(1);
        Stamp(fileId, "webp:high");
        var worker = new PageConversionWorker(_world.Db.ScopeFactory(), new SettingsService(_world.Db.ScopeFactory()),
            NullLogger<PageConversionWorker>.Instance);
        using var db = _world.Db.NewContext();
        var controller = Controller(db, worker);
        await worker.StartPassAsync(CancellationToken.None);
        var avif = new PageConversionController.SettingsDto("avif", "balanced", false);
        var scrapes = new PageConversionController.RuleDto(null, "Scrapes",
            new PageConversionConditions(Tiers: ["aggregator"]), "convert", "compact");

        Assert.IsType<BadRequestObjectResult>(await controller.SavePlan(new PageConversionController.PlanDto(avif,
            [scrapes, new PageConversionController.RuleDto(null, "", null, "delete", null)]), CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await controller.SavePlan(new PageConversionController.PlanDto(
            new PageConversionController.SettingsDto("jxl", "balanced", false), [scrapes]), CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await controller.SavePlan(
            new PageConversionController.PlanDto(null, [scrapes]), CancellationToken.None));
        Assert.Empty(_world.Db.NewContext().PageConversionRules);
        Assert.Equal(new PageConversionSettings(PageConversionFormat.Webp, PageConversionStop.High, false),
            await PageConversionOptions.LoadAsync(db, CancellationToken.None));
        Assert.Equal(PageConversionPassState.Running, await worker.StateAsync(CancellationToken.None));
        Assert.NotNull(Row(fileId).ConversionCheckedAtUtc);

        var status = Saved(await controller.SavePlan(new PageConversionController.PlanDto(avif, [scrapes]), CancellationToken.None));
        Assert.Equal(avif, status.Settings);
        Assert.Equal(1, status.Library.RuleCount);
        Assert.Equal(("paused", "settingsChanged"), (status.Pass.State, status.Pass.Hold));
        Assert.Null(Row(fileId).ConversionCheckedAtUtc);
        Assert.Equal("avif", await new SettingsService(_world.Db.ScopeFactory()).GetAsync(SettingKeys.PageConversionFormat));

        Stamp(fileId, "avif:compact");
        await worker.StartPassAsync(CancellationToken.None);
        status = Saved(await controller.SavePlan(new PageConversionController.PlanDto(avif, [scrapes]), CancellationToken.None));
        Assert.Equal(("running", (string?)null), (status.Pass.State, status.Pass.Hold));
        Assert.NotNull(Row(fileId).ConversionCheckedAtUtc);

        status = Saved(await controller.SavePlan(new PageConversionController.PlanDto(
            new PageConversionController.SettingsDto(null, "balanced", false), []), CancellationToken.None));
        Assert.Equal(("paused", "conversionOff"), (status.Pass.State, status.Pass.Hold));
        Assert.Equal(0, status.Library.RuleCount);
        Assert.Null(await PageConversionOptions.LoadAsync(db, CancellationToken.None));
        Assert.NotNull(Row(fileId).ConversionCheckedAtUtc);
    }
}
