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
//   mu-edges.db       361,007 MangaUpdates pairs over 86,172 series, built by build-mu-graph.cs.
//
// Only 19.8% of vote-graph pairs also appear in the co-read graph, so grading against one with that
// channel switched off is a genuinely held-out test rather than a graph reading its own answers.
// This tool enforces that: the graded channel is forced off whatever a variant asks for.
//
// THE MANGAUPDATES LABELS ARE THE INDEPENDENT ONES, AND THEY ARE TWO SETS NOT ONE
// The other three all come from AniList or MAL, which means a channel derived from AniList
// behaviour is partly reading its own answers however carefully the graded channel is switched off.
// MangaUpdates is a different site and a different population: 96.5% of its pairs appear in
// NEITHER shipped artifact. Nothing in the app reads mu-edges.db, so nothing has to be forced off.
//
//   --labels mu         331,736 pairs / 85,910 series. MangaUpdates' OWN derivation from category
//                       (tag) votes, so it partly encodes tags. Broad coverage, and the wrong
//                       primary grader for a tag-channel change - it will agree with itself.
//   --labels mu-human    29,692 pairs /  7,036 series. Human-submitted, same unit as the vote
//                       graph, 75.7% of it novel. Clean but narrow; read the interval, not the
//                       difference, because n is small.
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
using System.Text.Json;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Maki.Metadata.CoRead;
using Maki.Metadata.RecoGraph;
using Maki.Metadata.Taste;
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
string? muPathOverride = null;
var rngSeed = 20260827;
var strata = false;
var feel = false;
string? dumpFeatures = null;
var foldIndex = -1;
var foldCount = 0;
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
        case "--mu":
            muPathOverride = args[++i];
            break;
        case "--rng":
            rngSeed = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--strata":
            strata = true;
            break;
        case "--feel":
            feel = true;
            break;
        // Writes every pooled candidate's unblended channel values, with the label, for
        // distribution/fit-weights.cs. Only the first variant is dumped: the point is to fit
        // coefficients over one pool, not to compare two.
        case "--dump-features":
            dumpFeatures = args[++i];
            break;
        // Which slice of the reader population `library` mode is allowed to evaluate on, so a model
        // trained on the other slice can be graded without reading its own training data.
        case "--fold-users":
            var parts = args[++i].Split('/');
            if (parts.Length != 2
                || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out foldIndex)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out foldCount)
                || foldCount < 2 || foldIndex < 0 || foldIndex >= foldCount)
            {
                Console.WriteLine($"error: --fold-users wants k/n with n >= 2 and 0 <= k < n, not '{args[i]}'.");
                return 2;
            }

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

if (labelKind is not ("reco" or "coread" or "mu" or "mu-human"))
{
    Console.WriteLine($"error: --labels wants 'reco', 'coread', 'mu' or 'mu-human', not '{labelKind}'.");
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
// Built by distribution/build-mu-graph.cs out of the MangaBaka FULL dump. Unlike the other two it
// is not part of an install, so it defaults to .artifacts/ like the co-read working database does.
// Prefer an installed copy if one exists, so this keeps working once an MU channel ships.
var muInstalled = Path.Combine(configDir, "mu-edges.db");
var muPath = muPathOverride ?? (File.Exists(muInstalled) ? muInstalled : Path.Combine(".artifacts", "mu-edges.db"));
// Behavioural vectors. Declared beside the other artifacts because the fold guard below has to see
// it: this is the one artifact actually TRAINED on readers, so it is the one the guard exists for.
var tastePath = Path.Combine(configDir, "taste-vectors.db");

foreach (var (name, path) in new[] { ("dump", dumpPath), ("vector index", vectorPath) })
{
    if (!File.Exists(path))
    {
        Console.WriteLine($"error: no {name} at {path}");
        return 2;
    }
}

var labelPath = labelKind switch
{
    "reco" => graphPath,
    "coread" => coReadPath,
    _ => muPath,
};

if (mode != "library" && !File.Exists(labelPath))
{
    Console.WriteLine($"error: no {labelKind} graph at {labelPath} - that file IS the label set here.");
    if (labelKind.StartsWith("mu", StringComparison.Ordinal))
    {
        Console.WriteLine("  Build it first: dotnet run distribution/build-mu-graph.cs");
        Console.WriteLine("  It needs the MangaBaka FULL dump, which is not what an install downloads by default.");
    }

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
var cache = new VectorIndexCache(
    options, dumpOptions, new Quiet<VectorIndexCache>(),
    new TasteVectorOptions(tastePath, Path.Combine(configDir, "cache")));
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

if (foldCount > 0)
{
    if (mode != "library")
    {
        Console.WriteLine("error: --fold-users only means anything in `library` mode.");
        Console.WriteLine("  The pair modes are seeded from a label graph, not from readers, so there is no");
        Console.WriteLine("  user population to split. Use `library`.");
        return 2;
    }

    Console.WriteLine($"fold     : evaluating on user fold {foldIndex} of {foldCount}");

    // The leakage rule at the user level, enforced here rather than trusted to whoever built the
    // artifact. An artifact that recorded no training fold was built from everybody, which is the
    // normal state today and only an error once something claims otherwise.
    foreach (var (name, path) in new[]
             {
                 ("reco", graphPath), ("coread", coReadPath), ("mu", muPath), ("taste", tastePath),
             })
    {
        if (ReadTrainingFolds(path) is not { Count: > 0 } trained || !trained.Contains(foldIndex))
        {
            continue;
        }

        Console.WriteLine($"error: {name} artifact at {path} was trained on fold {foldIndex}.");
        Console.WriteLine("       Grading it against readers it learned from is not a held-out test.");
        Console.WriteLine($"       Rebuild it excluding fold {foldIndex}, or evaluate on a fold it did not see.");
        return 2;
    }
}

var feelIndex = feel ? FeelIndex.Build(dumpPath, index) : null;

// A recommender per distinct tuning triple. Near-free to construct: the expensive state (the vector
// index, both graphs) lives in the caches and is shared across all of them.
var recommenders =
    new Dictionary<(RecoGraphTuning, CoReadTuning, RecommenderTuning, TasteVectorTuning), SemanticRecommender>();
SemanticRecommender RecommenderFor(
    RecoGraphTuning graphTuning, CoReadTuning coReadTuning, RecommenderTuning recoTuning,
    TasteVectorTuning tasteTuning)
{
    if (!recommenders.TryGetValue((graphTuning, coReadTuning, recoTuning, tasteTuning), out var found))
    {
        found = new SemanticRecommender(
            options, dumpOptions, store, cache, graphCache, graphTuning, coReadCache, coReadTuning,
            new Quiet<SemanticRecommender>(), recoTuning, tasteTuning);
        recommenders[(graphTuning, coReadTuning, recoTuning, tasteTuning)] = found;
    }

    return found;
}

// The leakage rule, enforced here rather than trusted to the caller. A variant asking for the
// graded channel is told, not silently obeyed: a run where the graph reads its own answers looks
// like a spectacular result and is worth nothing.
// `mu` and `mu-human` forbid nothing: no shipped channel reads mu-edges.db, which is the entire
// point of them. THIS MUST CHANGE THE DAY AN MU CHANNEL SHIPS, or that channel will be graded
// against its own input and look spectacular.
var forbidGraph = mode == "library" ? false : labelKind == "reco";
var forbidCoRead = mode == "library" || labelKind == "coread";

// THE BEHAVIOURAL CHANNEL IS NOT THE CO-READ CHANNEL, BUT IT IS THE SAME DATA.
// coread-edges.db and taste-vectors.db are both folded out of coread-graph.db - one as a pair
// table, one as a factor matrix. Forcing the co-read CHANNEL off while grading against co-read
// labels therefore does not make the test held out if the taste channel is still on: it learned
// from the very rows the labels are counted from. Measured, the difference is not subtle - the
// taste channel reads as +0.102 nDCG against co-read labels and +0.022 against MangaUpdates ones.
//
// `library` mode is the exception, and only because it has a real mechanism: --fold-users plus an
// artifact built with --fold-out is a genuine reader-level split, which no flag can substitute for.
var forbidTaste = labelKind == "coread" || (mode == "library" && foldCount == 0);
foreach (var v in variants)
{
    if ((forbidGraph && v.CoGraph) || (forbidCoRead && v.CoRead))
    {
        var which = forbidGraph && v.CoGraph ? "vote" : "co-read";
        Console.WriteLine($"note     : forcing the {which} channel off for '{v.Name}' — it provides the labels.");
    }

    if (forbidTaste && v.Taste.Weight > 0)
    {
        Console.WriteLine(
            $"note     : forcing the behavioural channel off for '{v.Name}' — it is trained on the "
            + "reading lists these labels come from.");
    }
}

// The one label set that shares a population with the behavioural model without sharing a signal.
// Not forced off, because submitted "if you liked X, try Y" pairs are a curatorial act the trainer
// never sees, which is the same relationship the vote and co-read graphs already have with each
// other. Worth saying out loud all the same.
if (labelKind == "reco" && variants.Any(v => v.Taste.Weight > 0))
{
    Console.WriteLine(
        "note     : these labels come from the same AniList population the behavioural channel "
        + "trains on. Different signal, shared readers - read mu-human beside it.");
}

// What the per-request CSVs are keyed on. `library` mode grades against held-out slices of real
// reading lists, not against any label artifact, so its runs must not land in a label set's file.
var csvSuffix = mode == "library" ? "library" : labelKind;

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
        // MangaUpdates' two lists are different units and live in different columns: a category
        // weight sits near 18,000 and a human submitter count near 4, so summing them would make
        // the human list arithmetically invisible - the same mistake RecoGraphCache's per-provider
        // rescaling exists to avoid. Each is graded on its own.
        cmd.CommandText = labelKind switch
        {
            "reco" => "SELECT a_id, b_id, anilist_votes + mal_votes FROM pair",
            "mu" => "SELECT a_id, b_id, category_weight FROM pair WHERE category_weight > 0",
            "mu-human" => "SELECT a_id, b_id, human_weight FROM pair WHERE human_weight > 0",
            _ => "SELECT a_id, b_id, strength FROM pair",
        };
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
            if (foldCount > 0 && UserFold.Of(user, foldCount) != foldIndex)
            {
                continue;
            }

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
    var tasteTuning = forbidTaste ? variant.Taste with { Weight = 0 } : variant.Taste;
    var recommender = RecommenderFor(
        variant.Graph, variant.CoReadTuning, variant.Recommender, tasteTuning);

    // Only the first variant in a run is dumped. Two pools would interleave under one requestId and
    // the fit would pair candidates that never competed.
    var dumping = dumpFeatures is not null && ReferenceEquals(variant, variants[0])
        ? new StringBuilder("request,label,semantic,genre,tag,author,quality,graph,coread,taste,distinct,pop\n")
        : null;

    var reciprocal = new double[requests.Count];
    var r10 = new double[requests.Count];
    var r20 = new double[requests.Count];
    var r40 = new double[requests.Count];
    var ndcg = new double[requests.Count];
    var named = new double[requests.Count];
    var popularity = new List<double>();
    var feelRows = new List<FeelRow>();
    var hits = 0;
    var clock = Stopwatch.StartNew();

    for (var i = 0; i < requests.Count; i++)
    {
        var request = requests[i];
        var seedWeights = variant.ScoreWeights ? request.Scores : null;

        var features = dumping is null ? null : new List<EmbeddingMath.CandidateFeatures>();
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
            coRead: coRead,
            features: features);

        if (features is not null)
        {
            foreach (var f in features)
            {
                dumping!.Append(i).Append(',')
                    .Append(request.Positives.ContainsKey(f.Id) ? 1 : 0).Append(',')
                    .Append(Csv(f.Semantic)).Append(',').Append(Csv(f.Genre)).Append(',')
                    .Append(Csv(f.Tag)).Append(',').Append(Csv(f.Author)).Append(',')
                    .Append(Csv(f.Quality)).Append(',').Append(Csv(f.Graph)).Append(',')
                    .Append(Csv(f.CoRead)).Append(',').Append(Csv(f.Taste)).Append(',')
                    .Append(Csv(f.Distinct)).Append(',').Append(Csv(f.Percentile)).Append('\n');
            }
        }

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

        if (feelIndex is not null)
        {
            feelRows.Add(feelIndex.Measure(request.Seeds, ids));
        }

        if (i % 20 == 0 || i == requests.Count - 1)
        {
            Console.Write($"\r  {variant.Name}: {i + 1}/{requests.Count}, {clock.Elapsed.TotalSeconds:F0}s   ");
        }
    }

    Console.Write("\r".PadRight(72) + "\r");

    // Named rr-<variant>-<suffix>.csv, not rr-<suffix>-<variant>.csv: eval-compare.py builds its
    // paths as rr-<candidate>-<mode>.csv, so this layout is what lets it read these files
    // unmodified. Headerless for the same reason — it int-parses the first column of every line.
    // The suffix carries the label set, not the literal string "reco". It used to be "reco" for
    // every run, so grading the same variant against two label sets silently overwrote the first
    // file and the paired test compared a run against itself.
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

    File.WriteAllText(Path.Combine(".artifacts", "eval", $"rr-{variant.Name}-{csvSuffix}.csv"), csv.ToString());

    if (dumping is not null)
    {
        File.WriteAllText(dumpFeatures!, dumping.ToString());
        Console.WriteLine($"  features written to {dumpFeatures}");
    }

    return new ResultRow(
        variant.Name, r10, r20, r40, ndcg, reciprocal, named,
        (double)hits / requests.Count,
        popularity.Count == 0 ? double.NaN : Median(popularity),
        clock.Elapsed.TotalMilliseconds / Math.Max(1, requests.Count),
        feelRows.Count == 0 ? null : new FeelRow(
            MeanDefined(feelRows, f => f.Demographic),
            MeanDefined(feelRows, f => f.Format),
            MeanDefined(feelRows, f => f.Publisher),
            MeanDefined(feelRows, f => f.Era),
            MeanDefined(feelRows, f => f.TagTree),
            MeanDefined(feelRows, f => f.Franchise)));
}

/// <summary>
/// Averages only the requests where the column is defined. A request whose picks carry no
/// demographic at all is a coverage gap, and folding it in as a zero would report a ranking failure
/// where the dump simply says nothing.
/// </summary>
static double MeanDefined(List<FeelRow> rows, Func<FeelRow, double> select)
{
    var sum = 0.0;
    var n = 0;
    foreach (var row in rows)
    {
        var value = select(row);
        if (!double.IsNaN(value))
        {
            sum += value;
            n++;
        }
    }

    return n == 0 ? double.NaN : sum / n;
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
    var gainUnit = mode == "library"
        ? "1 per held-out title"
        : labelKind switch
        {
            "reco" => "vote count",
            "coread" => "co-read strength",
            "mu" => "MangaUpdates category weight",
            _ => "MangaUpdates submitter count",
        };
    Console.WriteLine($"  nDCG    : gain-weighted, gain = {gainUnit}.");
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

    if (feelIndex is not null)
    {
        ReportFeel();
    }

    if (strata)
    {
        ReportStrata();
    }

    if (rows.Count > 1)
    {
        Console.WriteLine();
        Console.WriteLine(
            $"  per-request {csvMetric} written to .artifacts/eval/rr-<variant>-{csvSuffix}.csv" +
            $"{(csvMetric == "rr" ? " (--csv ndcg|r40 to test a different column)" : string.Empty)}");
        Console.WriteLine(
            $"  paired stats: python distribution/eval-compare.py {rows[1].Name} {rows[0].Name} {csvSuffix}" +
            $"{(csvMetric == "rr" ? string.Empty : $" {csvMetric}")}");
    }
}

/// <summary>
/// Does a pick feel like the seed, as opposed to being a title some crowd paired with it. These
/// columns and the relevance table above can disagree, and when they do the disagreement is the
/// finding: a variant that lifts nDCG while dropping demographic and format agreement is buying
/// crowd-endorsed titles that read nothing like what the reader asked for.
/// </summary>
void ReportFeel()
{
    Console.WriteLine();
    Console.WriteLine($"{"variant",-24}{"demo",9}{"format",9}{"house",9}{"era",9}{"tree",9}{"franchise",11}");
    Console.WriteLine(new string('-', 80));
    foreach (var row in rows)
    {
        if (row.Feel is not { } f)
        {
            continue;
        }

        Console.WriteLine(
            $"{row.Name,-24}{Pct(f.Demographic),9}{Pct(f.Format),9}{Pct(f.Publisher),9}" +
            $"{Num(f.Era),9}{Num(f.TagTree),9}{Pct(f.Franchise),11}");
    }

    Console.WriteLine();
    Console.WriteLine("  demo    : share of picks whose Audience Demographics tag a seed also carries.");
    Console.WriteLine("  format  : same for Work Info > Publication Medium / Page Layout (longstrip, web,");
    Console.WriteLine("            4-koma, doujinshi). A webtoon returned for a tankoubon seed is a miss");
    Console.WriteLine("            no relevance metric here can see.");
    Console.WriteLine("  house   : share sharing an original-language publisher with a seed. The English");
    Console.WriteLine("            licensor is excluded: it is a fact about a market, not about the work.");
    Console.WriteLine("  era     : mean decades between a pick and the nearest seed. LOWER is closer.");
    Console.WriteLine("  tree    : mean taxonomy distance from a pick's tags to the nearest seed tag,");
    Console.WriteLine("            through name_path. LOWER is closer. The only column that sees \"nearly");
    Console.WriteLine("            the same kind of thing\" when no tag matches exactly.");
    Console.WriteLine("  franchise: share of picks in a seed's own relationships_v2 component. This one is");
    Console.WriteLine("            a DEFECT rate - another volume of what you are reading - so LOWER is");
    Console.WriteLine("            better, unlike every other column here.");
    Console.WriteLine();
    Console.WriteLine("  Agreement is not the goal on its own: a variant returning only the seed's own");
    Console.WriteLine("  demographic scores 100% and recommends nothing new. Read these against nDCG and");
    Console.WriteLine("  pop, the same way the diversity columns in eval-reco.cs are read.");

    static string Pct(double v) => double.IsNaN(v) ? "-" : v.ToString("P0", CultureInfo.InvariantCulture);
    static string Num(double v) => double.IsNaN(v) ? "-" : v.ToString("F2", CultureInfo.InvariantCulture);
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

/// <summary>Round-trippable and culture-pinned, so a fit reads what the eval wrote.</summary>
static string Csv(double value) => value.ToString("R", CultureInfo.InvariantCulture);

static double Median(List<double> values)
{
    var sorted = values.Order().ToList();
    return sorted[sorted.Count / 2];
}

/// <summary>
/// Which folds an artifact was trained on, from its <c>meta.trainingFold</c> ("0,2,3" or "all").
/// Absent or "all" means it saw every reader, which is what every shipped artifact says today.
/// </summary>
static HashSet<int>? ReadTrainingFolds(string path)
{
    if (!File.Exists(path))
    {
        return null;
    }

    try
    {
        using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = 'trainingFold'";
        if (cmd.ExecuteScalar()?.ToString() is not { Length: > 0 } value
            || value.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var f) ? f : -1)
            .Where(f => f >= 0)
            .ToHashSet();
    }
    catch (SqliteException)
    {
        // No meta table at all is an older artifact, not a fold claim.
        return null;
    }
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

/// <summary>
/// Which evaluation fold a reader belongs to. Deterministic across processes and machines, which
/// <c>HashCode.Combine</c> is not: .NET randomizes its seed per process, so using it here would put
/// the same reader in a different fold on every run and quietly destroy the held-out guarantee.
///
/// <para>
/// ANY TOOL THAT BUILDS A FOLD-LIMITED ARTIFACT MUST USE THIS EXACT FUNCTION. A builder that
/// partitions readers differently from the grader produces an artifact that trained on part of the
/// evaluation set while honestly reporting that it did not.
/// </para>
/// </summary>
file static class UserFold
{
    public static int Of(long userId, int folds)
    {
        // FNV-1a over the eight bytes of the id. Fixed constants, no framework randomization, and
        // well spread in the low bits, which is the half the modulo reads. AniList ids are dense
        // and sequential, so taking the id modulo the fold count directly would correlate a fold
        // with signup date.
        var hash = 2166136261u;
        var value = (ulong)userId;
        for (var i = 0; i < 8; i++)
        {
            hash = (hash ^ (byte)(value >> (i * 8))) * 16777619u;
        }

        return (int)(hash % (uint)folds);
    }
}

/// <param name="Demographic">
/// Share of picks carrying a demographic that any seed also carries. Undefined (NaN) when no pick
/// has one at all, which is a coverage fact rather than a score of zero.
/// </param>
/// <param name="TagTree">
/// Mean taxonomy distance from a pick's tags to the seeds' tags: for every tag the pick carries,
/// the shortest path through the <c>name_path</c> tree to the nearest seed tag, averaged. LOWER is
/// closer. This is the one column here that sees "nearly the same kind of thing" when nothing
/// matches exactly, which is exactly what an exact-id tag cosine cannot.
/// </param>
/// <param name="Franchise">
/// Share of picks sitting in the same <c>relationships_v2</c> component as a seed. This one is a
/// DEFECT rate, not an agreement rate: recommending volume two of what you are reading is the
/// failure mode, so lower is better and it is the only column here read in that direction.
/// </param>
file readonly record struct FeelRow(
    double Demographic, double Format, double Publisher, double Era, double TagTree, double Franchise);

/// <summary>
/// The "does it FEEL like the seed" side of the measurement, which no relevance metric can see.
/// nDCG asks whether a crowd paired these two titles; these columns ask whether the pick is the same
/// KIND of thing - same demographic, same format, same house, same era, near in the tag taxonomy,
/// and not simply another volume of the seed.
///
/// <para>
/// Built from the dump rather than the vector index because none of it is indexed: there is no
/// demographic column, no format column and no magazine column in the schema at all. Demographic
/// lives only under the <c>Audience Demographics</c> tag root and format only under
/// <c>Work Info</c>, which is why this reads <c>tags_v2</c> directly.
/// </para>
///
/// <para>
/// Opt-in behind <c>--feel</c> because it costs a full dump scan, and a sweep runs the harness
/// dozens of times.
/// </para>
/// </summary>
file sealed class FeelIndex
{
    private const string DemographicRoot = "Audience Demographics";
    private const string MediumPrefix = "Work Info > Publication Medium";
    private const string LayoutPrefix = "Work Info > Page Layout";

    private readonly VectorIndex _index;
    private readonly Dictionary<int, int[]> _tagPath;
    private readonly HashSet<int> _demographicTags;
    private readonly HashSet<int> _formatTags;
    private readonly Dictionary<long, int[]> _publishers;
    private readonly Dictionary<long, int> _decade;


    private FeelIndex(
        VectorIndex index, Dictionary<int, int[]> tagPath, HashSet<int> demographicTags,
        HashSet<int> formatTags, Dictionary<long, int[]> publishers, Dictionary<long, int> decade)
    {
        _index = index;
        _tagPath = tagPath;
        _demographicTags = demographicTags;
        _formatTags = formatTags;
        _publishers = publishers;
        _decade = decade;
    }

    public static FeelIndex Build(string dumpPath, VectorIndex index)
    {
        var clock = Stopwatch.StartNew();
        var segments = new Dictionary<string, int>(StringComparer.Ordinal);
        var tagPath = new Dictionary<int, int[]>(2600);
        var demographicTags = new HashSet<int>();
        var formatTags = new HashSet<int>();
        var publisherIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var publishers = new Dictionary<long, int[]>(index.Count);
        var decade = new Dictionary<long, int>(index.Count);

        using var conn = new SqliteConnection($"Data Source={dumpPath};Mode=ReadOnly;Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, tags_v2, publishers, published_start_date, published_start_date_is_estimated,
                   year
            FROM series
            WHERE state = 'active' AND rating IS NOT NULL AND type != 'novel'
            """;
        cmd.CommandTimeout = 900;
        using var reader = cmd.ExecuteReader();

        var rows = 0;
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            rows++;
            if (rows % 20_000 == 0)
            {
                Console.Write($"\r  feel index: {rows:N0} rows   ");
            }

            // The taxonomy is global, so it is collected from every row even when the series itself
            // is not in the index: a rare tag might only ever appear on unindexed titles, and a
            // missing path silently reads as distance zero.
            if (!reader.IsDBNull(1))
            {
                CollectTags(reader.GetString(1), segments, tagPath, demographicTags, formatTags);
            }

            if (!index.TryGetRow(id, out _))
            {
                continue;
            }

            if (!reader.IsDBNull(2) && CollectPublishers(reader.GetString(2), publisherIds) is { Length: > 0 } houses)
            {
                publishers[id] = houses;
            }

            // A confirmed start date wins; 54% of them are flagged estimated, and for those `year`
            // is the same guess with less precision, so it is used rather than dropping the row.
            // Decade granularity absorbs most of the error either way.
            var estimated = !reader.IsDBNull(4) && reader.GetInt64(4) != 0;
            int? startYear = !estimated && !reader.IsDBNull(3)
                && int.TryParse(reader.GetString(3).AsSpan(0, Math.Min(4, reader.GetString(3).Length)),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : reader.IsDBNull(5) ? null : (int)reader.GetInt64(5);

            // 1021 and 2054 both appear in this column. A decade outside the medium's existence is
            // dirt, and averaging it in would move the median by decades.
            if (startYear is >= 1900 and <= 2030)
            {
                decade[id] = startYear.Value / 10;
            }
        }

        Console.Write("\r".PadRight(48) + "\r");

        Console.WriteLine(
            $"feel     : {tagPath.Count} tags ({demographicTags.Count} demographic, {formatTags.Count} format), " +
            $"{publishers.Count:N0} with a house, {decade.Count:N0} dated ({clock.Elapsed.TotalSeconds:F0}s)");

        return new FeelIndex(index, tagPath, demographicTags, formatTags, publishers, decade);
    }

    public FeelRow Measure(IReadOnlyList<long> seeds, IReadOnlyList<long> picks)
    {
        if (picks.Count == 0)
        {
            return new FeelRow(double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
        }

        var seedDemographic = new HashSet<int>();
        var seedFormat = new HashSet<int>();
        var seedHouses = new HashSet<int>();
        var seedDecades = new List<int>();
        var seedTags = new List<int>();
        var seedFranchises = new HashSet<int>();

        foreach (var seed in seeds)
        {
            foreach (var tag in TagsOf(seed))
            {
                if (_demographicTags.Contains(tag))
                {
                    seedDemographic.Add(tag);
                }

                if (_formatTags.Contains(tag))
                {
                    seedFormat.Add(tag);
                }

                seedTags.Add(tag);
            }

            if (_publishers.TryGetValue(seed, out var houses))
            {
                seedHouses.UnionWith(houses);
            }

            if (_decade.TryGetValue(seed, out var d))
            {
                seedDecades.Add(d);
            }

            // FranchiseAt, not a second union-find here: the column the ranker collapses on IS the
            // definition this metric has to measure, or a fix could move one and not the other.
            if (_index.TryGetRow(seed, out var seedRow)
                && _index.FranchiseAt(seedRow) != VectorIndex.Unknown)
            {
                seedFranchises.Add(_index.FranchiseAt(seedRow));
            }
        }

        var demoHit = 0;
        var demoSeen = 0;
        var fmtHit = 0;
        var fmtSeen = 0;
        var pubHit = 0;
        var pubSeen = 0;
        var eraGaps = new List<double>();
        var treeSum = 0.0;
        var treeSeen = 0;
        var franchiseHit = 0;

        foreach (var pick in picks)
        {
            var pickTags = TagsOf(pick);
            var hasDemographic = false;
            var demoMatch = false;
            var hasFormat = false;
            var formatMatch = false;
            var distanceSum = 0.0;
            var distanceCount = 0;

            foreach (var tag in pickTags)
            {
                if (_demographicTags.Contains(tag))
                {
                    hasDemographic = true;
                    demoMatch |= seedDemographic.Contains(tag);
                }

                if (_formatTags.Contains(tag))
                {
                    hasFormat = true;
                    formatMatch |= seedFormat.Contains(tag);
                }

                if (seedTags.Count > 0 && _tagPath.TryGetValue(tag, out var path))
                {
                    var nearest = int.MaxValue;
                    foreach (var seedTag in seedTags)
                    {
                        if (_tagPath.TryGetValue(seedTag, out var seedPath))
                        {
                            nearest = Math.Min(nearest, Distance(path, seedPath));
                        }
                    }

                    if (nearest != int.MaxValue)
                    {
                        distanceSum += nearest;
                        distanceCount++;
                    }
                }
            }

            if (hasDemographic)
            {
                demoSeen++;
                if (demoMatch)
                {
                    demoHit++;
                }
            }

            if (hasFormat)
            {
                fmtSeen++;
                if (formatMatch)
                {
                    fmtHit++;
                }
            }

            if (_publishers.TryGetValue(pick, out var pickHouses) && seedHouses.Count > 0)
            {
                pubSeen++;
                if (pickHouses.Any(seedHouses.Contains))
                {
                    pubHit++;
                }
            }

            if (_decade.TryGetValue(pick, out var pickDecade) && seedDecades.Count > 0)
            {
                eraGaps.Add(seedDecades.Min(d => Math.Abs(d - pickDecade)));
            }

            if (distanceCount > 0)
            {
                treeSum += distanceSum / distanceCount;
                treeSeen++;
            }

            if (_index.TryGetRow(pick, out var pickRow)
                && _index.FranchiseAt(pickRow) is var pickFranchise and not VectorIndex.Unknown
                && seedFranchises.Contains(pickFranchise))
            {
                franchiseHit++;
            }
        }

        return new FeelRow(
            demoSeen == 0 ? double.NaN : (double)demoHit / demoSeen,
            fmtSeen == 0 ? double.NaN : (double)fmtHit / fmtSeen,
            pubSeen == 0 ? double.NaN : (double)pubHit / pubSeen,
            eraGaps.Count == 0 ? double.NaN : eraGaps.Average(),
            treeSeen == 0 ? double.NaN : treeSum / treeSeen,
            (double)franchiseHit / picks.Count);
    }

    private List<int> TagsOf(long id)
    {
        var tags = new List<int>();
        if (_index.TryGetRow(id, out var row))
        {
            foreach (var (tagId, _) in TagMath.Unpack(_index.TagsAt(row)))
            {
                tags.Add(tagId);
            }
        }

        return tags;
    }

    /// <summary>
    /// Shortest path through the taxonomy: down from each node to their lowest common ancestor and
    /// back up. Two siblings are 2 apart, a parent and child 1, the same node 0, and two tags under
    /// different roots are the sum of their depths.
    /// </summary>
    private static int Distance(int[] a, int[] b)
    {
        var common = 0;
        while (common < a.Length && common < b.Length && a[common] == b[common])
        {
            common++;
        }

        return a.Length + b.Length - (2 * common);
    }

    private static void CollectTags(
        string json, Dictionary<string, int> segments, Dictionary<int, int[]> tagPath,
        HashSet<int> demographicTags, HashSet<int> formatTags)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var tag in doc.RootElement.EnumerateArray())
            {
                if (tag.ValueKind != JsonValueKind.Object
                    || !tag.TryGetProperty("id", out var idElement)
                    || !idElement.TryGetInt32(out var tagId)
                    || tagPath.ContainsKey(tagId)
                    || !tag.TryGetProperty("name_path", out var pathElement)
                    || pathElement.GetString() is not { Length: > 0 } path)
                {
                    continue;
                }

                var parts = path.Split(" > ", StringSplitOptions.TrimEntries);
                var encoded = new int[parts.Length];
                for (var i = 0; i < parts.Length; i++)
                {
                    // Interned by PREFIX, not by segment name, so "Themes > Sports" and
                    // "Activities > Sports" do not collapse into one node.
                    var prefix = string.Join(" > ", parts, 0, i + 1);
                    if (!segments.TryGetValue(prefix, out var code))
                    {
                        segments[prefix] = code = segments.Count;
                    }

                    encoded[i] = code;
                }

                tagPath[tagId] = encoded;

                if (path.StartsWith(DemographicRoot, StringComparison.Ordinal))
                {
                    demographicTags.Add(tagId);
                }
                else if (path.StartsWith(MediumPrefix, StringComparison.Ordinal)
                    || path.StartsWith(LayoutPrefix, StringComparison.Ordinal))
                {
                    formatTags.Add(tagId);
                }
            }
        }
    }

    /// <summary>
    /// Original-language houses only. The English column is a licensing fact about a market, not
    /// about the work: Yen Press publishing two titles says nothing about whether they feel alike,
    /// whereas both running in Afternoon says quite a lot.
    /// </summary>
    private static int[] CollectPublishers(string json, Dictionary<string, int> ids)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var found = new List<int>();
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("type", out var type)
                    || !string.Equals(type.GetString(), "Original", StringComparison.OrdinalIgnoreCase)
                    || !entry.TryGetProperty("name", out var name)
                    || name.GetString() is not { Length: > 0 } house)
                {
                    continue;
                }

                if (!ids.TryGetValue(house, out var code))
                {
                    ids[house] = code = ids.Count;
                }

                found.Add(code);
            }

            return found.Distinct().ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }
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
    double Hit, double Popularity, double MillisecondsPerRequest, FeelRow? Feel);

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
        // `notaste` is the baseline the behavioural channel has to be read against, the same way
        // `nograph` and `nocoread` are for the crowd graphs.
        var taste = lower == "notaste" ? TasteVectorTuning.Default with { Weight = 0 } : TasteVectorTuning.Default;
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
            else if (key == "tasteweight")
            {
                taste = taste with { Weight = double.Parse(value, CultureInfo.InvariantCulture) };
            }
            else if (key == "tastemininjected")
            {
                taste = taste with { MinInjectedScore = double.Parse(value, CultureInfo.InvariantCulture) };
            }
            else if (key == "tastemaxinjected")
            {
                taste = taste with { MaxInjected = int.Parse(value, CultureInfo.InvariantCulture) };
            }
            else if (key == "tasteseedqueries")
            {
                taste = taste with { MaxSeedQueries = int.Parse(value, CultureInfo.InvariantCulture) };
            }
            else if (key == "creditartists")
            {
                recommender = recommender with { CreditsIncludeArtists = bool.Parse(value) };
            }
            else if (key == "maxperfranchise")
            {
                recommender = recommender with
                {
                    MaxPerFranchise = int.Parse(value, CultureInfo.InvariantCulture),
                };
            }
            else if (key == "excludeseedfranchise")
            {
                recommender = recommender with { ExcludeSeedFranchise = bool.Parse(value) };
            }
            else if (key == "tagancestordecay")
            {
                recommender = recommender with
                {
                    TagAncestorDecay = double.Parse(value, CultureInfo.InvariantCulture),
                };
            }
            else if (key == "tagancestorself")
            {
                recommender = recommender with
                {
                    TagAncestorIncludesSelf = bool.Parse(value),
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
            name, weights, diversity, graph, coGraph, coReadTuning, coRead, recommender, scoreWeights,
            taste);
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
    bool ScoreWeights,
    TasteVectorTuning Taste);

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
