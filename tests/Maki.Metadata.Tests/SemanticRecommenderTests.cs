using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Maki.Metadata.CoRead;
using Maki.Metadata.RecoGraph;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Maki.Metadata.Tests;

/// <summary>
/// End-to-end over a tiny fake dump plus a real vector store, so the retrieval shape is exercised
/// through the same code path the app uses (index build included) rather than against a stub.
/// </summary>
public class SemanticRecommenderTests : IDisposable
{
    private const int Dim = 16;

    private readonly string _dir;
    private readonly string _dumpPath;
    private readonly string _vectorPath;
    private readonly List<string> _rows = [];

    public SemanticRecommenderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "maki-semreco-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dumpPath = Path.Combine(_dir, "mangabaka.db");
        _vectorPath = Path.Combine(_dir, "embeddings.db");
    }

    [Fact]
    public async Task ASeedWhoseTasteIsTwoThings_StillSurfacesAMatchForEitherHalf()
    {
        // Two seeds pointing in unrelated directions, so their centroid sits near neither. Twin is
        // a ~0.997 match for one seed but only ~0.70 against that centroid, while a crowd of
        // middling candidates sits at ~0.90 against the centroid and ~0.64 against either seed.
        // Ranked by the centroid alone — what a single mean-vector query does — every middling
        // candidate outranks Twin and it never makes the page, which is the dilution being fixed.
        var alpha = Axis(0);
        var beta = Axis(1);
        var centroid = Blend(alpha, beta);

        var vectors = new List<(long Id, string Hash, float[] Vector)>
        {
            (1, "h", alpha),
            (2, "h", beta),
            (10, "h", Nudge(alpha, 2, 0.08f)),
        };
        Add(1, "Alpha");
        Add(2, "Beta");
        Add(10, "Twin of Alpha");
        for (var i = 0; i < 8; i++)
        {
            Add(100 + i, $"Middling {i}");
            vectors.Add((100 + i, "h", Nudge(centroid, 3 + i, 0.48f)));
        }

        WriteDump();
        Store().UpsertBatch(vectors);

        var picks = await Recommender().GetSimilarAsync([1, 2], [], limit: 3);

        Assert.Equal("10", picks[0].ProviderId);
        // And the attribution names the seed that actually drove it, not the whole seed set.
        Assert.Equal("Alpha", picks[0].BecauseOfTitle);
        // The middlings are still recommendable, just no longer ahead of a better match.
        Assert.Equal(3, picks.Count);
        Assert.All(picks.Skip(1), p => Assert.StartsWith("Middling", p.Title));
    }

    [Fact]
    public async Task Seeds_AndExplicitExclusions_AreNeverRecommendedBack()
    {
        Add(1, "Alpha");
        Add(10, "Excluded");
        Add(11, "Wanted");
        WriteDump();
        Store().UpsertBatch([
            (1L, "h", Axis(0)),
            (10L, "h", Nudge(Axis(0), 2, 0.1f)),
            (11L, "h", Nudge(Axis(0), 3, 0.1f)),
        ]);

        var picks = await Recommender().GetSimilarAsync([1], [10], limit: 10);

        Assert.Equal(["11"], picks.Select(p => p.ProviderId));
    }

    [Fact]
    public async Task Diversity_SwapsANearDuplicateForSomethingElseThatMatches()
    {
        // Pair and PairTwin are all but the same vector; Other is a weaker match but unrelated to
        // both. At diversity 0 the pair takes both slots, which is the homogeneity complaint.
        var seed = Spread(0, 1, 2, 3);
        Add(1, "Seed");
        Add(10, "Pair");
        Add(11, "Pair Twin");
        Add(12, "Other");
        WriteDump();
        Store().UpsertBatch([
            (1L, "h", seed),
            (10L, "h", Spread(0, 1, 2)),
            (11L, "h", Nudge(Spread(0, 1, 2), 8, 0.02f)),
            (12L, "h", Blend(Axis(3), Scaled(Axis(0), 0.3f))),
        ]);

        var packed = await Recommender().GetSimilarAsync([1], [], limit: 2);
        var spread = await Recommender().GetSimilarAsync([1], [], limit: 2, diversity: 0.6);

        Assert.Equal(["10", "11"], packed.Select(p => p.ProviderId));
        Assert.Equal(["10", "12"], spread.Select(p => p.ProviderId));
    }

    [Fact]
    public async Task FiltersAreAppliedBeforeTheTopK_SoAFilteredQueryIsNotTruncatedToNothing()
    {
        // Every close match is a manhwa; the one manga is the distant candidate. A filter applied
        // to an already-chosen top-K would leave nothing.
        Add(1, "Seed");
        for (var i = 0; i < 6; i++)
        {
            Add(10 + i, $"Near {i}", type: "manhwa");
        }

        Add(50, "Distant Manga");
        WriteDump();
        var vectors = new List<(long, string, float[])> { (1L, "h", Axis(0)), (50L, "h", Spread(0, 1, 2)) };
        for (var i = 0; i < 6; i++)
        {
            vectors.Add((10 + i, "h", Nudge(Axis(0), 4 + i, 0.05f)));
        }

        Store().UpsertBatch(vectors);

        var picks = await Recommender().GetSimilarAsync(
            [1], [], limit: 3, new RecommendationFilters(Types: ["manga"]));

        Assert.Equal(["50"], picks.Select(p => p.ProviderId));
    }

    [Fact]
    public async Task TheGenreChannelDoesNotGetLouderAsTheSeedSetNarrows()
    {
        // The bug this pins used to be structural: BuildProfileAsync spreads 1/seedCount over each
        // seed's genres, so a genre EVERY seed carries scores 1.0 whether that is one seed or a
        // hundred, and the old raw-sum channel then paid a three-genre candidate ~3.0 — as much as
        // the semantic term pays for a perfect cosine. Feel lost to genre on exactly the requests
        // where feel is all there is: the "More like this" rail and a two-seed Discover.
        //
        // GenreTwin is a poor feel match (~0.45) sharing all three genres and the author; FeelMatch
        // is an excellent one (~0.95) sharing neither. The ordering between them must not depend on
        // how many seeds asked, which is what "the channel is a cosine now" means in practice.
        Add(1, "Seed", genres: """["Action","Drama","Fantasy"]""");
        Add(2, "Second Seed", genres: """["Action","Drama","Fantasy"]""");
        Add(3, "Third Seed", genres: """["Action","Drama","Fantasy"]""");
        Add(10, "Genre Twin", genres: """["Action","Drama","Fantasy"]""");
        Add(11, "Feel Match", genres: """["Sports"]""", authors: """["Someone Else"]""");
        WriteDump();
        Store().UpsertBatch([
            (1L, "h", Axis(0)),
            (2L, "h", Nudge(Axis(0), 6, 0.05f)),
            (3L, "h", Nudge(Axis(0), 7, 0.05f)),
            (10L, "h", Spread(0, 1, 2, 3, 4)),
            (11L, "h", Nudge(Axis(0), 5, 0.33f)),
        ]);

        // The other two seeds stay excluded from the single-seed call, so both requests choose from
        // the identical candidate set and the only thing that differs is how wide the profile is.
        var one = await Recommender().GetSimilarAsync([1], [2, 3], limit: 2);
        var three = await Recommender().GetSimilarAsync([1, 2, 3], [], limit: 2);

        Assert.Equal(one.Select(p => p.ProviderId), three.Select(p => p.ProviderId));
    }

    [Fact]
    public async Task APerfectGenreMatchCannotOutpayAPerfectFeelMatch()
    {
        // The ceiling the normalization buys. A candidate sharing the seed's entire genre list
        // scores 1.0 on that channel and no more, so with Genre at 1.0 and Semantic at 3.0 genre is
        // a tiebreak between comparable matches rather than a second ranking that overrides the
        // first. Author is deliberately held equal here: it is a flat 0.75 either way, and lowering
        // it measured worse, so it is not part of what this asserts.
        Add(1, "Seed", genres: """["Action","Drama","Fantasy"]""");
        Add(10, "Genre Twin", genres: """["Action","Drama","Fantasy"]""");
        Add(11, "Feel Match", genres: """["Sports"]""");
        WriteDump();
        Store().UpsertBatch([
            (1L, "h", Axis(0)),
            // Far enough away that the ~1.0 of genre it collects cannot close the semantic gap.
            (10L, "h", Spread(0, 1, 2, 3, 4, 5, 6, 7)),
            (11L, "h", Nudge(Axis(0), 5, 0.10f)),
        ]);

        var picks = await Recommender().GetSimilarAsync([1], [], limit: 2);

        Assert.Equal(["11", "10"], picks.Select(p => p.ProviderId));
    }

    [Fact]
    public async Task SeedWeightsReachTheGenreProfile_NotJustTheCentroid()
    {
        // Rating and reading history used to steer only which query vectors got built. Genre and Tag
        // carry 2.5 of the hybrid score's ~7 points between them, so half the ranking was blind to
        // the taste the rest of it was weighted by.
        //
        // Two seeds with disjoint genres, sitting on the same axis so the centroid is the same
        // whichever one is favoured and the semantic channel cannot be what moves the answer. The
        // candidates share one genre each; whichever seed is weighted up should bring its own.
        Add(1, "Sports Seed", genres: """["Sports"]""", authors: """["A"]""");
        Add(2, "Horror Seed", genres: """["Horror"]""", authors: """["B"]""");
        Add(10, "Sports Candidate", genres: """["Sports"]""", authors: """["C"]""");
        Add(11, "Horror Candidate", genres: """["Horror"]""", authors: """["D"]""");
        WriteDump();
        Store().UpsertBatch([
            (1L, "h", Axis(0)),
            (2L, "h", Axis(0)),
            (10L, "h", Nudge(Axis(0), 3, 0.20f)),
            (11L, "h", Nudge(Axis(0), 4, 0.20f)),
        ]);

        var favouringSports = await Recommender().GetSimilarAsync(
            [1, 2], [], limit: 2, seedWeights: new Dictionary<long, double> { [1] = 1.8, [2] = 0.4 });
        var favouringHorror = await Recommender().GetSimilarAsync(
            [1, 2], [], limit: 2, seedWeights: new Dictionary<long, double> { [1] = 0.4, [2] = 1.8 });

        Assert.Equal("10", favouringSports[0].ProviderId);
        Assert.Equal("11", favouringHorror[0].ProviderId);
    }

    [Fact]
    public async Task OneSeed_AttributesNothing_BecauseTheCentroidIsTheSeed()
    {
        // With a single seed the centroid *is* that seed's vector, so BuildQueries emits it alone and
        // BecauseOfTitle stays null. "Feels like <the one series you asked about>" would be noise, and
        // the duplicate per-seed query would double the scan to produce it.
        Add(1, "Seed");
        Add(10, "Candidate");
        WriteDump();
        Store().UpsertBatch([(1L, "h", Axis(0)), (10L, "h", Nudge(Axis(0), 2, 0.1f))]);

        var picks = await Recommender().GetSimilarAsync([1], [], limit: 5);

        Assert.Equal(["10"], picks.Select(p => p.ProviderId));
        Assert.Null(picks[0].BecauseOfTitle);
    }

    [Fact]
    public async Task NoIndexYet_ReturnsNothingSoTheCallerCanFallBack()
    {
        Add(1, "Seed");
        WriteDump();
        Store(); // schema only — no vectors

        Assert.Empty(await Recommender().GetSimilarAsync([1], [], limit: 5));
    }

    [Fact]
    public async Task ACoRecommendedCandidate_OutranksACloserFeelMatch()
    {
        // The point of the whole channel, in one case. Vagabond is not what Berserk's *description*
        // is nearest to, which is exactly why the embeddings rank it below something blander and
        // why readers do not. Closer wins on cosine (~0.86 against ~0.74); the vote graph wins anyway,
        // because the seed's readers overwhelmingly went on to it.
        Add(1, "Seed");
        Add(10, "Closer by feel");
        Add(11, "Co-read");
        WriteDump();
        Store().UpsertBatch([
            (1L, "h", Axis(0)),
            (10L, "h", Nudge(Axis(0), 2, 0.6f)),
            (11L, "h", Nudge(Axis(0), 3, 0.9f)),
        ]);

        var withoutGraph = await Recommender().GetSimilarAsync([1], [], limit: 5);
        var withGraph = await Recommender(WriteGraph((1, 11, 500))).GetSimilarAsync([1], [], limit: 5);

        Assert.Equal(["10", "11"], withoutGraph.Select(p => p.ProviderId));
        Assert.Equal(["11", "10"], withGraph.Select(p => p.ProviderId));
        // And it says so, since a pick that reads as the weaker match needs explaining.
        Assert.True(withGraph[0].CoRecommended);
        Assert.False(withGraph[1].CoRecommended);
    }

    [Fact]
    public async Task TheSameCandidate_IsStillDroppedByAFilter_BecauseInjectionRespectsThePlan()
    {
        // The injection path reads the scan's own negative-infinity sentinel rather than re-testing
        // the filters, so a filter it never learned about must still bind. If this ever fails, the
        // channel has become a way to smuggle rows past RecommendationFilters.
        Add(1, "Seed");
        Add(10, "Co-read", type: "manhwa");
        WriteDump();
        Store().UpsertBatch([(1L, "h", Axis(0)), (10L, "h", Nudge(Axis(0), 3, 0.9f))]);

        var graph = WriteGraph((1, 10, 500));

        Assert.Empty(await Recommender(graph).GetSimilarAsync(
            [1], [], limit: 5, new RecommendationFilters(Types: ["manga"])));

        // Same series, same graph, no filter: proving the emptiness above is the filter's doing and
        // not the candidate quietly failing to qualify for some other reason.
        Assert.Equal(
            ["10"],
            (await Recommender(graph).GetSimilarAsync([1], [], limit: 5)).Select(p => p.ProviderId));
    }

    [Fact]
    public async Task ASeedIsNeverRecommendedBack_EvenWhenTheGraphPairsItWithAnotherSeed()
    {
        Add(1, "Alpha");
        Add(2, "Beta");
        Add(10, "Candidate");
        WriteDump();
        Store().UpsertBatch([
            (1L, "h", Axis(0)),
            (2L, "h", Axis(1)),
            (10L, "h", Nudge(Axis(0), 2, 0.1f)),
        ]);

        var picks = await Recommender(WriteGraph((1, 2, 900), (1, 10, 40)))
            .GetSimilarAsync([1, 2], [], limit: 5);

        Assert.Equal(["10"], picks.Select(p => p.ProviderId));
    }

    [Fact]
    public async Task AnEdgeUnderTheVoteFloor_IsNotEvidence()
    {
        // One person clicking "recommend" is noise, and the long tail of the real artifact is
        // single-vote pairs — median 2 against a maximum of 6008. Same setup as the reordering test
        // above, but the edge carries one vote, so the order must not move.
        Add(1, "Seed");
        Add(10, "Closer by feel");
        Add(11, "Barely paired");
        WriteDump();
        Store().UpsertBatch([
            (1L, "h", Axis(0)),
            (10L, "h", Nudge(Axis(0), 2, 0.6f)),
            (11L, "h", Nudge(Axis(0), 3, 0.9f)),
        ]);

        var graph = WriteGraph((1, 11, 1));

        var ignored = await Recommender(graph).GetSimilarAsync([1], [], limit: 5);
        var counted = await Recommender(graph, RecoGraphTuning.Default with { MinVotes = 1 })
            .GetSimilarAsync([1], [], limit: 5);

        Assert.Equal(["10", "11"], ignored.Select(p => p.ProviderId));
        Assert.All(ignored, p => Assert.False(p.CoRecommended));
        Assert.Equal(["11", "10"], counted.Select(p => p.ProviderId));
    }

    [Fact]
    public void Injection_CapsItself_AndTakesTheBestVouchedRowsFirst()
    {
        // Tested directly rather than through GetSimilarAsync: the RRF pool is 200 rows deep, so a
        // fixture would need a bigger catalogue than that before anything is *injected* at all
        // rather than simply pooled, and at that size the assertion stops being about the cap.
        // Row 3 is the sentinel case — it failed the filter plan, so the scan wrote negative
        // infinity and it must not come back however well the graph vouches for it.
        var cosines = new[] { new[] { 0.9f, 0.2f, 0.2f, float.NegativeInfinity, 0.2f, 0.2f } };
        var graph = new Dictionary<int, double>
        {
            [1] = 0.65, [2] = 0.90, [3] = 1.00, [4] = 0.10, [5] = 0.70,
        };

        var open = RecoGraphTuning.Default with { MaxInjected = 10, MinInjectedScore = 0 };
        var capped = SemanticRecommender.InjectGraphCandidates(cosines, [0], graph, open with { MaxInjected = 2 });
        var uncapped = SemanticRecommender.InjectGraphCandidates(cosines, [0], graph, open);
        var corroborated = SemanticRecommender.InjectGraphCandidates(cosines, [0], graph, open with { MinInjectedScore = 0.60 });

        Assert.Equal([2, 5], capped);
        Assert.Equal([1, 2, 4, 5], uncapped.Order());
        // Row 4 is the thin-evidence case the real library surfaced: real, but nowhere near enough
        // to earn a place the cosine ranking never gave it.
        Assert.Equal([1, 2, 5], corroborated.Order());
    }

    [Fact]
    public async Task ASecondDumpEntryForASeed_IsNotRecommendedBackAsItsOwnLookalike()
    {
        // MangaBaka carries genuine duplicates: two active rows for one work, different ids, and
        // merged_with null on both so the dump's own dedupe never fires. Found in production - "A
        // Couple of Cuckoos" is seed 543 and also id 67567, and seeding on it put the copy at rank
        // two labelled "feels like A Couple of Cuckoos".
        Add(1, "A Couple of Cuckoos");
        Add(2, "A Couple of Cuckoos");
        Add(10, "Something Else");
        WriteDump();
        Store().UpsertBatch([
            (1L, "h", Axis(0)),
            // Not byte-identical, because a duplicate entry is a separate description of the same
            // work rather than a copy of the row. Excluding it cannot rely on the vectors matching.
            (2L, "h", Nudge(Axis(0), 1, 0.02f)),
            (10L, "h", Nudge(Axis(0), 2, 0.30f)),
        ]);

        var picks = await Recommender().GetSimilarAsync([1], [], limit: 5);

        Assert.DoesNotContain("2", picks.Select(p => p.ProviderId));
        Assert.Contains("10", picks.Select(p => p.ProviderId));
    }

    [Fact]
    public async Task DuplicateExclusion_MatchesOnNormalizedTitle_NotOnTheRawString()
    {
        // Casing and punctuation differences are what SeriesIdentity.NormalizeTitle exists to
        // absorb, and the dump is inconsistent about both. Id 3 differs only in case, which the SQL
        // prefilter catches; nothing here should reach the results but the genuinely different work.
        Add(1, "Blue Box");
        Add(2, "BLUE BOX");
        Add(10, "Blue Period");
        WriteDump();
        Store().UpsertBatch([
            (1L, "h", Axis(0)),
            (2L, "h", Nudge(Axis(0), 1, 0.02f)),
            (10L, "h", Nudge(Axis(0), 2, 0.30f)),
        ]);

        var picks = await Recommender().GetSimilarAsync([1], [], limit: 5);

        Assert.Equal(["10"], picks.Select(p => p.ProviderId));
    }

    [Fact]
    public async Task StandardizedLabelOnly_RenamesThePicksWithoutReorderingThem()
    {
        // The control's entire claim. It moves which query is credited and nothing else, so the
        // ranking has to come back byte for byte identical to the shipped mode - otherwise the eval
        // cannot separate "naming a seed is worth something" from "the score moved underneath it".
        SpreadFixture();

        var open = RecommenderTuning.Default with
        {
            AttributionMargin = 0, AttributionScale = AttributionScale.Absolute,
        };
        var baseline = await Recommender(tuning: open with
            {
                QueryAttribution = QueryAttribution.RawCosine,
            })
            .GetSimilarAsync([1, 2], [], limit: 12);
        var labelled = await Recommender(
                tuning: open with
                {
                    QueryAttribution = QueryAttribution.StandardizedLabelOnly,
                })
            .GetSimilarAsync([1, 2], [], limit: 12);

        Assert.Equal(baseline.Select(p => p.ProviderId), labelled.Select(p => p.ProviderId));
        // And it is not a no-op: something has to actually get named differently, or the fixture
        // stopped exercising the thing and the equality above is passing for the wrong reason.
        Assert.NotEqual(
            baseline.Select(p => p.BecauseOfTitle), labelled.Select(p => p.BecauseOfTitle));
    }

    [Fact]
    public async Task AttributionMargin_TurnsNamingDown_WithoutTouchingTheRanking()
    {
        // The two halves have to stay separable. The margin decides who may be named and nothing
        // else, so every margin has to return the same rows in the same order - otherwise tuning how
        // often the UI says "feels like X" quietly retunes what the UI shows, and the eval cannot
        // attribute a relevance change to either one.
        SpreadFixture();

        var picks = new List<IReadOnlyList<MangaBakaRecommendation>>();
        foreach (var margin in new[] { 0.0, 0.25, 0.75, 50.0 })
        {
            picks.Add(await Recommender(
                    tuning: RecommenderTuning.Default with
                    {
                        QueryAttribution = QueryAttribution.Standardized,
                        AttributionMargin = margin,
                    })
                .GetSimilarAsync([1, 2], [], limit: 12));
        }

        Assert.All(picks, p => Assert.Equal(
            picks[0].Select(x => x.ProviderId), p.Select(x => x.ProviderId)));

        var named = picks.Select(p => p.Count(x => x.BecauseOfTitle is not null)).ToList();
        // Monotone down, and an unreachable margin names nobody rather than falling back to a
        // best guess. "Nothing here is distinctively like one title" is a real answer.
        Assert.Equal(named.OrderByDescending(n => n), named);
        Assert.True(named[0] > named[^1], $"margin did not reduce naming: [{string.Join(", ", named)}]");
        Assert.Equal(0, named[^1]);
    }

    [Fact]
    public async Task TheDistinctWeight_PutsMoreNameableRowsOnThePage()
    {
        // The ranking half. At a fixed margin - so the bar for naming is held still - paying for
        // distinctiveness in the score has to surface more rows that clear it. This is the knob for
        // "show more of these" as opposed to "call more of them that".
        CrowdedFixture();

        // Margin 0, so "named" is exactly "some seed explains this better than the library does"
        // and the count is measuring the ranking rather than the gate.
        var tuning = RecommenderTuning.Default with
        {
            AttributionMargin = 0, AttributionScale = AttributionScale.Absolute,
        };

        async Task<int> NamedInTopTen(double distinct) =>
            (await Recommender(tuning: tuning).GetSimilarAsync(
                [1, 2], [], limit: 10, weights: new EmbeddingMath.Weights(Distinct: distinct)))
            .Count(p => p.BecauseOfTitle is not null);

        var flat = await NamedInTopTen(0);
        var boosted = await NamedInTopTen(3.0);

        Assert.True(
            boosted > flat,
            $"paying for distinctiveness should surface more single-seed rows: {flat} -> {boosted}");
    }

    [Fact]
    public async Task ASingleSeed_NamesNothing_HoweverLowTheMarginGoes()
    {
        // A single-seed request is centroid only - the centroid of one vector is that vector, so
        // BuildQueries drops the duplicate per-seed query. There is then no seed query to compare
        // against the centroid, and "feels like the series you are looking at" is not an
        // explanation. The margin must not be able to talk the recommender into printing it.
        SpreadFixture();

        var picks = await Recommender(
                tuning: RecommenderTuning.Default with
                {
                    QueryAttribution = QueryAttribution.Standardized,
                    AttributionMargin = -100,
                })
            .GetSimilarAsync([1], [], limit: 8);

        Assert.NotEmpty(picks);
        Assert.All(picks, p => Assert.Null(p.BecauseOfTitle));
    }

    [Fact]
    public async Task TheMarginGatesRawCosineToo_InItsOwnUnits()
    {
        // The margin is not a standardization feature. Under RawCosine it is a cosine difference
        // rather than a count of standard deviations, so the numbers that mean anything are far
        // smaller - which is the trap the tuning doc warns about, and worth a test rather than only
        // a comment.
        SpreadFixture();

        async Task<int> Named(double margin) =>
            (await Recommender(
                    tuning: RecommenderTuning.Default with
                    {
                        QueryAttribution = QueryAttribution.RawCosine,
                        AttributionMargin = margin,
                        AttributionScale = AttributionScale.Absolute,
                    })
                .GetSimilarAsync([1, 2], [], limit: 14)).Count(p => p.BecauseOfTitle is not null);

        var open = await Named(0);
        var gated = await Named(0.08);
        var shut = await Named(1);

        Assert.True(open > 0, "RawCosine at margin 0 should still name whatever beat the centroid");
        Assert.True(gated < open, $"a 0.08 cosine margin should gate something: {gated} vs {open}");
        Assert.True(gated > 0, "and should not gate everything - that is what margin 1 is for");
        // A margin of 1 is unreachable in cosine units and shuts naming off entirely, where under
        // Standardized 1 is a mild setting that still names most rows. Same number, different
        // question, which is why the tuning doc says a margin does not carry across modes.
        Assert.Equal(0, shut);
    }

    [Fact]
    public async Task TheDistinctWeight_NeverPenalisesARowNoSeedStandsBehind()
    {
        // Same contract the graph channel has: a bonus, never a gate. Distinctiveness is clamped at
        // zero, so a row the library as a whole explains better than any single seed does has to
        // score exactly where it scored before the weight existed. Otherwise turning the knob up
        // does not surface distinctive rows, it buries generic ones, and those are different
        // changes with different failure modes.
        SpreadFixture();

        // Margin 0 on purpose. The clamp applies to rows no seed beats the centroid on at all, and
        // at any higher margin "unattributed" is a wider set than that - a row can be distinctive
        // enough to earn the bonus and still not distinctive enough to claim a title, and it is
        // supposed to move. At margin 0 the two sets coincide, which is what makes this exact.
        // RawCosine because standardizing three channels leaves a seed ahead of the centroid on
        // nearly every row, so the clamped set is empty there and the assertion would be vacuous.
        // Absolute because the equivalence this rests on - unattributed exactly when the clamp bit -
        // only holds when the bar is the raw zero rather than a position in the pool's spread.
        var tuning = RecommenderTuning.Default with
        {
            QueryAttribution = QueryAttribution.RawCosine,
            AttributionMargin = 0,
            AttributionScale = AttributionScale.Absolute,
        };

        var flat = await Recommender(tuning: tuning).GetSimilarAsync(
            [1, 2], [], limit: 14, weights: new EmbeddingMath.Weights());
        var boosted = await Recommender(tuning: tuning).GetSimilarAsync(
            [1, 2], [], limit: 14, weights: new EmbeddingMath.Weights(Distinct: 3.0));

        // Every row still comes back; the weight reorders, it does not exclude.
        Assert.Equal(
            flat.Select(p => p.ProviderId).Order(),
            boosted.Select(p => p.ProviderId).Order());

        var clamped = (IReadOnlyList<MangaBakaRecommendation> picks) => picks
            .Where(p => p.BecauseOfTitle is null).Select(p => p.ProviderId).ToList();
        Assert.NotEmpty(clamped(flat));
        Assert.Equal(clamped(flat), clamped(boosted));
    }

    [Fact]
    public async Task TheDistinctWeight_DoesNotChangeWhoMayBeNamed()
    {
        // The two knobs have to stay orthogonal in both directions. The margin does not reorder
        // (pinned above) and the weight does not re-gate: a row either clears the bar or it does
        // not, and how heavily the score paid for distinctiveness is beside that question. Without
        // this, sweeping wdistinct silently moves the naming rate through two mechanisms at once and
        // the calibration in the eval means nothing.
        SpreadFixture();

        var tuning = RecommenderTuning.Default with
        {
            QueryAttribution = QueryAttribution.Standardized,
            AttributionMargin = 0.75,
        };

        var flat = await Recommender(tuning: tuning).GetSimilarAsync(
            [1, 2], [], limit: 14, weights: new EmbeddingMath.Weights());
        var boosted = await Recommender(tuning: tuning).GetSimilarAsync(
            [1, 2], [], limit: 14, weights: new EmbeddingMath.Weights(Distinct: 3.0));

        Dictionary<string, string?> Naming(IReadOnlyList<MangaBakaRecommendation> picks) =>
            picks.ToDictionary(p => p.ProviderId, p => p.BecauseOfTitle);

        Assert.Equal(Naming(flat), Naming(boosted));
    }

    /// <summary>
    /// Thirty rows hugging the seed centroid and ten sitting on one seed, which is the shape that
    /// makes the ranking question visible: the centroid-huggers are the more similar rows on any
    /// single cosine, so they take the whole top of the page by default and the single-seed rows sit
    /// below the cut until distinctiveness is worth paying for. <see cref="SpreadFixture"/> is too
    /// small to show this - at fourteen rows a top-ten is most of the catalogue and there is nothing
    /// for a reordering to promote.
    /// </summary>
    private void CrowdedFixture()
    {
        Add(1, "Seed A");
        Add(2, "Seed B");
        var vectors = new List<(long, string, float[])>
        {
            (1L, "h", Spread(0, 1, 2)),
            (2L, "h", Spread(0, 1, 3)),
        };

        for (var i = 0; i < 30; i++)
        {
            Add(100 + i, $"Generic {i}");
            vectors.Add((100L + i, "h", Nudge(Spread(0, 1, 2, 3), 4 + (i % 8), 0.02f + (i * 0.002f))));
        }

        for (var i = 0; i < 10; i++)
        {
            Add(200 + i, $"Distinctive {i}");
            vectors.Add((200L + i, "h", Nudge(Spread(0, 1, 2), 10 + (i % 6), 0.50f + (i * 0.02f))));
        }

        WriteDump();
        Store().UpsertBatch(vectors);
    }

    /// <summary>
    /// Two seeds, eight rows that sit near their centroid and six that sit on top of one seed. The
    /// split is the point. The generic rows are the more similar ones on any single number, so they
    /// take the ranking by default and the distinctive ones only surface if something pays for
    /// distinctiveness - which is exactly the arrangement the two knobs are for, and a fixture where
    /// every candidate is equally close to everything could not tell a naming rule from a coin toss.
    /// </summary>
    private void SpreadFixture()
    {
        Add(1, "Seed A");
        Add(2, "Seed B");
        var vectors = new List<(long, string, float[])>
        {
            (1L, "h", Spread(0, 1, 2)),
            (2L, "h", Spread(0, 1, 3)),
        };

        for (var i = 0; i < 8; i++)
        {
            Add(10 + i, $"Generic {i}");
            vectors.Add((10L + i, "h", Nudge(Spread(0, 1, 2, 3), 4 + i, 0.05f + (i * 0.01f))));
        }

        for (var i = 0; i < 6; i++)
        {
            Add(20 + i, $"Distinctive {i}");
            vectors.Add((20L + i, "h", Nudge(Spread(0, 1, 2), 10 + i, 0.15f + (i * 0.18f))));
        }

        WriteDump();
        Store().UpsertBatch(vectors);
    }

    [Fact]
    public async Task ACandidateWithNoEdges_ScoresIdenticallyWhetherOrNotAGraphIsInstalled()
    {
        // The channel is a bonus, never a gate: three quarters of the catalogue has no edge at all,
        // and those series must rank exactly where they ranked before this existed.
        Add(1, "Seed");
        Add(10, "Unpaired A");
        Add(11, "Unpaired B");
        Add(12, "Paired elsewhere");
        WriteDump();
        Store().UpsertBatch([
            (1L, "h", Axis(0)),
            (10L, "h", Nudge(Axis(0), 2, 0.10f)),
            (11L, "h", Nudge(Axis(0), 3, 0.12f)),
            (12L, "h", Nudge(Axis(0), 4, 0.14f)),
        ]);

        var without = await Recommender().GetSimilarAsync([1], [], limit: 5);
        // An edge between two candidates, touching no seed, so nothing here should move.
        var with = await Recommender(WriteGraph((11, 12, 800))).GetSimilarAsync([1], [], limit: 5);

        Assert.Equal(without.Select(p => p.ProviderId), with.Select(p => p.ProviderId));
        Assert.All(with, p => Assert.False(p.CoRecommended));
    }

    private void Add(
        long id, string title, string type = "manga", string genres = """["Action"]""",
        string authors = """["Author"]""") =>
        _rows.Add(
            $"({id}, 'active', 80, 'safe', '{type}', 'completed', 2000, '{title}', " +
            $"'http://c/{id}.jpg', 'desc', '12', '{genres}', '{authors}', {id})");

    private void WriteDump()
    {
        using var conn = new SqliteConnection($"Data Source={_dumpPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE series (
                id INTEGER PRIMARY KEY, state TEXT, rating REAL, content_rating TEXT, type TEXT,
                status TEXT, year INTEGER, title TEXT, titles TEXT, cover_raw_url TEXT, description TEXT,
                total_chapters TEXT, genres TEXT, authors TEXT, popularity_global_current INTEGER,
                -- The pre-sized thumbnail columns the hydrate query reads. Named here rather than
                -- listed in the INSERT, so adding a column to the dump doesn't mean editing every
                -- row literal in this file.
                cover_x250_x1 TEXT, cover_x250_x2 TEXT);
            """ + $"""
            INSERT INTO series (
                id, state, rating, content_rating, type, status, year, title, cover_raw_url,
                description, total_chapters, genres, authors, popularity_global_current)
            VALUES {string.Join(",", _rows)};
            """;
        cmd.ExecuteNonQuery();
    }

    private EmbeddingOptions Options() =>
        new(_dir, _vectorPath, _dir, EmbeddingModelProfile.Base with { Dimensions = Dim }) { Enabled = true };

    private EmbeddingStore Store()
    {
        var store = new EmbeddingStore(Options());
        store.EnsureSchema();
        return store;
    }

    /// <summary>
    /// A recommender over this fixture's dump and vector store. <paramref name="graphPath"/> defaults
    /// to a file that does not exist, which is the shipping default state: no artifact installed, so
    /// the co-recommendation channel contributes nothing and every pre-existing assertion here is
    /// still testing the behaviour it was written for.
    /// </summary>
    private SemanticRecommender Recommender(
        string? graphPath = null,
        RecoGraphTuning? graphTuning = null,
        string? coReadPath = null,
        CoReadTuning? coReadTuning = null,
        RecommenderTuning? tuning = null)
    {
        var dump = new MangaBakaDumpOptions(_dumpPath, _dir);
        var graph = new RecoGraphOptions(graphPath ?? Path.Combine(_dir, "absent-reco-edges.db"), _dir);
        var coRead = new CoReadOptions(coReadPath ?? Path.Combine(_dir, "absent-coread-edges.db"), _dir);
        return new SemanticRecommender(
            Options(),
            dump,
            new EmbeddingStore(Options()),
            new VectorIndexCache(Options(), dump, NullLogger<VectorIndexCache>.Instance),
            new RecoGraphCache(graph, NullLogger<RecoGraphCache>.Instance),
            graphTuning ?? RecoGraphTuning.Default,
            new CoReadCache(coRead, NullLogger<CoReadCache>.Instance),
            coReadTuning ?? CoReadTuning.Default,
            NullLogger<SemanticRecommender>.Instance,
            tuning);
    }

    /// <summary>
    /// Writes a <c>reco-edges.db</c> holding the given unordered pairs and returns its path. Same
    /// schema <c>distribution/fetch-reco-graph.cs</c> exports.
    /// </summary>
    private string WriteGraph(params (long A, long B, int Votes)[] pairs)
    {
        var path = Path.Combine(_dir, "reco-edges.db");
        using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE pair (
                a_id INTEGER NOT NULL, b_id INTEGER NOT NULL,
                anilist_votes INTEGER NOT NULL DEFAULT 0, mal_votes INTEGER NOT NULL DEFAULT 0,
                directions INTEGER NOT NULL DEFAULT 1, PRIMARY KEY (a_id, b_id)) WITHOUT ROWID;
            """ + $"""
            INSERT INTO pair (a_id, b_id, anilist_votes) VALUES
            {string.Join(",", pairs.Select(p => $"({p.A}, {p.B}, {p.Votes})"))};
            """;
        cmd.ExecuteNonQuery();
        return path;
    }

    private static float[] Axis(int i)
    {
        var v = new float[Dim];
        v[i] = 1f;
        return v;
    }

    /// <summary>A unit vector with equal weight on each named axis.</summary>
    private static float[] Spread(params int[] axes)
    {
        var v = new float[Dim];
        foreach (var a in axes)
        {
            v[a] = 1f;
        }

        EmbeddingMath.NormalizeInPlace(v);
        return v;
    }

    private static float[] Blend(float[] a, float[] b) => EmbeddingMath.Mean([a, b])!;

    private static float[] Scaled(float[] v, float by) => v.Select(x => x * by).ToArray();

    /// <summary>The vector, tilted slightly onto one other axis, so candidates aren't identical.</summary>
    private static float[] Nudge(float[] v, int axis, float amount)
    {
        var copy = (float[])v.Clone();
        copy[axis] += amount;
        EmbeddingMath.NormalizeInPlace(copy);
        return copy;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
