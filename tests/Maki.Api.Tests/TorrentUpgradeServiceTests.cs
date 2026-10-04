using System.IO.Compression;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Inbox;
using Maki.Core.Parsing;
using Maki.Core.Quality;
using Maki.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>A Prowlarr and qBittorrent stand-in: search answers from a list, a grab is recorded.</summary>
internal sealed class FakeReleases() : ReleaseService(null!, null!, null!, null!, NullLogger<ReleaseService>.Instance)
{
    public List<ReleaseDto> Results { get; } = [];
    public List<(int SeriesId, string Query)> Queries { get; } = [];
    public List<(int SeriesId, ReleaseDto Release, DownloadOrigin Origin, int? UserId, string? Info)> Grabs { get; } = [];
    public Exception? SearchError { get; set; }
    public Action? OnSearch { get; set; }

    public override Task<ReleaseSearchResult> SearchAsync(int seriesId, string? query = null, CancellationToken ct = default)
    {
        Queries.Add((seriesId, query ?? ""));
        OnSearch?.Invoke();
        if (SearchError is not null)
        {
            throw SearchError;
        }

        return Task.FromResult(new ReleaseSearchResult(query ?? "", [.. Results]));
    }

    public override Task<DownloadQueueItem> GrabAsync(int seriesId, ReleaseDto release, DownloadOrigin origin,
        int? queuedByUserId, string? upgradeInfoJson, CancellationToken ct = default)
    {
        Grabs.Add((seriesId, release, origin, queuedByUserId, upgradeInfoJson));
        return Task.FromResult(new DownloadQueueItem { Id = 1000 + Grabs.Count, SeriesId = seriesId, Origin = origin });
    }

    public static ReleaseDto Release(string title, long size = 100L * 1024 * 1024, int seeders = 5, string? guid = null) =>
        new(guid ?? title, title, size, "Nyaa", seeders, 0, "torrent", null, "magnet:?xt=urn:btih:abc", null);
}

internal sealed class SettableClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;

    /// <summary>Noon local time, so a job's "after the scan hour" check passes whatever the machine's zone.</summary>
    public static SettableClock LocalNoon() =>
        new(new DateTimeOffset(DateTime.SpecifyKind(new DateTime(2026, 9, 29, 12, 0, 0), DateTimeKind.Local)));
}

[Collection(ConfigDirCollection.Name)]
public class TorrentUpgradeServiceTests : IDisposable
{
    private readonly UpgradeWorld _world = new();
    private readonly FakeReleases _releases = new();
    private readonly SettableClock _clock = SettableClock.LocalNoon();

    public TorrentUpgradeServiceTests()
    {
        _world.Seed(p => p.Cutoff = QualityTier.Volume, s => s.Title = "Kaguya");
        _world.Settings.Set(SettingKeys.UpgradesEnabled, "true");
        _world.Settings.Set(SettingKeys.ProwlarrUrl, "http://prowlarr.test");
        _world.Settings.Set(SettingKeys.ProwlarrApiKey, "key");
    }

    public void Dispose() => _world.Dispose();

    private TorrentUpgradeService Service(MakiDbContext db) => new(db,
        new UpgradeEvaluationService(db, TestQuality.Create(_world.Registry)), _releases, _world.Inbox, _world.Settings,
        _clock, NullLogger<TorrentUpgradeService>.Instance);

    private int Chapter(decimal number, int? volume, bool withFile = true, bool wanted = true, Action<ChapterFile>? file = null,
        string language = "en")
    {
        var (chapterId, _) = _world.Chapter(number, file, withFile, wanted);
        using var db = _world.Db.NewContext();
        var chapter = db.Chapters.Single(c => c.Id == chapterId);
        chapter.Volume = volume;
        chapter.Language = language;
        db.SaveChanges();
        return chapterId;
    }

    private async Task<SeriesVolumeSearchResult> SearchAsync(bool respectInterval = false, bool ignoreGlobalSwitch = false)
    {
        using var db = _world.Db.NewContext();
        return await Service(db).SearchSeriesAsync(_world.SeriesId, CancellationToken.None, respectInterval,
            ignoreGlobalSwitch);
    }

    private async Task<TorrentCandidateView> EvaluateAsync(string title, long size = 100L * 1024 * 1024)
    {
        using var db = _world.Db.NewContext();
        return (await Service(db).EvaluateAsync(_world.SeriesId, [FakeReleases.Release(title, size)], CancellationToken.None)).Single();
    }

    private Dictionary<int, bool> WantedFlags()
    {
        using var db = _world.Db.NewContext();
        return db.Chapters.AsNoTracking().ToDictionary(c => c.Id, c => c.Wanted);
    }

    [Fact]
    public async Task Chapter_states_follow_the_file_the_profile_and_the_wanted_flag()
    {
        var below = Chapter(1, 1);
        Chapter(2, 1, file: f => f.Tier = QualityTier.Volume);
        Chapter(3, 1, file: f => f.Trusted = true);
        Chapter(4, 1, withFile: false, wanted: true);
        Chapter(5, 1, withFile: false, wanted: false);

        var view = await EvaluateAsync("Kaguya v01 + 006-007 (Digital) (1r0n)");

        var v = view.Verdict;
        Assert.True(view.TitleMatched);
        Assert.Equal(QualityTier.Volume, view.Tier);
        Assert.Equal((1, 2, 1, 1, 2), (v.UpgradeCount, v.AlreadyMetCount, v.MissingCount, v.SkippedCount, v.UnknownCount));
        using var db = _world.Db.NewContext();
        Assert.Equal([db.Chapters.Single(c => c.Id == below).ChapterFileId!.Value], v.ReplacedFileIds);
        Assert.Equal(SpanOutcome.Proposal, v.Outcome);
        Assert.Contains(SpanVerdictReasons.UnknownChapters, v.Reasons);
    }

    [Fact]
    public async Task Volume_mapping_comes_from_the_chapter_column_and_from_an_existing_volume_file()
    {
        Chapter(1, 1);
        Chapter(2, 1);
        var volumeFile = Path.Combine(_world.Library, "Series", "Kaguya v02.cbz");
        Directory.CreateDirectory(Path.GetDirectoryName(volumeFile)!);
        using (var zip = ZipFile.Open(volumeFile, ZipArchiveMode.Create))
        {
            zip.CreateEntry("Kaguya - c003 - p001.png");
            zip.CreateEntry("Kaguya - c004 - p001.png");
        }

        int volumeFileId;
        using (var db = _world.Db.NewContext())
        {
            var row = new ChapterFile
            {
                SeriesId = _world.SeriesId, RelativePath = "Series/Kaguya v02.cbz", SourceName = "rescan",
                Tier = QualityTier.Aggregator, MeasuredAtUtc = DateTime.UtcNow, MedianWidth = 800, PageCount = 40,
                DateAdded = DateTime.UtcNow
            };
            db.ChapterFiles.Add(row);
            db.SaveChanges();
            volumeFileId = row.Id;
        }

        Chapter(3, null, withFile: false);
        Chapter(4, null, withFile: false);
        using (var db = _world.Db.NewContext())
        {
            foreach (var chapter in db.Chapters.Where(c => c.Number == 3 || c.Number == 4))
            {
                chapter.ChapterFileId = volumeFileId;
            }

            db.SaveChanges();
        }

        var known = await EvaluateAsync("Kaguya v01-02 (Digital) (1r0n)");
        var unknown = await EvaluateAsync("Kaguya v01-03 (Digital) (1r0n)");

        Assert.DoesNotContain(SpanVerdictReasons.VolumeSpanUnknown, known.Verdict.Reasons);
        Assert.Equal(4, known.Verdict.UpgradeCount);
        Assert.Contains(volumeFileId, known.Verdict.ReplacedFileIds);
        Assert.Contains(SpanVerdictReasons.VolumeSpanUnknown, unknown.Verdict.Reasons);
    }

    [Fact]
    public async Task A_chapter_read_off_a_trailing_number_is_at_most_a_proposal()
    {
        Chapter(2, 1);

        // A group tag makes it a scanlator copy, which outranks the aggregator file on disk; without
        // one the tier is unknown and the release is no upgrade at all.
        var view = await EvaluateAsync("Kaguya 2 (Digital) (1r0n)");

        Assert.Equal(SpanOutcome.Proposal, view.Verdict.Outcome);
        Assert.Contains(SpanVerdictReasons.TrailingNumber, view.Verdict.Reasons);
        Assert.Equal(1, view.Verdict.UpgradeCount);
    }

    [Fact]
    public async Task Without_english_the_verdict_judges_only_the_language_with_the_most_files()
    {
        Action<ChapterFile> Named(string suffix) => f => f.RelativePath = f.RelativePath.Replace(".cbz", $" [{suffix}].cbz");
        Chapter(1, 1, file: Named("es"), language: "es");
        Chapter(2, 1, withFile: false, language: "es");
        Chapter(3, 1, withFile: false, language: "es");
        var fr1 = Chapter(1, 1, file: Named("fr"), language: "fr");
        var fr2 = Chapter(2, 1, file: Named("fr"), language: "fr");
        _releases.Results.Add(FakeReleases.Release("Kaguya v01 (Digital) (1r0n)"));

        var view = await EvaluateAsync("Kaguya v01 (Digital) (1r0n)");
        await SearchAsync();

        using var db = _world.Db.NewContext();
        var frFiles = db.Chapters.Where(c => c.Id == fr1 || c.Id == fr2).Select(c => c.ChapterFileId!.Value).ToHashSet();
        Assert.Equal("fr", view.Language);
        Assert.Equal((2, 0), (view.Verdict.UpgradeCount, view.Verdict.MissingCount));
        Assert.Equal(frFiles, view.Verdict.ReplacedFileIds.ToHashSet());
        var info = TorrentUpgradeInfo.Parse(Assert.Single(_releases.Grabs).Info)!;
        Assert.Equal("fr", info.Language);
        Assert.Equal(frFiles, info.ReplacedFileIds.ToHashSet());
    }

    [Fact]
    public void A_file_count_tie_goes_to_the_language_with_more_rows_then_to_ordinal_order()
    {
        List<Chapter> Rows(params (string Language, int? FileId)[] rows) =>
            [.. rows.Select((r, i) => new Chapter { Id = i + 1, Number = i + 1, Language = r.Language, ChapterFileId = r.FileId })];

        Assert.Equal("fr", TorrentUpgradeRules.PrimaryLanguage(Rows(("es", 1), ("fr", 2), ("fr", null))).Language);
        Assert.Equal("de", TorrentUpgradeRules.PrimaryLanguage(Rows(("fr", 1), ("de", 2))).Language);
        Assert.Equal("en", TorrentUpgradeRules.PrimaryLanguage(Rows(("fr", 1), ("fr", 2), ("en", null))).Language);
    }

    [Fact]
    public async Task No_profile_ignores_every_release()
    {
        Chapter(1, 1);
        using (var db = _world.Db.NewContext())
        {
            db.Series.Single().UpgradeProfileId = null;
            db.SaveChanges();
        }

        var view = await EvaluateAsync("Kaguya v01 (Digital) (1r0n)");

        Assert.Equal(SpanOutcome.Ignore, view.Verdict.Outcome);
        Assert.Equal([SpanVerdictReasons.NoProfile], view.Verdict.Reasons);
    }

    [Fact]
    public async Task An_auto_grab_goes_through_the_release_service_as_an_upgrade_with_no_user()
    {
        Chapter(1, 1);
        Chapter(2, 1);
        Chapter(3, 2);
        _releases.Results.Add(FakeReleases.Release("Kaguya v01 (Digital) (1r0n)"));
        var wanted = WantedFlags();

        var result = await SearchAsync();

        Assert.Equal("", Assert.Single(_releases.Queries).Query);
        var grab = Assert.Single(_releases.Grabs);
        Assert.Equal(DownloadOrigin.Upgrade, grab.Origin);
        Assert.Null(grab.UserId);
        var info = TorrentUpgradeInfo.Parse(grab.Info)!;
        Assert.Equal(2, info.ReplacedFileIds.Count);
        Assert.Equal(_world.ProfileId, info.ProfileId);
        Assert.Equal(1001, result.Grabbed);
        Assert.Null(result.ProposalId);
        Assert.Equal(wanted, WantedFlags());
    }

    [Fact]
    public async Task A_proposal_is_written_and_announced()
    {
        Chapter(1, 1);
        _releases.Results.Add(FakeReleases.Release("Kaguya v01 (Digital) (1r0n)", size: 900L * 1024 * 1024));
        var wanted = WantedFlags();

        var result = await SearchAsync();

        Assert.Empty(_releases.Grabs);
        Assert.NotNull(result.ProposalId);
        using var db = _world.Db.NewContext();
        var row = db.TorrentProposals.Single();
        Assert.Equal(TorrentProposalStatus.Pending, row.Status);
        Assert.Contains(SpanVerdictReasons.OverBudget, row.ReasonsJson);
        Assert.Equal(1, row.UpgradeCount);
        var raised = Assert.Single(_world.Inbox.RaisedForSeries);
        Assert.Equal(InboxEventType.TorrentProposalPending, raised.Type);
        Assert.Equal($"/series/{_world.SeriesId}?tab=chapters", raised.Message.Url);
        Assert.NotNull(db.Series.Single().LastVolumeSearchUtc);
        Assert.Equal(wanted, WantedFlags());
    }

    [Fact]
    public async Task A_dismissed_release_is_never_proposed_or_grabbed_again()
    {
        Chapter(1, 1);
        _releases.Results.Add(FakeReleases.Release("Kaguya v01 (Digital) (1r0n)", size: 900L * 1024 * 1024, guid: "g1"));
        _releases.Results.Add(FakeReleases.Release("Kaguya v01 (Digital) (other)", guid: "g2"));
        using (var db = _world.Db.NewContext())
        {
            db.TorrentProposals.AddRange(
                new TorrentProposal { SeriesId = _world.SeriesId, ReleaseGuid = "g1", Status = TorrentProposalStatus.Dismissed },
                new TorrentProposal { SeriesId = _world.SeriesId, ReleaseGuid = "g2", Status = TorrentProposalStatus.Dismissed });
            db.SaveChanges();
        }

        var result = await SearchAsync();

        Assert.True(result.Searched);
        Assert.Null(result.ProposalId);
        Assert.Empty(_releases.Grabs);
        using var check = _world.Db.NewContext();
        Assert.All(check.TorrentProposals.ToList(), p => Assert.Equal(TorrentProposalStatus.Dismissed, p.Status));
        Assert.Empty(_world.Inbox.RaisedForSeries);
    }

    [Fact]
    public async Task A_pending_proposal_blocks_the_search()
    {
        Chapter(1, 1);
        using (var db = _world.Db.NewContext())
        {
            db.TorrentProposals.Add(new TorrentProposal { SeriesId = _world.SeriesId, ReleaseGuid = "g", Status = TorrentProposalStatus.Pending });
            db.SaveChanges();
        }

        var result = await SearchAsync();

        Assert.Equal(VolumeSearchReasons.PendingProposal, result.Reason);
        Assert.Empty(_releases.Queries);
    }

    [Fact]
    public async Task Each_rule_that_makes_a_series_ineligible_names_itself()
    {
        Chapter(1, 1);

        _world.Settings.Set(SettingKeys.UpgradesVolumeSearch, "false");
        Assert.Equal(VolumeSearchReasons.Disabled, (await SearchAsync()).Reason);
        _world.Settings.Set(SettingKeys.UpgradesVolumeSearch, "true");
        _world.Settings.Set(SettingKeys.UpgradesEnabled, "false");
        Assert.Equal(VolumeSearchReasons.Disabled, (await SearchAsync()).Reason);
        _world.Settings.Set(SettingKeys.UpgradesEnabled, "true");

        using (var db = _world.Db.NewContext())
        {
            db.UpgradeProfiles.Single().UpgradesEnabled = false;
            db.SaveChanges();
        }

        Assert.Equal(VolumeSearchReasons.ProfileDisabled, (await SearchAsync(ignoreGlobalSwitch: true)).Reason);
        using (var db = _world.Db.NewContext())
        {
            db.UpgradeProfiles.Single().UpgradesEnabled = true;
            db.SaveChanges();
        }

        _world.Settings.Set(SettingKeys.ProwlarrApiKey, "");
        Assert.Equal(VolumeSearchReasons.NoProwlarr, (await SearchAsync()).Reason);
        _world.Settings.Set(SettingKeys.ProwlarrApiKey, "key");

        using (var db = _world.Db.NewContext())
        {
            db.Series.Single().Incognito = IncognitoMode.Full;
            db.SaveChanges();
        }

        _world.Settings.Set(SettingKeys.UpgradesScanIncognito, "false");
        Assert.Equal(VolumeSearchReasons.Incognito, (await SearchAsync()).Reason);
        _world.Settings.Set(SettingKeys.UpgradesScanIncognito, "true");

        using (var db = _world.Db.NewContext())
        {
            db.UpgradeProfiles.Single().Cutoff = QualityTier.Official;
            db.SaveChanges();
        }

        Assert.Equal(VolumeSearchReasons.Cutoff, (await SearchAsync()).Reason);

        using (var db = _world.Db.NewContext())
        {
            db.UpgradeProfiles.Single().Cutoff = QualityTier.Volume;
            db.ChapterFiles.Single().Trusted = true;
            db.SaveChanges();
        }

        Assert.Equal(VolumeSearchReasons.NothingBelowCutoff, (await SearchAsync()).Reason);

        using (var db = _world.Db.NewContext())
        {
            db.Series.Single().UpgradeProfileId = null;
            db.SaveChanges();
        }

        Assert.Equal(VolumeSearchReasons.NoProfile, (await SearchAsync()).Reason);
        Assert.Empty(_releases.Queries);
    }

    [Fact]
    public async Task The_manual_search_overrides_only_the_instance_switches()
    {
        Chapter(1, 1);
        _world.Settings.Set(SettingKeys.UpgradesEnabled, "false");
        _world.Settings.Set(SettingKeys.UpgradesVolumeSearch, "false");

        Assert.Equal(VolumeSearchReasons.Disabled, (await SearchAsync()).Reason);
        _releases.Results.Add(FakeReleases.Release("Kaguya v01 (Digital) (1r0n)"));
        var manual = await SearchAsync(ignoreGlobalSwitch: true);
        Assert.True(manual.Searched);
        Assert.Null(manual.Reason);
        Assert.Empty(_releases.Grabs);
        Assert.NotNull(manual.ProposalId);
        using (var db = _world.Db.NewContext())
        {
            Assert.Contains(SpanVerdictReasons.UpgradesDisabled, db.TorrentProposals.Single().ReasonsJson);
            db.TorrentProposals.RemoveRange(db.TorrentProposals);
            db.SaveChanges();
        }

        _world.Settings.Set(SettingKeys.ProwlarrUrl, "");
        Assert.Equal(VolumeSearchReasons.NoProwlarr, (await SearchAsync(ignoreGlobalSwitch: true)).Reason);
        _world.Settings.Set(SettingKeys.ProwlarrUrl, "http://prowlarr.test");
        using (var db = _world.Db.NewContext())
        {
            db.UpgradeProfiles.Single().Cutoff = QualityTier.Official;
            db.SaveChanges();
        }

        Assert.Equal(VolumeSearchReasons.Cutoff, (await SearchAsync(ignoreGlobalSwitch: true)).Reason);
    }

    [Fact]
    public async Task The_weekly_interval_binds_the_job_but_not_the_manual_search()
    {
        Chapter(1, 1);
        using (var db = _world.Db.NewContext())
        {
            db.Series.Single().LastVolumeSearchUtc = _clock.Now.UtcDateTime.AddDays(-2);
            db.SaveChanges();
        }

        Assert.Equal(VolumeSearchReasons.Recent, (await SearchAsync(respectInterval: true)).Reason);
        using (var db = _world.Db.NewContext())
        {
            Assert.Empty(await Service(db).CandidateSeriesAsync(CancellationToken.None));
        }

        Assert.True((await SearchAsync()).Searched);
        _clock.Now = _clock.Now.AddDays(8);
        using (var db = _world.Db.NewContext())
        {
            Assert.Equal([_world.SeriesId], await Service(db).CandidateSeriesAsync(CancellationToken.None));
        }
    }

    [Fact]
    public async Task The_last_search_time_is_written_even_when_the_search_fails()
    {
        Chapter(1, 1);
        _releases.SearchError = new HttpRequestException("prowlarr down");

        var result = await SearchAsync();

        Assert.Equal(VolumeSearchReasons.SearchFailed, result.Reason);
        using var db = _world.Db.NewContext();
        Assert.Equal(_clock.Now.UtcDateTime, db.Series.Single().LastVolumeSearchUtc);
    }

    private void RenameSeries(string title)
    {
        using var db = _world.Db.NewContext();
        db.Series.Single().Title = title;
        db.SaveChanges();
    }

    private void SeedEightFilesAndTwoGaps()
    {
        for (var n = 1; n <= 8; n++)
        {
            Chapter(n, null);
        }

        Chapter(9, null, withFile: false, wanted: true);
        Chapter(10, null, withFile: false, wanted: true);
    }

    [Fact]
    public async Task A_digital_pack_with_no_span_is_a_whole_series_proposal_that_never_auto_grabs()
    {
        RenameSeries("Boy Meets Maria");
        SeedEightFilesAndTwoGaps();
        _world.Settings.Set(SettingKeys.UpgradesVolumeMissingTolerance, "50");
        const string title = "Boy Meets Maria (2021) (Digital) (danke-Empire)";
        _releases.Results.Add(FakeReleases.Release(title, size: 10L * 1024 * 1024));

        var view = await EvaluateAsync(title, size: 10L * 1024 * 1024);
        var result = await SearchAsync();

        Assert.True(view.WholeSeries);
        Assert.Equal(QualityTier.Volume, view.Tier);
        Assert.Equal(SpanOutcome.Proposal, view.Verdict.Outcome);
        Assert.Equal([SpanVerdictReasons.WholeSeriesPack, SpanVerdictReasons.AddsMissingChapters], view.Verdict.Reasons);
        Assert.Equal((8, 2), (view.Verdict.UpgradeCount, view.Verdict.MissingCount));
        Assert.Empty(_releases.Grabs);
        Assert.NotNull(result.ProposalId);
        using var db = _world.Db.NewContext();
        var row = db.TorrentProposals.Single();
        Assert.Equal(8, row.UpgradeCount);
        Assert.Contains(SpanVerdictReasons.WholeSeriesPack, row.ReasonsJson);
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<StoredReleaseSpan>(row.SpanJson, QualitySnapshot.Json)!.WholeSeries);
    }

    [Fact]
    public async Task The_same_pack_without_the_digital_tag_stays_ignored()
    {
        RenameSeries("Boy Meets Maria");
        SeedEightFilesAndTwoGaps();

        var view = await EvaluateAsync("Boy Meets Maria (2021) (danke-Empire)", size: 10L * 1024 * 1024);

        Assert.False(view.WholeSeries);
        Assert.Equal(SpanOutcome.Ignore, view.Verdict.Outcome);
    }

    [Fact]
    public async Task A_trailing_number_the_series_title_lacks_is_a_chapter()
    {
        RenameSeries("Ayakashi Triangle");
        Chapter(133, null);

        var view = await EvaluateAsync("Ayakashi Triangle 133 (2023) (Digital) (Oak)");

        Assert.True(view.TitleMatched);
        Assert.False(view.WholeSeries);
        Assert.Equal([new NumberRange(133, 133)], view.Parsed.Span.ChapterSegments);
        Assert.Equal(1, view.Verdict.UpgradeCount);
    }

    [Fact]
    public async Task A_trailing_number_that_ends_the_series_title_is_part_of_the_title()
    {
        RenameSeries("Mob Psycho 100");
        Chapter(1, null);
        Chapter(100, null);

        var view = await EvaluateAsync("Mob Psycho 100 (Digital)");

        Assert.True(view.WholeSeries);
        Assert.True(view.Parsed.Span.IsEmpty);
        Assert.Equal(2, view.Verdict.UpgradeCount);
        Assert.Contains(SpanVerdictReasons.WholeSeriesPack, view.Verdict.Reasons);
    }

    [Fact]
    public async Task The_search_leaves_the_query_to_the_release_service_title_candidates()
    {
        Chapter(1, 1);

        await SearchAsync();

        Assert.Equal("", Assert.Single(_releases.Queries).Query);
    }

    [Fact]
    public async Task A_sharp_file_is_still_an_upgrade_for_a_volume_under_a_tier_drop_limit()
    {
        using (var db = _world.Db.NewContext())
        {
            var profile = db.UpgradeProfiles.Single();
            profile.MaxTierScoreDrop = 10;
            profile.ResolutionWeight = 10;
            profile.CompressionWeight = 10;
            db.SaveChanges();
        }

        Chapter(1, 1, file: f =>
        {
            f.MedianWidth = 2000;
            f.MedianHeight = 3000;
            f.ImageFormat = "jpg";
            f.Size = 90_000_000;
        });

        var view = await EvaluateAsync("Kaguya v01 (Digital) (1r0n)");

        Assert.Equal(1, view.Verdict.UpgradeCount);
    }

    [Fact]
    public async Task A_profile_that_never_stops_searches_for_volumes()
    {
        using (var db = _world.Db.NewContext())
        {
            var profile = db.UpgradeProfiles.Single();
            profile.Cutoff = QualityTier.Official;
            profile.UpgradeUntilScore = 10000;
            profile.Tiers =
            [
                new(QualityTier.Volume, true),
                new(QualityTier.Official, true),
                new(QualityTier.Scanlator, true, Grouped: true),
                new(QualityTier.Aggregator, true, Grouped: true),
                new(QualityTier.Unknown, true, Grouped: true)
            ];
            db.SaveChanges();
        }

        Chapter(1, 1, file: f => f.Tier = QualityTier.Official);

        var result = await SearchAsync();

        Assert.True(result.Searched);
        Assert.Null(result.Reason);
    }

    [Fact]
    public async Task A_rejected_import_is_never_grabbed_again_once_the_history_is_cleared()
    {
        Chapter(1, 1);
        _releases.Results.Add(FakeReleases.Release("Kaguya v01 (Digital) (1r0n)", guid: "g1"));
        int itemId;
        using (var db = _world.Db.NewContext())
        {
            var item = new DownloadQueueItem
            {
                SeriesId = _world.SeriesId, Protocol = AcquisitionProtocol.Torrent, Status = QueueStatus.Cancelled,
                QueuedAt = DateTime.UtcNow, Origin = DownloadOrigin.Upgrade,
                ReleaseInfoJson = System.Text.Json.JsonSerializer.Serialize(new ReleaseInfo("g1", "Kaguya v01", "Nyaa", null))
            };
            item.SetError("error.download.importRejected");
            db.DownloadQueue.Add(item);
            db.SaveChanges();
            itemId = item.Id;
        }

        Assert.True((await SearchAsync()).Searched);
        using (var db = _world.Db.NewContext())
        {
            db.DownloadQueue.Where(q => q.Id == itemId).ExecuteDelete();
        }

        Assert.True((await SearchAsync()).Searched);

        Assert.Empty(_releases.Grabs);
        using var check = _world.Db.NewContext();
        Assert.Equal(TorrentProposalStatus.Dismissed, check.TorrentProposals.Single(p => p.ReleaseGuid == "g1").Status);
    }

    [Fact]
    public async Task The_job_stamps_a_series_with_nothing_to_upgrade_so_it_is_not_walked_daily()
    {
        Chapter(1, 1, file: f => f.Trusted = true);

        Assert.Equal(VolumeSearchReasons.NothingBelowCutoff, (await SearchAsync(respectInterval: true)).Reason);

        using var db = _world.Db.NewContext();
        Assert.NotNull(db.Series.Single().LastVolumeSearchUtc);
        Assert.Empty(await Service(db).CandidateSeriesAsync(CancellationToken.None));
        Assert.Empty(_releases.Queries);
    }
}
