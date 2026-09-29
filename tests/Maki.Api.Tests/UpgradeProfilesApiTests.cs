using System.Text.Json;
using Maki.Api.Controllers;
using Maki.Api.Dtos;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Quality;
using Maki.Core.Sources;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public sealed class UpgradeProfilesApiTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static UpgradeProfilesController Profiles(MakiDbContext db) => new(new TestLocalizer(), db);

    private static QualityFormatsController Formats(MakiDbContext db) => new(new TestLocalizer(), db);

    private static UpgradeEvaluationService Evaluation(MakiDbContext db) => new(db, TestQuality.Create());

    [Fact]
    public void A_candidate_is_never_scored_on_the_current_files_name()
    {
        var penalty = new QualityFormat
        {
            Id = 1, Name = "LowQ",
            Conditions = [new FormatCondition(FormatConditionType.ReleaseNameMatches, @"\[LowQ\]", Required: true, Negate: false)]
        };
        var profile = new UpgradeProfile { Name = "P", FormatScores = [new FormatScore(1, -50)] };
        UpgradeProfileDefaults.Normalise(profile);
        var evaluator = new UpgradeEvaluator(profile, [penalty], TestQuality.Create());

        var candidate = evaluator.CandidateFor("Somewhere", null, "[LowQ] Series c001.cbz", 20, 1200, "jpg", null, "en");

        Assert.Null(candidate.ReleaseName);
        Assert.Equal(0, evaluator.Score(candidate).Score);
    }

    private static SeriesController Series(MakiDbContext db) =>
        new(new TestLocalizer(), db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);

    private SettingsController Settings(MakiDbContext db) => new(
        localizer: new TestLocalizer(), userLocales: new TestUserLocaleResolver(),
        settings: new SettingsService(_db.ScopeFactory()), naming: null!, flareSolverr: null!, prowlarr: null!,
        qbittorrent: null!, kavita: null!, sourceRegistry: null!, sourceAvailability: null!,
        mangaBakaDump: null!, embeddingModel: null!, embeddingStore: null!, embeddingStatus: null!,
        embeddingIndexer: null!, prebuiltIndex: null!, recoGraph: null!,
        recoGraphCache: null!, coReadInstaller: null!, coReadCache: null!, readerCohortInstaller: null!,
        readerCohortCache: null!, tasteVectorInstaller: null!, vectorIndexCache: null!,
        modelSwitcher: null!, db: db, updateCheck: null!, currentUser: new TestCurrentUser(1),
        userSettings: null!, kavitaUser: null!, schedulerFactory: null!, scopeFactory: _db.ScopeFactory(),
        logger: NullLogger<SettingsController>.Instance);

    private static UpgradeProfileWriteDto ProfileBody(
        string name = "Strict", string cutoff = "official", List<ProfileTierDto>? tiers = null,
        List<FormatScoreDto>? scores = null, int minScoreDelta = 1, int pageTolerance = 10,
        int upgradeUntilScore = 0) =>
        new(name, tiers ?? [], cutoff, true, minScoreDelta, upgradeUntilScore, scores ?? [], pageTolerance, true);

    [Fact]
    public async Task A_negative_upgrade_until_score_is_rejected_on_create_and_update()
    {
        var id = SeedProfile("Existing");
        using var db = _db.NewContext();

        var created = await Profiles(db).Create(ProfileBody(upgradeUntilScore: -1), default);
        var updated = await Profiles(db).Update(id, ProfileBody(name: "Existing", upgradeUntilScore: -5), default);

        Assert.Equal("error.upgrades.upgradeUntilScoreRange", Code(created));
        Assert.Equal("error.upgrades.upgradeUntilScoreRange", Code(updated));
        using var check = _db.NewContext();
        var stored = Assert.Single(check.UpgradeProfiles.ToList());
        Assert.Equal((0, 1), (stored.UpgradeUntilScore, stored.Version));
    }

    private static QualityFormatWriteDto FormatBody(string name = "Wide", params FormatConditionDto[] conditions) =>
        new(name, conditions.Length == 0 ? [new FormatConditionDto("minWidth", "1400", true, false)] : [.. conditions]);

    private static T Value<T>(IActionResult result) => result switch
    {
        ObjectResult { Value: T value } => value,
        _ => throw new Xunit.Sdk.XunitException($"Unexpected result {result}")
    };

    private static string Code(IActionResult result) =>
        JsonSerializer.SerializeToElement(((ObjectResult)result).Value).GetProperty("code").GetString()!;

    private int SeedProfile(string name = "Profile", QualityTier cutoff = QualityTier.Official,
        List<FormatScore>? scores = null, int upgradeUntilScore = 0)
    {
        using var db = _db.NewContext();
        var profile = new UpgradeProfile
        {
            Name = name, Cutoff = cutoff, FormatScores = scores ?? [], UpgradeUntilScore = upgradeUntilScore
        };
        UpgradeProfileDefaults.Normalise(profile);
        db.UpgradeProfiles.Add(profile);
        db.SaveChanges();
        return profile.Id;
    }

    private int SeedFormat(string name, params FormatCondition[] conditions)
    {
        using var db = _db.NewContext();
        var format = new QualityFormat { Name = name, Conditions = [.. conditions] };
        db.QualityFormats.Add(format);
        db.SaveChanges();
        return format.Id;
    }

    private int SeedFile(int seriesId, decimal number, QualityTier tier, bool measured = true, int width = 1000)
    {
        using var db = _db.NewContext();
        var file = new ChapterFile
        {
            SeriesId = seriesId,
            RelativePath = $"Series/{number}.cbz",
            SourceName = "import",
            Size = 10_000_000,
            Tier = tier,
            PageCount = measured ? 20 : null,
            MedianWidth = measured ? width : null,
            ImageFormat = measured ? "jpg" : null,
            MeasuredAtUtc = measured ? DateTime.UtcNow : null,
            DateAdded = DateTime.UtcNow
        };
        db.ChapterFiles.Add(file);
        db.SaveChanges();
        db.Chapters.Add(new Chapter { SeriesId = seriesId, Number = number, NumberRaw = number.ToString(), ChapterFileId = file.Id });
        db.SaveChanges();
        return file.Id;
    }

    [Fact]
    public async Task Create_normalises_tiers_and_returns_201()
    {
        using var db = _db.NewContext();

        var result = await Profiles(db).Create(
            ProfileBody(tiers: [new("scanlator", true), new("official", true), new("scanlator", false)]), default);

        Assert.Equal(201, ((ObjectResult)result).StatusCode);
        var dto = Value<UpgradeProfileDto>(result);
        Assert.Equal(["scanlator", "official", "volume", "aggregator", "unknown"], dto.Tiers.Select(t => t.Tier));
        Assert.Equal("official", dto.Cutoff);
        Assert.Equal(1, dto.Version);
    }

    [Fact]
    public async Task Update_bumps_the_version_and_list_reports_series_counts()
    {
        var id = SeedProfile("Old");
        _db.SeedSeries(configure: s => s.UpgradeProfileId = id);
        using var db = _db.NewContext();

        var updated = Value<UpgradeProfileDto>(await Profiles(db).Update(id, ProfileBody(name: "New"), default));
        var listed = Assert.Single(Value<IEnumerable<UpgradeProfileDto>>(await Profiles(db).List(default)));

        Assert.Equal(2, updated.Version);
        Assert.Equal("New", listed.Name);
        Assert.Equal(1, listed.SeriesCount);
    }

    [Fact]
    public async Task A_duplicate_name_is_a_conflict_whatever_its_case()
    {
        SeedProfile("Strict");
        using var db = _db.NewContext();

        var result = await Profiles(db).Create(ProfileBody(name: "STRICT"), default);

        Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal("error.upgrades.profileNameTaken", Code(result));
    }

    [Fact]
    public async Task Validation_rejects_a_disallowed_cutoff_bad_ranges_and_unknown_formats()
    {
        using var db = _db.NewContext();
        var controller = Profiles(db);

        var cutoff = await controller.Create(ProfileBody(tiers: [new("official", false)]), default);
        var delta = await controller.Create(ProfileBody(minScoreDelta: -1), default);
        var tolerance = await controller.Create(ProfileBody(pageTolerance: 101), default);
        var format = await controller.Create(ProfileBody(scores: [new(999, 5)]), default);
        var tier = await controller.Create(ProfileBody(cutoff: "legendary"), default);

        Assert.Equal("error.upgrades.cutoffNotAllowed", Code(cutoff));
        Assert.Equal("error.upgrades.minScoreDeltaRange", Code(delta));
        Assert.Equal("error.upgrades.pageToleranceRange", Code(tolerance));
        Assert.Equal("error.upgrades.formatNotFound", Code(format));
        Assert.Equal("error.upgrades.unknownTier", Code(tier));
        Assert.Empty(db.UpgradeProfiles);
    }

    [Fact]
    public async Task Delete_refuses_a_profile_a_series_uses()
    {
        var id = SeedProfile();
        _db.SeedSeries(configure: s => s.UpgradeProfileId = id);
        using var db = _db.NewContext();

        var result = await Profiles(db).Delete(id, default);

        Assert.Equal("error.upgrades.profileInUse", Code(result));
        Assert.Equal(409, ((ObjectResult)result).StatusCode);
    }

    [Fact]
    public async Task Delete_refuses_the_default_profile_and_removes_an_unused_one()
    {
        var used = SeedProfile("Default");
        var unused = SeedProfile("Spare");
        _db.SetConfig((SettingKeys.UpgradesDefaultProfileId, used.ToString()));
        using var db = _db.NewContext();

        var refused = await Profiles(db).Delete(used, default);
        var deleted = await Profiles(db).Delete(unused, default);

        Assert.Equal("error.upgrades.profileInUse", Code(refused));
        Assert.IsType<NoContentResult>(deleted);
        Assert.Equal(["Default"], db.UpgradeProfiles.Select(p => p.Name));
    }

    [Fact]
    public async Task Format_validation_rejects_bad_regex_bad_numbers_empty_lists_and_duplicates()
    {
        SeedFormat("Taken", new FormatCondition(FormatConditionType.MinPages, "1", true, false));
        using var db = _db.NewContext();
        var controller = Formats(db);

        var regex = await controller.Create(FormatBody("A", new FormatConditionDto("groupMatches", "([", true, false)), default);
        var number = await controller.Create(FormatBody("B", new FormatConditionDto("minWidth", "-5", true, false)), default);
        var empty = await controller.Create(new QualityFormatWriteDto("C", []), default);
        var taken = await controller.Create(FormatBody("taken"), default);
        var created = await controller.Create(
            FormatBody("D", new FormatConditionDto("releaseNameMatches", "digital", false, true)), default);

        Assert.Equal("error.upgrades.invalidRegex", Code(regex));
        Assert.Equal("error.upgrades.invalidNumber", Code(number));
        Assert.Equal("error.upgrades.conditionsRequired", Code(empty));
        Assert.Equal("error.upgrades.formatNameTaken", Code(taken));
        var dto = Value<QualityFormatDto>(created);
        Assert.Equal("releaseNameMatches", Assert.Single(dto.Conditions).Type);
    }

    [Fact]
    public async Task Deleting_a_format_removes_it_from_profile_scores_and_bumps_their_version()
    {
        var wide = SeedFormat("Wide", new FormatCondition(FormatConditionType.MinWidth, "1400", true, false));
        var png = SeedFormat("Png", new FormatCondition(FormatConditionType.ImageFormatIs, "png", true, false));
        var scoring = SeedProfile("Scoring", scores: [new(wide, 10), new(png, 3)]);
        var other = SeedProfile("Other", scores: [new(png, 3)]);
        using var db = _db.NewContext();

        Assert.IsType<NoContentResult>(await Formats(db).Delete(wide, default));

        using var check = _db.NewContext();
        var profiles = check.UpgradeProfiles.ToDictionary(p => p.Id);
        Assert.Equal([new FormatScore(png, 3)], profiles[scoring].FormatScores);
        Assert.Equal(2, profiles[scoring].Version);
        Assert.Equal(1, profiles[other].Version);
        Assert.Equal(["Png"], check.QualityFormats.Select(f => f.Name));
    }

    [Fact]
    public async Task Editing_a_formats_conditions_bumps_every_profile_scoring_it_and_a_rename_bumps_none()
    {
        var wide = SeedFormat("Wide", new FormatCondition(FormatConditionType.MinWidth, "1400", true, false));
        var png = SeedFormat("Png", new FormatCondition(FormatConditionType.ImageFormatIs, "png", true, false));
        var first = SeedProfile("First", scores: [new(wide, 10)]);
        var second = SeedProfile("Second", scores: [new(wide, 5), new(png, 3)]);
        var other = SeedProfile("Other", scores: [new(png, 3)]);

        using (var db = _db.NewContext())
        {
            Value<QualityFormatDto>(await Formats(db).Update(wide, FormatBody("Wider", new FormatConditionDto("minWidth", "1400", true, false)), default));
        }

        using (var check = _db.NewContext())
        {
            Assert.All(check.UpgradeProfiles.ToList(), p => Assert.Equal(1, p.Version));
        }

        using (var db = _db.NewContext())
        {
            Value<QualityFormatDto>(await Formats(db).Update(wide, FormatBody("Wider", new FormatConditionDto("minWidth", "1600", true, false)), default));
        }

        using var after = _db.NewContext();
        var profiles = after.UpgradeProfiles.ToDictionary(p => p.Id, p => p.Version);
        Assert.Equal((2, 2, 1), (profiles[first], profiles[second], profiles[other]));
    }

    [Fact]
    public async Task Settings_default_must_reference_an_existing_profile()
    {
        var id = SeedProfile();
        using var db = _db.NewContext();
        var controller = Settings(db);

        var rejected = await controller.SetUpgrades(new SettingsController.UpgradeSettings(true, id + 100), default);
        var accepted = await controller.SetUpgrades(new SettingsController.UpgradeSettings(true, id), default);
        var read = Value<SettingsController.UpgradeSettings>(await controller.GetUpgrades(default));
        await controller.SetUpgrades(new SettingsController.UpgradeSettings(false, null), default);
        var cleared = Value<SettingsController.UpgradeSettings>(await controller.GetUpgrades(default));

        Assert.Equal("error.upgrades.profileNotFound", Code(rejected));
        Assert.IsType<OkObjectResult>(accepted);
        Assert.Equal(new SettingsController.UpgradeSettings(true, id), read);
        Assert.Equal(new SettingsController.UpgradeSettings(false, null), cleared);
    }

    [Fact]
    public async Task Series_pin_accepts_an_existing_profile_clears_with_null_and_rejects_an_unknown_one()
    {
        var id = SeedProfile();
        var seriesId = _db.SeedSeries();
        using var db = _db.NewContext();
        var controller = Series(db);

        var rejected = await controller.SetUpgradeProfile(seriesId, new(id + 100), default);
        Assert.Equal("error.upgrades.profileNotFound", Code(rejected));

        Assert.IsType<OkObjectResult>(await controller.SetUpgradeProfile(seriesId, new(id), default));
        using (var check = _db.NewContext())
        {
            Assert.Equal(id, check.Series.Single().UpgradeProfileId);
            Assert.Equal(id, SeriesDto.FromEntity(check.Series.Single()).UpgradeProfileId);
        }

        Assert.IsType<OkObjectResult>(await controller.SetUpgradeProfile(seriesId, new(null), default));
        using var cleared = _db.NewContext();
        Assert.Null(cleared.Series.Single().UpgradeProfileId);
    }

    [Fact]
    public async Task Bulk_pin_sets_and_clears_many_series_at_once_and_rejects_an_unknown_profile()
    {
        var id = SeedProfile();
        var first = _db.SeedSeries("First");
        var second = _db.SeedSeries("Second");
        using var db = _db.NewContext();
        var controller = Series(db);

        var rejected = await controller.SetUpgradeProfileBulk(new([first, second], id + 100), default);
        var set = await controller.SetUpgradeProfileBulk(new([first, second, 999_999], id), default);

        Assert.Equal("error.upgrades.profileNotFound", Code(rejected));
        var body = ((OkObjectResult)set).Value!;
        Assert.Equal(2, body.GetType().GetProperty("updated")!.GetValue(body));
        using (var check = _db.NewContext())
        {
            Assert.All(check.Series.ToList(), s => Assert.Equal(id, s.UpgradeProfileId));
        }

        await controller.SetUpgradeProfileBulk(new([first], null), default);
        using var cleared = _db.NewContext();
        Assert.Null(cleared.Series.Single(s => s.Id == first).UpgradeProfileId);
        Assert.Equal(id, cleared.Series.Single(s => s.Id == second).UpgradeProfileId);
    }

    [Fact]
    public async Task Adding_a_series_rejects_an_unknown_profile_before_creating_anything()
    {
        using var db = _db.NewContext();

        var result = await Series(db).Add(
            new AddSeriesRequest("123", 1, ClientMutationId: Guid.NewGuid(), UpgradeProfileId: 42), default);

        Assert.Equal("error.upgrades.profileNotFound", Code(result));
    }

    [Fact]
    public async Task Cutoff_unmet_skips_unmeasured_files_met_files_and_series_without_a_profile()
    {
        var profile = SeedProfile("Strict", QualityTier.Official);
        var pinned = _db.SeedSeries("Pinned", configure: s => s.UpgradeProfileId = profile);
        var unpinned = _db.SeedSeries("Unpinned");
        var low = SeedFile(pinned, 2, QualityTier.Aggregator);
        SeedFile(pinned, 1, QualityTier.Official);
        SeedFile(pinned, 3, QualityTier.Aggregator, measured: false);
        SeedFile(unpinned, 1, QualityTier.Aggregator);
        using var db = _db.NewContext();

        var page = await Evaluation(db).CutoffUnmetAsync(null, 1, 50, default);
        var summary = await Evaluation(db).SummaryAsync(default);

        var row = Assert.Single(page.Rows);
        Assert.Equal(low, row.FileId);
        Assert.Equal(2m, row.ChapterNumber);
        Assert.Equal("official", row.Cutoff);
        Assert.Equal("Strict", row.ProfileName);
        Assert.False(row.Quality.CutoffMet);
        Assert.Equal(0, row.Quality.Score);
        Assert.Equal(new UpgradeSummaryDto(true), summary);
    }

    [Fact]
    public async Task Cutoff_unmet_falls_back_to_the_default_profile_and_sorts_by_title_then_chapter()
    {
        var wide = SeedFormat("Wide", new FormatCondition(FormatConditionType.MinWidth, "1400", true, false));
        var profile = SeedProfile("Default", QualityTier.Scanlator, scores: [new(wide, 10)], upgradeUntilScore: 10);
        _db.SetConfig((SettingKeys.UpgradesDefaultProfileId, profile.ToString()));
        var zeta = _db.SeedSeries("Zeta");
        var alpha = _db.SeedSeries("Alpha");
        SeedFile(zeta, 1, QualityTier.Aggregator);
        SeedFile(alpha, 5, QualityTier.Scanlator, width: 1000);
        SeedFile(alpha, 4, QualityTier.Scanlator, width: 1600);
        SeedFile(alpha, 2, QualityTier.Aggregator, width: 1600);
        using var db = _db.NewContext();

        var page = await Evaluation(db).CutoffUnmetAsync(null, 1, 2, default);

        Assert.Equal(3, page.Total);
        Assert.Equal([("Alpha", 2m), ("Alpha", 5m)], page.Rows.Select(r => (r.SeriesTitle, r.ChapterNumber!.Value)));
        Assert.Equal(10, page.Rows[0].Quality.Score);
        var second = await Evaluation(db).CutoffUnmetAsync(null, 2, 2, default);
        Assert.Equal("Zeta", Assert.Single(second.Rows).SeriesTitle);
        var onlyZeta = await Evaluation(db).CutoffUnmetAsync(zeta, 1, 50, default);
        Assert.Equal(1, onlyZeta.Total);
    }

    [Fact]
    public async Task Cutoff_unmet_reuses_its_evaluation_until_the_library_changes()
    {
        var profile = SeedProfile("Strict", QualityTier.Official);
        var pinned = _db.SeedSeries("Pinned", configure: s => s.UpgradeProfileId = profile);
        var low = SeedFile(pinned, 1, QualityTier.Aggregator);
        SeedFile(pinned, 2, QualityTier.Aggregator);
        using var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());

        using (var db = _db.NewContext())
        {
            Assert.Equal(2, (await new UpgradeEvaluationService(db, TestQuality.Create(), cache)
                .CutoffUnmetAsync(null, 1, 50, default)).Total);
            Assert.Equal(2, (await new UpgradeEvaluationService(db, TestQuality.Create(), cache)
                .CutoffUnmetAsync(null, 2, 1, default)).Total);
        }

        using (var db = _db.NewContext())
        {
            db.ChapterFiles.Single(f => f.Id == low).Trusted = true;
            db.SaveChanges();
        }

        using (var db = _db.NewContext())
        {
            var page = await new UpgradeEvaluationService(db, TestQuality.Create(), cache)
                .CutoffUnmetAsync(null, 1, 50, default);
            Assert.Equal(1, page.Total);
            Assert.DoesNotContain(page.Rows, r => r.FileId == low);
        }
    }

    [Fact]
    public async Task Cutoff_unmet_hides_series_in_root_folders_the_caller_cannot_see()
    {
        var profile = SeedProfile();
        var visible = _db.SeedSeries("Visible", configure: s => s.UpgradeProfileId = profile);
        var hidden = _db.SeedSeries("Hidden", configure: s => s.UpgradeProfileId = profile);
        SeedFile(visible, 1, QualityTier.Aggregator);
        SeedFile(hidden, 1, QualityTier.Aggregator);
        var reader = _db.SeedUser("reader", allRootFolders: false);
        using (var setup = _db.NewContext())
        {
            var rootFolderId = setup.Series.Single(s => s.Id == visible).RootFolderId;
            setup.UserRootFolders.Add(new UserRootFolder { UserId = reader, RootFolderId = rootFolderId });
            setup.SaveChanges();
        }

        using var db = _db.NewContext(reader, allRootFolders: false);
        var page = await Evaluation(db).CutoffUnmetAsync(null, 1, 50, default);

        Assert.Equal(["Visible"], page.Rows.Select(r => r.SeriesTitle));
    }

    [Fact]
    public async Task Cutoff_unmet_is_empty_and_unconfigured_when_nothing_resolves_to_a_profile()
    {
        SeedProfile();
        var seriesId = _db.SeedSeries();
        SeedFile(seriesId, 1, QualityTier.Aggregator);
        using var db = _db.NewContext();

        var page = await Evaluation(db).CutoffUnmetAsync(null, 1, 50, default);
        var summary = await Evaluation(db).SummaryAsync(default);

        Assert.Empty(page.Rows);
        Assert.Equal(new UpgradeSummaryDto(false), summary);
    }

    [Fact]
    public async Task Chapter_list_fills_score_and_cutoff_only_for_measured_files_under_a_profile()
    {
        var profile = SeedProfile(cutoff: QualityTier.Official);
        var pinned = _db.SeedSeries("Pinned", configure: s => s.UpgradeProfileId = profile);
        var plain = _db.SeedSeries("Plain");
        SeedFile(pinned, 1, QualityTier.Aggregator);
        SeedFile(pinned, 2, QualityTier.Aggregator, measured: false);
        SeedFile(plain, 1, QualityTier.Aggregator);
        using var db = _db.NewContext();
        var controller = ChapterController(db);

        var pinnedRows = Qualities(await controller.List(pinned, Evaluation(db), default));
        var plainRows = Qualities(await controller.List(plain, Evaluation(db), default));

        Assert.Equal([(0, false), (null, null)], pinnedRows.Select(q => (q.Score, q.CutoffMet)));
        Assert.Equal([(null, null)], plainRows.Select(q => (q.Score, q.CutoffMet)));
    }

    [Fact]
    public async Task Chapter_list_scores_formats_under_the_default_profile()
    {
        var wide = SeedFormat("Wide", new FormatCondition(FormatConditionType.MinWidth, "1400", true, false));
        SeedFormat("Unscored", new FormatCondition(FormatConditionType.MinPages, "1", true, false));
        var profile = SeedProfile(cutoff: QualityTier.Aggregator, scores: [new(wide, 7)], upgradeUntilScore: 7);
        _db.SetConfig((SettingKeys.UpgradesDefaultProfileId, profile.ToString()));
        var seriesId = _db.SeedSeries();
        SeedFile(seriesId, 1, QualityTier.Aggregator, width: 1600);
        SeedFile(seriesId, 2, QualityTier.Aggregator, width: 800);
        using var db = _db.NewContext();

        var rows = Qualities(await ChapterController(db).List(seriesId, Evaluation(db), default));

        Assert.Equal([(7, true), (0, false)], rows.Select(q => (q.Score, q.CutoffMet)));
    }

    private static List<ChapterFileQualityDto> Qualities(IActionResult result) =>
        [.. ((IEnumerable<object>)((OkObjectResult)result).Value!)
            .Select(row => (ChapterFileQualityDto)row.GetType().GetProperty("FileQuality")!.GetValue(row)!)];

    private ChapterController ChapterController(MakiDbContext db) => new(
        new TestLocalizer(),
        db,
        new DownloadQueueService(_db.ScopeFactory(), TimeProvider.System, null!, NullLogger<DownloadQueueService>.Instance),
        new StatsEventService(db),
        new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance),
        new SourceRegistry([]),
        new SourceChapterListCache(TimeProvider.System, NullLogger<SourceChapterListCache>.Instance),
        new DownloadBatchNotifier(
            new RecordingNotifications(), new RecordingInbox(), new TestLocalizer(),
            new TestUserLocaleResolver(), TimeProvider.System,
            NullLogger<DownloadBatchNotifier>.Instance),
        new TestCurrentUser(1),
        NullLogger<ChapterController>.Instance);

    [Fact]
    public async Task Seeding_runs_once_and_does_not_come_back_after_the_profiles_are_deleted()
    {
        using (var db = _db.NewContext())
        {
            var seeder = new UpgradeProfileSeeder(db, NullLogger<UpgradeProfileSeeder>.Instance);
            await seeder.RunOnceAsync();
            await seeder.RunOnceAsync();
        }

        using (var db = _db.NewContext())
        {
            var profiles = db.UpgradeProfiles.ToDictionary(p => p.Name);
            Assert.Equal(2, db.QualityFormats.Count());
            Assert.Equal(
                [UpgradeProfileSeeder.NeverUpgrade, UpgradeProfileSeeder.ReplaceAggregatorCopies,
                    UpgradeProfileSeeder.UpgradeToVolumes, UpgradeProfileSeeder.UpgradeToOfficial],
                profiles.Keys.Order());
            Assert.False(profiles[UpgradeProfileSeeder.NeverUpgrade].UpgradesEnabled);
            Assert.Equal(QualityTier.Scanlator, profiles[UpgradeProfileSeeder.ReplaceAggregatorCopies].Cutoff);
            Assert.Equal(QualityTier.Official, profiles[UpgradeProfileSeeder.UpgradeToOfficial].Cutoff);
            Assert.Equal(QualityTier.Volume, profiles[UpgradeProfileSeeder.UpgradeToVolumes].Cutoff);
            var scores = profiles[UpgradeProfileSeeder.NeverUpgrade].FormatScores;
            Assert.Equal(2, scores.Count);
            Assert.All(profiles.Values, p =>
            {
                Assert.False(string.IsNullOrWhiteSpace(p.Description));
                Assert.Equal(scores, p.FormatScores);
                Assert.False(p.AllowReplacingUnknown);
                Assert.Equal(UpgradeProfileSeeder.MeasuredWeight, p.ResolutionWeight);
                Assert.Equal(UpgradeProfileSeeder.MeasuredWeight, p.CompressionWeight);
                Assert.Equal(UpgradeProfileDefaults.DefaultOrder, p.Tiers.Select(t => t.Tier));
            });
            Assert.False(db.AppConfig.Any(c => c.Key == SettingKeys.UpgradesDefaultProfileId));

            db.UpgradeProfiles.RemoveRange(profiles.Values);
            db.SaveChanges();
        }

        using (var db = _db.NewContext())
        {
            await new UpgradeProfileSeeder(db, NullLogger<UpgradeProfileSeeder>.Instance).RunOnceAsync();
            Assert.Empty(db.UpgradeProfiles);
        }
    }

    [Fact]
    public async Task Seeding_leaves_a_profile_or_format_whose_name_is_taken_alone()
    {
        using (var db = _db.NewContext())
        {
            db.QualityFormats.Add(new QualityFormat
            {
                Name = UpgradeProfileSeeder.RawOrMachineTranslated,
                Conditions = [new FormatCondition(FormatConditionType.ReleaseNameMatches, "RAW", true, false)]
            });
            db.UpgradeProfiles.Add(new UpgradeProfile { Name = UpgradeProfileSeeder.ReplaceAggregatorCopies, Cutoff = QualityTier.Official });
            db.SaveChanges();

            await new UpgradeProfileSeeder(db, NullLogger<UpgradeProfileSeeder>.Instance).RunOnceAsync();
        }

        using (var db = _db.NewContext())
        {
            var raw = Assert.Single(db.QualityFormats.ToList(), f => f.Name == UpgradeProfileSeeder.RawOrMachineTranslated);
            Assert.Equal("RAW", raw.Conditions[0].Value);
            var mine = Assert.Single(db.UpgradeProfiles.ToList(), p => p.Name == UpgradeProfileSeeder.ReplaceAggregatorCopies);
            Assert.Equal(QualityTier.Official, mine.Cutoff);
            Assert.Null(mine.Description);
            Assert.Contains(db.UpgradeProfiles.ToList(), p => p.Name == UpgradeProfileSeeder.UpgradeToOfficial &&
                p.FormatScores.Contains(new FormatScore(raw.Id, -100)));
        }
    }

    [Fact]
    public async Task Untouched_starters_from_the_previous_seed_are_renamed_and_edited_or_deleted_ones_are_left_alone()
    {
        using (var db = _db.NewContext())
        {
            var raw = new QualityFormat { Name = UpgradeProfileSeeder.RawOrMachineTranslated, Conditions = [new(FormatConditionType.MinPages, "1", true, false)] };
            var ripper = new QualityFormat { Name = UpgradeProfileSeeder.TrustedDigitalRipper, Conditions = [new(FormatConditionType.MinPages, "1", true, false)] };
            db.QualityFormats.AddRange(raw, ripper);
            db.SaveChanges();
            db.UpgradeProfiles.AddRange(
                new UpgradeProfile { Name = "Balanced", Cutoff = QualityTier.Scanlator, FormatScores = [new(raw.Id, -100)] },
                new UpgradeProfile { Name = "Official releases", Cutoff = QualityTier.Official, Version = 4 });
            db.AppConfig.Add(new AppConfigEntry { Key = UpgradeProfileSeeder.PreviousMarkerKey, Value = "x" });
            db.SaveChanges();

            await new UpgradeProfileSeeder(db, NullLogger<UpgradeProfileSeeder>.Instance).RunOnceAsync();
        }

        using (var db = _db.NewContext())
        {
            var profiles = db.UpgradeProfiles.ToDictionary(p => p.Name);
            Assert.Equal(["Official releases", UpgradeProfileSeeder.ReplaceAggregatorCopies], profiles.Keys.Order());
            var renamed = profiles[UpgradeProfileSeeder.ReplaceAggregatorCopies];
            Assert.NotNull(renamed.Description);
            Assert.Equal(2, renamed.FormatScores.Count);
            Assert.Equal(2, renamed.Version);
            Assert.Null(profiles["Official releases"].Description);
            Assert.Equal(2, db.QualityFormats.Count());
        }
    }

    /// <summary>Bits per pixel are medians from sampling a real library, per source.</summary>
    [Theory]
    [InlineData("Berserk v01 (2019) (Digital) (1r0n).cbz", "1r0n", 2250, "jpg", 2.3, 36)]
    [InlineData("Raw Hero v01 (2018) (Digital) (Oak).cbz", "Oak", 1000, "jpg", 1.5, 20)]
    [InlineData("Some Series c012 (Raw).cbz", null, 1000, "jpg", 1.5, -100)]
    [InlineData("Some Series c012 [MTL].cbz", null, 1000, "jpg", 1.5, -100)]
    [InlineData(null, null, 1000, "png", 3.93, 8)]
    [InlineData(null, null, 800, "jpg", 2.13, 2)]
    [InlineData(null, null, 720, "webp", 0.47, -17)]
    public async Task The_starter_profiles_score_typical_files(
        string? releaseName, string? group, int width, string format, double bitsPerPixel, int expected)
    {
        using var db = _db.NewContext();
        await new UpgradeProfileSeeder(db, NullLogger<UpgradeProfileSeeder>.Instance).RunOnceAsync();
        var profile = db.UpgradeProfiles.Single(p => p.Name == UpgradeProfileSeeder.UpgradeToVolumes);
        const int height = 1500, pages = 20;
        var size = (long)(bitsPerPixel * width * height / 8) * pages;
        var candidate = new QualityCandidate(
            QualityTier.Volume, null, null, group, releaseName, pages, width, format, size, "en", height);

        Assert.Equal(expected, QualityScorer.Score(profile, db.QualityFormats.ToList(), candidate).Score);
    }
}
