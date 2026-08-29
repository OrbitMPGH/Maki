#:project ../src/Maki.Metadata/Maki.Metadata.csproj

// Measures whether recommendations are RIGHT, against held-out human labels — the claim
// `eval-reco.cs` cannot make and says so in its own header.
//
// Run:
//   dotnet run distribution/eval-reco-labels.cs -- single
//   dotnet run distribution/eval-reco-labels.cs -- small --per-request 3 nocrowd default rail
//   dotnet run distribution/eval-reco-labels.cs -- library --holdout 0.2 default "scored:seedweights=score"
//   dotnet run distribution/eval-reco-labels.cs -- single --labels coread --strata
//   dotnet run distribution/eval-reco-labels.cs -- single "cw0.5:coreadweight=0.5" --strata
//   dotnet run distribution/eval-reco-labels.cs -- library "old:genrerawsum=true" default
//
// Then, for an interval rather than a difference:
//   python distribution/eval-compare.py default old reco
//
// WHY THIS EXISTS, NEXT TO eval-reco.cs
// That tool has two modes and neither one can say "better". `spread` reports how CONCENTRATED a pool
// is (distinct genres/authors/tags, cohesion, median popularity) against synthetic profiles — an
// over-fit tripwire, no labels, no notion of a right answer. `loo` asks the right question against
// one real library and prints NOT A RESULT at the default limit, because n=1 and holding one series
// out of ~126k is a needle in a haystack. Every knob in RecoGraphTuning, CoReadTuning and
// TasteTuning was therefore tuned against "did the pool stay spread out", never "did the picks get
// more right".
//
// WHERE THE LABELS COME FROM
// They are already on disk, and they were being spent as INPUT to the ranker instead of held out:
//
//   reco-edges.db     134,818 "if you liked X, try Y" pairs submitted by AniList and MAL readers.
//   coread-edges.db   1.58M behavioural pairs over 19,667 finished reading lists.
//   coread-graph.db   the co-read fetcher's working state, holding 19,935 REAL reading lists.
//                     `library` mode holds a slice of one out and asks for it back.
//
// Only 19.8% of vote-graph pairs also appear in the co-read graph, so grading against one with that
// channel switched off is a genuinely held-out test rather than a graph reading its own answers.
// This tool enforces that: the graded channel is forced off whatever a variant asks for.
//
// WHAT THE NUMBERS DO NOT MEAN
//   * Absence of an edge is not evidence of irrelevance. These graphs are sparse and popularity
//     skewed, so recall here is a LOWER BOUND. Read differences between variants, never the
//     absolute value.
//   * Both label sets over-represent famous titles, so a variant that simply returns famous things
//     scores better. The `pop` column and `--strata` exist to make that visible; read them the same
//     way eval-reco.cs says to read its own `pop`.
//   * Never tune a channel against the graph that feeds it. `--labels reco` forces the vote channel
//     off, `--labels coread` forces the co-read channel off, and `library` mode forces co-read off
//     unconditionally because those lists ARE its training data.
//
// WHAT IT RUNS AGAINST
// The installed index, dump and graphs under MAKI_CONFIG_DIR (or %APPDATA%\Maki), same as
// eval-reco.cs and eval-search.cs. `library` mode additionally needs the co-read fetcher's working
// database, which lives in .artifacts/ and is NOT part of an install.
//
// GOTCHA
// A file-based app caches its build under %TEMP%\dotnet\runfile. Delete it when comparing an edited
// default against `default`, or you are scoring the previous build.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Maki.Metadata.CoRead;
using Maki.Metadata.RecoGraph;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

// VectorIndexCache reads the dump's genre and author JSON arrays reflectively; a file-based app
// otherwise builds with reflection-free System.Text.Json and the index build throws. Same reason
// eval-reco.cs sets this, and for the same reason it has to happen first.
AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", true);

// Every number here is compared against another run, possibly on another machine. Pin the culture
// so a decimal comma never makes two runs look different.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

var configDir = Environment.GetEnvironmentVariable("MAKI_CONFIG_DIR")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Maki");

var mode = "single";
var labelKind = "reco";
var requestCount = 500;
var seedsPerRequest = 3;
var limit = 40;
var minLabels = 5;
var holdout = 0.2;
var minLibrary = 20;
var maxLibrary = 300;
var workPath = Path.Combine(".artifacts", "coread-graph.db");
var rngSeed = 20260827;
var strata = false;
var csvMetric = "rr";
var variantArgs = new List<string>();

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "single" or "small" or "library":
            mode = args[i];
            break;
        case "--labels":
            labelKind = args[++i].ToLowerInvariant();
            break;
        case "--requests" or "--seeds":
            requestCount = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--per-request":
            seedsPerRequest = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--limit":
            limit = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--min-labels":
            minLabels = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--holdout":
            holdout = double.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--min-library":
            minLibrary = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--max-library":
            maxLibrary = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--work":
            workPath = args[++i];
            break;
        case "--rng":
            rngSeed = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--strata":
            strata = true;
            break;
        // Which per-request metric the .csv carries for eval-compare.py: rr (default), ndcg or r40.
        case "--csv":
            csvMetric = args[++i].ToLowerInvariant();
            break;
        default:
            variantArgs.Add(args[i]);
            break;
    }
}

if (labelKind is not ("reco" or "coread"))
{
    Console.WriteLine($"error: --labels wants 'reco' or 'coread', not '{labelKind}'.");
    return 2;
}

if (csvMetric is not ("rr" or "ndcg" or "r40"))
{
    Console.WriteLine($"error: --csv wants 'rr', 'ndcg' or 'r40', not '{csvMetric}'.");
    return 2;
}

if (mode == "single")
{
    seedsPerRequest = 1;
}

var dumpPath = Path.Combine(configDir, "mangabaka.db");
var vectorPath = Path.Combine(configDir, "embeddings.db");
var graphPath = Path.Combine(configDir, "reco-edges.db");
var coReadPath = Path.Combine(configDir, "coread-edges.db");

foreach (var (name, path) in new[] { ("dump", dumpPath), ("vector index", vectorPath) })
{
    if (!File.Exists(path))
    {
        Console.WriteLine($"error: no {name} at {path}");
        return 2;
    }
}

var labelPath = labelKind == "reco" ? graphPath : coReadPath;
if (mode != "library" && !File.Exists(labelPath))
{
    Console.WriteLine($"error: no {labelKind} graph at {labelPath} — that file IS the label set here.");
    return 2;
}

if (mode == "library" && !File.Exists(workPath))
{
    Console.WriteLine($"error: no co-read working database at {workPath}.");
    Console.WriteLine("  `library` mode reads real reading lists out of the fetcher's working state,");
    Console.WriteLine("  which lives in .artifacts/ and does not ship with an install. Pass --work.");
    return 2;
}

var variants = variantArgs.Count > 0
    ? variantArgs.Select(Variants.Parse).ToList()
    : [Variants.Parse("nocrowd"), Variants.Parse("default")];

var modelKind = Settings.ReadEmbeddingModel(Path.Combine(configDir, "maki.db")) ?? "base";
var options = new EmbeddingOptions(
    Path.Combine(configDir, "models"), vectorPath, Path.Combine(configDir, "cache"),
    EmbeddingModelProfile.Resolve(modelKind))
{
    Enabled = true,
};
var dumpOptions = new MangaBakaDumpOptions(dumpPath, Path.Combine(configDir, "cache"));

Console.WriteLine($"config   : {configDir}");
Console.WriteLine($"model    : {modelKind} ({options.Model.Version}, {options.Dimensions} dims)");
Console.WriteLine($"mode     : {mode}");
Console.WriteLine($"limit    : top {limit} per request");

var store = new EmbeddingStore(options);
// One cache across every variant: tuning changes scoring, never the index, and a rebuild per
// variant would dominate the run.
var cache = new VectorIndexCache(options, dumpOptions, new Quiet<VectorIndexCache>());
var graphCache = new RecoGraphCache(
    new RecoGraphOptions(graphPath, Path.Combine(configDir, "cache")), new Quiet<RecoGraphCache>());
var coReadCache = new CoReadCache(
    new CoReadOptions(coReadPath, Path.Combine(configDir, "cache")), new Quiet<CoReadCache>());

var warm = Stopwatch.StartNew();
if (await cache.GetAsync() is not { } index)
{
    Console.WriteLine("error: the vector index is empty — nothing embedded yet.");
    return 1;
}

Console.WriteLine($"index    : {index.Count} series, built in {warm.Elapsed.TotalSeconds:F1}s");

// A recommender per distinct tuning triple. Near-free to construct: the expensive state (the vector
// index, both graphs) lives in the caches and is shared across all of them.
var recommenders = new Dictionary<(RecoGraphTuning, CoReadTuning, RecommenderTuning), SemanticRecommender>();
SemanticRecommender RecommenderFor(
    RecoGraphTuning graphTuning, CoReadTuning coReadTuning, RecommenderTuning recoTuning)
{
    if (!recommenders.TryGetValue((graphTuning, coReadTuning, recoTuning), out var found))
    {
        found = new SemanticRecommender(
            options, dumpOptions, store, cache, graphCache, graphTuning, coReadCache, coReadTuning,
            new Quiet<SemanticRecommender>(), recoTuning);
        recommenders[(graphTuning, coReadTuning, recoTuning)] = found;
    }

    return found;
}

// The leakage rule, enforced here rather than trusted to the caller. A variant asking for the
// graded channel is told, not silently obeyed: a run where the graph reads its own answers looks
// like a spectacular result and is worth nothing.
var forbidGraph = mode == "library" ? false : labelKind == "reco";
var forbidCoRead = mode == "library" || labelKind == "coread";
foreach (var v in variants)
{
    if ((forbidGraph && v.CoGraph) || (forbidCoRead && v.CoRead))
    {
        var which = forbidGraph && v.CoGraph ? "vote" : "co-read";
        Console.WriteLine($"note     : forcing the {which} channel off for '{v.Name}' — it provides the labels.");
    }
}

var rng = new Random(rngSeed);
var requests = mode == "library"
    ? BuildLibraryRequests()
    : BuildPairRequests();

if (requests.Count == 0)
{
    Console.WriteLine("error: no scorable requests — nothing has enough labels.");
    return 1;
}

Console.WriteLine($"requests : {requests.Count}");
Console.WriteLine();

Directory.CreateDirectory(Path.Combine(".artifacts", "eval"));

var rows = new List<ResultRow>();
foreach (var variant in variants)
{
    rows.Add(await Score(variant));
}

Report();
return 0;

// -------------------------------------------------------------------------------------------------
// Request construction
// -------------------------------------------------------------------------------------------------

/// <summary>
/// One request per seed (or per <c>--per-request</c> seeds), graded against those seeds' neighbours
/// in the label graph. This is the shape the "More like this" rail and a seeded Discover receive.
/// </summary>
List<Request> BuildPairRequests()
{
    var labels = new Dictionary<long, List<(long Id, double Gain)>>();
    using (var conn = new SqliteConnection($"Data Source={labelPath};Mode=ReadOnly;Pooling=False"))
    {
        conn.Open();
        using var cmd = conn.CreateCommand();
        // The vote graph's two providers are on different scales and RecoGraphCache rescales them;
        // as a GAIN their sum is fine, because nDCG normalizes by the ideal ordering of these very
        // numbers and a monotone relabelling of one provider cannot reorder the other.
        cmd.CommandText = labelKind == "reco"
            ? "SELECT a_id, b_id, anilist_votes + mal_votes FROM pair"
            : "SELECT a_id, b_id, strength FROM pair";
        cmd.CommandTimeout = 600;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var a = reader.GetInt64(0);
            var b = reader.GetInt64(1);
            var gain = reader.GetDouble(2);
            if (gain <= 0)
            {
                continue;
            }

            // The artifact stores each pair once in a canonical order; both directions are labels.
            Add(a, b);
            Add(b, a);

            void Add(long from, long to)
            {
                // A candidate the index does not carry can never be returned, so counting it as a
                // miss measures index coverage rather than ranking. Dropped, not scored.
                if (!index.TryGetRow(to, out _))
                {
                    return;
                }

                if (!labels.TryGetValue(from, out var list))
                {
                    labels[from] = list = [];
                }

                list.Add((to, gain));
            }
        }
    }

    var eligible = labels
        .Where(kv => kv.Value.Count >= minLabels && index.TryGetRow(kv.Key, out _))
        .Select(kv => kv.Key)
        .Order()
        .ToList();
    Console.WriteLine($"labels   : {labelKind}, {eligible.Count} seeds with >= {minLabels} in-index neighbours");

    var shuffled = eligible.OrderBy(_ => rng.Next()).ToList();
    var built = new List<Request>();
    for (var i = 0; i + seedsPerRequest <= shuffled.Count && built.Count < requestCount; i += seedsPerRequest)
    {
        var seeds = shuffled.GetRange(i, seedsPerRequest);
        var positives = new Dictionary<long, double>();
        foreach (var seed in seeds)
        {
            foreach (var (id, gain) in labels[seed])
            {
                if (seeds.Contains(id))
                {
                    continue;
                }

                // A candidate several seeds vouch for keeps its strongest endorsement rather than a
                // sum: summing would make "one seed loves it" and "three seeds mildly like it"
                // indistinguishable, and only the second is evidence the fusion is working.
                positives[id] = Math.Max(positives.GetValueOrDefault(id), gain);
            }
        }

        if (positives.Count > 0)
        {
            built.Add(new Request(seeds, null, positives));
        }
    }

    return built;
}

/// <summary>
/// One request per real reading list: seed from most of it, ask for the rest back. The only mode
/// whose n is a population rather than one install, and the only one that measures the whole-library
/// Recommendations tab as a user experiences it.
/// </summary>
List<Request> BuildLibraryRequests()
{
    var mapClock = Stopwatch.StartNew();
    var crossRef = CrossReference(dumpPath);
    Console.WriteLine(
        $"cross-ref: {crossRef.Count} AniList ids map to MangaBaka series ({mapClock.Elapsed.TotalSeconds:F0}s)");

    var byUser = new Dictionary<int, List<(long Id, int Score)>>();
    using (var conn = new SqliteConnection($"Data Source={workPath};Mode=ReadOnly;Pooling=False"))
    {
        conn.Open();
        using var cmd = conn.CreateCommand();
        // COMPLETED only, exactly the status the co-read matrix is built from: a PLANNING entry is
        // an intention, and asking the recommender to predict one measures something else.
        cmd.CommandText = "SELECT user_id, media_id, score FROM user_entry WHERE status = 'COMPLETED'";
        cmd.CommandTimeout = 600;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!crossRef.TryGetValue(reader.GetInt64(1), out var mangaBakaId))
            {
                continue;
            }

            // Same rule as the pair modes: a series the index cannot return is not a fair label,
            // and as a seed it contributes no vector either.
            if (!index.TryGetRow(mangaBakaId, out _))
            {
                continue;
            }

            var user = (int)reader.GetInt64(0);
            if (!byUser.TryGetValue(user, out var list))
            {
                byUser[user] = list = [];
            }

            list.Add((mangaBakaId, reader.GetInt32(2)));
        }
    }

    var usable = byUser.Where(kv => kv.Value.Count >= minLibrary).ToList();
    Console.WriteLine(
        $"libraries: {usable.Count} of {byUser.Count} reading lists have >= {minLibrary} in-index completed titles");

    var built = new List<Request>();
    foreach (var (_, entries) in usable.OrderBy(_ => rng.Next()))
    {
        if (built.Count >= requestCount)
        {
            break;
        }

        // Deduplicated and shuffled before the split, so the held-out slice is a random sample of
        // the list rather than whatever order SQLite returned it in.
        var shuffled = entries
            .GroupBy(e => e.Id)
            .Select(g => (Id: g.Key, Score: g.Max(e => e.Score)))
            .OrderBy(_ => rng.Next())
            .ToList();

        var heldOut = Math.Max(1, (int)Math.Round(shuffled.Count * holdout));
        if (shuffled.Count - heldOut < 2)
        {
            continue;
        }

        var positives = shuffled.Take(heldOut).ToDictionary(e => e.Id, _ => 1.0);
        // Capped, because a 4,000-title list costs one dot product per catalogue row per seed query
        // and buys nothing a 300-title one does not: MaxSeedQueries stops at 8 either way.
        var seedEntries = shuffled.Skip(heldOut).Take(maxLibrary).ToList();
        var scores = seedEntries
            .Where(e => e.Score > 0)
            .ToDictionary(e => e.Id, e => e.Score / 50.0); // POINT_100, mirroring rating/5.0 locally

        built.Add(new Request(
            seedEntries.Select(e => e.Id).ToList(),
            scores.Count > 0 ? scores : null,
            positives));
    }

    return built;
}

// -------------------------------------------------------------------------------------------------
// Scoring
// -------------------------------------------------------------------------------------------------

async Task<ResultRow> Score(Variant variant)
{
    var coGraph = variant.CoGraph && !forbidGraph;
    var coRead = variant.CoRead && !forbidCoRead;
    var recommender = RecommenderFor(variant.Graph, variant.CoReadTuning, variant.Recommender);

    var reciprocal = new double[requests.Count];
    var r10 = new double[requests.Count];
    var r20 = new double[requests.Count];
    var r40 = new double[requests.Count];
    var ndcg = new double[requests.Count];
    var named = new double[requests.Count];
    var popularity = new List<double>();
    var hits = 0;
    var clock = Stopwatch.StartNew();

    for (var i = 0; i < requests.Count; i++)
    {
        var request = requests[i];
        var seedWeights = variant.ScoreWeights ? request.Scores : null;

        var picks = await recommender.GetSimilarAsync(
            request.Seeds,
            request.Seeds,
            limit,
            RecommendationFilters.None,
            obscurity: 0,
            seedWeights: seedWeights,
            diversity: variant.Diversity,
            weights: variant.Weights,
            coGraph: coGraph,
            coRead: coRead);

        var ids = picks.Select(p => long.Parse(p.ProviderId, CultureInfo.InvariantCulture)).ToList();
        // The thing `queryattribution` exists to move, and the one column here that is not a
        // relevance measure: what share of the picks can name a seed at all. A variant that raises
        // it while holding nDCG is buying an explanation for free; one that raises it by dropping
        // nDCG is buying a caption with relevance, which is not the trade.
        named[i] = picks.Count == 0
            ? 0
            : (double)picks.Count(p => p.BecauseOfTitle is not null) / picks.Count;
        r10[i] = RecallAt(ids, request.Positives, 10);
        r20[i] = RecallAt(ids, request.Positives, 20);
        r40[i] = RecallAt(ids, request.Positives, limit);
        ndcg[i] = Ndcg(ids, request.Positives, limit);

        var first = ids.FindIndex(request.Positives.ContainsKey);
        reciprocal[i] = first >= 0 ? 1.0 / (first + 1) : 0;
        if (first >= 0)
        {
            hits++;
        }

        if (MedianPopularity(ids) is { } median)
        {
            popularity.Add(median);
        }

        if (i % 20 == 0 || i == requests.Count - 1)
        {
            Console.Write($"\r  {variant.Name}: {i + 1}/{requests.Count}, {clock.Elapsed.TotalSeconds:F0}s   ");
        }
    }

    Console.Write("\r".PadRight(72) + "\r");

    // Named rr-<variant>-reco.csv, not rr-reco-<variant>.csv: eval-compare.py builds its paths as
    // rr-<candidate>-<mode>.csv, so this layout is what lets it read these files unmodified.
    // Headerless for the same reason — it int-parses the first column of every line.
    // Which per-request metric gets the interval. Reciprocal rank is the default because that is
    // what eval-compare.py was written for, but a change can move recall without moving the rank of
    // the FIRST hit — `maxseedqueries` does exactly that, recovering more of a held-out list while
    // leaving what it finds first alone — and testing only rr would call that no difference.
    var series = csvMetric switch
    {
        "ndcg" => ndcg,
        "r40" => r40,
        _ => reciprocal,
    };

    var csv = new StringBuilder();
    for (var i = 0; i < series.Length; i++)
    {
        csv.Append(i).Append(',').Append(series[i].ToString("R", CultureInfo.InvariantCulture)).Append('\n');
    }

    File.WriteAllText(Path.Combine(".artifacts", "eval", $"rr-{variant.Name}-reco.csv"), csv.ToString());

    return new ResultRow(
        variant.Name, r10, r20, r40, ndcg, reciprocal, named,
        (double)hits / requests.Count,
        popularity.Count == 0 ? double.NaN : Median(popularity),
        clock.Elapsed.TotalMilliseconds / Math.Max(1, requests.Count));
}

double? MedianPopularity(List<long> ids)
{
    var ranks = new List<double>(ids.Count);
    foreach (var id in ids)
    {
        if (index.TryGetRow(id, out var row))
        {
            var rank = index.PopularityAt(row);
            if (rank != VectorIndex.Unknown && rank > 0)
            {
                ranks.Add(rank);
            }
        }
    }

    return ranks.Count == 0 ? null : Median(ranks);
}

// -------------------------------------------------------------------------------------------------
// Reporting
// -------------------------------------------------------------------------------------------------

void Report()
{
    Console.WriteLine(
        $"{"variant",-24}{"R@10",8}{"R@20",8}{$"R@{limit}",8}{$"nDCG@{limit}",10}{"MRR",8}{"hit",8}" +
        $"{"named",8}{"pop",9}{"ms",8}");
    Console.WriteLine(new string('-', 97));
    foreach (var row in rows)
    {
        var pop = double.IsNaN(row.Popularity) ? "-" : row.Popularity.ToString("F0", CultureInfo.InvariantCulture);
        Console.WriteLine(
            $"{row.Name,-24}{row.R10.Average(),8:F3}{row.R20.Average(),8:F3}{row.R40.Average(),8:F3}" +
            $"{row.Ndcg.Average(),10:F3}{row.Rr.Average(),8:F3}{row.Hit,8:P0}" +
            $"{row.Named.Average(),8:P0}{pop,9}{row.MillisecondsPerRequest,8:F0}");
    }

    Console.WriteLine();
    Console.WriteLine($"  R@k     : share of this request's held-out labels recovered in the top k.");
    Console.WriteLine($"  nDCG    : gain-weighted, gain = {(mode == "library" ? "1 per held-out title" : "vote count / co-read strength")}.");
    Console.WriteLine("  MRR     : reciprocal rank of the FIRST label recovered; hit = requests recovering any.");
    Console.WriteLine("  pop     : median popularity rank of the picks; LOWER means more famous. A label set");
    Console.WriteLine("            skewed toward famous titles rewards a variant that simply returns them, and");
    Console.WriteLine("            no relevance column here can see that. Read the two together.");
    Console.WriteLine("  named   : share of picks carrying a BecauseOfTitle, i.e. ones the UI can label");
    Console.WriteLine("            \"Feels like X\" instead of leaving unattributed. Not a quality measure -");
    Console.WriteLine("            read it against nDCG, never on its own.");
    Console.WriteLine("  ms      : mean wall time for one GetSimilarAsync, so `maxseedqueries` has a price");
    Console.WriteLine("            next to its gain. Comparable within a run only.");
    Console.WriteLine();
    Console.WriteLine("  Recall is a LOWER BOUND: a missing edge is not evidence of irrelevance. Compare");
    Console.WriteLine("  variants against each other, never quote the absolute value as a quality score.");

    if (strata)
    {
        ReportStrata();
    }

    if (rows.Count > 1)
    {
        Console.WriteLine();
        Console.WriteLine(
            $"  per-request {csvMetric} written to .artifacts/eval/rr-<variant>-reco.csv" +
            $"{(csvMetric == "rr" ? " (--csv ndcg|r40 to test a different column)" : string.Empty)}");
        Console.WriteLine(
            $"  paired stats: python distribution/eval-compare.py {rows[1].Name} {rows[0].Name} reco");
    }
}

/// <summary>
/// The same metrics split by how famous the SEEDS are. A change that helps only where the crowd
/// graphs are dense is a different feature from one that helps everywhere, and the pooled mean
/// cannot tell them apart — the famous buckets carry most of the labels.
/// </summary>
void ReportStrata()
{
    var bounds = new[] { 1_000L, 5_000L, 20_000L, long.MaxValue };
    var names = new[] { "top 1k", "1k-5k", "5k-20k", "20k+" };

    var bucketOf = new int[requests.Count];
    for (var i = 0; i < requests.Count; i++)
    {
        var ranks = new List<double>();
        foreach (var seed in requests[i].Seeds)
        {
            if (index.TryGetRow(seed, out var row))
            {
                var rank = index.PopularityAt(row);
                if (rank != VectorIndex.Unknown && rank > 0)
                {
                    ranks.Add(rank);
                }
            }
        }

        var median = ranks.Count == 0 ? double.MaxValue : Median(ranks);
        bucketOf[i] = Array.FindIndex(bounds, b => median <= b) is var found and >= 0 ? found : bounds.Length - 1;
    }

    Console.WriteLine();
    Console.WriteLine($"{"seed popularity",-18}{"variant",-24}{"n",6}{$"R@{limit}",8}{$"nDCG@{limit}",10}{"MRR",8}");
    Console.WriteLine(new string('-', 74));
    for (var b = 0; b < bounds.Length; b++)
    {
        var members = Enumerable.Range(0, requests.Count).Where(i => bucketOf[i] == b).ToList();
        if (members.Count == 0)
        {
            continue;
        }

        foreach (var row in rows)
        {
            Console.WriteLine(
                $"{names[b],-18}{row.Name,-24}{members.Count,6}" +
                $"{members.Average(i => row.R40[i]),8:F3}{members.Average(i => row.Ndcg[i]),10:F3}" +
                $"{members.Average(i => row.Rr[i]),8:F3}");
        }
    }
}

// -------------------------------------------------------------------------------------------------
// Metrics
// -------------------------------------------------------------------------------------------------

/// <summary>
/// Recall capped by the window, not by the label count: a seed with 300 neighbours cannot have them
/// all inside a 40-result page, and dividing by 300 would score every variant near zero and hide the
/// difference between them.
/// </summary>
static double RecallAt(List<long> ids, Dictionary<long, double> positives, int k)
{
    var found = 0;
    for (var i = 0; i < Math.Min(k, ids.Count); i++)
    {
        if (positives.ContainsKey(ids[i]))
        {
            found++;
        }
    }

    return (double)found / Math.Min(k, positives.Count);
}

static double Ndcg(List<long> ids, Dictionary<long, double> positives, int k)
{
    var dcg = 0.0;
    for (var i = 0; i < Math.Min(k, ids.Count); i++)
    {
        if (positives.TryGetValue(ids[i], out var gain))
        {
            dcg += gain / Math.Log2(i + 2);
        }
    }

    var ideal = positives.Values
        .OrderByDescending(g => g)
        .Take(k)
        .Select((g, i) => g / Math.Log2(i + 2))
        .Sum();
    return ideal > 0 ? dcg / ideal : 0;
}

static double Median(List<double> values)
{
    var sorted = values.Order().ToList();
    return sorted[sorted.Count / 2];
}

/// <summary>AniList id to MangaBaka id, the same query and the same collision rule the fetchers use.</summary>
static Dictionary<long, long> CrossReference(string dumpPath)
{
    var map = new Dictionary<long, long>(150_000);
    using var conn = new SqliteConnection($"Data Source={dumpPath};Mode=ReadOnly;Pooling=False");
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText =
        """
        SELECT source_anilist_id, id
        FROM series
        WHERE state = 'active' AND type != 'novel' AND source_anilist_id IS NOT NULL
        ORDER BY COALESCE(popularity_global_current, 2147483647)
        """;
    cmd.CommandTimeout = 600;
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        // Ordered by popularity, so the first row to claim an AniList id wins — the same collision
        // rule fetch-coread-graph.cs uses, for the same reason.
        map.TryAdd(reader.GetInt64(0), reader.GetInt64(1));
    }

    return map;
}

/// <param name="Scores">
/// Seed weights derived from the reader's own AniList scores, or null when they scored nothing. Only
/// a variant carrying <c>seedweights=score</c> uses them, which is what makes the weighting itself
/// measurable rather than assumed.
/// </param>
file record Request(
    IReadOnlyList<long> Seeds,
    IReadOnlyDictionary<long, double>? Scores,
    Dictionary<long, double> Positives);

/// <param name="MillisecondsPerRequest">
/// Mean wall time for one <c>GetSimilarAsync</c>. Reported because <c>maxseedqueries</c> is a pure
/// CPU-cost knob - every extra query is another dot product per catalogue row - and a relevance
/// table with no price on it makes "more is better" look free.
/// </param>
file record ResultRow(
    string Name, double[] R10, double[] R20, double[] R40, double[] Ndcg, double[] Rr, double[] Named,
    double Hit, double Popularity, double MillisecondsPerRequest);

/// <summary>
/// Variant syntax: <c>name:key=value,key=value</c>.
///
/// <para>
/// Prefixes, tested in this order because the namespaces would otherwise overlap: <c>coread*</c> is
/// <see cref="CoReadTuning"/>, <c>graph*</c> is <see cref="RecoGraphTuning"/>, <c>w*</c> is
/// <see cref="EmbeddingMath.Weights"/> (so <c>wcoread</c> is the channel's coefficient in the hybrid
/// score and <c>coreadweight</c> is the scorer's own — they are different numbers and both matter).
/// <c>diversity</c> and <c>seedweights</c> stand alone, as do the <see cref="RecommenderTuning"/>
/// keys: <c>cosinefloor</c>, <c>crowdbypassesfloor</c>, <c>genrerawsum</c>, <c>maxseedqueries</c>
/// and <c>seedselection</c> (<c>farthest</c> / <c>weight</c> / <c>medoid</c> /
/// <c>weightedfarthest</c>). The last two only move anything in <c>library</c> mode: below
/// <c>maxseedqueries</c> seeds every seed is queried and the strategy cannot matter.
/// </para>
///
/// <para>
/// <c>queryattribution</c> (<c>rawcosine</c> / <c>standardized</c> / <c>standardizedlabelonly</c>)
/// is a partial exception to that: the centroid competes at every seed count above one, so it moves
/// <c>small</c> mode as well as <c>library</c>. It does <strong>not</strong> move <c>single</c>,
/// which is seed count one exactly - <c>BuildQueries</c> returns the centroid alone there, so there
/// is no seed query to attribute to and the two modes are identical by construction. Measured, to
/// save the next person the confusion: at one seed <c>rawcosine</c> and <c>standardized</c> agree on
/// every column over 800 requests and <c>named</c> is 0%; at three they diverge and <c>named</c> is
/// 29% against 13%. Sweep this in <c>small</c> or <c>library</c>, never in <c>single</c>.
/// </para>
///
/// <para>
/// Read <c>standardizedlabelonly</c> first - it is rank-identical to the baseline by construction,
/// so any metric that moves under it is the harness being noisy rather than the variant doing
/// something. Then read <c>standardized</c>. Sweeping <c>cosinefloor</c> underneath it confirms the
/// interaction, and shows the floor has no cost of its own: over 400 held-out libraries, raising it
/// to 0.65 under <c>rawcosine</c> is free (-0.0008 nDCG@40, 95% [-0.0042, +0.0025]) while the same
/// raise under <c>standardized</c> costs -0.0140 (95% [-0.0182, -0.0100]). Three-seed mode agrees.
/// So a floor swept under one attribution mode says nothing about the other, and the two must move
/// together.
/// </para>
///
/// <para>
/// All of which is moot at the shipped floor: 0.30 rejects nothing at all, in either attribution
/// mode and at every seed count, so <c>cosinefloor=-1</c> and <c>cosinefloor=0.30</c> come back
/// byte-identical. The floor only starts removing rows somewhere above 0.60.
/// </para>
///
/// <para>
/// <c>attributionscale</c> (<c>absolute</c> / <c>poolrelative</c>) decides what
/// <c>attributionmargin</c> means, and the two have completely different useful ranges - roughly
/// 0 to 3 raw units against roughly -1 to +2 standard deviations. A margin swept under one is
/// meaningless under the other. Note also that this harness cannot see the defect
/// <c>poolrelative</c> exists to fix: its held-out libraries are all 16 to 20 seeds, and the
/// absolute margin only falls apart once a library is several times that.
/// </para>
///
/// <para>
/// <c>attributionmargin</c> is the one to sweep against the <c>named</c> column, and the only knob
/// here whose target is a rate rather than a metric: pick the share of picks that should carry a
/// "feels like X" and find the margin that produces it, then check <c>nDCG</c> did not pay for it.
/// <c>wdistinct</c> is its ranking counterpart - it does not change who may be named, it changes how
/// many nameable rows reach the page. Sweep them in that order, because raising <c>wdistinct</c>
/// moves the <c>named</c> rate at a fixed margin and re-sweeping the margin afterwards is cheaper
/// than untangling the two.
/// </para>
///
/// <para>
/// Shorthand names: <c>default</c> is what ships; <c>nograph</c>, <c>nocoread</c> and <c>nocrowd</c>
/// switch one or both crowd channels off, which are the baselines those features have to be read
/// against; <c>rail</c> is the reduced-weight, slightly-diversified configuration
/// <c>SimilarSeriesService</c> uses for a single seed.
/// </para>
/// </summary>
file static class Variants
{
    public static Variant Parse(string spec)
    {
        var (name, overrides) = spec.Split(':', 2) is [var n, var rest] ? (n, rest) : (spec, string.Empty);
        var lower = name.ToLowerInvariant();

        var noCrowd = lower == "nocrowd";
        var coGraph = !noCrowd && lower != "nograph";
        var coRead = !noCrowd && lower != "nocoread";
        var graph = RecoGraphTuning.Default;
        var coReadTuning = CoReadTuning.Default;
        var recommender = RecommenderTuning.Default;
        var weights = (EmbeddingMath.Weights?)null;
        var diversity = 0.0;
        var scoreWeights = false;

        if (lower == "rail")
        {
            weights = new EmbeddingMath.Weights(Genre: 0.15, Author: 0.25);
            diversity = 0.15;
        }

        foreach (var pair in overrides.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2)
            {
                throw new InvalidOperationException($"Malformed override '{pair}' (want key=value).");
            }

            var key = parts[0].Trim().ToLowerInvariant();
            var value = parts[1].Trim();

            if (key == "diversity")
            {
                diversity = double.Parse(value, CultureInfo.InvariantCulture);
            }
            else if (key == "seedweights")
            {
                scoreWeights = value.Equals("score", StringComparison.OrdinalIgnoreCase);
            }
            else if (key == "cosinefloor")
            {
                recommender = recommender with { CosineFloor = double.Parse(value, CultureInfo.InvariantCulture) };
            }
            else if (key == "crowdbypassesfloor")
            {
                recommender = recommender with { CrowdBypassesCosineFloor = bool.Parse(value) };
            }
            else if (key == "genrerawsum")
            {
                recommender = recommender with { GenreChannelIsRawSum = bool.Parse(value) };
            }
            else if (key == "maxseedqueries")
            {
                recommender = recommender with
                {
                    MaxSeedQueries = int.Parse(value, CultureInfo.InvariantCulture),
                };
            }
            else if (key == "seedselection")
            {
                recommender = recommender with
                {
                    SeedSelection = Enum.Parse<SeedSelection>(value, ignoreCase: true),
                };
            }
            else if (key == "queryattribution")
            {
                recommender = recommender with
                {
                    QueryAttribution = Enum.Parse<QueryAttribution>(value, ignoreCase: true),
                };
            }
            else if (key == "attributionmargin")
            {
                recommender = recommender with
                {
                    AttributionMargin = double.Parse(value, CultureInfo.InvariantCulture),
                };
            }
            else if (key == "tagconsensus")
            {
                recommender = recommender with
                {
                    TagConsensusPower = double.Parse(value, CultureInfo.InvariantCulture),
                };
            }
            else if (key == "tagstoryboost")
            {
                recommender = recommender with
                {
                    TagStoryCategoryBoost = double.Parse(value, CultureInfo.InvariantCulture),
                };
            }
            else if (key == "tagnormpower")
            {
                recommender = recommender with
                {
                    TagCandidateNormPower = double.Parse(value, CultureInfo.InvariantCulture),
                };
            }
            else if (key == "tagsharpening")
            {
                recommender = recommender with
                {
                    TagProfileSharpening = double.Parse(value, CultureInfo.InvariantCulture),
                };
            }
            else if (key == "attributionscale")
            {
                recommender = recommender with
                {
                    AttributionScale = Enum.Parse<AttributionScale>(value, ignoreCase: true),
                };
            }
            else if (key.StartsWith("coread", StringComparison.Ordinal) && key.Length > 6)
            {
                coReadTuning = ApplyCoRead(coReadTuning, key[6..], value);
            }
            else if (key.StartsWith("graph", StringComparison.Ordinal) && key.Length > 5)
            {
                graph = ApplyGraph(graph, key[5..], value);
            }
            else if (key.StartsWith('w') && key.Length > 1)
            {
                weights = ApplyWeight(weights ?? new EmbeddingMath.Weights(), key[1..], value);
            }
            else
            {
                throw new InvalidOperationException($"Unknown key '{key}'.");
            }
        }

        return new Variant(
            name, weights, diversity, graph, coGraph, coReadTuning, coRead, recommender, scoreWeights);
    }

    private static EmbeddingMath.Weights ApplyWeight(EmbeddingMath.Weights w, string key, string value)
    {
        var d = double.Parse(value, CultureInfo.InvariantCulture);
        return key switch
        {
            "semantic" => w with { Semantic = d },
            "genre" => w with { Genre = d },
            "tag" => w with { Tag = d },
            "author" => w with { Author = d },
            "quality" => w with { Quality = d },
            "obscurity" => w with { Obscurity = d },
            // Set only when its graph returned something, so overriding it here is a way to see what
            // the channel would be worth if it always fired — not a way to switch it on.
            "graph" => w with { Graph = d },
            "coread" => w with { CoRead = d },
            "distinct" => w with { Distinct = d },
            _ => throw new InvalidOperationException($"Unknown hybrid weight 'w{key}'."),
        };
    }

    private static CoReadTuning ApplyCoRead(CoReadTuning coRead, string key, string value) => key switch
    {
        "weight" => coRead with { Weight = double.Parse(value, CultureInfo.InvariantCulture) },
        "minstrength" => coRead with { MinStrength = double.Parse(value, CultureInfo.InvariantCulture) },
        "maxinjected" => coRead with { MaxInjected = int.Parse(value, CultureInfo.InvariantCulture) },
        "mininjectedscore" => coRead with { MinInjectedScore = double.Parse(value, CultureInfo.InvariantCulture) },
        _ => throw new InvalidOperationException($"Unknown co-read tuning key 'coread{key}'."),
    };

    private static RecoGraphTuning ApplyGraph(RecoGraphTuning graph, string key, string value) => key switch
    {
        "weight" => graph with { Weight = double.Parse(value, CultureInfo.InvariantCulture) },
        "degreepenalty" => graph with { DegreePenalty = double.Parse(value, CultureInfo.InvariantCulture) },
        "degreesmoothing" => graph with { DegreeSmoothing = double.Parse(value, CultureInfo.InvariantCulture) },
        "minvotes" => graph with { MinVotes = int.Parse(value, CultureInfo.InvariantCulture) },
        "maxinjected" => graph with { MaxInjected = int.Parse(value, CultureInfo.InvariantCulture) },
        "mininjectedscore" => graph with { MinInjectedScore = double.Parse(value, CultureInfo.InvariantCulture) },
        _ => throw new InvalidOperationException($"Unknown graph tuning key 'graph{key}'."),
    };
}

file sealed record Variant(
    string Name,
    EmbeddingMath.Weights? Weights,
    double Diversity,
    RecoGraphTuning Graph,
    bool CoGraph,
    CoReadTuning CoReadTuning,
    bool CoRead,
    RecommenderTuning Recommender,
    bool ScoreWeights);

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
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Value FROM AppConfig WHERE Key = 'recommendations.embeddingmodel'";
            return cmd.ExecuteScalar() as string;
        }
        catch (SqliteException)
        {
            return null;
        }
    }
}

file sealed class Quiet<T> : ILogger<T>
{
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => new Scope();

    public bool IsEnabled(LogLevel level) => level >= LogLevel.Error;

    public void Log<TState>(
        LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
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
