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

        public override bool IsReady() => true;

        public override Task<IReadOnlyList<MangaBakaRecommendation>> GetSimilarAsync(
            IReadOnlyCollection<long> seedIds, IReadOnlyCollection<long> excludeIds,
            int limit, RecommendationFilters? filters = null, double obscurity = 0,
            IReadOnlyDictionary<long, double>? seedWeights = null, double diversity = 0,
            EmbeddingMath.Weights? weights = null, bool coGraph = true, bool coRead = true,
            bool taste = true, ICollection<EmbeddingMath.CandidateFeatures>? features = null,
            CancellationToken ct = default)
        {
            Seen.Add(seedWeights);
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
        var service = new RecommendationService(
            _db.ScopeFactory(),
            new EmptyStore(),
            recommender,
            new BehavioralTasteService(tuning ?? TasteTuning.Default),
            tuning ?? TasteTuning.Default,
            settings ?? new FakeAppSettings(),
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
        SeedRating(seriesId, 3);

        var (service, recommender) = Service();
        await service.GetAsync(new RecommendationRequest(), new TestCurrentUser(1));

        // RatingBlendAlpha ships at 1, so behaviour fills gaps and never argues with an explicit score.
        var weights = Assert.Single(recommender.Seen);
        Assert.Equal(3 / 5.0, weights![101]);
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
