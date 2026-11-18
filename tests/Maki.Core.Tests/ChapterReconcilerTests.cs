using System.Text.Json;
using Maki.Core.Sources;

namespace Maki.Core.Tests;

public class ChapterReconcilerTests
{
    private static ReconcileListing Listing(string mapping, params (decimal N, int? Pages, string? Title)[] entries) =>
        new(mapping, [.. entries.Select(e => new ReconcileEntry(e.N, e.Title, e.Pages))]);

    private static (decimal, int?, string?) C(decimal n, int? pages = null, string? title = null) => (n, pages, title);

    [Fact]
    public void PartsThatAddUpToAnotherSourcesWholeFold()
    {
        // Pajamas: Weeb Central's chapter 1 is MangaDex 1.1 + 1.2 (page hashes, round 1 of the research).
        var decisions = ChapterReconciler.Decide([
            Listing("mangadex", C(1.1m, 27), C(1.2m, 23), C(2.1m, 14), C(2.2m, 16)),
            Listing("weebcentral", C(1, 43), C(2, 48), C(3, 34)),
        ]);

        var one = decisions[1];
        Assert.Equal(FoldState.Merged, one.State);
        Assert.True(one.Fold);
        Assert.Equal(["weebcentral"], one.Wholes);
        Assert.Equal([1.1m, 1.2m], one.Parts["mangadex"]);
    }

    [Fact]
    public void AWholeTooSmallForThePartsStays()
    {
        var decisions = ChapterReconciler.Decide([
            Listing("a", C(4.1m, 20), C(4.2m, 20)),
            Listing("b", C(4, 18), C(5, 20)),
        ]);

        Assert.Equal(FoldState.Keep, decisions[4].State);
        Assert.False(decisions[4].Fold);
    }

    [Fact]
    public void ExtrasByTitleNeverFold()
    {
        var decisions = ChapterReconciler.Decide([
            Listing("mangadex", C(4, 20), C(4.1m, 3, "Twitter Extra"), C(5, 20)),
        ]);

        Assert.Equal(FoldState.Plain, decisions[4].State);
        Assert.False(decisions[4].Fold);
    }

    [Fact]
    public void RepeatsOfOneUploadAreNotParts()
    {
        // 12cm Promise on Atsumaru: 1.1 to 1.3, every one the same 29-page upload.
        var decisions = ChapterReconciler.Decide([
            Listing("atsumaru", C(1, 29), C(1.1m, 29), C(1.2m, 29), C(1.3m, 29), C(2, 30)),
        ]);

        Assert.False(decisions[1].Fold);
    }

    [Fact]
    public void ChapterZeroDecimalsArePrologueNumbering()
    {
        var decisions = ChapterReconciler.Decide([
            Listing("mangadex", C(0.01m, 20), C(0.02m, 20), C(1, 40)),
            Listing("topmanhua", C(0, 40), C(1, 40)),
        ]);

        Assert.False(decisions[0].Fold);
    }

    [Fact]
    public void DecimalsAfterTheLastChapterStayUnlessCertified()
    {
        var listings = new[]
        {
            Listing("a", C(199, 20), C(200, 40), C(200.1m, 20), C(200.2m, 20)),
        };

        Assert.Equal("lastChapter", ChapterReconciler.Decide(listings)[200].Reason);
        Assert.Equal(FoldState.Merged, ChapterReconciler.Decide(listings, publisherNumbers: [200.1m, 200.2m])[200].State);
    }

    [Fact]
    public void PartsNoSourceListsWholeAreAnAgreedSplit()
    {
        var decisions = ChapterReconciler.Decide([
            Listing("mangadex", C(6.1m, 15), C(6.2m, 16), C(7.1m, 15), C(7.2m, 17)),
            Listing("mangakakalot", C(6.1m), C(6.2m), C(7.1m), C(7.2m)),
        ]);

        Assert.Equal(FoldState.AgreedSplit, decisions[6].State);
        Assert.True(decisions[6].Fold);
        Assert.Equal(FoldState.AgreedSplit, decisions[7].State);
        Assert.False(decisions[7].Fold);
    }

    [Fact]
    public void AWholeWithoutPageCountsWaits()
    {
        var decisions = ChapterReconciler.Decide([
            Listing("mangadex", C(3.1m, 13), C(3.2m, 26), C(4, 50)),
            Listing("weebcentral", C(3), C(4)),
        ]);

        Assert.Equal(FoldState.Pending, decisions[3].State);
    }

    [Fact]
    public void AWholeBesideItsPointTwoWaitsWhenTheOtherWholeHasNoCount()
    {
        // Hidden Dungeon: MangaDex lists 8 and 8.2, Weeb Central 8. Only Weeb Central's page count can
        // tell whether MangaDex's 8 is part 1, so the floor waits for it instead of reading as kept.
        var listings = new[]
        {
            Listing("mangadex", C(8, 24), C(8.2m, 20), C(9, 30)),
            Listing("weebcentral", C(8), C(9)),
        };

        var waiting = ChapterReconciler.Decide(listings)[8];

        Assert.Equal(FoldState.Pending, waiting.State);
        Assert.Equal(["weebcentral"], waiting.Wholes);
        Assert.Equal([8m, 8.2m], waiting.Parts["mangadex"]);
        Assert.Equal([("weebcentral", 8m)], ChapterReconciler.MissingCounts(waiting, listings));

        var counted = ChapterReconciler.Decide([listings[0], Listing("weebcentral", C(8, 42), C(9, 30))])[8];
        Assert.Equal(FoldState.Merged, counted.State);
        Assert.Equal(["weebcentral"], counted.Wholes);
    }

    [Fact]
    public void MissingCountsNamesEveryUncountedCandidate()
    {
        var listings = new[]
        {
            Listing("mangadex", C(3.1m), C(3.2m, 26), C(4, 50)),
            Listing("weebcentral", C(3), C(4)),
        };

        var waiting = ChapterReconciler.Decide(listings)[3];

        Assert.Equal(FoldState.Pending, waiting.State);
        Assert.Equal([("weebcentral", 3m), ("mangadex", 3.1m)], ChapterReconciler.MissingCounts(waiting, listings));
        Assert.Empty(ChapterReconciler.MissingCounts(ChapterReconciler.Decide(listings)[4], listings));
    }

    private static (decimal, int?, string?)[] Plain(int from, int to, int? pages) =>
        [.. Enumerable.Range(from, to - from + 1).Select(n => C(n, pages))];

    [Fact]
    public void ASourceAtTwiceTheScaleIsComparedAfterDividingByIt()
    {
        // Too Many Losing Heroines: MangaKakalot has about twice the images per chapter of everyone else.
        var decisions = ChapterReconciler.Decide([
            Listing("parts", [.. Plain(1, 5, 20), C(6.1m, 10), C(6.2m, 11), C(7, 20)]),
            Listing("double", [.. Plain(1, 5, 40), C(6, 40), C(7, 40)]),
        ]);

        var six = decisions[6];
        Assert.Equal(FoldState.Merged, six.State);
        Assert.Equal(2, Assert.Single(six.Ratios).Scale);
    }

    [Fact]
    public void AScaleWaitsForThePlainChaptersToBeCounted()
    {
        var listings = new[]
        {
            Listing("parts", [.. Plain(1, 5, 20), C(6.1m, 10), C(6.2m, 11), C(7, 20)]),
            Listing("double", [.. Plain(1, 5, null), C(6, 40), C(7, 40)]),
        };

        var six = ChapterReconciler.Decide(listings)[6];

        Assert.Equal(FoldState.Pending, six.State);
        Assert.Equal([("double", 5m), ("double", 4m), ("double", 3m)], ChapterReconciler.MissingCounts(six, listings));
    }

    [Fact]
    public void SourcesWithoutASteadyScaleSayNothing()
    {
        var decisions = ChapterReconciler.Decide([
            Listing("parts", C(1, 20), C(2, 20), C(3, 20), C(4, 20), C(5, 20), C(6.1m, 10), C(6.2m, 11), C(7, 20)),
            Listing("other", C(1, 10), C(2, 45), C(3, 20), C(4, 60), C(5, 8), C(6, 21), C(7, 20)),
        ]);

        Assert.Equal(FoldState.Keep, decisions[6].State);
        Assert.Equal("pageScalesDisagree", decisions[6].Reason);
    }

    [Fact]
    public void AMergeMostOtherSourcesCallPartOneStays()
    {
        // Please Go Home, Miss Akutsu 49: TopManhua's own 49 is its 49.1 + 49.2, every other source's 49 is 49.1.
        var decisions = ChapterReconciler.Decide([
            Listing("topmanhua", C(49, 35), C(49.1m, 20), C(49.2m, 15), C(50, 30)),
            Listing("a", C(49, 20), C(50, 30)),
            Listing("b", C(49, 20), C(50, 30)),
        ]);

        Assert.Equal(FoldState.Keep, decisions[49].State);
        Assert.Equal("wholesDisagree", decisions[49].Reason);
    }

    [Fact]
    public void PublisherNumbersRoundTrip()
    {
        var stored = PublisherNumbering.Format([10.4m, 2.1m, 10.1m, 2.1m]);

        Assert.Equal("2.1;10.1;10.4", stored);
        Assert.Equal([2.1m, 10.1m, 10.4m], PublisherNumbering.Parse(stored));
        Assert.Null(PublisherNumbering.Format([]));
        Assert.Empty(PublisherNumbering.Parse(null));
    }

    // The labelled corpus from the 2026-11-17 reconciliation research: 18 real listings from 17
    // series, trimmed to the floors that decide anything, with reconcile.py's answer for each floor.
    private static readonly Lazy<JsonElement> Corpus = new(() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "chapter-reconciliation-corpus.json")))
            .RootElement.GetProperty("cases").Clone());

    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var c in Corpus.Value.EnumerateArray())
        {
            data.Add(c.GetProperty("name").GetString()!);
        }

        return data;
    }

    private static JsonElement Case(string name) =>
        Corpus.Value.EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);

    private static IReadOnlyDictionary<decimal, FloorDecision> DecideCase(JsonElement c) =>
        ChapterReconciler.Decide(
            [.. c.GetProperty("mappings").EnumerateArray().Select(m => new ReconcileListing(
                m.GetProperty("name").GetString()!,
                [.. m.GetProperty("entries").EnumerateArray().Select(e => new ReconcileEntry(
                    e.GetProperty("n").GetDecimal(),
                    e.GetProperty("title").ValueKind == JsonValueKind.String ? e.GetProperty("title").GetString() : null,
                    e.GetProperty("pages").ValueKind == JsonValueKind.Number ? e.GetProperty("pages").GetInt32() : null))]))],
            [.. c.GetProperty("publisher").EnumerateArray().Select(n => n.GetDecimal())]);

    private static FoldState StateOf(string python) => python switch
    {
        "plain" => FoldState.Plain,
        "keep" => FoldState.Keep,
        "agreed-split" => FoldState.AgreedSplit,
        "merged" => FoldState.Merged,
        "pending" => FoldState.Pending,
        _ => throw new ArgumentOutOfRangeException(nameof(python), python, null),
    };

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void MatchesThePrototypeOnEveryFloor(string name)
    {
        var c = Case(name);
        var decisions = DecideCase(c);
        var expected = c.GetProperty("expected");

        foreach (var floor in expected.EnumerateObject())
        {
            var want = floor.Value;
            var got = decisions[decimal.Parse(floor.Name, System.Globalization.CultureInfo.InvariantCulture)];
            var at = $"{name}, floor {floor.Name}";
            Assert.True(StateOf(want.GetProperty("state").GetString()!) == got.State, $"{at}: {got.State} {got.Reason}");
            Assert.True(want.GetProperty("fold").GetBoolean() == got.Fold, at);
            Assert.Equal(
                want.GetProperty("parts").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Select(n => n.GetDecimal()).ToList()),
                got.Parts.ToDictionary(p => p.Key, p => p.Value.ToList()));
            Assert.Equal(want.GetProperty("wholes").EnumerateArray().Select(w => w.GetString()!), got.Wholes);
            Assert.Equal(want.GetProperty("aliases").EnumerateObject().Select(a => a.Name).Order(), got.Aliases.Keys.Order());
            Assert.Equal(
                want.GetProperty("calibration").EnumerateArray().Select(n => (n[0].GetString()!, n[1].GetDecimal())),
                got.Calibration);
        }

        var decided = expected.EnumerateObject().Select(f => decimal.Parse(f.Name, System.Globalization.CultureInfo.InvariantCulture)).ToHashSet();
        Assert.All(decisions.Values.Where(d => !decided.Contains(d.Floor)), d => Assert.Equal(FoldState.Plain, d.State));
    }

    [Fact]
    public void NoWrongMergesOnTheLabelledCorpus()
    {
        var folded = 0;
        var waiting = 0;
        var problems = new List<string>();
        foreach (var c in Corpus.Value.EnumerateArray())
        {
            var name = c.GetProperty("name").GetString();
            var decisions = DecideCase(c);
            foreach (var label in c.GetProperty("labels").EnumerateObject())
            {
                var d = decisions.GetValueOrDefault(decimal.Parse(label.Name, System.Globalization.CultureInfo.InvariantCulture));
                var mergedFold = d is { Fold: true, State: FoldState.Merged };
                switch (label.Value.GetString())
                {
                    case "merge" when mergedFold:
                        folded++;
                        break;
                    case "merge" when d?.State == FoldState.Pending:
                        waiting++;
                        break;
                    case "merge":
                        problems.Add($"{name} {label.Name}: merge missed ({d?.State})");
                        break;
                    case "keep" when d?.Fold == true:
                        problems.Add($"{name} {label.Name}: wrong merge");
                        break;
                    case "split" when d?.State != FoldState.AgreedSplit:
                        problems.Add($"{name} {label.Name}: split not recognised ({d?.State})");
                        break;
                }
            }

            var glued = decisions.Values.SelectMany(d => d.Parts.Values).SelectMany(n => n).ToHashSet();
            foreach (var extra in c.GetProperty("extras").EnumerateArray().Select(n => n.GetDecimal()).Where(glued.Contains))
            {
                problems.Add($"{name}: extra {extra} glued into a fold");
            }
        }

        Assert.Empty(problems);
        Assert.Equal(40, folded);
        Assert.Equal(5, waiting);
    }

    private static List<ulong> Hashes(int from, int count) => [.. Enumerable.Range(from, count).Select(seed =>
    {
        var random = new Random(seed);
        ulong hash;
        do
        {
            hash = (ulong)random.NextInt64();
        }
        while (System.Numerics.BitOperations.PopCount(hash) is < 24 or > 40);

        return hash;
    })];

    private static PageEvidence? Evidence(List<ulong>? pages) => pages is null ? null : new PageEvidence(pages, pages);

    private static FloorDecision MergedByCounts(params ReconcileListing[] extra)
    {
        var decision = ChapterReconciler.Decide([
            Listing("mangadex", C(1.1m, 27), C(1.2m, 23), C(2, 40)),
            Listing("weebcentral", C(1, 43), C(2, 40)),
            .. extra,
        ])[1];
        Assert.Equal(FoldState.Merged, decision.State);
        return decision;
    }

    [Fact]
    public void AMergeWaitsForThePagesOfItsPartsAndOneWhole()
    {
        var d = MergedByCounts();

        ChapterReconciler.CheckPages(d, (_, _) => null);

        Assert.Equal(FoldState.Pending, d.State);
        Assert.Equal("needsPageCheck", d.Reason);
        Assert.Equal("pageCounts", d.Nominated);
        Assert.False(d.Fold);
        Assert.Equal([("mangadex", 1.1m), ("mangadex", 1.2m), ("weebcentral", 1m)], d.MissingPages);
        Assert.Empty(ChapterReconciler.MissingCounts(d, []));
    }

    [Fact]
    public void AMergeTheWholesPagesHoldFolds()
    {
        var whole = Hashes(0, 40);
        var d = MergedByCounts();

        ChapterReconciler.CheckPages(d, (m, n) => Evidence((m, n) switch
        {
            ("weebcentral", 1) => whole,
            ("mangadex", 1.1m) => whole[..22],
            ("mangadex", 1.2m) => whole[22..],
            _ => null,
        }));

        Assert.Equal(FoldState.Merged, d.State);
        Assert.Equal("pagesMatch", d.Reason);
        Assert.True(d.Fold);
        Assert.Equal([1.1m, 1.2m], d.Parts["mangadex"]);
        Assert.Equal(nameof(PageMatchVerdict.Match), Assert.Single(d.PageChecks).Verdict);
    }

    [Fact]
    public void PartsCutDifferentlyFoldWhenTheirStripMatches()
    {
        // TopManhua cuts every page in two: no page matches a whole page, the strip does.
        var strip = Hashes(0, 120);
        var d = MergedByCounts();

        ChapterReconciler.CheckPages(d, (m, n) => (m, n) switch
        {
            ("weebcentral", 1) => new PageEvidence(Hashes(0, 40), strip),
            ("mangadex", 1.1m) => new PageEvidence(Hashes(300, 22), strip[..60]),
            ("mangadex", 1.2m) => new PageEvidence(Hashes(400, 18), strip[60..]),
            _ => null,
        });

        Assert.Equal(FoldState.Merged, d.State);
        Assert.Equal(["pages", "strip"], d.PageChecks.Select(c => c.Method));
    }

    [Fact]
    public void PagesThatDifferWaitForTheirStrips()
    {
        var d = MergedByCounts();

        ChapterReconciler.CheckPages(d, (m, n) => (m, n) switch
        {
            ("weebcentral", 1) => new PageEvidence(Hashes(0, 40), null),
            ("mangadex", 1.1m) => new PageEvidence(Hashes(300, 22), null),
            ("mangadex", 1.2m) => new PageEvidence(Hashes(400, 18), Hashes(400, 30)),
            _ => null,
        });

        Assert.Equal(FoldState.Pending, d.State);
        Assert.Equal([("weebcentral", 1m), ("mangadex", 1.1m)], d.MissingPages);
    }

    [Fact]
    public void PartsThatAddUpButHoldOtherPagesStay()
    {
        // Dress-Up Darling 71: 71.1 was the volume's afterword; the page counts added up all the same.
        var d = MergedByCounts();

        ChapterReconciler.CheckPages(d, (m, n) => Evidence((m, n) switch
        {
            ("weebcentral", 1) => Hashes(0, 40),
            ("mangadex", 1.1m) => Hashes(500, 22),
            ("mangadex", 1.2m) => Hashes(22, 18),
            _ => null,
        }));

        Assert.Equal(FoldState.Keep, d.State);
        Assert.Equal("pagesDiffer", d.Reason);
        Assert.False(d.Fold);
        Assert.Empty(d.Parts);
        Assert.Equal([1.1m, 1.2m], d.Held["mangadex"]);
    }

    [Fact]
    public void OnlyThePartsAWholeHoldsFold()
    {
        // Miss Akutsu 88: another source's 88.1 and 88.2 were different content on the same floor.
        var whole = Hashes(0, 40);
        var d = MergedByCounts(Listing("mangapill", C(1.1m, 20), C(1.2m, 23), C(2, 40)));
        Assert.Equal(["mangadex", "mangapill"], d.Parts.Keys.Order());

        ChapterReconciler.CheckPages(d, (m, n) => Evidence((m, n) switch
        {
            ("weebcentral", 1) => whole,
            ("mangadex", 1.1m) => whole[..22],
            ("mangadex", 1.2m) => whole[22..],
            ("mangapill", _) => Hashes(900 + (int)(n * 10), 20),
            _ => null,
        }));

        Assert.Equal(FoldState.Merged, d.State);
        Assert.Equal(["mangadex"], d.Parts.Keys);
        Assert.Equal([1.1m, 1.2m], d.Held["mangapill"]);
    }

    [Fact]
    public void AFloorNoMergeNominatedIsLeftAlone()
    {
        var d = ChapterReconciler.Decide([
            Listing("a", C(4.1m, 20), C(4.2m, 20)),
            Listing("b", C(4, 18), C(5, 20)),
        ])[4];

        ChapterReconciler.CheckPages(d, (_, _) => null);

        Assert.Equal(FoldState.Keep, d.State);
        Assert.Null(d.Nominated);
        Assert.Empty(d.MissingPages);
    }
}
