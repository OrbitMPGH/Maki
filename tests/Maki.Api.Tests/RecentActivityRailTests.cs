using Maki.Api.Services;
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
/// Which series seed Discover's "Based on your recent activity" rail, and what the rail is shaped
/// like once they have. The scoring is <c>SemanticRecommender</c>'s and the pool cache is
/// <c>RecommendationService</c>'s; what is under test here is the seed picking in front of both.
/// </summary>
public class RecentActivityRailTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

    private readonly TestDb _db = new();

    public RecentActivityRailTests() => _db.SeedUser();

    public void Dispose() => _db.Dispose();

    /// <summary>Records the seeds each scan was given and answers with one pick per seed.</summary>
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
        public readonly List<IReadOnlyCollection<long>> Seen = [];

        /// <summary>What <see cref="FranchisesAsync"/> answers; empty means nothing is in a franchise.</summary>
        public Dictionary<long, int> Franchises { get; } = [];

        public override bool IsReady() => true;

        /// <summary>
        /// Overridden because the real one reads the vector index, which these tests hand in as
        /// null. Answering from a dictionary is also what lets a test say "these picks are the same
        /// work" without building an index to say it with.
        /// </summary>
        public override Task<IReadOnlyDictionary<long, int>> FranchisesAsync(
            IReadOnlyCollection<long> ids, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<long, int>>(
                ids.Where(Franchises.ContainsKey).ToDictionary(id => id, id => Franchises[id]));

        public override Task<IReadOnlyList<MangaBakaRecommendation>> GetSimilarAsync(
            IReadOnlyCollection<long> seedIds, IReadOnlyCollection<long> excludeIds,
            int limit, RecommendationFilters? filters = null, double obscurity = 0,
            IReadOnlyDictionary<long, double>? seedWeights = null,
            IReadOnlyDictionary<long, double>? avoidWeights = null, double diversity = 0,
            EmbeddingMath.Weights? weights = null, bool coGraph = true, bool coRead = true,
            bool taste = true, ICollection<EmbeddingMath.CandidateFeatures>? features = null,
            CancellationToken ct = default)
        {
            Seen.Add(seedIds);
            IReadOnlyList<MangaBakaRecommendation> result =
            [
                .. Enumerable.Range(5001, 50).Select(i => Pick(i))
            ];
            return Task.FromResult(result);
        }
    }

    /// <summary>
    /// A store that reports the dump present and hands back a fixed set of relations.
    /// <paramref name="relatedTo"/> is the seed title they all hang off, as the real
    /// <c>GetRelatedAsync</c> always sets; null leaves it unattributed.
    /// </summary>
    private sealed class RelatingStore(int relations, string? relatedTo = null) : MangaBakaLocalStore(
        new MangaBakaDumpOptions("", ""), new FakeAppSettings(), NullLogger<MangaBakaLocalStore>.Instance)
    {
        public override Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public override Task<IReadOnlyList<MangaBakaRecommendation>> GetRelatedAsync(
            IReadOnlyCollection<long> seedIds, IReadOnlyCollection<long> excludeIds,
            IReadOnlyList<string>? contentRatings = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MangaBakaRecommendation>>(
                [.. Enumerable.Range(9001, relations).Select(i => Pick(i, "Sequel", relatedTo: relatedTo))]);
    }

    /// <summary>
    /// A catalogue pick. The id has to parse as a long: <c>RecommendationService</c> reads it back
    /// out to exclude everything it returned as related from the similarity scan.
    /// </summary>
    private static MangaBakaRecommendation Pick(
        int id, string? relation = null, string? because = null,
        IReadOnlyList<string>? tags = null, IReadOnlyList<string>? genres = null,
        string? relatedTo = null) =>
        new($"{id}", $"Pick {id}", null, null, null, SeriesStatus.Completed, 80, null,
            genres ?? [], tags ?? [], false, relation, relatedTo, because);

    private (RecentActivityRailService Rail, CapturingRecommender Recommender) Service(
        int relations = 0, string? relatedTo = null)
    {
        var recommender = new CapturingRecommender();
        var settings = new FakeAppSettings();
        var recommendations = new RecommendationService(
            _db.ScopeFactory(),
            new RelatingStore(relations, relatedTo),
            recommender,
            new SeedWeightService(
                new BehavioralTasteService(TasteTuning.Default), TasteTuning.Default, settings),
            settings,
            NullLogger<RecommendationService>.Instance);
        var rail = new RecentActivityRailService(
            _db.ScopeFactory(), recommendations,
            new SeedWeightService(new BehavioralTasteService(TasteTuning.Default), TasteTuning.Default, settings),
            NullLogger<RecentActivityRailService>.Instance);
        return (rail, recommender);
    }

    /// <summary>A library series with a MangaBaka id, and one completed chapter read at <paramref name="readAt"/>.</summary>
    private int SeedRead(
        int mangaBakaId, DateTime readAt, Action<Series>? configure = null, int unread = 0)
    {
        var seriesId = _db.SeedSeries($"Series {mangaBakaId}", configure: s =>
        {
            s.MangaBakaId = mangaBakaId;
            configure?.Invoke(s);
        });

        using var db = _db.NewContext();
        var file = new ChapterFile { SeriesId = seriesId, RelativePath = $"{seriesId}.cbz", DateAdded = readAt };
        db.ChapterFiles.Add(file);
        db.SaveChanges();

        var chapter = new Chapter { SeriesId = seriesId, Number = 1, ChapterFileId = file.Id };
        db.Chapters.Add(chapter);
        db.SaveChanges();

        // Downloaded but unread, so the seed is mid-series rather than caught up.
        for (var i = 0; i < unread; i++)
        {
            db.Chapters.Add(new Chapter { SeriesId = seriesId, Number = i + 2, ChapterFileId = file.Id });
        }

        if (unread > 0)
        {
            db.SaveChanges();
        }

        db.ChapterProgress.Add(new ChapterProgress
        {
            UserId = 1,
            SeriesId = seriesId,
            ChapterId = chapter.Id,
            PageCount = 20,
            Completed = true,
            ReadSeconds = 600,
            StartedAt = readAt,
            UpdatedAt = readAt,
        });
        db.SaveChanges();
        return seriesId;
    }

    [Fact]
    public async Task Nothing_read_means_no_rail()
    {
        _db.SeedSeries("Never opened", configure: s => s.MangaBakaId = 101);

        var (rail, recommender) = Service();

        Assert.Null(await rail.GetAsync(new TestCurrentUser(1), refresh: false));
        // Not just an empty rail — the recommender is never asked at all.
        Assert.Empty(recommender.Seen);
    }

    [Fact]
    public async Task Seeds_are_the_most_recently_read_series_newest_first()
    {
        SeedRead(101, Now.AddDays(-30));
        SeedRead(202, Now.AddDays(-1));
        SeedRead(303, Now.AddDays(-10));

        var (rail, recommender) = Service();
        var result = await rail.GetAsync(new TestCurrentUser(1), refresh: false);

        Assert.NotNull(result);
        // RecommendationService sorts the seed list for its cache key, so the ordering that matters
        // is the subtitle's — that is the only place recency is user-visible.
        Assert.Equal([101, 202, 303], recommender.Seen.Single().Order());
        // Subtitle is a catalogue key, rendered by the controller, not the service; see the
        // DiscoverRail doc. The client joins SubtitleTitles into the {list} marker.
        Assert.Equal("discover.rail.becauseYouRead", result.Subtitle);
        Assert.Equivalent(new { list = "{list}" }, result.SubtitleArgs);
        Assert.Equal(["Series 202", "Series 303", "Series 101"], result.SubtitleTitles);
        Assert.Equal(0, result.SubtitleMore);
    }

    [Fact]
    public async Task Ignored_disliked_and_low_rated_titles_do_not_seed_the_rail()
    {
        SeedRead(101, Now.AddDays(-30));
        SeedRead(202, Now.AddDays(-1));
        SeedRead(303, Now.AddDays(-2));
        var lowRated = SeedRead(404, Now.AddDays(-3));
        using (var db = _db.NewContext())
        {
            db.RecommendationSignalOverrides.Add(new RecommendationSignalOverride
            { UserId = 1, ProviderId = 202, IgnoreAsSeed = true });
            db.RecommendationFeedback.Add(new RecommendationFeedback
            { UserId = 1, ProviderId = 303, Sentiment = RecommendationSentiment.Disliked, Revision = 1 });
            db.UserSeriesStates.Add(new UserSeriesState { UserId = 1, SeriesId = lowRated, Rating = 2 });
            db.SaveChanges();
        }

        var (rail, recommender) = Service();
        var result = await rail.GetAsync(new TestCurrentUser(1), refresh: false);

        Assert.NotNull(result);
        Assert.Equal([101], recommender.Seen.Single());
        Assert.Equal(["Series 101"], result.SubtitleTitles);
    }

    [Fact]
    public async Task Only_the_latest_few_series_seed_the_rail()
    {
        for (var i = 0; i < 12; i++)
        {
            SeedRead(100 + i, Now.AddDays(-i));
        }

        var (rail, recommender) = Service();
        var result = await rail.GetAsync(new TestCurrentUser(1), refresh: false);

        // The six most recent, which here are the six smallest ids. Six rather than a rounder
        // number.
        Assert.Equal([100, 101, 102, 103, 104, 105], recommender.Seen.Single().Order());
        Assert.Equal("discover.rail.becauseYouRead", result!.Subtitle);
        Assert.Equal(["Series 100", "Series 101", "Series 102"], result.SubtitleTitles);
        Assert.Equal(3, result.SubtitleMore);
    }

    [Fact]
    public async Task A_fully_incognito_series_never_seeds_the_rail()
    {
        SeedRead(101, Now.AddDays(-1), s => s.Incognito = IncognitoMode.Full);
        SeedRead(202, Now.AddDays(-5));

        var (rail, recommender) = Service();
        var result = await rail.GetAsync(new TestCurrentUser(1), refresh: false);

        // Its ChapterProgress rows exist — only the StatsEvents are suppressed — so this rail has to
        // write the gate out itself, and it must not name the title in the subtitle either.
        Assert.Equal([202], recommender.Seen.Single().Order());
        Assert.Equal("discover.rail.becauseYouRead", result!.Subtitle);
        Assert.Equal(["Series 202"], result.SubtitleTitles);
    }

    [Fact]
    public async Task A_series_the_catalogue_does_not_know_never_seeds_the_rail()
    {
        SeedRead(101, Now.AddDays(-5));
        var unmatched = _db.SeedSeries("Unmatched", configure: s => s.MangaBakaId = null);
        using (var db = _db.NewContext())
        {
            var file = new ChapterFile { SeriesId = unmatched, RelativePath = "u.cbz", DateAdded = Now };
            db.ChapterFiles.Add(file);
            db.SaveChanges();
            var chapter = new Chapter { SeriesId = unmatched, Number = 1, ChapterFileId = file.Id };
            db.Chapters.Add(chapter);
            db.SaveChanges();
            db.ChapterProgress.Add(new ChapterProgress
            {
                UserId = 1,
                SeriesId = unmatched,
                ChapterId = chapter.Id,
                PageCount = 20,
                Completed = true,
                StartedAt = Now,
                UpdatedAt = Now,
            });
            db.SaveChanges();
        }

        var (rail, recommender) = Service();
        await rail.GetAsync(new TestCurrentUser(1), refresh: false);

        // A seed is a MangaBaka id; a series without one cannot be expressed in that space at all.
        Assert.Equal([101], recommender.Seen.Single().Order());
    }

    [Fact]
    public async Task Another_users_reading_does_not_seed_the_rail()
    {
        var other = _db.SeedUser("other");
        SeedRead(101, Now.AddDays(-1));

        var (rail, recommender) = Service();

        Assert.Null(await rail.GetAsync(new TestCurrentUser(other), refresh: false));
        Assert.Empty(recommender.Seen);
    }

    [Fact]
    public async Task Relations_lead_the_rail_but_cannot_take_it_over()
    {
        SeedRead(101, Now.AddDays(-1));

        var (rail, _) = Service(relations: 20);
        var result = await rail.GetAsync(new TestCurrentUser(1), refresh: false);

        Assert.NotNull(result);
        // A finished long-runner can have a dozen side stories; six of them may lead, the rest of
        // the rail is the similarity picks.
        Assert.Equal(6, result.Items.Count(i => i.RelationKind is not null));
        Assert.Equal(40, result.Items.Count);
        Assert.All(result.Items.Take(6), i => Assert.NotNull(i.RelationKind));
    }

    [Fact]
    public async Task One_seeds_relations_cannot_fill_the_lead_of_the_rail()
    {
        SeedRead(101, Now.AddDays(-1));

        // Every relation hangs off the same seed, which is what a franchise with a pile of
        // well-rated spin-offs looks like: the reported case had four of them at positions 3-6.
        var (rail, _) = Service(relations: 20, relatedTo: "Series 101");
        var result = await rail.GetAsync(new TestCurrentUser(1), refresh: false);

        Assert.NotNull(result);
        Assert.Equal(2, result.Items.Count(i => i.RelationKind is not null));
        Assert.All(result.Items.Skip(2), i => Assert.Null(i.RelationKind));
        // The slots the surplus gave up go to the similarity picks, not to a shorter rail.
        Assert.Equal(40, result.Items.Count);
    }

    [Fact]
    public async Task Relations_are_grouped_on_the_franchise_not_the_seed_title()
    {
        SeedRead(101, Now.AddDays(-1));

        // Nothing names a seed here, so the title fallback would treat all twenty as unrelated and
        // six would lead. The vector index says they are one work, and that is the answer that wins:
        // it is also what covers two seeds in one franchise, where the titles differ but the work
        // does not.
        var (rail, recommender) = Service(relations: 20);
        foreach (var id in Enumerable.Range(9001, 20))
        {
            recommender.Franchises[id] = 7;
        }

        var result = await rail.GetAsync(new TestCurrentUser(1), refresh: false);

        Assert.NotNull(result);
        Assert.Equal(2, result.Items.Count(i => i.RelationKind is not null));
    }

    [Fact]
    public async Task One_franchise_cannot_own_a_run_of_the_similarity_picks()
    {
        SeedRead(101, Now.AddDays(-1));

        // The first ten similarity picks are all the same work. Nothing may be dropped, but they
        // must not arrive as a block: at most two of them can sit in the first nine cards.
        var (rail, recommender) = Service();
        foreach (var id in Enumerable.Range(5001, 10))
        {
            recommender.Franchises[id] = 7;
        }

        var result = await rail.GetAsync(new TestCurrentUser(1), refresh: false);

        Assert.NotNull(result);
        Assert.Equal(2, result.Items.Take(9).Count(i => i.FranchiseId == 7));
        Assert.Equal(40, result.Items.Count);

        // Spacing, not suppression: the ones that did not fit were pushed further down the pool,
        // and the ones that did are eight apart rather than dropped.
        var positions = result.Items
            .Select((item, at) => (item.FranchiseId, At: at))
            .Where(x => x.FranchiseId == 7)
            .Select(x => x.At)
            .ToList();
        Assert.NotEmpty(positions);
        Assert.All(
            positions.Zip(positions.Skip(1)),
            pair => Assert.True(pair.Second - pair.First >= 8));
    }

    [Fact]
    public async Task The_rail_carries_its_seeds_for_the_expanded_view()
    {
        SeedRead(101, Now.AddDays(-1));

        var (rail, _) = Service();
        var result = await rail.GetAsync(new TestCurrentUser(1), refresh: false);

        // The client branches on this: the rail's feed name is not a BrowseFeed and the expanded
        // view has to re-query the recommender with the same seeds instead.
        Assert.Equal(RecentActivityRailService.RailKey, result!.Key);
        Assert.Equal(RecentActivityRailService.RailFeed, result.Feed);
        Assert.Equal([101], result.SeedIds!);
    }
}
