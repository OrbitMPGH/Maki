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

        public override bool IsReady() => true;

        public override Task<IReadOnlyList<MangaBakaRecommendation>> GetSimilarAsync(
            IReadOnlyCollection<long> seedIds, IReadOnlyCollection<long> excludeIds,
            int limit, RecommendationFilters? filters = null, double obscurity = 0,
            IReadOnlyDictionary<long, double>? seedWeights = null, double diversity = 0,
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

    /// <summary>A store that reports the dump present and hands back a fixed set of relations.</summary>
    private sealed class RelatingStore(int relations) : MangaBakaLocalStore(
        new MangaBakaDumpOptions("", ""), new FakeAppSettings(), NullLogger<MangaBakaLocalStore>.Instance)
    {
        public override Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public override Task<IReadOnlyList<MangaBakaRecommendation>> GetRelatedAsync(
            IReadOnlyCollection<long> seedIds, IReadOnlyCollection<long> excludeIds,
            IReadOnlyList<string>? contentRatings = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MangaBakaRecommendation>>(
                [.. Enumerable.Range(9001, relations).Select(i => Pick(i, "Sequel"))]);
    }

    /// <summary>
    /// A catalogue pick. The id has to parse as a long: <c>RecommendationService</c> reads it back
    /// out to exclude everything it returned as related from the similarity scan.
    /// </summary>
    private static MangaBakaRecommendation Pick(
        int id, string? relation = null, string? because = null,
        IReadOnlyList<string>? tags = null, IReadOnlyList<string>? genres = null) =>
        new($"{id}", $"Pick {id}", null, null, null, SeriesStatus.Completed, 80, null,
            genres ?? [], tags ?? [], false, relation, null, because);

    private (RecentActivityRailService Rail, CapturingRecommender Recommender) Service(int relations = 0)
    {
        var recommender = new CapturingRecommender();
        var settings = new FakeAppSettings();
        var recommendations = new RecommendationService(
            _db.ScopeFactory(),
            new RelatingStore(relations),
            recommender,
            new SeedWeightService(
                new BehavioralTasteService(TasteTuning.Default), TasteTuning.Default, settings),
            settings,
            NullLogger<RecommendationService>.Instance);
        var rail = new RecentActivityRailService(
            _db.ScopeFactory(), recommendations, NullLogger<RecentActivityRailService>.Instance);
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
        Assert.Equal("Because you read Series 202, Series 303 and Series 101", result.Subtitle);
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
        // number because the grouped rail draws one card per seed in a three-column grid.
        Assert.Equal([100, 101, 102, 103, 104, 105], recommender.Seen.Single().Order());
        Assert.Equal("Because you read Series 100, Series 101 and Series 102 and 3 more", result!.Subtitle);
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
        Assert.Equal("Because you read Series 202", result!.Subtitle);
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

    /// <summary>Attributes one pick to each seed it is given, the way the real recommender does.</summary>
    private sealed class AttributingRecommender(int strays = 0, int attributeTo = int.MaxValue)
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
        public override bool IsReady() => true;

        public override Task<IReadOnlyList<MangaBakaRecommendation>> GetSimilarAsync(
            IReadOnlyCollection<long> seedIds, IReadOnlyCollection<long> excludeIds,
            int limit, RecommendationFilters? filters = null, double obscurity = 0,
            IReadOnlyDictionary<long, double>? seedWeights = null, double diversity = 0,
            EmbeddingMath.Weights? weights = null, bool coGraph = true, bool coRead = true,
            bool taste = true, ICollection<EmbeddingMath.CandidateFeatures>? features = null,
            CancellationToken ct = default)
        {
            var picks = seedIds
                .Take(attributeTo)
                .Select((id, n) => Pick(6000 + n, because: $"Series {id}"))
                .Concat(Enumerable.Range(0, strays).Select(n => Pick(7000 + n)))
                .ToList();
            return Task.FromResult<IReadOnlyList<MangaBakaRecommendation>>(picks);
        }
    }

    private RecentActivityRailService Grouping(int strays = 0, int attributeTo = int.MaxValue)
    {
        var settings = new FakeAppSettings();
        var recommendations = new RecommendationService(
            _db.ScopeFactory(),
            new RelatingStore(0),
            new AttributingRecommender(strays, attributeTo),
            new SeedWeightService(
                new BehavioralTasteService(TasteTuning.Default), TasteTuning.Default, settings),
            settings,
            NullLogger<RecommendationService>.Instance);
        return new RecentActivityRailService(
            _db.ScopeFactory(), recommendations, NullLogger<RecentActivityRailService>.Instance);
    }

    [Fact]
    public async Task Grouped_gives_each_seed_its_own_rail_newest_first()
    {
        SeedRead(101, Now.AddDays(-30));
        SeedRead(202, Now.AddDays(-1));
        SeedRead(303, Now.AddDays(-10));

        var rails = await Grouping().GetGroupedAsync(new TestCurrentUser(1), refresh: false);

        Assert.Equal(
            ["Because you read Series 202", "Because you read Series 303", "Because you read Series 101"],
            rails.Select(r => r.Title));
        // One rail per seed, each carrying only its own seed so "Show more" re-queries that seed.
        Assert.All(rails, r => Assert.Single(r.Items));
        Assert.Equal([[202L], [303L], [101L]], rails.Select(r => r.SeedIds!.ToArray()));
    }

    [Fact]
    public async Task Grouped_omits_a_seed_nothing_was_attributed_to()
    {
        SeedRead(101, Now.AddDays(-1));
        SeedRead(202, Now.AddDays(-2));
        SeedRead(303, Now.AddDays(-3));

        // RecommendationService sorts the seed list, so the one seed that gets picks is the lowest
        // id, 101. The other two seeded the scan and attracted nothing.
        var rails = await Grouping(attributeTo: 1).GetGroupedAsync(new TestCurrentUser(1), refresh: false);

        // A card with a heading and no picks under it is worse than no card, so the rail is skipped
        // rather than emitted empty.
        var rail = Assert.Single(rails);
        Assert.Equal("Because you read Series 101", rail.Title);
        Assert.NotEmpty(rail.Items);
    }

    [Fact]
    public async Task Grouped_drops_a_pick_that_attributes_to_no_seed()
    {
        SeedRead(101, Now.AddDays(-1));

        var rails = await Grouping(strays: 5).GetGroupedAsync(new TestCurrentUser(1), refresh: false);

        // A genre-only hit names no seed. A card headed by a series you read is the whole claim
        // this rail makes, so an unattributable pick is dropped rather than swept into the group.
        var rail = Assert.Single(rails);
        Assert.Single(rail.Items);
        Assert.Equal("Series 101", rail.Items[0].BecauseOfTitle);
    }

    [Fact]
    public async Task Grouped_carries_how_far_through_each_seed_the_reader_is()
    {
        SeedRead(101, Now.AddDays(-1), unread: 4);
        SeedRead(202, Now.AddDays(-2), s => s.Status = SeriesStatus.Ongoing);
        SeedRead(303, Now.AddDays(-3), s => s.Status = SeriesStatus.Completed);

        var rails = await Grouping().GetGroupedAsync(new TestCurrentUser(1), refresh: false);
        var byTitle = rails.ToDictionary(r => r.Seed!.Title, r => r.Seed!);

        // Chapters left to read.
        Assert.Equal(("reading", 1, 5), Shape(byTitle["Series 101"]));
        // Nothing left, but the series continues upstream.
        Assert.Equal(("caught-up", 1, 1), Shape(byTitle["Series 202"]));
        // Nothing left and nothing coming.
        Assert.Equal(("finished", 1, 1), Shape(byTitle["Series 303"]));

        static (string, int, int) Shape(SeedState s) => (s.State, s.ChaptersRead, s.ChaptersAvailable);
    }

    [Fact]
    public async Task Grouped_is_empty_when_there_is_nothing_to_seed_with()
    {
        _db.SeedSeries("Never opened", configure: s => s.MangaBakaId = 404);

        Assert.Empty(await Grouping().GetGroupedAsync(new TestCurrentUser(1), refresh: false));
    }

    /// <summary>Returns one unattributed pick per tag, so overlap is the only thing that can file it.</summary>
    private sealed class TaggedRecommender(params string[] tags) : SemanticRecommender(
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
        public override bool IsReady() => true;

        public override Task<IReadOnlyList<MangaBakaRecommendation>> GetSimilarAsync(
            IReadOnlyCollection<long> seedIds, IReadOnlyCollection<long> excludeIds,
            int limit, RecommendationFilters? filters = null, double obscurity = 0,
            IReadOnlyDictionary<long, double>? seedWeights = null, double diversity = 0,
            EmbeddingMath.Weights? weights = null, bool coGraph = true, bool coRead = true,
            bool taste = true, ICollection<EmbeddingMath.CandidateFeatures>? features = null,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MangaBakaRecommendation>>(
                [.. tags.Select((t, n) => Pick(8000 + n, tags: [t]))]);
    }

    /// <summary>
    /// The recommender names a seed on a minority of picks — it is null for a genre-only hit, which
    /// on a library whose seeds share a genre is most of them. Overlap against the tags the pick was
    /// matched on is what fills the rest of the cards.
    /// </summary>
    [Fact]
    public async Task Grouped_files_an_unattributed_pick_under_the_seed_it_overlaps()
    {
        SeedRead(101, Now.AddDays(-1), s => s.Tags = ["Murim"]);
        SeedRead(202, Now.AddDays(-2), s => s.Tags = ["Time Loop"]);

        var settings = new FakeAppSettings();
        var recommendations = new RecommendationService(
            _db.ScopeFactory(),
            new RelatingStore(0),
            new TaggedRecommender("Time Loop", "Murim"),
            new SeedWeightService(
                new BehavioralTasteService(TasteTuning.Default), TasteTuning.Default, settings),
            settings,
            NullLogger<RecommendationService>.Instance);
        var rail = new RecentActivityRailService(
            _db.ScopeFactory(), recommendations, NullLogger<RecentActivityRailService>.Instance);

        var rails = await rail.GetGroupedAsync(new TestCurrentUser(1), refresh: false);
        var bySeed = rails.ToDictionary(r => r.Seed!.Title, r => r.Items.Select(i => i.MatchedTags[0]).ToList());

        // Neither pick names a seed; each lands under the one sharing its tag rather than being
        // dropped or piling onto whichever seed sorted first.
        Assert.Equal(["Murim"], bySeed["Series 101"]);
        Assert.Equal(["Time Loop"], bySeed["Series 202"]);
    }
}
