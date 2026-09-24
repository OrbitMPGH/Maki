using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Recommendations;
using Maki.Metadata.CoRead;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Maki.Metadata.RecoGraph;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// How a Want-to-read save reaches the seed space: a weak positive, never part of the shelf, and
/// never a source of relations.
/// </summary>
public class PlannedSeedTests : IDisposable
{
    private readonly TestDb _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static SeedWeightService Seeds() => new(
        new BehavioralTasteService(TasteTuning.Default), TasteTuning.Default, new FakeAppSettings());

    private void Plan(long id, TimeSpan? age = null, string? rating = null)
    {
        using var db = _fixture.NewContext();
        db.PlanToReadEntries.Add(new PlanToReadEntry
        {
            UserId = 1, ProviderId = id, Title = $"Saved {id}", ContentRating = rating,
            AddedAtUtc = DateTime.UtcNow - (age ?? TimeSpan.FromHours(1)),
        });
        db.SaveChanges();
    }

    private void Feedback(long id, RecommendationSentiment sentiment)
    {
        using var db = _fixture.NewContext();
        db.RecommendationFeedback.Add(new RecommendationFeedback { UserId = 1, ProviderId = id, Sentiment = sentiment });
        db.SaveChanges();
    }

    [Fact]
    public async Task A_save_seeds_at_about_a_third_of_a_shelf_title_and_stays_off_the_shelf()
    {
        _fixture.SeedSeries("Shelf", configure: s => s.MangaBakaId = 10);
        Plan(500);

        using var db = _fixture.NewContext(1);
        var snapshot = await Seeds().SnapshotAsync(db, new TestCurrentUser(1));

        Assert.Contains(500L, snapshot.Effective.EligibleIds);
        Assert.Contains(500L, snapshot.Planned!);
        var ratio = snapshot.Effective.Weights[500] / snapshot.Effective.Weights.GetValueOrDefault(10, 1.0);
        Assert.InRange(ratio, 0.3, 0.4);
        Assert.DoesNotContain(500L, snapshot.Observed.EligibleIds);
        Assert.DoesNotContain(500L, snapshot.Observed.LibraryIds);
        Assert.DoesNotContain(500L, snapshot.Effective.LibraryIds);
    }

    [Fact]
    public async Task Stronger_evidence_about_the_same_title_wins()
    {
        _fixture.SeedSeries("Shelf", configure: s => s.MangaBakaId = 10);
        Plan(10);
        Plan(20);
        Feedback(20, RecommendationSentiment.Liked);
        Plan(30);
        Feedback(30, RecommendationSentiment.Disliked);

        using var db = _fixture.NewContext(1);
        var snapshot = await Seeds().SnapshotAsync(db, new TestCurrentUser(1));

        Assert.Equal(1.0, snapshot.Effective.Weights.GetValueOrDefault(10, 1.0));
        Assert.Equal(RecommendationFeedbackPolicy.LikedWeight, snapshot.Effective.Weights[20]);
        Assert.DoesNotContain(30L, snapshot.Effective.EligibleIds);
        Assert.Empty(snapshot.Planned!);
    }

    [Fact]
    public async Task A_save_seeds_only_once_it_has_settled()
    {
        Plan(500, age: TimeSpan.FromMinutes(1));
        Plan(501, age: RecommendationFeedbackPolicy.PlannedSettle + TimeSpan.FromMinutes(1));

        using var db = _fixture.NewContext(1);
        var snapshot = await Seeds().SnapshotAsync(db, new TestCurrentUser(1));

        Assert.DoesNotContain(500L, snapshot.Effective.EligibleIds);
        Assert.Contains(501L, snapshot.Effective.EligibleIds);
    }

    [Fact]
    public async Task A_save_the_reader_later_hid_does_not_seed()
    {
        Plan(500);
        using (var seed = _fixture.NewContext())
        {
            seed.RecommendationFeedback.Add(new RecommendationFeedback
            {
                UserId = 1, ProviderId = 500, Suppression = RecommendationSuppression.Hidden
            });
            seed.SaveChanges();
        }

        using var db = _fixture.NewContext(1);
        var snapshot = await Seeds().SnapshotAsync(db, new TestCurrentUser(1));

        Assert.DoesNotContain(500L, snapshot.Effective.EligibleIds);
    }

    private sealed class SafeViewer : Maki.Core.Security.ICurrentUser
    {
        public bool IsAuthenticated => true;
        public int UserId => 1;
        public string UserName => "safe";
        public Maki.Core.Security.MakiPermission Permissions => Maki.Core.Security.MakiPermission.None;
        public bool AllRootFolders => true;
        public IReadOnlySet<int> RootFolderIds => new HashSet<int>();
        public string MaxContentRating => "safe";
    }

    [Fact]
    public async Task The_ceiling_applies_to_the_stored_rating_and_an_unrated_save_still_seeds()
    {
        Plan(500, rating: "erotica");
        Plan(501, rating: null);
        Plan(502, rating: "safe");

        using var db = _fixture.NewContext(1);
        var snapshot = await Seeds().SnapshotAsync(db, new SafeViewer());

        Assert.Equal([501L, 502], snapshot.Planned!.Order());
    }

    private sealed class Recommender() : SemanticRecommender(
        new EmbeddingOptions("", "", "", EmbeddingModelProfile.Base),
        new MangaBakaDumpOptions("", ""),
        new EmbeddingStore(new EmbeddingOptions("", "", "", EmbeddingModelProfile.Base)),
        null!, null!, RecoGraphTuning.Default, null!, CoReadTuning.Default,
        NullLogger<SemanticRecommender>.Instance)
    {
        public int Builds;

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
            bool taste = true, ICollection<EmbeddingMath.CandidateFeatures>? features = null,
            CancellationToken ct = default)
        {
            Builds++;
            IReadOnlyList<MangaBakaRecommendation> result =
            [
                new("77", "Title", null, null, null, SeriesStatus.Completed, 80, null, [], [], false, null, null)
            ];
            return Task.FromResult(result);
        }
    }

    private sealed class Store() : MangaBakaLocalStore(
        new MangaBakaDumpOptions("", ""), new FakeAppSettings(), NullLogger<MangaBakaLocalStore>.Instance)
    {
        public readonly List<IReadOnlyList<long>> RelationSeeds = [];

        public override Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public override Task<IReadOnlyList<MangaBakaRecommendation>> GetRelatedAsync(
            IReadOnlyCollection<long> seedIds, IReadOnlyCollection<long> excludeIds,
            IReadOnlyList<string>? contentRatings = null, CancellationToken ct = default)
        {
            RelationSeeds.Add([.. seedIds]);
            return Task.FromResult<IReadOnlyList<MangaBakaRecommendation>>([]);
        }
    }

    [Fact]
    public async Task A_save_rebuilds_the_pool_and_never_seeds_relations()
    {
        _fixture.SeedSeries("Shelf", configure: s => s.MangaBakaId = 10);
        var recommender = new Recommender();
        var store = new Store();
        var settings = new FakeAppSettings();
        var service = new RecommendationService(_fixture.ScopeFactory(), store, recommender,
            new SeedWeightService(new BehavioralTasteService(TasteTuning.Default), TasteTuning.Default, settings),
            settings, NullLogger<RecommendationService>.Instance);
        var user = new TestCurrentUser(1);

        await service.GetAsync(new RecommendationRequest(), user);
        await service.GetAsync(new RecommendationRequest(), user);
        Assert.Equal(1, recommender.Builds);

        Plan(500);
        var similar = await service.VisibleSimilarAsync(new RecommendationRequest(), user);

        Assert.Equal(2, recommender.Builds);
        Assert.NotNull(similar);
        Assert.Equal([10L], store.RelationSeeds[^1]);
    }

    [Fact]
    public async Task Saves_alone_are_enough_to_seed_a_pool_but_not_relations()
    {
        var recommender = new Recommender();
        var store = new Store();
        var settings = new FakeAppSettings();
        var service = new RecommendationService(_fixture.ScopeFactory(), store, recommender,
            new SeedWeightService(new BehavioralTasteService(TasteTuning.Default), TasteTuning.Default, settings),
            settings, NullLogger<RecommendationService>.Instance);
        var user = new TestCurrentUser(1);

        Assert.Null(await service.VisibleSimilarAsync(new RecommendationRequest(), user));

        Plan(500);
        var similar = await service.VisibleSimilarAsync(new RecommendationRequest(), user);

        Assert.Single(similar!);
        Assert.Empty(store.RelationSeeds);
    }
}
