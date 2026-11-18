using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Recommendations;
using Maki.Data.Identity;
using Maki.Metadata.CoRead;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Maki.Metadata.RecoGraph;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// What Recommended does with a manga whose anime is on the reader's watch list, and with a famous
/// adaptation they skipped (<see cref="KnownTitleTuning"/>). Watched anime is excluded at the
/// shipped tuning, inside the scan; the demotions are off.
/// </summary>
public class RecommendationServiceKnownTitleTests : IDisposable
{
    private static readonly DateTime Now = DateTime.UtcNow;

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private sealed class CapturingRecommender(IReadOnlyList<MangaBakaRecommendation> picks)
        : SemanticRecommender(
            new EmbeddingOptions("", "", "", EmbeddingModelProfile.Base),
            new MangaBakaDumpOptions("", ""),
            new EmbeddingStore(new EmbeddingOptions("", "", "", EmbeddingModelProfile.Base)),
            null!,
            null!,
            RecoGraphTuning.Default,
            null!,
            CoReadTuning.Default,
            NullLogger<SemanticRecommender>.Instance)
    {
        public readonly List<IReadOnlyDictionary<long, double>?> SeenDemote = [];

        public readonly List<IReadOnlyCollection<long>> SeenExclude = [];

        public readonly List<bool> SeenCommon = [];

        public override bool IsReady() => true;

        public override Task<IReadOnlyDictionary<long, int>> FranchisesAsync(
            IReadOnlyCollection<long> ids, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<long, int>>(new Dictionary<long, int>());

        public override Task<IReadOnlyList<MangaBakaRecommendation>> GetSimilarAsync(
            IReadOnlyCollection<long> seedIds, IReadOnlyCollection<long> excludeIds,
            int limit, RecommendationFilters? filters = null, double obscurity = 0,
            IReadOnlyDictionary<long, double>? seedWeights = null,
            IReadOnlyDictionary<long, double>? avoidWeights = null, double diversity = 0,
            EmbeddingMath.Weights? weights = null, bool coGraph = true, bool coRead = true,
            bool taste = true, bool ease = true, ICollection<EmbeddingMath.CandidateFeatures>? features = null, double? graphWeight = null,
            IReadOnlyDictionary<long, double>? demote = null, bool common = false,
            CancellationToken ct = default)
        {
            SeenDemote.Add(demote);
            SeenExclude.Add(excludeIds.ToList());
            SeenCommon.Add(common);
            return Task.FromResult<IReadOnlyList<MangaBakaRecommendation>>(
                picks.Where(p => !excludeIds.Contains(long.Parse(p.ProviderId))).ToList());
        }
    }

    private sealed class FakeStore(IReadOnlyDictionary<long, long>? aired = null) : MangaBakaLocalStore(
        new MangaBakaDumpOptions("", ""), NullLogger<MangaBakaLocalStore>.Instance)
    {
        public readonly List<IReadOnlyCollection<long>> SeenRelatedExclude = [];

        public override Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public override Task<IReadOnlyList<MangaBakaRecommendation>> GetRelatedAsync(
            IReadOnlyCollection<long> seedIds, IReadOnlyCollection<long> excludeIds,
            IReadOnlyList<string>? contentRatings = null, CancellationToken ct = default)
        {
            SeenRelatedExclude.Add(excludeIds.ToList());
            return Task.FromResult<IReadOnlyList<MangaBakaRecommendation>>([]);
        }

        public override Task<IReadOnlyDictionary<long, long>> GetAiredAnimeRanksAsync(
            int maxRank, CancellationToken ct = default) =>
            Task.FromResult(aired ?? new Dictionary<long, long>());
    }

    private RecommendationService Service(
        CapturingRecommender recommender, KnownTitleTuning? known, IReadOnlyDictionary<long, long>? aired = null,
        FakeAppSettings? settings = null, FakeStore? store = null)
    {
        settings ??= new FakeAppSettings();
        return new(
            _db.ScopeFactory(),
            store ?? new FakeStore(aired),
            recommender,
            new SeedWeightService(new BehavioralTasteService(TasteTuning.Default), TasteTuning.Default, settings),
            settings,
            NullLogger<RecommendationService>.Instance,
            known);
    }

    private static MangaBakaRecommendation Pick(long id) =>
        new(id.ToString(), $"Pick {id}", null, null, null, SeriesStatus.Completed, 80, null, [], [], false, null, null);

    private void SeedLibrary(params int[] mangaBakaIds)
    {
        foreach (var mangaBakaId in mangaBakaIds)
        {
            _db.SeedSeries($"Series {mangaBakaId}", configure: s => s.MangaBakaId = mangaBakaId);
        }
    }

    private void Watch(long mangaBakaId, AnimeWatchStatus status = AnimeWatchStatus.Watching, int? score = null,
        int? progress = 3)
    {
        using var db = _db.NewContext();
        db.AnimeSignals.Add(new AnimeSignal
        {
            UserId = 1, Service = "anilist", AnimeId = 9000 + mangaBakaId, Title = $"Anime {mangaBakaId}",
            Status = status, Score = score, Progress = progress, MangaBakaId = mangaBakaId, UpdatedAtUtc = Now,
        });
        if (!db.UserSettings.Any(x => x.UserId == 1 && x.Key == SettingKeys.RecommendationsAnimeSignalsEnabled))
        {
            db.UserSettings.Add(new UserSetting
            {
                UserId = 1, Key = SettingKeys.RecommendationsAnimeSignalsEnabled, Value = "true",
            });
        }

        db.SaveChanges();
    }

    [Fact]
    public async Task At_the_shipped_tuning_completed_and_watching_titles_leave_the_scan_scored_or_not()
    {
        SeedLibrary(101, 202);
        Watch(77, AnimeWatchStatus.Completed, progress: 12);
        Watch(78, AnimeWatchStatus.Watching, progress: 0);
        Watch(79, AnimeWatchStatus.Watching, score: 6);
        Watch(80, AnimeWatchStatus.Planning, progress: 0);
        var store = new FakeStore();
        var recommender = new CapturingRecommender([Pick(77), Pick(78), Pick(79), Pick(80), Pick(81)]);

        var result = await Service(recommender, null, store: store)
            .GetAsync(new RecommendationRequest(), new TestCurrentUser(1));

        Assert.Equal(["80", "81"], result.Similar.Select(p => p.ProviderId));
        // Handed to the scan as exclusions, so the fame cap never counts them; relations keep them.
        var excluded = Assert.Single(recommender.SeenExclude);
        Assert.Contains(77L, excluded);
        Assert.Contains(78L, excluded);
        Assert.Contains(79L, excluded);
        Assert.DoesNotContain(80L, excluded);
        Assert.DoesNotContain(77L, Assert.Single(store.SeenRelatedExclude));
        Assert.Null(Assert.Single(recommender.SeenDemote));
    }

    [Fact]
    public async Task A_changed_watch_list_builds_a_new_pool()
    {
        SeedLibrary(101, 202);
        Watch(77);
        var recommender = new CapturingRecommender([Pick(77), Pick(78), Pick(79)]);
        var service = Service(recommender, null);

        var first = await service.GetAsync(new RecommendationRequest(), new TestCurrentUser(1));
        Watch(78);
        var second = await service.GetAsync(new RecommendationRequest(), new TestCurrentUser(1));

        Assert.Equal(["78", "79"], first.Similar.Select(p => p.ProviderId));
        Assert.Equal(["79"], second.Similar.Select(p => p.ProviderId));
        Assert.Equal(2, recommender.SeenExclude.Count);
        Assert.NotEqual(first.PoolVersion, second.PoolVersion);
    }

    [Fact]
    public async Task The_page_share_switch_reaches_the_scan_and_the_pool_key()
    {
        SeedLibrary(101, 202);
        var settings = new FakeAppSettings();
        var recommender = new CapturingRecommender([Pick(77)]);
        var service = Service(recommender, KnownTitleTuning.Default with { WatchedAnime = KnownTitleMode.Off }, settings: settings);

        await service.GetAsync(new RecommendationRequest(), new TestCurrentUser(1));
        await settings.SetAsync(SettingKeys.RecommendationsPageShare, "false");
        await service.GetAsync(new RecommendationRequest(), new TestCurrentUser(1));

        Assert.Equal([true, false], recommender.SeenCommon);
    }

    [Fact]
    public async Task Exclude_drops_an_unscored_watched_title_and_keeps_the_rest()
    {
        SeedLibrary(101, 202);
        Watch(77);
        Watch(79, AnimeWatchStatus.Planning, progress: 0);
        var recommender = new CapturingRecommender([Pick(77), Pick(78), Pick(79)]);

        var result = await Service(recommender, new KnownTitleTuning { WatchedAnime = KnownTitleMode.Exclude })
            .GetAsync(new RecommendationRequest(), new TestCurrentUser(1));

        Assert.Equal(["78", "79"], result.Similar.Select(p => p.ProviderId));
    }

    [Fact]
    public async Task Exclude_does_nothing_for_a_reader_who_has_not_opted_in()
    {
        SeedLibrary(101, 202);
        using (var db = _db.NewContext())
        {
            db.AnimeSignals.Add(new AnimeSignal
            {
                UserId = 1, Service = "anilist", AnimeId = 9077, Status = AnimeWatchStatus.Completed,
                MangaBakaId = 77, UpdatedAtUtc = Now,
            });
            db.SaveChanges();
        }

        var recommender = new CapturingRecommender([Pick(77), Pick(78)]);

        var result = await Service(recommender, new KnownTitleTuning { WatchedAnime = KnownTitleMode.Exclude })
            .GetAsync(new RecommendationRequest(), new TestCurrentUser(1));

        Assert.Equal(["77", "78"], result.Similar.Select(p => p.ProviderId));
    }

    [Fact]
    public async Task Demote_hands_the_recommender_a_penalty_and_keeps_the_title()
    {
        SeedLibrary(101, 202);
        Watch(77, AnimeWatchStatus.Completed, score: 6);
        var recommender = new CapturingRecommender([Pick(77), Pick(78)]);

        var result = await Service(
                recommender, new KnownTitleTuning { WatchedAnime = KnownTitleMode.Demote, WatchedDemotion = 0.8 })
            .GetAsync(new RecommendationRequest(), new TestCurrentUser(1));

        Assert.Equal(2, result.Similar.Count);
        var demote = Assert.Single(recommender.SeenDemote);
        Assert.NotNull(demote);
        Assert.Equal(0.8, demote[77]);
    }

    [Fact]
    public async Task A_skipped_famous_adaptation_is_demoted_by_fame_and_the_readers_rate()
    {
        SeedLibrary(101, 202);
        // Four famous adaptations, two of them watched: a rate of 0.5, past the full-strength bar.
        Watch(1);
        Watch(2);
        var aired = new Dictionary<long, long> { [1] = 5, [2] = 50, [3] = 10, [4] = 900 };
        var recommender = new CapturingRecommender([Pick(3), Pick(4)]);
        var tuning = new KnownTitleTuning { UnwatchedWeight = 2, MinWatched = 2 };

        await Service(recommender, tuning, aired).GetAsync(new RecommendationRequest(), new TestCurrentUser(1));

        var demote = Assert.Single(recommender.SeenDemote);
        Assert.NotNull(demote);
        Assert.Equal([3L, 4L], demote.Keys.Order());
        Assert.Equal(2 * (1 - (Math.Log(10) / Math.Log(1000))), demote[3], 6);
        Assert.True(demote[4] < demote[3]);
    }

    [Fact]
    public void A_reader_with_a_short_watch_list_gets_no_unwatched_demotion()
    {
        var tuning = new KnownTitleTuning { UnwatchedWeight = 2 };

        Assert.Equal(0, tuning.UnwatchedStrength(rank: 5, watchedFamous: 19, famous: 450));
        Assert.True(tuning.UnwatchedStrength(rank: 5, watchedFamous: 20, famous: 450) > 0);
        Assert.Equal(0, tuning.UnwatchedStrength(rank: 1001, watchedFamous: 200, famous: 450));
    }

    [Fact]
    public void Watched_manga_holds_every_completed_and_watching_entry_and_started_paused_or_dropped_ones()
    {
        AnimeSignalRow Row(long manga, AnimeWatchStatus status, int? score = null, int? progress = null) =>
            new("anilist", 9000 + manga, null, "t", score, status, manga, Progress: progress);

        var watched = AnimeSignalGrouping.WatchedManga(
        [
            Row(1, AnimeWatchStatus.Watching, progress: 5),
            Row(2, AnimeWatchStatus.Completed),
            Row(3, AnimeWatchStatus.Planning, score: 8),
            Row(4, AnimeWatchStatus.Watching, progress: 0),
            Row(5, AnimeWatchStatus.Dropped, progress: 2),
            Row(6, AnimeWatchStatus.OnHold, progress: 0),
        ]);

        Assert.Equal([1L, 2L, 4L, 5L], watched.Order());
    }
}
