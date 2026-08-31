#:project ../src/Maki.Metadata/Maki.Metadata.csproj

// Scores the SHIPPED Discover search end to end — dense + FTS5 lexical + tag channel, fused exactly
// as SemanticSearcher fuses them — against the labelled query set in distribution/eval-queries.tsv.
// Run:
//   dotnet run distribution/eval-search.cs
//   dotnet run distribution/eval-search.cs -- baseline default
//   dotnet run distribution/eval-search.cs -- "pop:popularityweight=0.004"
//   dotnet run distribution/eval-search.cs -- --explain "childhood friends turned lovers"
//
// WHY THIS EXISTS, NEXT TO eval-embeddings.cs
// That tool scores an embedding MODEL: it holds its own vectors, cosines a query against them and
// reports MRR. It is the right instrument for "should we swap bge-base for arctic-m" and it is
// blind to everything this file measures, because it never runs the fusion. Every failure that
// prompted this tool lived in the fusion:
//
//   - The tag channel had an absolute similarity floor of 0.55 that its own cosines could not
//     reach (measured peak 0.42 against the shipped index), so the channel silently contributed
//     nothing on every query ever run. eval-embeddings.cs cannot see that; it has no tag channel.
//   - The tag channel's RRF damping was shared with the dense channel, which meant a series found
//     only by its tags could not enter a 60-result page even when the channel did fire.
//   - Nothing in the ranking preferred a series anyone has heard of, and 95% of the ~95.8k indexed
//     series sit outside global popularity rank 5,000.
//
// WHAT IT RUNS AGAINST
// The INSTALLED index and dump under MAKI_CONFIG_DIR (or %APPDATA%\Maki), not a cached eval pool.
// That is deliberate: the point is to measure what a user's install actually answers, including
// the FTS5 title index, which no synthetic pool has.
//
// WHAT THE NUMBERS MEAN
// Read the classes separately, as eval-queries.tsv's header explains: `premise` is the query shape
// Discover receives and the only one the fusion is really being asked about; `alias` and `title`
// are the lexical channel's job and are here to catch a change that wins on premise by wrecking
// name search. A label resolves to ANY row whose title matches, since a famous series has many
// rows in the pool.
//
// Per-query reciprocal ranks are written to .artifacts/eval/rr-<variant>-search.csv and one file
// per class, so distribution/eval-compare.py can put a bootstrap interval and a McNemar test on a
// change instead of the better/worse counts printed below. At n=90 on `premise` the counts are a
// summary, not a decision, and the interval usually spans zero for a change worth arguing about.
//
// `--engine title` scores the FALLBACK instead of the fusion — the plain FTS5 path DiscoverService
// drops to when the embedding index is missing, which is what a fresh install's "Smart" toggle
// actually answers with. It shares this query set so the cost of not having an index is a number.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Maki.Core.Configuration;
using Maki.Core.Metadata;
using Maki.Metadata.Embedding;
using Maki.Metadata.Catalogue;
using Maki.Metadata.MangaBaka;
using Microsoft.Extensions.Logging;

// A file-based app builds with the trimming-friendly defaults, under which System.Text.Json
// refuses reflection-based serialization. VectorIndexCache reads the dump's genre and author JSON
// arrays that way, so without this the index build throws before a single query runs. Must happen
// before anything touches a JsonSerializerOptions.
AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", true);

// Every number this tool prints is a measurement to be compared against another run, possibly on
// another machine. Without this a decimal comma makes two identical runs look different, and the
// numbers quoted in SearchTuning's doc comments unreadable next to a fresh table. Same reason
// eval-reco.cs pins it.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

var configDir = Environment.GetEnvironmentVariable("MAKI_CONFIG_DIR")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Maki");

var queriesPath = Environment.GetEnvironmentVariable("MAKI_EVAL_QUERIES")
    ?? Path.Combine(Directory.GetCurrentDirectory(), "distribution", "eval-queries.tsv");

var dumpPath = Path.Combine(configDir, "mangabaka.db");
var vectorPath = Path.Combine(configDir, "embeddings.db");

foreach (var (label, path) in new[] { ("dump", dumpPath), ("vector index", vectorPath), ("query set", queriesPath) })
{
    if (!File.Exists(path))
    {
        Console.WriteLine($"error: no {label} at {path}");
        return 2;
    }
}

var limit = 50;
var explain = (string?)null;
var filterTags = (string[]?)null;
var engine = "semantic";
var facetPassage = false;
var variantArgs = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--limit":
            limit = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--explain":
            explain = args[++i];
            break;
        // Only meaningful with --explain. A scored run takes its filters from the query set's
        // fourth column, so that a filtered query is scored against the label somebody chose the
        // filter for rather than against one filter applied to all 176.
        case "--tags":
            filterTags = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            break;
        // Which engine answers. `semantic` is the fusion; `title` is the FTS5 fallback
        // DiscoverService drops to with no index, scored over the identical query set so the cost
        // of not having one is measurable. Tuning variants still apply — the fuzzy and credit
        // knobs live in CatalogueOptions and are what the title path is made of.
        case "--engine":
            engine = args[++i].ToLowerInvariant();
            break;
        // Score an index built by `build-embeddings.cs base-facets`. Its rows carry a different
        // model version and would otherwise be dropped wholesale at load, leaving an empty index
        // and a table of zeroes rather than an error. Recommendation is the objective that passage
        // is for; this is here to price what it costs SEARCH, which is the objective the plain one
        // was chosen against.
        case "--facets":
            facetPassage = true;
            break;
        default:
            variantArgs.Add(args[i]);
            break;
    }
}

if (engine is not ("semantic" or "title"))
{
    Console.WriteLine($"error: --engine wants 'semantic' or 'title', not '{engine}'.");
    return 2;
}

var variants = variantArgs.Count > 0
    ? variantArgs.Select(Variants.Parse).ToList()
    : [Variants.Parse("baseline"), Variants.Parse("default")];

var modelKind = Settings.ReadEmbeddingModel(Path.Combine(configDir, "maki.db")) ?? "base";
var modelProfile = EmbeddingModelProfile.Resolve(modelKind);
if (facetPassage)
{
    modelProfile = modelProfile with
    {
        Version = modelProfile.Version + "-facets",
        PassageFacets = true,
    };
}

var options = new EmbeddingOptions(
    Path.Combine(configDir, "models"), vectorPath, Path.Combine(configDir, "cache"),
    modelProfile)
{
    Enabled = true,
};

var dumpOptions = new MangaBakaDumpOptions(dumpPath, Path.Combine(configDir, "cache"));

Console.WriteLine($"config   : {configDir}");
Console.WriteLine($"model    : {modelKind} ({options.Model.Version}, {options.Dimensions} dims)");
Console.WriteLine($"queries  : {queriesPath}");
Console.WriteLine($"limit    : top {limit} per query");
Console.WriteLine($"engine   : {engine}");
Console.WriteLine();

var embedder = new TextEmbedder(
    options,
    new EmbeddingModelStore(new Factory(), options, new ConsoleLogger<EmbeddingModelStore>()),
    new ConsoleLogger<TextEmbedder>());
if (!await embedder.EnsureReadyAsync())
{
    Console.WriteLine("error: the embedding model failed to load.");
    return 1;
}

var store = new EmbeddingStore(options);
// One cache for every variant: the index is identical across them (tuning only changes scoring),
// and rebuilding 95k x 768 per variant would dominate the run.
var cache = new VectorIndexCache(options, dumpOptions, new ConsoleLogger<VectorIndexCache>());
// Shared like the vector cache: tuning changes scoring, never the indexes themselves, and a rebuild
// per variant would dominate the run.
var catalogueCache = new CatalogueIndexCache(dumpOptions, new ConsoleLogger<CatalogueIndexCache>());

var warm = Stopwatch.StartNew();
if (await cache.GetAsync() is not { } index)
{
    Console.WriteLine("error: the vector index is empty — nothing embedded yet.");
    return 1;
}

Console.WriteLine($"index    : {index.Count} series, built in {warm.Elapsed.TotalSeconds:F1}s");
Console.WriteLine();

var items = EvalQueries.Load(queriesPath);
var filtered = items.Count(i => i.Filters is not null);
if (filtered > 0)
{
    Console.WriteLine($"filters  : {filtered} of {items.Length} queries carry their own filter set");
    Console.WriteLine();
}

if (explain is not null)
{
    foreach (var variant in variants)
    {
        await Explain(variant, explain);
    }

    return 0;
}

var results = new List<(Variant Variant, Dictionary<string, Metrics> ByClass, Metrics Overall, double[] Rr)>();
foreach (var variant in variants)
{
    results.Add(await Score(variant));
}

Report(results, items);
return 0;

async Task<(Variant, Dictionary<string, Metrics>, Metrics, double[])> Score(Variant variant)
{
    var searcher = Build(variant);
    var titleStore = BuildStore(variant);
    var reciprocal = new double[items.Length];
    var ranks = new int[items.Length];
    var clock = Stopwatch.StartNew();

    for (var i = 0; i < items.Length; i++)
    {
        var hits = engine == "title"
            // Mirrors DiscoverService's fallback: no filters reach that path (the store takes a
            // single content-rating ceiling, not a filter set), so a filtered query is scored here
            // against the unfiltered answer — which is exactly what the fallback would serve.
            ? (await titleStore.SearchWithCorrectionAsync(
                items[i].Query, ContentRating.Default, limit: limit)).Items.Select(ToRecommendation).ToList()
            : (await searcher.SearchAsync(items[i].Query, items[i].Filters, limit)).Items;
        ranks[i] = FirstMatch(hits, items[i].Expected);
        reciprocal[i] = ranks[i] > 0 ? 1.0 / ranks[i] : 0;

        if (i % 25 == 24 || i == items.Length - 1)
        {
            Console.Write($"\r  {variant.Name}: {i + 1}/{items.Length} queries, {clock.Elapsed.TotalSeconds:F0}s   ");
        }
    }

    Console.WriteLine();

    var byClass = items
        .Select((item, i) => (item.Class, Index: i))
        .GroupBy(x => x.Class)
        .ToDictionary(g => g.Key, g => Metrics.From(g.Select(x => ranks[x.Index]).ToArray()));

    WriteReciprocalRanks(variant.Name, items, reciprocal);
    return (variant, byClass, Metrics.From(ranks), reciprocal);
}

/// <summary>
/// Per-query reciprocal ranks, one file for everything and one per class, in the layout
/// eval-compare.py builds its paths from (<c>rr-{candidate}-{mode}.csv</c>) and headerless because
/// it int-parses the first column of every line. The query's line number in the .tsv is the id, so
/// two runs of the same set line up even if one of them scored a subset.
/// </summary>
static void WriteReciprocalRanks(string variant, EvalQueries.Item[] items, double[] reciprocal)
{
    var dir = Path.Combine(".artifacts", "eval");
    Directory.CreateDirectory(dir);

    Write("search", Enumerable.Range(0, items.Length));
    foreach (var cls in items.Select(i => i.Class).Distinct())
    {
        Write($"search-{cls}", Enumerable.Range(0, items.Length).Where(i => items[i].Class == cls));
    }

    void Write(string mode, IEnumerable<int> indexes)
    {
        var text = new StringBuilder();
        foreach (var i in indexes)
        {
            text.Append(items[i].Id).Append(',')
                .Append(reciprocal[i].ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        }

        File.WriteAllText(Path.Combine(dir, $"rr-{variant}-{mode}.csv"), text.ToString());
    }
}

/// <summary>Shapes a title-index hit like a fused one, the same way DiscoverService does.</summary>
static MangaBakaRecommendation ToRecommendation(MetadataSearchResult hit) =>
    new(hit.ProviderId, hit.Title, hit.CoverUrl, hit.Year, hit.Description,
        hit.Status, null, hit.TotalChapters, [], [], false, null, null);

async Task Explain(Variant variant, string query)
{
    var searcher = Build(variant);
    var filters = filterTags is null ? null : new RecommendationFilters(Tags: filterTags);
    var hits = engine == "title"
        ? (await BuildStore(variant).SearchWithCorrectionAsync(query, ContentRating.Default, limit: limit))
            .Items.Select(ToRecommendation).ToList()
        : (await searcher.SearchAsync(query, filters, limit)).Items;
    Console.WriteLine(
        $"=== {variant.Name}: \"{query}\"" +
        $"{(filterTags is null ? string.Empty : $" tags={string.Join("+", filterTags)}")} ({hits.Count} hits)");
    for (var i = 0; i < hits.Count; i++)
    {
        Console.WriteLine($"  {i + 1,3}. {hits[i].Title}");
    }

    Console.WriteLine();
}

// One store per variant, not one shared: the fuzzy pass runs inside MangaBakaLocalStore and reads
// its options from the constructor, so a shared store would silently ignore every fuzzy.* sweep.
MangaBakaLocalStore BuildStore(Variant variant) => new(
    dumpOptions,
    new NoSettings(),
    new ConsoleLogger<MangaBakaLocalStore>(),
    catalogueCache,
    variant.Tuning.Catalogue);

SemanticSearcher Build(Variant variant) => new(
    options,
    dumpOptions,
    store,
    cache,
    embedder,
    BuildStore(variant),
    variant.Tuning,
    catalogueCache,
    new ConsoleLogger<SemanticSearcher>());

/// <summary>1-based rank of the first result whose title matches the label, or 0 for a miss.</summary>
static int FirstMatch(IReadOnlyList<MangaBakaRecommendation> hits, string expected)
{
    for (var i = 0; i < hits.Count; i++)
    {
        if (TitleMatch.Matches(hits[i].Title, expected))
        {
            return i + 1;
        }
    }

    return 0;
}

static void Report(
    List<(Variant Variant, Dictionary<string, Metrics> ByClass, Metrics Overall, double[] Rr)> results,
    EvalQueries.Item[] items)
{
    var classes = items.Select(i => i.Class).Distinct().OrderBy(c => c, StringComparer.Ordinal).ToList();

    Console.WriteLine();
    Console.WriteLine($"{"variant",-22}{"class",-10}{"n",5}{"MRR",9}{"R@1",8}{"R@5",8}{"R@10",8}{"R@50",8}");
    Console.WriteLine(new string('-', 78));
    foreach (var (variant, byClass, overall, _) in results)
    {
        foreach (var cls in classes)
        {
            if (byClass.TryGetValue(cls, out var m))
            {
                Console.WriteLine($"{variant.Name,-22}{cls,-10}{m.Count,5}{m.Mrr,9:F3}{m.At1,8:F3}{m.At5,8:F3}{m.At10,8:F3}{m.At50,8:F3}");
            }
        }

        Console.WriteLine($"{variant.Name,-22}{"ALL",-10}{overall.Count,5}{overall.Mrr,9:F3}{overall.At1,8:F3}{overall.At5,8:F3}{overall.At10,8:F3}{overall.At50,8:F3}");
        Console.WriteLine();
    }

    if (results.Count < 2)
    {
        return;
    }

    // Paired against the first variant: every variant answers the identical queries over the
    // identical index, so most of the spread is query difficulty and cancels. Reported as a plain
    // win/loss count rather than a t-statistic — at n=90 on the class that matters, a difference
    // carried by three queries is not a result whatever the arithmetic says.
    var baseline = results[0];
    Console.WriteLine($"paired against '{baseline.Variant.Name}'");

    // Every class, not just premise. The whole risk of a change that adds a channel is winning on
    // the class it was written for while quietly wrecking another, and one hardcoded class cannot
    // show that.
    foreach (var cls in classes)
    {
        var indexes = items
            .Select((item, i) => (item.Class, Index: i))
            .Where(x => x.Class == cls)
            .Select(x => x.Index)
            .ToList();
        if (indexes.Count == 0)
        {
            continue;
        }

        foreach (var candidate in results.Skip(1))
        {
            var better = indexes.Count(i => candidate.Rr[i] > baseline.Rr[i]);
            var worse = indexes.Count(i => candidate.Rr[i] < baseline.Rr[i]);
            var delta = indexes.Average(i => candidate.Rr[i] - baseline.Rr[i]);
            Console.WriteLine(
                $"  {cls,-10}{candidate.Variant.Name,-22} better on {better,3}, worse on {worse,3}, " +
                $"unchanged on {indexes.Count - better - worse,3}   mean ΔRR {delta,+7:F3}");
        }
    }

    Console.WriteLine();
    Console.WriteLine("  Counts, not a test. At n=90 on `premise` a three-query difference reads as a result");
    Console.WriteLine("  here and is inside the noise. Per-query reciprocal ranks are written to");
    Console.WriteLine("  .artifacts/eval/rr-<variant>-search[-<class>].csv for the paired stats:");
    Console.WriteLine(
        $"    python distribution/eval-compare.py {results[1].Variant.Name} {baseline.Variant.Name} search-premise");
    Console.WriteLine();
}

/// <summary>MRR and recall@k over 1-based ranks, where 0 means the query missed entirely.</summary>
file readonly record struct Metrics(int Count, double Mrr, double At1, double At5, double At10, double At50)
{
    public static Metrics From(int[] ranks)
    {
        double Recall(int k) => ranks.Count(r => r > 0 && r <= k) / (double)ranks.Length;
        return new Metrics(
            ranks.Length,
            ranks.Average(r => r > 0 ? 1.0 / r : 0),
            Recall(1), Recall(5), Recall(10), Recall(50));
    }
}

file sealed record Variant(string Name, SearchTuning Tuning);

/// <summary>
/// Variant syntax: <c>name:key=value,key=value</c>, keys being <see cref="SearchTuning"/> property
/// names, case-insensitively. Two shorthands are built in: <c>baseline</c> reproduces the behaviour
/// this tool was written to measure (absolute tag floor of 0.55, tag channel sharing the dense
/// channel's damping, no popularity prior) and <c>default</c> is whatever ships today.
/// </summary>
file static class Variants
{
    public static Variant Parse(string spec)
    {
        var (name, overrides) = spec.Split(':', 2) is [var n, var rest] ? (n, rest) : (spec, string.Empty);

        var tuning = name.ToLowerInvariant() switch
        {
            "baseline" => SearchTuning.Default with
            {
                TagFloorAbsolute = 0.55,
                TagFloorRelative = 0,
                TagFloorMedianGap = 0,
                TagRrfK = 60,
                PopularityWeight = 0,
            },
            // What search did before credits and typo tolerance existed. Kept separate from
            // "baseline", whose meaning is already quoted by measurements recorded in
            // SearchTuning's doc comments and must not drift.
            "pre-catalogue" => SearchTuning.Default with
            {
                CreditChannelWeight = 0,
                Catalogue = SearchTuning.Default.Catalogue with
                {
                    Fuzzy = SearchTuning.Default.Catalogue.Fuzzy with { Enabled = false },
                },
            },
            _ => SearchTuning.Default,
        };

        foreach (var pair in overrides.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2)
            {
                throw new InvalidOperationException($"Malformed override '{pair}' (want key=value).");
            }

            tuning = Apply(tuning, parts[0].Trim(), parts[1].Trim());
        }

        return new Variant(name, tuning);
    }

    private static SearchTuning Apply(SearchTuning tuning, string key, string value)
    {
        double D() => double.Parse(value, CultureInfo.InvariantCulture);
        int I() => int.Parse(value, CultureInfo.InvariantCulture);
        bool B() => bool.Parse(value);

        return key.ToLowerInvariant() switch
        {
            "rrfk" => tuning with { RrfK = D() },
            "tagrrfk" => tuning with { TagRrfK = D() },
            "tagchannelweight" => tuning with { TagChannelWeight = D() },
            "tagfloorabsolute" => tuning with { TagFloorAbsolute = D() },
            "tagfloorrelative" => tuning with { TagFloorRelative = D() },
            "tagfloormediangap" => tuning with { TagFloorMedianGap = D() },
            "maxquerytags" => tuning with { MaxQueryTags = I() },
            "popularityweight" => tuning with { PopularityWeight = D() },
            "popularityfloorrank" => tuning with { PopularityFloorRank = I() },
            "poolmultiplier" => tuning with { PoolMultiplier = I() },
            "poolmin" => tuning with { PoolMin = I() },
            "poolmax" => tuning with { PoolMax = I() },
            "creditchannelweight" => tuning with { CreditChannelWeight = D() },
            "creditchannelrrfk" => tuning with { CreditChannelRrfK = D() },
            "creditchannelmaxworks" => tuning with { CreditChannelMaxWorks = I() },
            "creditchannelminrunchars" => tuning with { CreditChannelMinRunChars = I() },
            "creditchannelminruntokens" => tuning with { CreditChannelMinRunTokens = I() },
            "credits.resolvemaxdistance" =>
                tuning with { Catalogue = tuning.Catalogue with { CreditResolveMaxDistance = I() } },
            "credits.sqlidcap" =>
                tuning with { Catalogue = tuning.Catalogue with { CreditSqlIdCap = I() } },
            "fuzzy.enabled" =>
                tuning with { Catalogue = tuning.Catalogue with { Fuzzy = tuning.Catalogue.Fuzzy with { Enabled = B() } } },
            "fuzzy.minlengthfordistance1" =>
                tuning with { Catalogue = tuning.Catalogue with { Fuzzy = tuning.Catalogue.Fuzzy with { MinLengthForDistance1 = I() } } },
            "fuzzy.minlengthfordistance2" =>
                tuning with { Catalogue = tuning.Catalogue with { Fuzzy = tuning.Catalogue.Fuzzy with { MinLengthForDistance2 = I() } } },
            "fuzzy.maxexpansionspertoken" =>
                tuning with { Catalogue = tuning.Catalogue with { Fuzzy = tuning.Catalogue.Fuzzy with { MaxExpansionsPerToken = I() } } },
            "fuzzy.maxtokens" =>
                tuning with { Catalogue = tuning.Catalogue with { Fuzzy = tuning.Catalogue.Fuzzy with { MaxTokens = I() } } },
            "fuzzy.maxtermdocfrequency" =>
                tuning with { Catalogue = tuning.Catalogue with { Fuzzy = tuning.Catalogue.Fuzzy with { MaxTermDocFrequency = I() } } },
            "fuzzy.rescuebelow" =>
                tuning with { Catalogue = tuning.Catalogue with { Fuzzy = tuning.Catalogue.Fuzzy with { RescueBelow = I() } } },
            "fuzzy.mincorrectiondominance" =>
                tuning with { Catalogue = tuning.Catalogue with { Fuzzy = tuning.Catalogue.Fuzzy with { MinCorrectionDominance = I() } } },
            _ => throw new InvalidOperationException($"Unknown tuning key '{key}'."),
        };
    }
}

/// <summary>
/// The hand-written query set. Copied from eval-embeddings.cs rather than shared, because a
/// file-based app cannot reference another one; the format is three tab-separated fields and is
/// documented in the .tsv's own header.
/// </summary>
file static class EvalQueries
{
    /// <param name="Id">
    /// The query's line number in the .tsv, 1-based. It is what the reciprocal-rank CSVs are keyed
    /// by, so two runs line up even when one scored a subset — an index into the loaded array would
    /// shift the moment a query is added anywhere but the end.
    /// </param>
    /// <param name="Filters">
    /// The filter set this query is meant to be answered under, or null for the unfiltered majority.
    /// Filters are a separate code path (a row mask on the <c>FilterPlan</c>, applied per row before
    /// the top-K rather than to the result page), and nothing measured it until this column existed.
    /// </param>
    public sealed record Item(int Id, string Query, string Expected, string Class, RecommendationFilters? Filters);

    public static Item[] Load(string path)
    {
        var items = new List<Item>();
        var lineNumber = 0;
        foreach (var raw in File.ReadAllLines(path))
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = raw.Split('\t');
            if (parts.Length is not (3 or 4))
            {
                throw new InvalidOperationException(
                    $"Malformed line in {Path.GetFileName(path)} (want 3 or 4 tab-separated fields): {raw}");
            }

            var filters = parts.Length == 4 && parts[3].Trim().Length > 0
                ? ParseFilters(parts[3].Trim())
                : null;
            items.Add(new Item(lineNumber, parts[0].Trim(), parts[1].Trim(), parts[2].Trim(), filters));
        }

        return [.. items];
    }

    /// <summary>
    /// <c>key=value;key=value</c>, values comma-separated where the filter takes a list. Deliberately
    /// tiny and deliberately strict: an unknown key throws rather than being ignored, because a typo
    /// that silently drops a filter turns a filtered query into an unfiltered one and scores BETTER,
    /// which is the failure hardest to notice in a results table.
    /// </summary>
    private static RecommendationFilters ParseFilters(string spec)
    {
        var filters = RecommendationFilters.None;
        foreach (var clause in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = clause.Split('=', 2);
            if (parts.Length != 2)
            {
                throw new InvalidOperationException($"Malformed filter '{clause}' (want key=value).");
            }

            var key = parts[0].Trim().ToLowerInvariant();
            var value = parts[1].Trim();
            var list = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            filters = key switch
            {
                "genre" or "genres" => filters with { Genres = list },
                "tag" or "tags" => filters with { Tags = list },
                "type" or "types" => filters with { Types = list },
                "status" or "statuses" => filters with { Statuses = list },
                "rating" or "ratings" => filters with { ContentRatings = list },
                "yearmin" => filters with { YearMin = int.Parse(value, CultureInfo.InvariantCulture) },
                "yearmax" => filters with { YearMax = int.Parse(value, CultureInfo.InvariantCulture) },
                "minrating" => filters with { MinRating = double.Parse(value, CultureInfo.InvariantCulture) },
                "minchapters" => filters with { MinChapters = int.Parse(value, CultureInfo.InvariantCulture) },
                "maxchapters" => filters with { MaxChapters = int.Parse(value, CultureInfo.InvariantCulture) },
                _ => throw new InvalidOperationException($"Unknown filter key '{key}'."),
            };
        }

        return filters;
    }
}

/// <summary>
/// Label matching, identical to eval-embeddings.cs's: punctuation and case are dropped and runs of
/// a repeated character are collapsed, so "Haikyu!!" and "Haikyuu!!" are the same title. A subtitle
/// after a colon is also accepted, since MangaBaka splits long titles inconsistently.
/// </summary>
file static class TitleMatch
{
    public static bool Matches(string title, string expected)
    {
        var target = Normalize(expected);
        return Normalize(title) == target || Normalize(title.Split(':')[0]) == target;
    }

    private static string Normalize(string s)
    {
        var builder = new StringBuilder(s.Length);
        foreach (var c in s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant))
        {
            if (builder.Length == 0 || builder[^1] != c)
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}

/// <summary>Reads the one setting that decides which model's vectors are in the index.</summary>
file static class Settings
{
    public static string? ReadEmbeddingModel(string dbPath)
    {
        if (!File.Exists(dbPath))
        {
            return null;
        }

        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Value FROM AppConfig WHERE Key = 'recommendations.embeddingmodel'";
            return cmd.ExecuteScalar() as string;
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return null;
        }
    }
}

/// <summary>MangaBakaLocalStore only reads a setting to answer "is the local DB on"; here it is.</summary>
file sealed class NoSettings : IAppSettings
{
    public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);

    public Task SetAsync(string key, string? value, CancellationToken ct = default) => Task.CompletedTask;
}

file sealed class Factory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromMinutes(60) };
}

file sealed class ConsoleLogger<T> : ILogger<T>
{
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => new Scope();

    public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning;

    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
    {
        if (IsEnabled(level))
        {
            Console.WriteLine($"  [{level}] {formatter(state, ex)}");
        }
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
