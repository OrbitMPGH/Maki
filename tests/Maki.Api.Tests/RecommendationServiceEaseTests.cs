using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Recommendations;
using Maki.Metadata.CoRead;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Maki.Metadata.RecoGraph;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// How the EASE channel reaches personal recommendations: the instance switch decides the per-call
/// flag and sits in the pool key, the seed weights it scores with are the same ones the other
/// channels get, and the picks it explains with a co-read reason go through
/// <see cref="RecommendationReasons.Balance"/> and <c>Spread</c> like co-read picks do.
/// </summary>
public class RecommendationServiceEaseTests : IDisposable
{
    private static readonly DateTime Now = DateTime.UtcNow;

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private sealed class CapturingRecommender(IReadOnlyList<MangaBakaRecommendation> picks, IReadOnlyDictionary<long, int>? franchises = null)
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
        public readonly List<bool> SeenEase = [];
        public readonly List<IReadOnlyDictionary<long, double>?> SeenWeights = [];

        public override bool IsReady() => true;

        public override Task<IReadOnlyDictionary<long, int>> FranchisesAsync(
            IReadOnlyCollection<long> ids, CancellationToken ct = default) =>
            Task.FromResult(franchises ?? new Dictionary<long, int>());

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
            SeenEase.Add(ease);
            SeenWeights.Add(seedWeights);
            return Task.FromResult(picks);
        }
    }

    private sealed class EmptyStore() : MangaBakaLocalStore(
        new MangaBakaDumpOptions("", ""), NullLogger<MangaBakaLocalStore>.Instance)
    {
        public override Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public override Task<IReadOnlyList<MangaBakaRecommendation>> GetRelatedAsync(
            IReadOnlyCollection<long> seedIds, IReadOnlyCollection<long> excludeIds,
            IReadOnlyList<string>? contentRatings = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MangaBakaRecommendation>>([]);
    }

    private RecommendationService Service(CapturingRecommender recommender, FakeAppSettings settings) =>
        new(
            _db.ScopeFactory(),
            new EmptyStore(),
            recommender,
            new SeedWeightService(new BehavioralTasteService(TasteTuning.Default), TasteTuning.Default, settings),
            settings,
            NullLogger<RecommendationService>.Instance);

    private static async Task<FakeAppSettings> EaseOn()
    {
        var settings = new FakeAppSettings();
        await settings.SetAsync(SettingKeys.RecommendationsEase, "true");
        return settings;
    }

    private static MangaBakaRecommendation Pick(long id, ReasonSeed[]? alsoFinished = null) =>
        new(id.ToString(), $"Pick {id}", null, null, null, SeriesStatus.Completed, 80, null, [], [], false, null, null)
        {
            CoRead = alsoFinished is not null,
            CoReadWithTitle = alsoFinished?[0].Title,
            CoReadWithId = alsoFinished?[0].Id,
            CoReadSeeds = alsoFinished,
        };

    private void SeedLibrary(params int[] mangaBakaIds)
    {
        foreach (var mangaBakaId in mangaBakaIds)
        {
            var seriesId = _db.SeedSeries($"Series {mangaBakaId}", configure: s => s.MangaBakaId = mangaBakaId);
            using var db = _db.NewContext();
            var file = new ChapterFile { SeriesId = seriesId, RelativePath = $"{seriesId}.cbz", DateAdded = Now };
            db.ChapterFiles.Add(file);
            db.SaveChanges();
            var chapter = new Chapter { SeriesId = seriesId, Number = 1, ChapterFileId = file.Id };
            db.Chapters.Add(chapter);
            db.SaveChanges();
            db.ChapterProgress.Add(new ChapterProgress
            {
                UserId = 1, SeriesId = seriesId, ChapterId = chapter.Id, PageCount = 20, Completed = true,
                ReadSeconds = 600, StartedAt = Now, UpdatedAt = Now,
            });
            db.SaveChanges();
        }
    }

    [Fact]
    public async Task The_channel_is_off_by_default()
    {
        SeedLibrary(101, 202);
        var recommender = new CapturingRecommender([Pick(77)]);

        await Service(recommender, new FakeAppSettings()).GetAsync(new RecommendationRequest(), new TestCurrentUser(1));

        Assert.False(Assert.Single(recommender.SeenEase));
    }

    [Fact]
    public async Task Switched_on_the_channel_gets_the_same_seed_weights()
    {
        SeedLibrary(101, 202);
        var recommender = new CapturingRecommender([Pick(77)]);

        await Service(recommender, await EaseOn()).GetAsync(new RecommendationRequest(), new TestCurrentUser(1));

        Assert.True(Assert.Single(recommender.SeenEase));
        var weights = Assert.Single(recommender.SeenWeights);
        Assert.NotNull(weights);
        Assert.Equal([101L, 202L], weights.Keys.Order());
    }

    [Fact]
    public async Task The_kill_switch_turns_the_channel_off_and_misses_the_cached_pool()
    {
        SeedLibrary(101);
        var settings = await EaseOn();
        var recommender = new CapturingRecommender([Pick(77)]);
        var service = Service(recommender, settings);

        await service.GetAsync(new RecommendationRequest(), new TestCurrentUser(1));
        await settings.SetAsync(SettingKeys.RecommendationsEase, "false");
        await service.GetAsync(new RecommendationRequest(), new TestCurrentUser(1));

        Assert.Equal([true, false], recommender.SeenEase);
    }

    [Fact]
    public async Task A_pick_the_table_explains_is_balanced_like_a_co_read_pick()
    {
        // The recommender names the strongest seed on every card it explains, so three picks all
        // pushed by one seed would name it three times; Balance allows two per window and names the
        // third card's next seed instead.
        SeedLibrary(101, 202);
        ReasonSeed first = new("101", "Series 101", null);
        ReasonSeed second = new("202", "Series 202", null);
        var recommender = new CapturingRecommender(
        [
            Pick(1, [first, second]),
            Pick(2, [first, second]),
            Pick(3, [first, second]),
        ]);

        var result = await Service(recommender, await EaseOn())
            .GetAsync(new RecommendationRequest(), new TestCurrentUser(1));

        Assert.Equal(["Series 101", "Series 101", "Series 202"], result.Similar.Select(p => p.CoReadWithTitle));
        Assert.All(result.Similar, p => Assert.True(p.CoRead));
    }

    [Fact]
    public async Task Picks_the_table_pushed_are_still_spaced_by_franchise()
    {
        SeedLibrary(101);
        ReasonSeed seed = new("101", "Series 101", null);
        var picks = Enumerable.Range(1, 10).Select(i => Pick(i, i <= 2 ? [seed] : null)).ToList();
        var recommender = new CapturingRecommender(picks, new Dictionary<long, int> { [1] = 9, [2] = 9 });

        var result = await Service(recommender, await EaseOn())
            .GetAsync(new RecommendationRequest(), new TestCurrentUser(1));

        var order = result.Similar.Select(p => p.ProviderId).ToList();
        Assert.True(order.IndexOf("2") - order.IndexOf("1") >= 8);
    }
}
