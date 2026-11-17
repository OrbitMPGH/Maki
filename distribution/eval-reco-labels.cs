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
//   dotnet run distribution/eval-reco-labels.cs -- single --labels mu-human --split confirm default rail
//   dotnet run distribution/eval-reco-labels.cs -- single --panel distribution/eval-panel.tsv default rail
//
// SPLITS
// Requests come from the `dev` split unless told otherwise: four buckets of five, by a stable hash
// of the seed (pair modes) or the reader (library). Sweep and fit there. `--split confirm` is the
// fifth bucket and is for one look per decision. `--split all` is the request set every number
// recorded before v7 was scored on.
//
// THE JUDGED PANEL
// `--panel` swaps the label graph for a fixed list of seeds and grades each variant's top --depth
// picks against judgements stored in <artifacts>/judgements.db by distribution/judge-pairs.py.
// A pick with no judgement is reported as unjudged and queued, never counted as wrong, and the
// table carries two scales the label graphs cannot: `enjoy` and `feel`.
//
// MAKI_ARTIFACTS_DIR names the folder the label artifacts, the judgement store and the per-request
// CSVs live in (default .artifacts), so the tool can run from a worktree.
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
using Maki.Metadata.Ease;
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

// Where the label artifacts, the judgement store and the per-request CSVs live. They are git-ignored
// and sit in the main checkout, so a run from a worktree has to be able to point at them.
var artifactsDir = Environment.GetEnvironmentVariable("MAKI_ARTIFACTS_DIR") ?? ".artifacts";

var mode = "single";
var labelKind = "reco";
var requestCount = 500;
var seedsPerRequest = 3;
var limit = 40;
var minLabels = 5;
var holdout = 0.2;
var minLibrary = 20;
var maxLibrary = 300;
var workPath = Path.Combine(artifactsDir, "coread-graph.db");
string? muPathOverride = null;
string? tastePathOverride = null;
string? easePathOverride = null;
var rngSeed = 20260827;
var strata = false;
var negatives = false;
var feel = false;
var shelfJudged = false;
string? dumpFeatures = null;
var foldIndex = -1;
var foldCount = 0;
var csvMetric = "rr";
var facetPassage = false;
string? modelOverride = null;
var quantBits = 0;
var quantTaste = false;
var split = "dev";
string? panelPath = null;
var panelSet = "dev";
var depth = 10;
string? judge = null;
var rubric = "v1";
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
        // A taste artifact other than the installed one, so `library --fold-users` can grade a
        // fold-limited build without copying it into the config dir.
        case "--taste":
            tastePathOverride = args[++i];
            break;
        // An EASE table other than the installed one, for the same reason: `library --fold-users`
        // grades a fold-limited build (`build-ease-table.py --fold-out`) without installing it.
        case "--ease":
            easePathOverride = args[++i];
            break;
        case "--rng":
            rngSeed = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--strata":
            strata = true;
            break;
        // Splits a held-out reading list into titles the reader liked and titles they did NOT, and
        // hands the disliked half of the SEED slice to the avoid channel. `library` only: it is the
        // one mode that reads per-entry scores, and a pair graph has no notion of a negative.
        case "--negatives":
            negatives = true;
            break;
        case "--feel":
            feel = true;
            break;
        // Judges each top pick of a held-out reader's page against two of that reader's own series:
        // the one nearest to it in the text space, and the one its card names. `library` only.
        case "--shelf-judged":
            shelfJudged = true;
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
        // Grade an index built by `build-embeddings.cs base-facets`, whose rows carry a different
        // model version and would otherwise all be dropped at load as incompatible - leaving an
        // empty index and a table of zeroes rather than an error.
        case "--facets":
            facetPassage = true;
            break;
        // Score an index built by `build-embeddings.cs <candidate>` with that candidate's model,
        // one of EmbeddingModelProfile.Candidates, instead of the one the settings name.
        case "--model":
            modelOverride = args[++i];
            break;
        // Which per-request metric the .csv carries for eval-compare.py: rr (default), ndcg or r40.
        case "--csv":
            csvMetric = args[++i].ToLowerInvariant();
            break;
        // Prices a NARROWER stored vector without re-embedding anything. Each int8 element is
        // rounded onto the grid a vector of this many bits would have, which is the exact loss
        // packing them at that width would cause - see Requantize. 4 halves the text vectors,
        // which are the single largest thing the process holds. --quant-taste extends it to the
        // behavioural layer, which is a different width and worth pricing separately.
        case "--quant":
            quantBits = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--quant-taste":
            quantTaste = true;
            break;
        // Which slice of the seeds (pair modes) or readers (library) a run may draw requests from.
        // `dev` is four buckets of five and is what every sweep and fit uses; `confirm` is the
        // fifth, looked at once per decision; `all` is the request set every row before v7 used.
        case "--split":
            split = args[++i].ToLowerInvariant();
            break;
        // Judged mode: the seeds come from a fixed panel file instead of a label graph, and the
        // top --depth picks are graded against stored judgements rather than against edges.
        case "--panel":
            panelPath = args[++i];
            break;
        case "--panel-set":
            panelSet = args[++i].ToLowerInvariant();
            break;
        case "--depth":
            depth = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--judge":
            judge = args[++i];
            break;
        case "--rubric":
            rubric = args[++i];
            break;
        // An unrecognised flag is an error, never a variant. Variant names are free-form (they
        // become CSV filenames), so a mistyped or borrowed flag used to be accepted silently as
        // one: `--libraries 300` scored two variants called "--libraries" and "300", each at the
        // DEFAULT request count, and printed a table that looked entirely normal. The flag names
        // here and in run-reco-suite.ps1 are not the same set, which is how that happens.
        case var flag when flag.StartsWith("--", StringComparison.Ordinal):
            Console.WriteLine($"error: unknown option '{flag}'.");
            return 2;
        default:
            variantArgs.Add(args[i]);
            break;
    }
}

if (split is not ("dev" or "confirm" or "all"))
{
    Console.WriteLine($"error: --split wants 'dev', 'confirm' or 'all', not '{split}'.");
    return 2;
}

// The split and --fold-users hash the same id the same way, so five folds line up with the split's
// five buckets: fold 4 is exactly the confirm set and every other fold is empty inside it.
if (foldCount == 5 && split != "all")
{
    Console.WriteLine("error: --fold-users k/5 selects the same buckets --split does. Use another fold count, or --split all.");
    return 2;
}

if (panelPath is not null && depth > limit)
{
    Console.WriteLine($"error: --depth {depth} is deeper than the {limit} picks each request asks for (--limit).");
    return 2;
}

if (panelPath is not null)
{
    if (mode != "single")
    {
        Console.WriteLine("error: --panel is single-seed only. The panel is a list of seeds, one request each.");
        return 2;
    }

    if (panelSet is not ("dev" or "holdout" or "all"))
    {
        Console.WriteLine($"error: --panel-set wants 'dev', 'holdout' or 'all', not '{panelSet}'.");
        return 2;
    }

    if (!File.Exists(panelPath))
    {
        Console.WriteLine($"error: no panel at {panelPath}. Build it: python distribution/build-eval-panel.py");
        return 2;
    }

    labelKind = "judged";
}

if (shelfJudged && (mode != "library" || panelPath is not null))
{
    Console.WriteLine("error: --shelf-judged is library mode only, and replaces --panel there.");
    return 2;
}

if (shelfJudged && depth > limit)
{
    Console.WriteLine($"error: --depth {depth} is deeper than the {limit} picks each request asks for (--limit).");
    return 2;
}

if (labelKind == "judged" && panelPath is null)
{
    Console.WriteLine("error: --labels judged needs --panel. Judgements only exist for the panel's seeds.");
    return 2;
}

if (labelKind is not ("reco" or "coread" or "mu" or "mu-human" or "judged"))
{
    Console.WriteLine($"error: --labels wants 'reco', 'coread', 'mu' or 'mu-human', not '{labelKind}'.");
    return 2;
}

if (csvMetric is not ("rr" or "ndcg" or "r40" or "neg"))
{
    Console.WriteLine($"error: --csv wants 'rr', 'ndcg', 'r40' or 'neg', not '{csvMetric}'.");
    return 2;
}

if (negatives && mode != "library")
{
    Console.WriteLine("error: --negatives only means anything in `library` mode.");
    Console.WriteLine("  A pair graph says which titles go together, never which one somebody");
    Console.WriteLine("  disliked. Only a reading list carries a score to read that off.");
    return 2;
}

if (csvMetric == "neg" && !negatives)
{
    Console.WriteLine("error: --csv neg needs --negatives, which is what produces that column.");
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
var muPath = muPathOverride ?? (File.Exists(muInstalled) ? muInstalled : Path.Combine(artifactsDir, "mu-edges.db"));
// Behavioural vectors. Declared beside the other artifacts because the fold guard below has to see
// it: this is the one artifact actually TRAINED on readers, so it is the one the guard exists for.
var tastePath = tastePathOverride ?? Path.Combine(configDir, "taste-vectors.db");
// The EASE table, where the app installs it (AppPaths.EaseDbPath). Variants that do not name an
// `easepath` read it, so `default` stays the engine as shipped.
var easePath = easePathOverride ?? Path.Combine(configDir, "ease-table.db");

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

if (mode != "library" && panelPath is null && !File.Exists(labelPath))
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

// A variant that runs the EASE channel without naming a table reads the installed one, and with no
// table installed the channel is off, exactly as in the app. An explicit `easepath` that is missing
// is still an error below.
for (var v = 0; v < variants.Count; v++)
{
    if (variants[v].Ease.Weight > 0 && variants[v].EasePath is null)
    {
        variants[v] = File.Exists(easePath)
            ? variants[v] with { EasePath = easePath }
            : variants[v] with { Ease = variants[v].Ease with { Weight = 0 } };
    }
}

if (variants.Any(v => v.Ease.Weight > 0 && v.EasePath == easePath))
{
    Console.WriteLine($"ease     : {easePath}");
}
else if (easePathOverride is null && variants.Any(v => Variants.RunsEase(v.Name)))
{
    Console.WriteLine($"note     : no EASE table at {easePath}, so 'default' runs without that channel.");
}

var modelKind = modelOverride ?? Settings.ReadEmbeddingModel(Path.Combine(configDir, "maki.db")) ?? "base";
var modelProfile = EmbeddingModelProfile.Resolve(modelKind);
if (modelOverride is not null)
{
    if (!EmbeddingModelProfile.Candidates.TryGetValue(modelOverride, out var candidate))
    {
        Console.WriteLine($"error: unknown --model '{modelOverride}'. Known: {string.Join(", ", EmbeddingModelProfile.Candidates.Keys)}");
        return 2;
    }

    modelProfile = candidate;
}
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
Console.WriteLine($"mode     : {mode}");
Console.WriteLine($"limit    : top {limit} per request");
if (panelPath is not null)
{
    Console.WriteLine($"panel    : {panelPath}, set '{panelSet}', judged at depth {depth}");
}
else
{
    Console.WriteLine(split switch
    {
        "dev" => "split    : dev (four buckets of five). Sweep and fit here; confirm once with --split confirm.",
        "confirm" => "split    : CONFIRM (the held-back fifth). One look per decision, never a sweep.",
        _ => "split    : all (the request sets every row before v7 was scored on)",
    });
}

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

if (quantBits is > 0 and < 8)
{
    if (EmbeddingMath.ShouldPack(index.Dimensions))
    {
        Console.WriteLine($"error: --quant prices int8 rows, and this {index.Dimensions}-dim index already stores 4-bit levels.");
        Console.WriteLine($"  Raise EmbeddingMath.PackMinimumDimensions above {index.Dimensions} to compare against int8.");
        return 2;
    }

    index = index.Requantized(quantBits, quantTaste, out var text, out var taste);
    cache.ReplaceLoadedForEval(index);
    Console.WriteLine(
        $"quant    : {quantBits}-bit grid, {text.Changed * 100.0 / text.Total:F1}% of text elements moved" +
        (quantTaste ? $", {taste.Changed * 100.0 / Math.Max(1, taste.Total):F1}% of behavioural" : "") +
        $" (text vectors would be {index.Count * (long)index.Dimensions * quantBits / 8 / 1048576.0:F0} MB packed" +
        $" against {index.Count * (long)index.Dimensions / 1048576.0:F0} MB)");
}

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
        if (ReadTrainingFolds(path) is not { Count: > 0 } trained || HoldsOut(trained, foldIndex, foldCount))
        {
            continue;
        }

        Console.WriteLine($"error: {name} artifact at {path} was trained on folds {string.Join(',', trained.Order())}, "
            + $"not every fold of {foldCount} but {foldIndex}.");
        Console.WriteLine("       A different split or a fold it saw puts graded readers in its training data.");
        Console.WriteLine($"       Rebuild it with --fold-out {foldIndex}/{foldCount}.");
        return 2;
    }

    // The artifacts TRAINED on readers must also say which folds they saw: one that says "all", or
    // nothing, learned from the readers being graded too. The check above lets that through, which
    // is right for the pair graphs and wrong for these.
    var readerTrained = new List<(string Name, string Path)>();
    if (variants.Any(v => v.Taste.Weight > 0))
    {
        readerTrained.Add(("taste", tastePath));
    }

    readerTrained.AddRange(variants
        .Where(v => v.EasePath is not null && v.Ease.Weight > 0)
        .Select(v => v.EasePath!).Distinct().Select(p => ("ease", p)));
    foreach (var (name, path) in readerTrained)
    {
        if (!File.Exists(path))
        {
            continue;
        }

        if (ReadTrainingFolds(path) is not { Count: > 0 } trained)
        {
            Console.WriteLine($"error: {name} artifact at {path} records no training fold, so it learned from every reader.");
            Console.WriteLine($"       Build it with fold {foldIndex} held out{(name == "taste" ? " and pass it with --taste" : string.Empty)}.");
            return 2;
        }

        if (!HoldsOut(trained, foldIndex, foldCount))
        {
            Console.WriteLine($"error: {name} artifact at {path} was trained on folds {string.Join(',', trained.Order())}, "
                + $"not every fold of {foldCount} but {foldIndex}.");
            Console.WriteLine($"       Rebuild it with --fold-out {foldIndex}/{foldCount}.");
            return 2;
        }
    }
}

var feelIndex = feel ? FeelIndex.Build(dumpPath, index) : null;

// A recommender per distinct tuning triple. Near-free to construct: the expensive state (the vector
// index, both graphs) lives in the caches and is shared across all of them.
var recommenders = new Dictionary<
    (RecoGraphTuning, CoReadTuning, RecommenderTuning, TasteVectorTuning, EaseTuning, string?), SemanticRecommender>();
var easeCaches = new Dictionary<string, EaseCache>(StringComparer.OrdinalIgnoreCase);
SemanticRecommender RecommenderFor(
    RecoGraphTuning graphTuning, CoReadTuning coReadTuning, RecommenderTuning recoTuning,
    TasteVectorTuning tasteTuning, EaseTuning? easeTuning = null, string? easePath = null)
{
    easeTuning ??= EaseTuning.Default;
    if (easeTuning.Weight <= 0)
    {
        easePath = null;
    }

    var key = (graphTuning, coReadTuning, recoTuning, tasteTuning, easeTuning, easePath);
    if (!recommenders.TryGetValue(key, out var found))
    {
        EaseCache? easeCache = null;
        if (easePath is not null && !easeCaches.TryGetValue(easePath, out easeCache))
        {
            easeCache = easeCaches[easePath] = new EaseCache(new EaseOptions(easePath), new Quiet<EaseCache>());
        }

        found = new SemanticRecommender(
            options, dumpOptions, store, cache, graphCache, graphTuning, coReadCache, coReadTuning,
            new Quiet<SemanticRecommender>(), recoTuning, tasteTuning, easeCache, easeTuning);
        recommenders[key] = found;
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
// A co-read graph built with `fetch-coread-graph.cs build --fold-out` is the exception in `library`
// mode: the fold guard above has already refused one that saw the graded fold.
var coReadFoldLimited = mode == "library" && foldCount > 0
    && ReadTrainingFolds(coReadPath) is { Count: > 0 } coReadFolds && HoldsOut(coReadFolds, foldIndex, foldCount);
var forbidCoRead = (mode == "library" && !coReadFoldLimited) || labelKind == "coread";
if (coReadFoldLimited)
{
    Console.WriteLine("note     : the co-read graph was built without the graded fold, so it stays on.");
}

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

    if (v.EasePath is not null && !File.Exists(v.EasePath))
    {
        Console.WriteLine($"error: no EASE table at {v.EasePath}.");
        return 2;
    }

    if (forbidTaste && v.Ease.Weight > 0)
    {
        Console.WriteLine(
            $"note     : forcing the EASE channel off for '{v.Name}': it is trained on the "
            + "reading lists these labels come from.");
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

// The confirmation set draws from its own stream, so it is not the dev sample with rows removed.
var rng = new Random(split == "confirm" ? rngSeed + 7919 : rngSeed);
/// <summary>
/// Scored entries a reader needs before their own mean is worth subtracting from. Below this the
/// mean is one or two numbers and "well under it" is noise, which would hand the avoid channel
/// titles nobody disliked.
/// </summary>
const int MinScoredForMean = 5;

/// <summary>Upper popularity ranks of the three label bands the strata report splits recall by.</summary>
long[] TargetBounds = [1_000L, 20_000L, long.MaxValue];

/// <summary>
/// How hard a held-out dislike pushes, from how far under the reader's own mean it sits.
///
/// <para>
/// The app's own scale is <c>(5 - rating) / 4</c>: four steps between the neutral point and the
/// bottom. This lays the same four-step band over the gap instead of over an absolute score,
/// because a reader's own zero is where most of the variance in these lists is - the qualifying
/// gap of 15 POINT_100 points maps to 0.25 and a 40-point one to 1.0.
/// </para>
/// </summary>
static double AvoidStrength(double gap) => Math.Clamp(0.25 + (0.75 * (gap - 15.0) / 25.0), 0.25, 1.0);

var requests = panelPath is not null
    ? BuildPanelRequests()
    : mode == "library"
        ? BuildLibraryRequests()
        : BuildPairRequests();

if (requests.Count == 0)
{
    Console.WriteLine("error: no scorable requests — nothing has enough labels.");
    return 1;
}

Console.WriteLine($"requests : {requests.Count}");
Console.WriteLine();

Directory.CreateDirectory(Path.Combine(artifactsDir, "eval"));

var rows = new List<ResultRow>();
if (panelPath is not null)
{
    var judgements = Judgements.Load(Path.Combine(artifactsDir, "judgements.db"), rubric, ref judge);
    Console.WriteLine(judgements.Count == 0
        ? "judge    : no judgements stored yet. This run only builds the judging queue."
        : $"judge    : {judge}, rubric {rubric}, {judgements.Count} judged pairs");
    Console.WriteLine();

    var judgedRows = new List<JudgedRow>();
    var queue = new SortedSet<(long Seed, long Candidate)>();
    foreach (var variant in variants)
    {
        judgedRows.Add(await ScoreJudged(variant, judgements, queue));
    }

    ReportJudged(judgedRows, queue);
    return 0;
}

if (shelfJudged)
{
    var judgements = Judgements.Load(Path.Combine(artifactsDir, "judgements.db"), rubric, ref judge);
    Console.WriteLine($"judge    : {judge ?? "none yet"}, rubric {rubric}, {judgements.Count} judged pairs");
    Console.WriteLine();
    var shelfRows = new List<ShelfRow>();
    var shelfQueue = new SortedSet<(long Seed, long Candidate)>();
    foreach (var variant in variants)
    {
        shelfRows.Add(await ScoreShelf(variant, judgements, shelfQueue));
    }

    ReportShelf(shelfRows, shelfQueue);
    return 0;
}

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

    var labelled = labels
        .Where(kv => kv.Value.Count >= minLabels && index.TryGetRow(kv.Key, out _))
        .Select(kv => kv.Key)
        .Order()
        .ToList();
    var eligible = labelled.Where(InSplit).ToList();
    Console.WriteLine(
        $"labels   : {labelKind}, {labelled.Count} seeds with >= {minLabels} in-index neighbours" +
        (split == "all" ? string.Empty : $", {eligible.Count} in the {split} split"));

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
/// Whether a seed (pair modes) or a reader (library) belongs to the split this run may use. The
/// confirmation bucket is one of five by the same stable hash the reader folds use, so it is the
/// same set on every machine and in every run.
/// </summary>
bool InSplit(long id) => split switch
{
    "confirm" => UserFold.Of(id, 5) == 4,
    "dev" => UserFold.Of(id, 5) != 4,
    _ => true,
};

/// <summary>
/// One request per panel seed. No positives: a panel request is graded against stored judgements
/// of what the variants actually returned, not against a label graph.
/// </summary>
List<Request> BuildPanelRequests()
{
    var lines = File.ReadAllLines(panelPath!);
    var header = lines[0].Split('	');
    var idColumn = Array.IndexOf(header, "id");
    var setColumn = Array.IndexOf(header, "set");
    if (idColumn < 0 || setColumn < 0)
    {
        throw new InvalidOperationException("The panel file needs 'id' and 'set' columns in its header row.");
    }

    var built = new List<Request>();
    var missing = 0;
    foreach (var line in lines.Skip(1))
    {
        var cells = line.Split('	');
        if (cells.Length <= Math.Max(idColumn, setColumn)
            || (panelSet != "all" && cells[setColumn] != panelSet))
        {
            continue;
        }

        var id = long.Parse(cells[idColumn], CultureInfo.InvariantCulture);
        if (!index.TryGetRow(id, out _))
        {
            missing++;
            continue;
        }

        built.Add(new Request([id], null, []));
    }

    Console.WriteLine(
        $"panel    : {built.Count} seeds" + (missing > 0 ? $", {missing} not in this index and skipped" : string.Empty));
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

    var listed = byUser.Where(kv => kv.Value.Count >= minLibrary).ToList();
    var usable = listed.Where(kv => InSplit(kv.Key)).ToList();
    Console.WriteLine(
        $"libraries: {listed.Count} of {byUser.Count} reading lists have >= {minLibrary} in-index completed titles" +
        (split == "all" ? string.Empty : $", {usable.Count} in the {split} split"));

    var built = new List<Request>();
    var unscored = 0;
    var unusable = 0;
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

        // Capped, because a 4,000-title list costs one dot product per catalogue row per seed query
        // and buys nothing a 300-title one does not: MaxSeedQueries stops at 8 either way.
        var seedEntries = shuffled.Skip(heldOut).Take(maxLibrary).ToList();
        var heldOutEntries = shuffled.Take(heldOut).ToList();

        // A reader's own zero is where most of the variance is (see the reader-cohort phase), so a
        // dislike here is relative to their own mean rather than to an absolute score. 15 points on
        // AniList's POINT_100 scale is the 1.5 the plan asks for on a /10 one.
        var scoredEntries = shuffled.Where(e => e.Score > 0).ToList();
        var readerMean = scoredEntries.Count >= MinScoredForMean
            ? scoredEntries.Average(e => e.Score)
            : double.NaN;
        var threshold = readerMean - 15.0;
        bool Disliked((long Id, int Score) e) =>
            negatives && !double.IsNaN(readerMean) && e.Score > 0 && e.Score <= threshold;

        if (negatives && double.IsNaN(readerMean))
        {
            unscored++;
            continue;
        }

        var positives = heldOutEntries.Where(e => !Disliked(e)).ToDictionary(e => e.Id, _ => 1.0);
        var heldOutNegatives = heldOutEntries.Where(Disliked).Select(e => e.Id).ToList();
        if (negatives && (positives.Count == 0 || heldOutNegatives.Count == 0))
        {
            // Every graded reader has to carry both halves, or the two columns would be averaged
            // over different populations and the table would compare them anyway.
            unusable++;
            continue;
        }

        var seeds = seedEntries.Where(e => !Disliked(e)).ToList();
        var avoid = seedEntries.Where(Disliked)
            .ToDictionary(e => e.Id, e => AvoidStrength(readerMean - e.Score));
        var scores = seeds
            .Where(e => e.Score > 0)
            .ToDictionary(e => e.Id, e => e.Score / 50.0); // POINT_100, mirroring rating/5.0 locally

        built.Add(new Request(
            seeds.Select(e => e.Id).ToList(),
            scores.Count > 0 ? scores : null,
            positives,
            avoid.Count > 0 ? avoid : null,
            heldOutNegatives));
    }

    if (negatives)
    {
        Console.WriteLine(
            $"negatives: {built.Count} readers graded, {unscored} skipped for fewer than "
            + $"{MinScoredForMean} scored entries, {unusable} for holding out only one of the two halves");
        Console.WriteLine(
            $"           mean avoided seeds {built.Average(r => r.Avoid?.Count ?? 0):F1}, "
            + $"mean held-out negatives {built.Average(r => r.Negatives!.Count):F1}");
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
    var easeTuning = forbidTaste ? variant.Ease with { Weight = 0 } : variant.Ease;
    var recommender = RecommenderFor(
        variant.Graph, variant.CoReadTuning, variant.Recommender, tasteTuning, easeTuning, variant.EasePath);

    // Only the first variant in a run is dumped. Two pools would interleave under one requestId and
    // the fit would pair candidates that never competed.
    var dumping = dumpFeatures is not null && ReferenceEquals(variant, variants[0])
        ? new StringBuilder("request,label,semantic,genre,tag,author,quality,graph,coread,taste,distinct,avoid,ease,avoidtag,pop\n")
        : null;

    var reciprocal = new double[requests.Count];
    var r10 = new double[requests.Count];
    var r20 = new double[requests.Count];
    var r40 = new double[requests.Count];
    var ndcg = new double[requests.Count];
    var named = new double[requests.Count];
    var fans = new double[requests.Count];
    var negativeHit = new double[requests.Count];
    var top1k = new double[requests.Count];
    var targetRecall = new double[TargetBounds.Length][];
    for (var t = 0; t < targetRecall.Length; t++)
    {
        targetRecall[t] = new double[requests.Count];
    }

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
            avoidWeights: request.Avoid,
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
                    .Append(Csv(f.Distinct)).Append(',').Append(Csv(f.Avoid)).Append(',')
                    .Append(Csv(f.Ease)).Append(',').Append(Csv(f.AvoidTag)).Append(',')
                    .Append(Csv(f.Percentile)).Append('\n');
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
        // Cards whose visible reason is "Fans of X": a co-read seed and nothing the card ranks above it.
        fans[i] = picks.Count == 0
            ? 0
            : (double)picks.Count(p => p.CoReadWithTitle is not null && p.BecauseOfTitle is null && !p.AuthorMatch) / picks.Count;
        // The column the avoid channel exists to move: what share of the titles this reader
        // actually disliked still made the page. Lower is better, and it is only meaningful read
        // together with nDCG on the positives - dropping everything moves both.
        negativeHit[i] = request.Negatives is { Count: > 0 } wanted
            ? (double)ids.Take(limit).Count(wanted.Contains) / wanted.Count
            : 0;
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

        top1k[i] = ids.Count == 0 ? 0 : (double)ids.Count(id => RankOf(id) is > 0 and <= 1000) / ids.Count;

        // Recall split by how famous the LABEL is. A variant that wins only on top-1k targets is
        // returning famous titles; one that wins past rank 20,000 is finding something.
        for (var t = 0; t < TargetBounds.Length; t++)
        {
            var low = t == 0 ? 0 : TargetBounds[t - 1];
            var high = TargetBounds[t];
            var inBand = request.Positives.Keys.Where(id => RankOf(id) is var r && r > low && r <= high).ToHashSet();
            targetRecall[t][i] = inBand.Count == 0
                ? double.NaN
                : (double)ids.Take(limit).Count(inBand.Contains) / Math.Min(limit, inBand.Count);
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
    // That column is the request's identity (RequestKeys), not its position: two files from
    // different splits or panel sets then share no key and eval-compare.py refuses to pair them,
    // where a row index paired request 7 of one seed set with request 7 of another.
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
        "neg" => negativeHit,
        _ => reciprocal,
    };

    var keys = RequestKeys.Of(requests);
    var csv = new StringBuilder();
    for (var i = 0; i < series.Length; i++)
    {
        csv.Append(keys[i]).Append(',').Append(series[i].ToString("R", CultureInfo.InvariantCulture)).Append('\n');
    }

    File.WriteAllText(Path.Combine(artifactsDir, "eval", $"rr-{variant.Name}-{csvSuffix}.csv"), csv.ToString());

    // The negative hit rate gets its own file whenever it exists, under the suffix `library-neg`,
    // so one run answers both questions the avoid channel has to pass: the drop has to be real AND
    // nDCG on the positives has to hold. Two runs to read two columns is how a sweep this size
    // stops being affordable.
    if (negatives)
    {
        var negCsv = new StringBuilder();
        for (var i = 0; i < negativeHit.Length; i++)
        {
            negCsv.Append(keys[i]).Append(',')
                .Append(negativeHit[i].ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        }

        File.WriteAllText(
            Path.Combine(artifactsDir, "eval", $"rr-{variant.Name}-library-neg.csv"), negCsv.ToString());
    }

    if (dumping is not null)
    {
        File.WriteAllText(dumpFeatures!, dumping.ToString());
        Console.WriteLine($"  features written to {dumpFeatures}");
    }

    return new ResultRow(
        variant.Name, r10, r20, r40, ndcg, reciprocal, named, negativeHit, targetRecall, top1k.Average(),
        (double)hits / requests.Count,
        popularity.Count == 0 ? double.NaN : Median(popularity),
        clock.Elapsed.TotalMilliseconds / Math.Max(1, requests.Count),
        feelRows.Count == 0 ? null : new FeelRow(
            MeanDefined(feelRows, f => f.Demographic),
            MeanDefined(feelRows, f => f.Format),
            MeanDefined(feelRows, f => f.Publisher),
            MeanDefined(feelRows, f => f.Era),
            MeanDefined(feelRows, f => f.TagTree),
            MeanDefined(feelRows, f => f.Franchise)),
        fans);
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

/// <summary>Popularity rank of a series, or <c>long.MaxValue</c> when the dump has none.</summary>
long RankOf(long id) =>
    index.TryGetRow(id, out var row) && index.PopularityAt(row) is var rank and > 0 ? rank : long.MaxValue;

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

/// <summary>
/// Grades the top <c>--depth</c> picks for each panel seed against stored judgements. A pick with
/// no judgement is counted as unjudged and queued, never scored as wrong: the whole point of the
/// panel is that an unlabelled pick is not a miss.
/// </summary>
async Task<JudgedRow> ScoreJudged(
    Variant variant, Dictionary<(long Seed, long Candidate), Judgement> judgements,
    SortedSet<(long Seed, long Candidate)> queue)
{
    var recommender = RecommenderFor(
        variant.Graph, variant.CoReadTuning, variant.Recommender, variant.Taste, variant.Ease, variant.EasePath);
    var n = requests.Count;
    var enjoy = new double[n];
    var feel = new double[n];
    var good = new double[n];
    var poor = new double[n];
    var ndcg = new double[n];
    var judgedShare = new double[n];
    var insufficient = new double[n];
    var top1k = new double[n];
    var franchise = new double[n];
    var popularity = new List<double>();
    var clock = Stopwatch.StartNew();

    for (var i = 0; i < n; i++)
    {
        var seed = requests[i].Seeds[0];
        var picks = await recommender.GetSimilarAsync(
            requests[i].Seeds,
            requests[i].Seeds,
            limit,
            RecommendationFilters.None,
            obscurity: 0,
            diversity: variant.Diversity,
            weights: variant.Weights,
            coGraph: variant.CoGraph,
            coRead: variant.CoRead);
        var ids = picks
            .Select(p => long.Parse(p.ProviderId, CultureInfo.InvariantCulture))
            .Take(depth)
            .ToList();

        var gains = new List<(double Enjoy, double Feel)>();
        var thin = 0;
        foreach (var id in ids)
        {
            if (!judgements.TryGetValue((seed, id), out var j))
            {
                queue.Add((seed, id));
            }
            else if (j.Insufficient)
            {
                thin++;
            }
            else
            {
                gains.Add((j.Enjoy, j.Feel));
            }
        }

        judgedShare[i] = ids.Count == 0 ? 0 : (double)(gains.Count + thin) / ids.Count;
        insufficient[i] = ids.Count == 0 ? 0 : (double)thin / ids.Count;
        if (gains.Count == 0)
        {
            enjoy[i] = feel[i] = good[i] = poor[i] = ndcg[i] = double.NaN;
        }
        else
        {
            enjoy[i] = gains.Average(g => g.Enjoy);
            feel[i] = gains.Average(g => g.Feel);
            // feel is the scale the judge is trusted on, so the shares and nDCG are taken on it.
            good[i] = (double)gains.Count(g => g.Feel >= 2) / gains.Count;
            poor[i] = (double)gains.Count(g => g.Feel == 0) / gains.Count;

            // Condensed: unjudged picks are removed and the rest close ranks. The ideal is the best
            // ordering of everything judged for this seed, whichever variant returned it, so a
            // variant is measured against the pool rather than against its own picks.
            var dcg = gains.Select((g, rank) => g.Feel / Math.Log2(rank + 2)).Sum();
            var ideal = judgements
                .Where(kv => kv.Key.Seed == seed && !kv.Value.Insufficient)
                .Select(kv => (double)kv.Value.Feel)
                .OrderByDescending(g => g)
                .Take(gains.Count)
                .Select((g, rank) => g / Math.Log2(rank + 2))
                .Sum();
            ndcg[i] = ideal > 0 ? dcg / ideal : 0;
        }

        var seedFranchise = index.TryGetRow(seed, out var seedRow) ? index.FranchiseAt(seedRow) : VectorIndex.Unknown;
        var famous = 0;
        var related = 0;
        foreach (var id in ids)
        {
            if (!index.TryGetRow(id, out var row))
            {
                continue;
            }

            var rank = index.PopularityAt(row);
            if (rank != VectorIndex.Unknown && rank > 0 && rank <= 1000)
            {
                famous++;
            }

            if (seedFranchise != VectorIndex.Unknown && index.FranchiseAt(row) == seedFranchise)
            {
                related++;
            }
        }

        top1k[i] = ids.Count == 0 ? 0 : (double)famous / ids.Count;
        franchise[i] = ids.Count == 0 ? 0 : (double)related / ids.Count;
        if (MedianPopularity(ids) is { } median)
        {
            popularity.Add(median);
        }

        if (i % 20 == 0 || i == n - 1)
        {
            Console.Write($"\r  {variant.Name}: {i + 1}/{n}, {clock.Elapsed.TotalSeconds:F0}s   ");
        }
    }

    Console.Write("\r".PadRight(72) + "\r");

    // Same layout eval-compare.py reads. A seed with nothing judged is left out rather than written
    // as zero, and the comparison runs on the seeds both variants have.
    var keys = RequestKeys.Of(requests);
    foreach (var (suffix, series) in new[] { ("judged", enjoy), ("judged-feel", feel) })
    {
        var csv = new StringBuilder();
        for (var i = 0; i < n; i++)
        {
            if (!double.IsNaN(series[i]))
            {
                csv.Append(keys[i]).Append(',').Append(Csv(series[i])).Append('\n');
            }
        }

        File.WriteAllText(Path.Combine(artifactsDir, "eval", $"rr-{variant.Name}-{suffix}.csv"), csv.ToString());
    }

    return new JudgedRow(
        variant.Name, enjoy, feel, good, poor, ndcg, judgedShare, insufficient, top1k, franchise,
        popularity.Count == 0 ? double.NaN : Median(popularity));
}

/// <summary>
/// The judged panel for whole-shelf requests. The rubric scores one seed against one pick, so each
/// pick is scored against the reader's series nearest to it in the text space ("does this feel like
/// something they read") and against the series its card names ("is the reason true"). The card
/// kind follows <c>engineWhy</c>: Feels like, then author, then Fans of. The eval never runs
/// <c>RecommendationReasons.Balance</c>, so the named seed is each card's strongest one; the app
/// moves some cards to weaker seeds.
/// </summary>
async Task<ShelfRow> ScoreShelf(
    Variant variant, Dictionary<(long Seed, long Candidate), Judgement> judgements,
    SortedSet<(long Seed, long Candidate)> queue)
{
    var coGraph = variant.CoGraph && !forbidGraph;
    var coRead = variant.CoRead && !forbidCoRead;
    var tasteTuning = forbidTaste ? variant.Taste with { Weight = 0 } : variant.Taste;
    var easeTuning = forbidTaste ? variant.Ease with { Weight = 0 } : variant.Ease;
    var recommender = RecommenderFor(
        variant.Graph, variant.CoReadTuning, variant.Recommender, tasteTuning, easeTuning, variant.EasePath);
    var n = requests.Count;
    var nearFeel = new double[n];
    var nearPoor = new double[n];
    var nearGood = new double[n];
    var judged = new double[n];
    var top1k = new double[n];
    string[] kindNames = ["feels", "fans", "author", "other"];
    var claims = kindNames.ToDictionary(k => k, _ => new List<int>());
    var kinds = kindNames.ToDictionary(k => k, _ => 0);
    var fansNearer = new List<int>();
    var balancedFans = new List<int>();
    var movedFans = 0;
    var fansCards = 0;
    var total = 0;
    var clock = Stopwatch.StartNew();

    for (var i = 0; i < n; i++)
    {
        var request = requests[i];
        var picks = await recommender.GetSimilarAsync(
            request.Seeds,
            request.Seeds,
            limit,
            RecommendationFilters.None,
            obscurity: 0,
            seedWeights: variant.ScoreWeights ? request.Scores : null,
            avoidWeights: request.Avoid,
            diversity: variant.Diversity,
            weights: variant.Weights,
            coGraph: coGraph,
            coRead: coRead);

        var seedRows = new List<(long Id, int Row)>();
        foreach (var id in request.Seeds)
        {
            if (index.TryGetRow(id, out var seedRow))
            {
                seedRows.Add((id, seedRow));
            }
        }

        var feels = new List<int>();
        var famous = 0;
        var shown = picks.Take(depth).ToList();
        var balancedNames = BalancedFansSeeds(picks);
        foreach (var pick in shown)
        {
            var id = long.Parse(pick.ProviderId, CultureInfo.InvariantCulture);
            if (!index.TryGetRow(id, out var row))
            {
                continue;
            }

            if (index.PopularityAt(row) is > 0 and <= 1000)
            {
                famous++;
            }

            var nearest = seedRows.Count == 0 ? -1L : seedRows.MaxBy(s => index.CosineBetween(s.Row, row)).Id;
            var (kind, named) = pick.BecauseOfTitle is not null
                ? ("feels", ParseId(pick.BecauseOfId))
                : pick.AuthorMatch
                    ? ("author", (long?)null)
                    : pick.CoReadWithTitle is not null
                        ? ("fans", ParseId(pick.CoReadWithId))
                        : ("other", (long?)null);
            kinds[kind]++;
            total++;

            if (nearest >= 0)
            {
                if (!judgements.TryGetValue((nearest, id), out var j))
                {
                    queue.Add((nearest, id));
                }
                else if (!j.Insufficient)
                {
                    feels.Add(j.Feel);
                }
            }

            if (kind == "fans" && named is { } strongest)
            {
                fansCards++;
                var position = shown.IndexOf(pick);
                var balanced = ParseId(balancedNames[position]) ?? strongest;
                if (balanced != strongest)
                {
                    movedFans++;
                }

                if (!judgements.TryGetValue((balanced, id), out var jb))
                {
                    queue.Add((balanced, id));
                }
                else if (!jb.Insufficient)
                {
                    balancedFans.Add(jb.Feel);
                }
            }

            if (named is { } seed)
            {
                if (!judgements.TryGetValue((seed, id), out var j))
                {
                    queue.Add((seed, id));
                }
                else if (!j.Insufficient)
                {
                    claims[kind].Add(j.Feel);
                    if (kind == "fans" && nearest >= 0 && nearest != seed
                        && judgements.TryGetValue((nearest, id), out var jn) && !jn.Insufficient)
                    {
                        fansNearer.Add(jn.Feel - j.Feel);
                    }
                }
            }
        }

        judged[i] = shown.Count == 0 ? 0 : (double)feels.Count / shown.Count;
        top1k[i] = shown.Count == 0 ? 0 : (double)famous / shown.Count;
        if (feels.Count == 0)
        {
            nearFeel[i] = nearPoor[i] = nearGood[i] = double.NaN;
        }
        else
        {
            nearFeel[i] = feels.Average();
            nearPoor[i] = (double)feels.Count(f => f == 0) / feels.Count;
            nearGood[i] = (double)feels.Count(f => f >= 2) / feels.Count;
        }

        if (i % 10 == 0 || i == n - 1)
        {
            Console.Write($"\r  {variant.Name}: {i + 1}/{n}, {clock.Elapsed.TotalSeconds:F0}s   ");
        }
    }

    Console.Write("\r".PadRight(72) + "\r");

    var keys = RequestKeys.Of(requests);
    var csv = new StringBuilder();
    for (var i = 0; i < n; i++)
    {
        if (!double.IsNaN(nearFeel[i]))
        {
            csv.Append(keys[i]).Append(',').Append(Csv(nearFeel[i])).Append('\n');
        }
    }

    File.WriteAllText(Path.Combine(artifactsDir, "eval", $"rr-{variant.Name}-shelf-feel.csv"), csv.ToString());

    return new ShelfRow(
        variant.Name, nearFeel, nearGood, nearPoor, judged, top1k.Average(),
        kinds.ToDictionary(kv => kv.Key, kv => total == 0 ? 0 : (double)kv.Value / total),
        claims.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<int>)kv.Value),
        fansNearer, balancedFans, fansCards == 0 ? 0 : (double)movedFans / fansCards);

    static long? ParseId(string? id) =>
        long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
}

/// <summary>
/// The seed each card names after <c>RecommendationReasons.Balance</c> in <c>Maki.Api</c>, which this
/// tool cannot reference. A copy of its loop: 20-card window, two namings per seed, the next seed in
/// <c>FeelSeeds</c>/<c>CoReadSeeds</c> past that. Keep it in step with that class. Returns the
/// "Fans of" seed per card, null where the card shows another reason.
/// </summary>
static string?[] BalancedFansSeeds(IReadOnlyList<MangaBakaRecommendation> ranked)
{
    const int window = 20;
    const int maxPerWindow = 2;
    var result = new string?[ranked.Count];
    var named = new string?[ranked.Count];
    var counts = new Dictionary<string, int>(StringComparer.Ordinal);
    for (var i = 0; i < ranked.Count; i++)
    {
        if (i >= window && named[i - window] is { } leaving)
        {
            counts[leaving]--;
        }

        var pick = ranked[i];
        IReadOnlyList<ReasonSeed>? seeds = null;
        var fans = false;
        if (pick.RelationKind is not null && pick.RelatedToTitle is not null)
        {
            seeds = null;
        }
        else if (pick.BecauseOfTitle is not null)
        {
            seeds = pick.FeelSeeds is { Count: > 0 } feel ? feel : null;
        }
        else if (!pick.AuthorMatch && pick.CoRead && pick.CoReadWithTitle is not null
            && pick.CoReadSeeds is { Count: > 0 } coRead)
        {
            seeds = coRead;
            fans = true;
        }

        if (seeds is null)
        {
            continue;
        }

        var chosen = seeds.FirstOrDefault(s => counts.GetValueOrDefault(s.Id ?? $"t:{s.Title}") < maxPerWindow) ?? seeds[0];
        var key = chosen.Id ?? $"t:{chosen.Title}";
        named[i] = key;
        counts[key] = counts.GetValueOrDefault(key) + 1;
        if (fans)
        {
            result[i] = chosen.Id;
        }
    }

    return result;
}

void ReportShelf(List<ShelfRow> shelfRows, SortedSet<(long Seed, long Candidate)> queue)
{
    static double MeanOf(double[] values)
    {
        var defined = values.Where(v => !double.IsNaN(v)).ToList();
        return defined.Count == 0 ? double.NaN : defined.Average();
    }

    static string Mean(IReadOnlyList<int> values) =>
        values.Count == 0 ? "-" : values.Average().ToString("F2", CultureInfo.InvariantCulture);

    Console.WriteLine(
        $"{"variant",-24}{"judged",8}{$"feel@{depth}",9}{"good",7}{"poor",7}{"top1k",7}" +
        $"{"feels",8}{"fans",7}{"author",8}{"other",7}");
    Console.WriteLine(new string('-', 92));
    foreach (var row in shelfRows)
    {
        Console.WriteLine(
            $"{row.Name,-24}{MeanOf(row.Judged),8:P0}{MeanOf(row.NearFeel),9:F3}" +
            $"{MeanOf(row.NearGood),7:P0}{MeanOf(row.NearPoor),7:P0}{row.Top1k,7:P0}" +
            $"{row.Kinds["feels"],8:P0}{row.Kinds["fans"],7:P0}{row.Kinds["author"],8:P0}{row.Kinds["other"],7:P0}");
    }

    Console.WriteLine();
    Console.WriteLine(
        $"{"variant",-24}{"claim: feels",14}{"n",6}{"claim: fans",13}{"n",6}{"fans: nearest minus named",28}" +
        $"{"fans balanced",15}{"n",6}{"moved",7}");
    Console.WriteLine(new string('-', 119));
    foreach (var row in shelfRows)
    {
        var nearer = row.FansNearer.Count == 0
            ? "-"
            : row.FansNearer.Average().ToString("+0.00;-0.00", CultureInfo.InvariantCulture);
        Console.WriteLine(
            $"{row.Name,-24}{Mean(row.Claims["feels"]),14}{row.Claims["feels"].Count,6}" +
            $"{Mean(row.Claims["fans"]),13}{row.Claims["fans"].Count,6}{nearer,28}" +
            $"{Mean(row.BalancedFans),15}{row.BalancedFans.Count,6}{row.MovedFans,7:P0}");
    }

    Console.WriteLine();
    Console.WriteLine($"  feel@{depth} : judged feel (0-3) of each top pick against the reader's series nearest to it");
    Console.WriteLine("            in the text space, averaged over judged picks. good = 2 or 3, poor = 0.");
    Console.WriteLine("  feels/fans/author/other : share of top picks whose card shows that reason.");
    Console.WriteLine("  claim   : judged feel of a pick against the series its card names (strongest seed).");
    Console.WriteLine("  balanced: the same for \"Fans of\" cards after the app's two-per-20 reason cap;");
    Console.WriteLine("            moved = share of those cards whose named series the cap changed.");

    var queuePath = Path.Combine(artifactsDir, "eval", "shelf-queue.tsv");
    var text = new StringBuilder("seed_id\tcand_id\n");
    foreach (var (s, c) in queue)
    {
        text.Append(s).Append('\t').Append(c).Append('\n');
    }

    File.WriteAllText(queuePath, text.ToString());
    Console.WriteLine();
    Console.WriteLine($"  {queue.Count} unjudged pairs written to {queuePath}");
    if (queue.Count > 0)
    {
        Console.WriteLine($"  judge them: python distribution/judge-pairs.py run --queue {queuePath}");
    }

    if (shelfRows.Count > 1)
    {
        Console.WriteLine(
            $"  paired stats: python distribution/eval-compare.py {shelfRows[1].Name} {shelfRows[0].Name} shelf-feel feel@{depth}");
    }
}

// -------------------------------------------------------------------------------------------------
// Reporting
// -------------------------------------------------------------------------------------------------

void Report()
{
    Console.WriteLine(
        $"{"variant",-24}{"R@10",8}{"R@20",8}{$"R@{limit}",8}{$"nDCG@{limit}",10}{"MRR",8}{"hit",8}" +
        $"{"named",8}{"fans",8}{(negatives ? $"{"neg",8}" : string.Empty)}{"pop",9}{"top1k",8}{"ms",8}");
    Console.WriteLine(new string('-', negatives ? 113 : 105));
    foreach (var row in rows)
    {
        var pop = double.IsNaN(row.Popularity) ? "-" : row.Popularity.ToString("F0", CultureInfo.InvariantCulture);
        Console.WriteLine(
            $"{row.Name,-24}{row.R10.Average(),8:F3}{row.R20.Average(),8:F3}{row.R40.Average(),8:F3}" +
            $"{row.Ndcg.Average(),10:F3}{row.Rr.Average(),8:F3}{row.Hit,8:P0}" +
            $"{row.Named.Average(),8:P0}{(row.Fans is { Length: > 0 } f ? f.Average() : 0),8:P0}"
            + (negatives ? $"{row.NegativeHit.Average(),8:P0}" : string.Empty)
            + $"{pop,9}{row.Top1k,8:P0}{row.MillisecondsPerRequest,8:F0}");
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
    Console.WriteLine("  top1k   : share of picks inside the 1,000 most popular series. The same warning as");
    Console.WriteLine("            pop, as a share instead of a median.");
    Console.WriteLine("  named   : share of picks carrying a BecauseOfTitle, i.e. ones the UI can label");
    Console.WriteLine("            \"Feels like X\" instead of leaving unattributed. Not a quality measure -");
    Console.WriteLine("            read it against nDCG, never on its own.");
    if (negatives)
    {
        Console.WriteLine("  neg     : share of this reader's held-out DISLIKED titles still in the top 40.");
        Console.WriteLine("            LOWER is better, and it is only a result paired with nDCG holding:");
        Console.WriteLine("            a variant that returns fewer titles of any kind moves both.");
    }

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
        if (negatives)
        {
            Console.WriteLine(
                "  per-request negative hit rate written to .artifacts/eval/rr-<variant>-library-neg.csv");
            Console.WriteLine(
                $"  paired stats: python distribution/eval-compare.py {rows[1].Name} {rows[0].Name} library-neg");
        }

        Console.WriteLine(
            $"  paired stats: python distribution/eval-compare.py {rows[1].Name} {rows[0].Name} {csvSuffix}" +
            $"{(csvMetric == "rr" ? string.Empty : $" {csvMetric}")}");
    }
}

void ReportJudged(List<JudgedRow> judgedRows, SortedSet<(long Seed, long Candidate)> queue)
{
    static double Mean(double[] values)
    {
        var defined = values.Where(v => !double.IsNaN(v)).ToList();
        return defined.Count == 0 ? double.NaN : defined.Average();
    }

    static string Num(double v) => double.IsNaN(v) ? "-" : v.ToString("F3", CultureInfo.InvariantCulture);
    static string Pct(double v) => double.IsNaN(v) ? "-" : v.ToString("P0", CultureInfo.InvariantCulture);

    Console.WriteLine(
        $"{"variant",-24}{"seeds",7}{"judged",8}{$"enjoy@{depth}",10}{$"feel@{depth}",9}{"good",7}{"poor",7}" +
        $"{"nDCG",8}{"thin",7}{"pop",8}{"top1k",7}{"franch",8}");
    Console.WriteLine(new string('-', 110));
    foreach (var row in judgedRows)
    {
        Console.WriteLine(
            $"{row.Name,-24}{row.Enjoy.Count(v => !double.IsNaN(v)),7}{Pct(row.Judged.Average()),8}" +
            $"{Num(Mean(row.Enjoy)),10}{Num(Mean(row.Feel)),9}{Pct(Mean(row.Good)),7}{Pct(Mean(row.Poor)),7}" +
            $"{Num(Mean(row.Ndcg)),8}{Pct(row.Insufficient.Average()),7}" +
            $"{(double.IsNaN(row.Popularity) ? "-" : row.Popularity.ToString("F0", CultureInfo.InvariantCulture)),8}" +
            $"{Pct(row.Top1k.Average()),7}{Pct(row.Franchise.Average()),8}");
    }

    Console.WriteLine();
    Console.WriteLine($"  seeds   : panel seeds with at least one usable judgement in this variant's top {depth}.");
    Console.WriteLine($"  judged  : share of the top {depth} that has a stored judgement. Below 100% the other");
    Console.WriteLine("            columns describe only the judged part; judge the queue and run again.");
    Console.WriteLine("  enjoy   : mean 0 to 3 score for \"a reader who liked the seed would enjoy this\",");
    Console.WriteLine("            over judged picks only. This judge scores it close to feel.");
    Console.WriteLine("  feel    : the same for \"similar story, tone and feel\".");
    Console.WriteLine("  good    : share of judged picks scored 2 or 3 on feel; poor is the share scored 0.");
    Console.WriteLine("  nDCG    : rank-aware, on feel, with unjudged picks removed and the ideal taken");
    Console.WriteLine("            from everything judged for the seed across all variants, so it moves");
    Console.WriteLine("            as more pairs are judged and does not compare across dates.");
    Console.WriteLine("  thin    : share of picks the judge could not score for lack of information.");
    Console.WriteLine($"  pop     : median popularity rank of the top {depth}; top1k is the share inside the top 1,000.");
    Console.WriteLine("  franch  : share of picks in the seed's own same-work component.");

    var bounds = new[] { 1_000L, 5_000L, 20_000L, long.MaxValue };
    var names = new[] { "top 1k", "1k-5k", "5k-20k", "20k+" };
    var bucketOf = new int[requests.Count];
    for (var i = 0; i < requests.Count; i++)
    {
        var rank = index.TryGetRow(requests[i].Seeds[0], out var row) ? index.PopularityAt(row) : VectorIndex.Unknown;
        bucketOf[i] = rank == VectorIndex.Unknown || rank <= 0
            ? bounds.Length - 1
            : Array.FindIndex(bounds, b => rank <= b);
    }

    Console.WriteLine();
    Console.WriteLine($"{"seed popularity",-18}{"variant",-24}{"n",6}{"judged",8}{$"enjoy@{depth}",10}{$"feel@{depth}",9}{"poor",7}");
    Console.WriteLine(new string('-', 82));
    for (var b = 0; b < bounds.Length; b++)
    {
        var members = Enumerable.Range(0, requests.Count).Where(i => bucketOf[i] == b).ToList();
        if (members.Count == 0)
        {
            continue;
        }

        foreach (var row in judgedRows)
        {
            Console.WriteLine(
                $"{names[b],-18}{row.Name,-24}{members.Count,6}{Pct(members.Average(i => row.Judged[i])),8}" +
                $"{Num(Mean(members.Select(i => row.Enjoy[i]).ToArray())),10}" +
                $"{Num(Mean(members.Select(i => row.Feel[i]).ToArray())),9}" +
                $"{Pct(Mean(members.Select(i => row.Poor[i]).ToArray())),7}");
        }
    }

    Console.WriteLine();
    var queuePath = Path.Combine(artifactsDir, "eval", "judge-queue.tsv");
    if (queue.Count == 0)
    {
        Console.WriteLine("  every pick in this run is judged.");
    }
    else
    {
        // Merged with what is already waiting, so two runs over different variants build one queue.
        var waiting = new SortedSet<(long Seed, long Candidate)>(queue);
        if (File.Exists(queuePath))
        {
            foreach (var line in File.ReadLines(queuePath).Skip(1))
            {
                var cells = line.Split('\t');
                if (cells.Length >= 2
                    && long.TryParse(cells[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)
                    && long.TryParse(cells[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var c))
                {
                    waiting.Add((s, c));
                }
            }
        }

        var text = new StringBuilder("seed_id\tcand_id\n");
        foreach (var (s, c) in waiting)
        {
            text.Append(s).Append('\t').Append(c).Append('\n');
        }

        File.WriteAllText(queuePath, text.ToString());
        Console.WriteLine($"  {queue.Count} unjudged pairs from this run; {waiting.Count} waiting in {queuePath}");
        Console.WriteLine("  judge them: python distribution/judge-pairs.py run");
    }

    if (judgedRows.Count > 1)
    {
        Console.WriteLine(
            $"  paired stats: python distribution/eval-compare.py {judgedRows[1].Name} {judgedRows[0].Name} judged-feel feel@{depth}");
        Console.WriteLine($"                (judged enjoy@{depth} for the enjoy scale)");
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

    Console.WriteLine();
    Console.WriteLine($"{"label popularity",-18}{"variant",-24}{"n",6}{$"R@{limit}",8}");
    Console.WriteLine(new string('-', 56));
    var targetNames = new[] { "top 1k", "1k-20k", "20k+" };
    for (var t = 0; t < TargetBounds.Length; t++)
    {
        foreach (var row in rows)
        {
            var defined = row.TargetRecall[t].Where(v => !double.IsNaN(v)).ToList();
            Console.WriteLine(
                $"{targetNames[t],-18}{row.Name,-24}{defined.Count,6}" +
                $"{(defined.Count == 0 ? "-" : defined.Average().ToString("F3", CultureInfo.InvariantCulture)),8}");
        }
    }

    Console.WriteLine();
    Console.WriteLine("  label popularity: recall of the labels in each popularity band, over the requests");
    Console.WriteLine("  that have a label there. A gain confined to the top band is fame, not relevance.");
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
// The builders record every fold but the held-out one, so the same k/n split recorded is exactly
// the other n-1 folds. A list from a different n can still hold graded readers: 1,2,3,4 from
// --fold-out 0/5 trains on readers that --fold-users 0/4 grades.
static bool HoldsOut(HashSet<int> trained, int foldIndex, int foldCount) =>
    trained.SetEquals(Enumerable.Range(0, foldCount).Where(f => f != foldIndex));

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
/// <param name="Avoid">
/// Titles from this reader's SEED slice that they scored well under their own mean, at the same
/// strength the app derives from a low rating. Null unless --negatives.
/// </param>
/// <param name="Negatives">
/// Titles from this reader's HELD-OUT slice that they scored the same way: what a working avoid
/// channel should push out of the top 40. Empty unless --negatives.
/// </param>
file static class RequestKeys
{
    /// <summary>
    /// One stable number per request: the seed id for a single seed, an FNV-1a hash of the seed
    /// list otherwise. A repeat inside one run takes the next free number, so every row keeps a
    /// distinct key and the same request list always produces the same keys.
    /// </summary>
    public static long[] Of(IReadOnlyList<Request> requests)
    {
        var taken = new HashSet<long>();
        var keys = new long[requests.Count];
        for (var i = 0; i < requests.Count; i++)
        {
            var seeds = requests[i].Seeds;
            var key = seeds.Count == 1 ? seeds[0] : Hash(seeds);
            while (!taken.Add(key))
            {
                key++;
            }

            keys[i] = key;
        }

        return keys;
    }

    private static long Hash(IReadOnlyList<long> seeds)
    {
        var hash = 14695981039346656037UL;
        foreach (var seed in seeds.Order())
        {
            for (var shift = 0; shift < 64; shift += 8)
            {
                hash = (hash ^ (byte)(seed >> shift)) * 1099511628211UL;
            }
        }

        return (long)(hash >> 1);
    }
}

file record Request(
    IReadOnlyList<long> Seeds,
    IReadOnlyDictionary<long, double>? Scores,
    Dictionary<long, double> Positives,
    IReadOnlyDictionary<long, double>? Avoid = null,
    IReadOnlyList<long>? Negatives = null);

/// <param name="MillisecondsPerRequest">
/// Mean wall time for one <c>GetSimilarAsync</c>. Reported because <c>maxseedqueries</c> is a pure
/// CPU-cost knob - every extra query is another dot product per catalogue row - and a relevance
/// table with no price on it makes "more is better" look free.
/// </param>
/// <param name="TargetRecall">
/// Recall of the labels in each popularity band of <c>TargetBounds</c>, per request; NaN where a
/// request has no label in the band.
/// </param>
/// <param name="Top1k">Mean share of picks inside the catalogue's 1,000 most popular series.</param>
file record ResultRow(
    string Name, double[] R10, double[] R20, double[] R40, double[] Ndcg, double[] Rr, double[] Named,
    double[] NegativeHit, double[][] TargetRecall, double Top1k,
    double Hit, double Popularity, double MillisecondsPerRequest, FeelRow? Feel, double[]? Fans = null);

/// <param name="Judged">Share of the top picks with a stored judgement, usable or not.</param>
/// <param name="Insufficient">Share the judge declined to score for lack of information.</param>
file record JudgedRow(
    string Name, double[] Enjoy, double[] Feel, double[] Good, double[] Poor, double[] Ndcg,
    double[] Judged, double[] Insufficient, double[] Top1k, double[] Franchise, double Popularity);

file readonly record struct Judgement(int Feel, int Enjoy, bool Insufficient);

/// <param name="Kinds">Share of top picks whose card shows each reason, in <c>engineWhy</c> order.</param>
/// <param name="Claims">Judged feel of each pick against the series its card names, by reason.</param>
/// <param name="FansNearer">For "Fans of" cards: feel against the nearest series minus feel against the named one.</param>
file record ShelfRow(
    string Name, double[] NearFeel, double[] NearGood, double[] NearPoor, double[] Judged, double Top1k,
    Dictionary<string, double> Kinds, Dictionary<string, IReadOnlyList<int>> Claims, List<int> FansNearer,
    List<int> BalancedFans, double MovedFans);


/// <summary>
/// The judgement store written by <c>distribution/judge-pairs.py</c>: one row per (seed, candidate,
/// judge, rubric). Scores from two judges or two rubric versions are never mixed in one number.
/// </summary>
file static class Judgements
{
    public static Dictionary<(long Seed, long Candidate), Judgement> Load(string path, string rubric, ref string? judge)
    {
        var found = new Dictionary<(long, long), Judgement>();
        if (!File.Exists(path))
        {
            return found;
        }

        using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        conn.Open();
        if (judge is null)
        {
            // The judge with the most rows at this rubric, so a bare run reads the main judge and a
            // second judge has to be asked for by name.
            using var pick = conn.CreateCommand();
            pick.CommandText =
                "SELECT judge FROM judgement WHERE rubric = $rubric GROUP BY judge ORDER BY COUNT(*) DESC LIMIT 1";
            pick.Parameters.AddWithValue("$rubric", rubric);
            judge = pick.ExecuteScalar() as string;
            if (judge is null)
            {
                return found;
            }
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT seed_id, cand_id, feel, enjoy, insufficient FROM judgement WHERE judge = $judge AND rubric = $rubric";
        cmd.Parameters.AddWithValue("$judge", judge);
        cmd.Parameters.AddWithValue("$rubric", rubric);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            found[(reader.GetInt64(0), reader.GetInt64(1))] = new Judgement(
                reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                reader.GetInt32(4) != 0);
        }

        return found;
    }
}

/// <summary>
/// Variant syntax: <c>name:key=value,key=value</c>.
///
/// <para>
/// Prefixes, tested in this order because the namespaces would otherwise overlap: <c>coread*</c> is
/// <see cref="CoReadTuning"/>, <c>graph*</c> is <see cref="RecoGraphTuning"/>, <c>w*</c> is
/// <see cref="EmbeddingMath.Weights"/> (so <c>wcoread</c> is the channel's coefficient in the hybrid
/// score and <c>coreadweight</c> is the scorer's own — they are different numbers and both matter).
/// <c>diversity</c> and <c>seedweights</c> stand alone, as do the <see cref="RecommenderTuning"/>
/// keys: <c>cosinefloor</c>, <c>crowdbypassesfloor</c>, <c>genrerawsum</c>, <c>maxseedqueries</c>,
/// <c>avoidfloor</c>, <c>maxavoidqueries</c>, <c>avoidblend</c> (<c>semantic</c> / <c>tag</c> /
/// <c>product</c> / <c>gate</c>), <c>avoidtagminsupport</c>, <c>avoidtagmargin</c>,
/// <c>avoidtagfloor</c> (<c>gate</c> only), <c>avoidweight</c> (an alias for <c>wavoid</c>),
/// <c>avoidneutralize</c> (<c>none</c> / <c>relative</c> / <c>popresidual</c> /
/// <c>standardized</c>, what the semantic half measures), <c>avoidrelmargin</c>
/// (<c>relative</c> only), <c>avoidpopbuckets</c> and <c>avoidpopminbucket</c>
/// (<c>popresidual</c> only), <c>avoidzfloor</c> and <c>avoidzspan</c> (<c>standardized</c> only)
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
/// <c>easepath</c> names an EASE table (<c>distribution/build-ease-table.py</c>) and <c>easeweight</c>
/// sets its coefficient; <c>easenegative</c>, <c>easeminseeds</c>, <c>easemaxinjected</c> and <c>easemininjected</c> are
/// the rest of <see cref="EaseTuning"/>. The channel ships at weight 4 on personal recommendations,
/// so every variant runs it from the installed table (or <c>--ease</c>) unless it says otherwise;
/// <c>noease</c> is the baseline it has to be read against, and the three <c>rail*</c> shorthands
/// never run it, because <c>SimilarSeriesService</c> passes it off.
/// </para>
///
/// <para>
/// Shorthand names: <c>default</c> is what ships; <c>nograph</c>, <c>nocoread</c> and <c>nocrowd</c>
/// switch one or both crowd channels off, which are the baselines those features have to be read
/// against; <c>noavoid</c> is the same for the avoid channel and <c>noease</c> for the EASE
/// channel; <c>rail</c> is the series-page rail as
/// <c>SimilarSeriesService</c> ships it: the vote graph at
/// <c>RecoGraphTuning.SingleSeedWeight</c>, no co-read, taste vectors on, diversity 0.15;
/// <c>railtaste</c> is that without the votes and <c>railcontent</c> without the taste vectors
/// either, which are the two rails that shipped before it.
/// </para>
/// </summary>
file static class Variants
{
    /// <summary>Whether a shorthand runs the EASE channel before any override.</summary>
    public static bool RunsEase(string name) =>
        name.Split(':', 2)[0].ToLowerInvariant() is not ("noease" or "rail" or "railtaste" or "railcontent");

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
        // What RecommendationService asks for. The path is filled in from the installed table once
        // the config dir is known; `noease` and the rail shorthands start with the channel off.
        var ease = RunsEase(lower) ? EaseTuning.Default : EaseTuning.Default with { Weight = 0 };
        string? easePath = null;

        // The baseline the avoid channel has to be read against, the same way `nocrowd` and
        // `notaste` are for the other channels. A bare `avoid` would be a variant NAME rather than
        // an override, which is the trap this file's header already records once.
        if (lower == "noavoid")
        {
            weights = new EmbeddingMath.Weights() with { Avoid = 0 };
        }

        // What SimilarSeriesService asks for: the vote graph at its single-seed weight, co-read
        // off, the taste vectors on and a small MMR nudge. It used to stand for a reduced weight
        // vector that service dropped long ago, so the shipped rail went ungraded while a row
        // called `rail` sat in the table. `railtaste` and `railcontent` are the rail as it shipped
        // before: without the votes, and without the taste vectors either.
        if (lower is "rail" or "railtaste" or "railcontent")
        {
            coGraph = lower == "rail";
            coRead = false;
            diversity = 0.15;
            if (coGraph)
            {
                graph = graph with { Weight = RecoGraphTuning.SingleSeedWeight };
            }

            if (lower == "railcontent")
            {
                taste = TasteVectorTuning.Default with { Weight = 0 };
            }
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
            else if (key == "tasteusersolve")
            {
                taste = taste with { UserSolve = bool.Parse(value) };
            }
            else if (key == "tastesolvelambda")
            {
                taste = taste with { UserSolveLambda = double.Parse(value, CultureInfo.InvariantCulture) };
            }
            else if (key == "tastesolvealpha")
            {
                taste = taste with { UserSolveAlpha = double.Parse(value, CultureInfo.InvariantCulture) };
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
            else if (key == "tagformatboost")
            {
                recommender = recommender with
                {
                    TagFormatCategoryBoost = double.Parse(value, CultureInfo.InvariantCulture),
                };
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
            else if (key == "avoidfloor")
            {
                recommender = recommender with
                {
                    AvoidFloor = double.Parse(value, CultureInfo.InvariantCulture),
                };
            }
            else if (key == "maxavoidqueries")
            {
                recommender = recommender with
                {
                    MaxAvoidQueries = int.Parse(value, CultureInfo.InvariantCulture),
                };
            }
            else if (key == "avoidblend")
            {
                recommender = recommender with
                {
                    AvoidBlend = Enum.Parse<AvoidBlend>(value, ignoreCase: true),
                };
            }
            else if (key == "avoidtagminsupport")
            {
                recommender = recommender with
                {
                    AvoidTagMinSupport = int.Parse(value, CultureInfo.InvariantCulture),
                };
            }
            else if (key == "avoidtagmargin")
            {
                recommender = recommender with
                {
                    AvoidTagMargin = double.Parse(value, CultureInfo.InvariantCulture),
                };
            }
            else if (key == "avoidtagfloor")
            {
                recommender = recommender with
                {
                    AvoidTagFloor = double.Parse(value, CultureInfo.InvariantCulture),
                };
            }
            else if (key == "avoidneutralize")
            {
                recommender = recommender with
                {
                    AvoidNeutralize = Enum.Parse<AvoidNeutralize>(value, ignoreCase: true),
                };
            }
            else if (key == "avoidrelmargin")
            {
                recommender = recommender with
                {
                    AvoidRelativeMargin = double.Parse(value, CultureInfo.InvariantCulture),
                };
            }
            else if (key == "avoidpopbuckets")
            {
                recommender = recommender with
                {
                    AvoidPopBuckets = int.Parse(value, CultureInfo.InvariantCulture),
                };
            }
            else if (key == "avoidpopminbucket")
            {
                recommender = recommender with
                {
                    AvoidPopMinBucket = int.Parse(value, CultureInfo.InvariantCulture),
                };
            }
            else if (key == "avoidzfloor")
            {
                recommender = recommender with
                {
                    AvoidZFloor = double.Parse(value, CultureInfo.InvariantCulture),
                };
            }
            else if (key == "avoidzspan")
            {
                recommender = recommender with
                {
                    AvoidZSpan = double.Parse(value, CultureInfo.InvariantCulture),
                };
            }
            // Spelled out rather than left to the `w*` prefix, because every other key in this
            // family is `avoid*` and a sweep that reads `avoidblend=product,wavoid=6` invites the
            // reader to check which of the two is the coefficient. Same number as `wavoid`.
            else if (key == "avoidweight")
            {
                weights = ApplyWeight(weights ?? new EmbeddingMath.Weights(), "avoid", value);
            }
            else if (key == "attributionscale")
            {
                recommender = recommender with
                {
                    AttributionScale = Enum.Parse<AttributionScale>(value, ignoreCase: true),
                };
            }
            // The EASE channel: `easepath` names the table and `easeweight` switches it on.
            else if (key == "easepath")
            {
                easePath = value;
            }
            else if (key == "easeweight")
            {
                ease = ease with { Weight = double.Parse(value, CultureInfo.InvariantCulture) };
            }
            else if (key == "easenegative")
            {
                ease = ease with { KeepNegative = bool.Parse(value) };
            }
            else if (key == "easemaxinjected")
            {
                ease = ease with { MaxInjected = int.Parse(value, CultureInfo.InvariantCulture) };
            }
            else if (key == "easeminseeds")
            {
                ease = ease with { MinSeeds = int.Parse(value, CultureInfo.InvariantCulture) };
            }
            else if (key == "easemininjected")
            {
                ease = ease with { MinInjectedScore = double.Parse(value, CultureInfo.InvariantCulture) };
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
            taste, ease, easePath);
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
            // The avoid channel's coefficient. Unlike graph and coread it is never set for you when
            // the channel has something to say: it ships at 0 and only a sweep turns it on.
            "avoid" => w with { Avoid = d },
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
    TasteVectorTuning Taste,
    EaseTuning Ease,
    string? EasePath);

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
