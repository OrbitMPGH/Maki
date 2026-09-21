using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Recommendations;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Maki.Metadata.CoRead;
using Maki.Metadata.RecoGraph;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// How behavioural seed weights reach the recommender, and what they do to the pool cache. The scan
/// itself belongs to <c>SemanticRecommender</c>; what is under test here is the dictionary handed to
/// it and the cache key derived from the same numbers.
/// </summary>
public class RecommendationServiceTasteTests : IDisposable
{
    private static readonly DateTime Now = DateTime.UtcNow;

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    /// <summary>Records the seed weights each scan was given, so a test can assert on them.</summary>
    private sealed class CapturingRecommender() : SemanticRecommender(
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
        public readonly List<IReadOnlyDictionary<long, double>?> Seen = [];
        public readonly List<IReadOnlyDictionary<long, double>?> SeenAvoid = [];
        public readonly List<IReadOnlyList<long>> SeenSeeds = [];

        public override bool IsReady() => true;

        /// <summary>The real one reads the vector index, which these tests hand in as null.</summary>
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
            Seen.Add(seedWeights);
            SeenAvoid.Add(avoidWeights);
            SeenSeeds.Add([.. seedIds]);
            IReadOnlyList<MangaBakaRecommendation> result =
            [
                new("77", "Title", null, null, null, SeriesStatus.Completed, 80, null, [], [], false, null, null)
            ];
            return Task.FromResult(result);
        }
    }

    /// <summary>A store that reports the dump present and contributes no relations of its own.</summary>
    private sealed class EmptyStore() : MangaBakaLocalStore(
        new MangaBakaDumpOptions("", ""), new FakeAppSettings(), NullLogger<MangaBakaLocalStore>.Instance)
    {
        public override Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public override Task<IReadOnlyList<MangaBakaRecommendation>> GetRelatedAsync(
            IReadOnlyCollection<long> seedIds, IReadOnlyCollection<long> excludeIds,
            IReadOnlyList<string>? contentRatings = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MangaBakaRecommendation>>([]);
    }

    private (RecommendationService Service, CapturingRecommender Recommender) Service(
        FakeAppSettings? settings = null, TasteTuning? tuning = null)
    {
        var recommender = new CapturingRecommender();
        var effectiveTuning = tuning ?? TasteTuning.Default;
        var effectiveSettings = settings ?? new FakeAppSettings();
        var service = new RecommendationService(
            _db.ScopeFactory(),
            new EmptyStore(),
            recommender,
            new SeedWeightService(
                new BehavioralTasteService(effectiveTuning), effectiveTuning, effectiveSettings),
            effectiveSettings,
            NullLogger<RecommendationService>.Instance);
        return (service, recommender);
    }

    private int SeedSeries(int mangaBakaId) =>
        _db.SeedSeries($"Series {mangaBakaId}", configure: s => s.MangaBakaId = mangaBakaId);

    private void SeedFinished(int seriesId, int chapters = 40)
    {
        using var db = _db.NewContext();
        for (var i = 1; i <= chapters; i++)
        {
            var file = new ChapterFile { SeriesId = seriesId, RelativePath = $"{seriesId}-{i}.cbz", DateAdded = Now };
            db.ChapterFiles.Add(file);
            db.SaveChanges();

            var chapter = new Chapter { SeriesId = seriesId, Number = i, ChapterFileId = file.Id };
            db.Chapters.Add(chapter);
            db.SaveChanges();

            db.ChapterProgress.Add(new ChapterProgress
            {
                UserId = 1,
                SeriesId = seriesId,
                ChapterId = chapter.Id,
                PageCount = 20,
                Completed = true,
                ReadSeconds = 600,
                StartedAt = Now,
                UpdatedAt = Now
            });
            db.SaveChanges();
        }
    }

    private void SeedRating(int seriesId, int rating)
    {
        using var db = _db.NewContext();
        db.UserSeriesStates.Add(new Maki.Data.Identity.UserSeriesState
        {
            UserId = 1,
            SeriesId = seriesId,
            Rating = rating
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task Reading_history_weights_an_unrated_seed()
    {
        SeedFinished(SeedSeries(101));
        SeedSeries(202);

        var (service, recommender) = Service();
        await service.GetAsync(new RecommendationRequest(), new TestCurrentUser(1));

        var weights = Assert.Single(recommender.Seen);
        Assert.NotNull(weights);
        Assert.True(weights[101] > TasteWeights.Neutral);
        Assert.False(weights.ContainsKey(202));
    }

    [Fact]
    public async Task A_rating_survives_the_reading_history_untouched()
    {
        var seriesId = SeedSeries(101);
        SeedFinished(seriesId);
        SeedRating(seriesId, 7);

        // Seven and not three: a rating at or below AvoidRatingCeiling leaves the positive
        // population altogether, so it could not show this even if the blend were wrong.
        var (service, recommender) = Service();
        await service.GetAsync(new RecommendationRequest(), new TestCurrentUser(1));

        // RatingBlendAlpha ships at 1, so behaviour fills gaps and never argues with an explicit score.
        var weights = Assert.Single(recommender.Seen);
        Assert.Equal(7 / 5.0, weights![101]);
    }

    [Fact]
    public async Task A_low_rating_leaves_the_seeds_and_joins_the_avoided_set()
    {
        var seriesId = SeedSeries(101);
        // Read to the end, which is what would otherwise weight it back up: the row has to leave the
        // positive population before it is weighed, not have its weight removed afterwards.
        SeedFinished(seriesId);
        SeedRating(seriesId, 4);
        SeedRating(SeedSeries(202), 8);

        var (service, recommender) = Service();
        await service.GetAsync(new RecommendationRequest(), new TestCurrentUser(1));

        var weights = Assert.Single(recommender.Seen);
        Assert.False(weights!.ContainsKey(101));
        Assert.DoesNotContain(101L, recommender.SeenSeeds[0]);
        Assert.Equal(0.25, Assert.Single(recommender.SeenAvoid)![101]);
    }

    [Fact]
    public async Task A_neutral_rating_stays_a_positive_seed()
    {
        var seriesId = SeedSeries(101);
        SeedRating(seriesId, 5);

        var (service, recommender) = Service();
        await service.GetAsync(new RecommendationRequest(), new TestCurrentUser(1));

        Assert.Equal(1.0, Assert.Single(recommender.Seen)![101]);
        // Nothing to avoid, so the service hands in null rather than an empty dictionary.
        Assert.Null(Assert.Single(recommender.SeenAvoid));
    }

    [Fact]
    public async Task A_thumbs_down_avoids_a_title_that_is_not_in_the_library()
    {
        SeedRating(SeedSeries(101), 8);
        using (var db = _db.NewContext())
        {
            db.RecommendationFeedback.Add(new RecommendationFeedback
            {
                UserId = 1, ProviderId = 777, Sentiment = RecommendationSentiment.Disliked
            });
            db.SaveChanges();
        }

        var (service, recommender) = Service();
        await service.GetAsync(new RecommendationRequest(), new TestCurrentUser(1));

        Assert.Equal(1.0, Assert.Single(recommender.SeenAvoid)![777]);
        Assert.DoesNotContain(777L, recommender.SeenSeeds[0]);
        Assert.False(Assert.Single(recommender.Seen)!.ContainsKey(777));
    }

    [Fact]
    public async Task Ignoring_a_source_takes_it_out_of_the_avoided_set_too()
    {
        var seriesId = SeedSeries(101);
        SeedRating(seriesId, 2);
        using (var db = _db.NewContext())
        {
            db.RecommendationSignalOverrides.Add(new RecommendationSignalOverride
            {
                UserId = 1, ProviderId = 101, IgnoreAsSeed = true
            });
            db.RecommendationFeedback.Add(new RecommendationFeedback
            {
                UserId = 1, ProviderId = 888, Sentiment = RecommendationSentiment.Disliked
            });
            db.RecommendationSignalOverrides.Add(new RecommendationSignalOverride
            {
                UserId = 1, ProviderId = 888, IgnoreAsSeed = true
            });
            db.SaveChanges();
        }

        SeedRating(SeedSeries(202), 8);
        var (service, recommender) = Service();
        await service.GetAsync(new RecommendationRequest(), new TestCurrentUser(1));

        // "Stop steering by this" is one answer, not two: it has to remove the title from the
        // avoided set as well, or excluding a badly-rated title would leave its penalty behind.
        Assert.Null(Assert.Single(recommender.SeenAvoid));
    }

    [Fact]
    public async Task A_thumbs_down_invalidates_the_cached_pool()
    {
        SeedRating(SeedSeries(101), 8);

        var (service, recommender) = Service();
        await service.GetAsync(new RecommendationRequest(), new TestCurrentUser(1));
        using (var db = _db.NewContext())
        {
            db.RecommendationFeedback.Add(new RecommendationFeedback
            {
                UserId = 1, ProviderId = 777, Sentiment = RecommendationSentiment.Disliked
            });
            db.SaveChanges();
        }

        await service.GetAsync(new RecommendationRequest(), new TestCurrentUser(1));

        // Without the avoided set in the cache key a dislike would sit invisible for twelve hours.
        Assert.Equal(2, recommender.Seen.Count);
    }

    [Fact]
    public async Task The_kill_switch_restores_rating_only_weighting()
    {
        var rated = SeedSeries(101);
        SeedRating(rated, 8);
        SeedFinished(SeedSeries(202));

        var settings = new FakeAppSettings();
        await settings.SetAsync(SettingKeys.RecommendationsTasteWeighting, "false");

        var (service, recommender) = Service(settings);
        await service.GetAsync(new RecommendationRequest(), new TestCurrentUser(1));

        var weights = Assert.Single(recommender.Seen);
        Assert.Equal(new Dictionary<long, double> { [101] = 8 / 5.0 }, weights);
    }

    [Fact]
    public async Task A_second_call_on_the_same_day_is_served_from_the_cached_pool()
    {
        // The weights are part of the cache key. Quantizing them is what stops a day's reading from
        // invalidating a 12-hour pool and turning every request into a fresh index scan.
        SeedFinished(SeedSeries(101));

        var (service, recommender) = Service();
        await service.GetAsync(new RecommendationRequest(), new TestCurrentUser(1));
        await service.GetAsync(new RecommendationRequest(), new TestCurrentUser(1));

        Assert.Single(recommender.Seen);
    }

    [Fact]
    public async Task Ignoring_a_source_drops_it_from_automatic_seeds_but_not_from_more_like_this()
    {
        SeedSeries(101);
        SeedSeries(202);
        using (var db = _db.NewContext())
        {
            db.RecommendationSignalOverrides.Add(new RecommendationSignalOverride
            {
                UserId = 1, ProviderId = 101, IgnoreAsSeed = true
            });
            db.SaveChanges();
        }

        var (service, recommender) = Service();
        await service.GetAsync(new RecommendationRequest(), new TestCurrentUser(1));
        // Asking "more like this" about a title is a deliberate one-off. Excluding it from the
        // inferred profile is not a reason to refuse the question.
        await service.GetAsync(new RecommendationRequest(SeedIds: [101]), new TestCurrentUser(1));

        Assert.Equal(2, recommender.Seen.Count);
        var automatic = Assert.IsAssignableFrom<IReadOnlyList<long>>(recommender.SeenSeeds[0]);
        Assert.Equal([202L], automatic);
        Assert.Equal([101L], recommender.SeenSeeds[1]);
    }

    [Fact]
    public async Task More_like_this_accepts_a_catalogue_seed_that_is_not_in_the_library()
    {
        // The Discover hero asks "more like this" about a recommendation, which is by definition
        // not owned. Filtering chosen seeds down to shelf titles left the request with none.
        SeedSeries(101);

        var (service, recommender) = Service();
        await service.GetAsync(new RecommendationRequest(SeedIds: [909]), new TestCurrentUser(1));

        Assert.Equal([909L], Assert.Single(recommender.SeenSeeds));
    }

    [Fact]
    public async Task A_thumbs_up_seeds_a_title_that_is_not_in_the_library()
    {
        // The point of the action: a rating can only describe something already on the shelf, so
        // without this there is no way to say "more like that" about a recommendation.
        SeedSeries(101);
        using (var db = _db.NewContext())
        {
            db.RecommendationFeedback.Add(new RecommendationFeedback
            {
                UserId = 1, ProviderId = 777, Sentiment = RecommendationSentiment.Liked
            });
            db.SaveChanges();
        }

        var (service, recommender) = Service();
        await service.GetAsync(new RecommendationRequest(), new TestCurrentUser(1));

        Assert.Equal([101L, 777L], recommender.SeenSeeds[0]);
        var weights = Assert.Single(recommender.Seen);
        Assert.Equal(RecommendationFeedbackPolicy.LikedWeight, weights![777]);
    }

    [Fact]
    public async Task A_thumbs_up_loses_to_an_explicit_top_rating_on_the_same_work()
    {
        var seriesId = SeedSeries(101);
        SeedRating(seriesId, 10);
        using (var db = _db.NewContext())
        {
            db.RecommendationFeedback.Add(new RecommendationFeedback
            {
                UserId = 1, ProviderId = 101, Sentiment = RecommendationSentiment.Liked
            });
            db.SaveChanges();
        }

        var (service, recommender) = Service();
        await service.GetAsync(new RecommendationRequest(), new TestCurrentUser(1));

        var weights = Assert.Single(recommender.Seen);
        Assert.Equal(10 / 5.0, weights![101]);
    }

    [Fact]
    public async Task Two_users_with_the_same_inputs_share_one_pool()
    {
        // The pool is keyed on the seeds, not on who asked. On a shared library that is most of the
        // instance: naming the user in the key would give every reader a private copy of the same
        // 200 rows and thrash CacheSlots, so this is the case that has to stay a single scan.
        var other = _db.SeedUser("other");
        SeedSeries(101);

        var (service, recommender) = Service();
        await service.GetAsync(new RecommendationRequest(), new TestCurrentUser(1));
        await service.GetAsync(new RecommendationRequest(), new TestCurrentUser(other));

        Assert.Single(recommender.Seen);
    }

    [Fact]
    public async Task Two_users_do_not_evict_each_others_pools()
    {
        // Behavioural weights make every user's cache key distinct, so a single cache slot would
        // recompute on every alternating request.
        var other = _db.SeedUser("other");
        SeedFinished(SeedSeries(101));

        var (service, recommender) = Service();
        await service.GetAsync(new RecommendationRequest(), new TestCurrentUser(1));
        await service.GetAsync(new RecommendationRequest(), new TestCurrentUser(other));
        await service.GetAsync(new RecommendationRequest(), new TestCurrentUser(1));

        Assert.Equal(2, recommender.Seen.Count);
    }
}
