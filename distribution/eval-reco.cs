#:project ../src/Maki.Metadata/Maki.Metadata.csproj

// Measures what BEHAVIOURAL SEED WEIGHTING does to recommendations — the weights TasteTuning derives
// from reading history and hands to SemanticRecommender as seedWeights.
// Run:
//   dotnet run distribution/eval-reco.cs
//   dotnet run distribution/eval-reco.cs -- spread uniform default
//   dotnet run distribution/eval-reco.cs -- loo uniform default
//   dotnet run distribution/eval-reco.cs -- spread "deep:depthweight=0.7,ratioweight=0.3"
//   dotnet run distribution/eval-reco.cs -- spread nocoread default "hot:coreadmininjectedscore=0.5"
//
// WHY THIS EXISTS, NEXT TO eval-search.cs
// That tool scores free-text search against a labelled query set. Recommendations have no such set:
// nobody has written down what a given reading history *should* return, and no labelled corpus of
// reading behaviour ships with this repo. So this tool measures two different things, and it is worth
// being clear about which claim each one supports.
//
//   spread — the DEFENSIBLE one, and the default. No labels needed. It builds synthetic profiles that
//     are deliberately the worst case for this feature (a tight cluster of series read to the end, a
//     scattering of ones barely touched) and reports how concentrated the resulting pool is: distinct
//     genres, authors and tags across the picks, and the mean pairwise cosine between them. That is
//     the over-fit risk stated head-on — a weighting that collapses a narrow library into near-copies
//     of itself shows up as cohesion rising and distinct-genre count falling against `uniform`.
//
//   loo — leave-one-out over the INSTALLED reading history in maki.db. Hold out one series the user
//     finished, seed from the rest, report where the held-out series lands. It measures the thing we
//     actually care about, on real behaviour, and its n is one person's library. Read it as a
//     direction and a regression tripwire. It cannot justify moving a default on its own, and this
//     tool says so in its own output rather than leaving the reader to remember it.
//
// WHAT IT RUNS AGAINST
// The INSTALLED index, dump and database under MAKI_CONFIG_DIR (or %APPDATA%\Maki), same as
// eval-search.cs. No embedding model is loaded: seeds are ids, so nothing here embeds text.
//
// GOTCHA
// A file-based app caches its build under %TEMP%\dotnet\runfile. Delete it when comparing an edited
// default against `default`, or you will be scoring the previous build.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Maki.Core.Configuration;
using Maki.Core.Recommendations;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Maki.Metadata.CoRead;
using Maki.Metadata.RecoGraph;
using Microsoft.Extensions.Logging;

// VectorIndexCache reads the dump's genre and author JSON arrays reflectively; a file-based app
// otherwise builds with reflection-free System.Text.Json and the index build throws. Same reason
// eval-search.cs sets this, and for the same reason it has to happen first.
AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", true);

// Every number this tool prints is a measurement to be compared against another run, possibly on
// another machine. Pin the culture so a decimal comma never makes two runs look different.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

var configDir = Environment.GetEnvironmentVariable("MAKI_CONFIG_DIR")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Maki");

var dumpPath = Path.Combine(configDir, "mangabaka.db");
var vectorPath = Path.Combine(configDir, "embeddings.db");
var dbPath = Path.Combine(configDir, "maki.db");

foreach (var (label, path) in new[] { ("dump", dumpPath), ("vector index", vectorPath) })
{
    if (!File.Exists(path))
    {
        Console.WriteLine($"error: no {label} at {path}");
        return 2;
    }
}

var mode = "spread";
var limit = 40;
var profileCount = 24;
var librarySize = 60;
var coreSize = 8;
var diversity = 0.0;
var minChapters = 5;
var rngSeed = 20260824;
var useRealProfile = false;
var userId = (int?)null;
var variantArgs = new List<string>();

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "spread" or "loo":
            mode = args[i];
            break;
        case "--limit":
            limit = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--profiles":
            profileCount = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--library":
            librarySize = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--core":
            coreSize = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--diversity":
            diversity = double.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--min-chapters":
            minChapters = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        // Measures the library this install actually holds instead of synthetic ones. One profile, so
        // the averages are just that profile's numbers — but they are the numbers this user would see.
        case "--real":
            useRealProfile = true;
            break;
        case "--seed":
            rngSeed = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--user":
            userId = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        default:
            variantArgs.Add(args[i]);
            break;
    }
}

var variants = variantArgs.Count > 0
    ? variantArgs.Select(Variants.Parse).ToList()
    : [Variants.Parse("uniform"), Variants.Parse("default")];

var modelKind = Settings.ReadEmbeddingModel(dbPath) ?? "base";
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
Console.WriteLine($"limit    : top {limit} per profile, diversity {diversity:F2}");
Console.WriteLine();

var store = new EmbeddingStore(options);
// One cache across every variant: tuning changes the seed weights, never the index itself, and a
// rebuild per variant would dominate the run.
var cache = new VectorIndexCache(options, dumpOptions, new ConsoleLogger<VectorIndexCache>());
// The co-recommendation graph, if this install has one. Absent is normal and simply leaves the
// channel contributing nothing, so the harness runs either way.
var graphOptions = new RecoGraphOptions(Path.Combine(configDir, "reco-edges.db"), Path.Combine(configDir, "cache"));
var graphCache = new RecoGraphCache(graphOptions, new ConsoleLogger<RecoGraphCache>());
// The co-read graph, likewise optional. Two independent artifacts: an install can easily have one
// and not the other, and a sweep of one has to keep the other fixed to mean anything.
var coReadOptions = new CoReadOptions(Path.Combine(configDir, "coread-edges.db"), Path.Combine(configDir, "cache"));
var coReadCache = new CoReadCache(coReadOptions, new ConsoleLogger<CoReadCache>());
// A recommender per distinct pair of graph tunings. They are near-free to construct — the expensive
// state (the vector index, both graphs) lives in the caches and is shared across all of them.
var recommenders = new Dictionary<(RecoGraphTuning, CoReadTuning), SemanticRecommender>();
SemanticRecommender RecommenderFor(RecoGraphTuning graphTuning, CoReadTuning coReadTuning)
{
    if (!recommenders.TryGetValue((graphTuning, coReadTuning), out var found))
    {
        found = new SemanticRecommender(
            options, dumpOptions, store, cache, graphCache, graphTuning, coReadCache, coReadTuning,
            new ConsoleLogger<SemanticRecommender>());
        recommenders[(graphTuning, coReadTuning)] = found;
    }

    return found;
}

var recommender = RecommenderFor(RecoGraphTuning.Default, CoReadTuning.Default);

var warm = Stopwatch.StartNew();
if (await cache.GetAsync() is not { } index)
{
    Console.WriteLine("error: the vector index is empty — nothing embedded yet.");
    return 1;
}

Console.WriteLine($"index    : {index.Count} series, built in {warm.Elapsed.TotalSeconds:F1}s");
Console.WriteLine();

var today = DateOnly.FromDateTime(DateTime.UtcNow);

return mode switch
{
    "loo" => await RunLeaveOneOut(),
    _ => await RunSpread(),
};

// ---------------------------------------------------------------------------------------------
// spread: synthetic narrow-and-deep profiles, no labels, measuring pool concentration.
// ---------------------------------------------------------------------------------------------
async Task<int> RunSpread()
{
    List<Profile> profiles;
    if (useRealProfile)
    {
        if (!File.Exists(dbPath))
        {
            Console.WriteLine($"error: no database at {dbPath} — --real needs an installed library.");
            return 2;
        }

        var chosen = userId ?? History.BusiestUser(dbPath);
        if (chosen is null)
        {
            Console.WriteLine("error: no user in the database has any completed reading.");
            return 1;
        }

        var real = History.Load(dbPath, chosen.Value);
        var known = real.Entries.Where(e => index.TryGetRow(e.MangaBakaId, out _)).ToList();
        if (known.Count == 0)
        {
            Console.WriteLine("error: none of this user's read series are in the vector index.");
            return 1;
        }

        profiles = [new Profile($"user-{chosen}", known)];
        Console.WriteLine($"profiles : the installed library for user {chosen}");
        Console.WriteLine(
            $"  {known.Count} series with reading history and a vector " +
            $"({real.Entries.Count - known.Count} read but not in the index, {WeightedCount(known)} weighted off neutral)");
        Console.WriteLine();
    }
    else
    {
        profiles = Synthetic.Build(index, store, profileCount, librarySize, coreSize, rngSeed);
        if (profiles.Count == 0)
        {
            Console.WriteLine("error: could not build synthetic profiles — the index has too few rows.");
            return 1;
        }

        Console.WriteLine(
            $"profiles : {profiles.Count} synthetic, {librarySize} titles each " +
            $"({coreSize} read to the end, the rest barely touched)");
        Console.WriteLine(
            "  Deliberately the worst case for this feature: if behavioural weighting over-fits a narrow");
        Console.WriteLine(
            "  library, cohesion rises and the distinct-* counts fall against `uniform`.");
        Console.WriteLine();
    }

    int WeightedCount(IEnumerable<ProfileEntry> entries) => entries.Count(e =>
        Math.Abs(TasteWeights.Weight(e.Signal, today, TasteTuning.Default) - TasteWeights.Neutral) > 1e-9);

    var rows = new List<SpreadRow>();
    List<HashSet<long>>? baselinePicks = null;

    foreach (var variant in variants)
    {
        var clock = Stopwatch.StartNew();
        var picks = new List<HashSet<long>>();
        var genres = new List<double>();
        var authors = new List<double>();
        var tags = new List<double>();
        var cohesion = new List<double>();
        var overlap = new List<double>();
        // What share of each pool each crowd channel vouched for. Without these, "the channel
        // changed nothing" and "the channel fired and made things worse" read identically in the
        // table below. Two columns, not one: the graphs disagree on most pairs they both cover, so
        // a single number would hide which of them is actually carrying a run.
        var coRec = new List<double>();
        var coRead = new List<double>();
        // Median popularity rank of the picks. Added because the columns above could not see the
        // failure that matters most for a collaborative channel: a pool of famous titles spans many
        // genres and tags and scores WELL on every diversity metric here while being a popularity
        // chart. Measured on a real library, the co-read channel pushed the obscure picks down and
        // mega-titles up while `tags` rose and `cohesion` fell - the table said it was improving.
        var popularity = new List<double>();
        var weighted = 0;
        var minWeight = double.NaN;
        var maxWeight = double.NaN;

        for (var i = 0; i < profiles.Count; i++)
        {
            var applied = SeedWeights(profiles[i], variant.Tuning);
            if (i == 0 && applied.Count > 0)
            {
                weighted = applied.Count;
                minWeight = applied.Values.Min();
                maxWeight = applied.Values.Max();
            }

            var result = await Recommend(profiles[i], variant, exclude: null);
            var ids = result.Select(r => long.Parse(r.ProviderId, CultureInfo.InvariantCulture)).ToHashSet();
            picks.Add(ids);

            coRec.Add(result.Count == 0 ? 0 : (double)result.Count(r => r.CoRecommended) / result.Count);
            coRead.Add(result.Count == 0 ? 0 : (double)result.Count(r => r.CoRead) / result.Count);

            var ranks = ids
                .Where(id => index.TryGetRow(id, out _))
                .Select(id => { index.TryGetRow(id, out var row); return index.PopularityAt(row); })
                .Where(r => r != VectorIndex.Unknown && r > 0)
                .Order()
                .ToList();
            if (ranks.Count > 0)
            {
                popularity.Add(ranks[ranks.Count / 2]);
            }

            var metrics = Spread.Measure(index, ids);
            genres.Add(metrics.Genres);
            authors.Add(metrics.Authors);
            tags.Add(metrics.Tags);
            cohesion.Add(metrics.Cohesion);

            if (baselinePicks is not null)
            {
                overlap.Add(Spread.Jaccard(baselinePicks[i], ids));
            }

            Console.Write($"\r  {variant.Name}: {i + 1}/{profiles.Count} profiles, {clock.Elapsed.TotalSeconds:F0}s   ");
        }

        Console.Write("\r".PadRight(70) + "\r");
        baselinePicks ??= picks;
        rows.Add(new SpreadRow(
            variant.Name, Mean(genres), Mean(authors), Mean(tags), Mean(cohesion),
            overlap.Count > 0 ? Mean(overlap) : double.NaN,
            weighted, minWeight, maxWeight, Mean(coRec), Mean(coRead), Mean(popularity)));
    }

    Console.WriteLine(
        $"{"variant",-22} {"weights",16} {"genres",8} {"authors",8} {"tags",8} {"cohesion",9} " +
        $"{"overlap",8} {"co-rec",7} {"co-read",8} {"pop",8}");
    Console.WriteLine(new string('-', 113));
    foreach (var row in rows)
    {
        var overlapText = double.IsNaN(row.Overlap) ? "-" : row.Overlap.ToString("F3", CultureInfo.InvariantCulture);
        var weightText = row.Weighted == 0
            ? "none"
            : $"{row.Weighted} in {row.MinWeight:F1}-{row.MaxWeight:F1}";
        Console.WriteLine(
            $"{row.Name,-22} {weightText,16} {row.Genres,8:F2} {row.Authors,8:F2} {row.Tags,8:F2} " +
            $"{row.Cohesion,9:F4} {overlapText,8} {row.CoRec,7:P0} {row.CoRead,8:P0} {row.Popularity,8:F0}");
    }

    Console.WriteLine();
    Console.WriteLine("  weights             : how many seeds this tuning moved off neutral, and the band they span.");
    Console.WriteLine("  genres/authors/tags : distinct values across the top picks; higher is broader.");
    Console.WriteLine("  cohesion            : mean pairwise cosine between picks; higher is more same-y.");
    Console.WriteLine($"  overlap             : Jaccard against `{rows[0].Name}` picks; 1.00 means nothing moved.");
    Console.WriteLine("  co-rec / co-read    : share of picks the vote graph / the reading graph vouched for.");
    Console.WriteLine("  pop                 : median popularity rank of the picks; LOWER means more famous.");
    Console.WriteLine("                        The diversity columns cannot see this - a pool of mega-titles");
    Console.WriteLine("                        spans plenty of genres and tags. Read them together.");
    return 0;
}

// ---------------------------------------------------------------------------------------------
// loo: leave-one-out over the reading history this install actually holds.
// ---------------------------------------------------------------------------------------------
async Task<int> RunLeaveOneOut()
{
    if (!File.Exists(dbPath))
    {
        Console.WriteLine($"error: no database at {dbPath} — loo needs real reading history.");
        return 2;
    }

    var chosenUser = userId ?? History.BusiestUser(dbPath);
    if (chosenUser is null)
    {
        Console.WriteLine("error: no user in the database has any completed reading.");
        return 1;
    }

    var profile = History.Load(dbPath, chosenUser.Value);
    var holdouts = profile.Entries
        .Where(e => e.Signal.Completed >= minChapters && index.TryGetRow(e.MangaBakaId, out _))
        .OrderBy(e => e.MangaBakaId)
        .ToList();

    var withHistory = profile.Entries.Count(e => e.Signal.Completed > 0);
    Console.WriteLine(
        $"user     : {chosenUser} ({profile.Entries.Count} library series, {withHistory} with reading history)");
    Console.WriteLine($"holdouts : {holdouts.Count} at >= {minChapters} completed chapters");
    Console.WriteLine();

    if (holdouts.Count < 2)
    {
        Console.WriteLine("Not enough history to leave one out: this needs at least two series read past the");
        Console.WriteLine($"--min-chapters floor ({minChapters}) that also exist in the vector index.");
        Console.WriteLine("Lower the floor with --min-chapters, or run `spread`, which needs no history.");
        return 1;
    }

    Console.WriteLine("  n is one person's library. Read this as a direction and a regression tripwire, not");
    Console.WriteLine("  as a population result — it cannot justify moving a shipped default on its own.");
    Console.WriteLine();

    Directory.CreateDirectory(Path.Combine(".artifacts", "eval"));
    var rows = new List<LooRow>();

    foreach (var variant in variants)
    {
        var reciprocal = new double[holdouts.Count];
        var clock = Stopwatch.StartNew();

        for (var i = 0; i < holdouts.Count; i++)
        {
            var held = holdouts[i];
            var seeded = new Profile(profile.Name, profile.Entries.Where(e => e.MangaBakaId != held.MangaBakaId).ToList());

            // Everything the profile still holds stays excluded, exactly as the live path excludes the
            // library — except the held-out series, which has to be reachable to be ranked.
            var exclude = seeded.Entries.Select(e => e.MangaBakaId).ToHashSet();
            var result = await Recommend(seeded, variant, exclude);

            var rank = 0;
            for (var r = 0; r < result.Count; r++)
            {
                if (long.Parse(result[r].ProviderId, CultureInfo.InvariantCulture) == held.MangaBakaId)
                {
                    rank = r + 1;
                    break;
                }
            }

            reciprocal[i] = rank > 0 ? 1.0 / rank : 0;
            Console.Write($"\r  {variant.Name}: {i + 1}/{holdouts.Count} holdouts, {clock.Elapsed.TotalSeconds:F0}s   ");
        }

        Console.Write("\r".PadRight(70) + "\r");

        // Named rr-<variant>-reco.csv, not rr-reco-<variant>.csv: eval-compare.py builds its paths as
        // rr-<candidate>-<mode>.csv, so this layout is what lets it read these files unmodified.
        var csv = Path.Combine(".artifacts", "eval", $"rr-{variant.Name}-reco.csv");
        // Headerless, matching what eval-embeddings.cs writes — eval-compare.py int-parses the first
        // column of every line, so a header row makes it throw.
        var text = new StringBuilder();
        for (var i = 0; i < holdouts.Count; i++)
        {
            text.Append(holdouts[i].MangaBakaId).Append(',')
                .Append(reciprocal[i].ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        }

        File.WriteAllText(csv, text.ToString());

        rows.Add(new LooRow(
            variant.Name,
            reciprocal.Average(),
            reciprocal.Count(r => r >= 1.0 / 10) / (double)reciprocal.Length,
            reciprocal.Count(r => r > 0) / (double)reciprocal.Length));
    }

    Console.WriteLine($"{"variant",-22} {"MRR",8} {"R@10",8} {$"R@{limit}",8}");
    Console.WriteLine(new string('-', 50));
    foreach (var row in rows)
    {
        Console.WriteLine($"{row.Name,-22} {row.Mrr,8:F3} {row.RecallAt10,8:F3} {row.RecallAtLimit,8:F3}");
    }

    Console.WriteLine();
    if (rows.All(r => r.RecallAtLimit <= 0))
    {
        // A table of zeros is not a tie, and printing it without saying so invites reading "no
        // difference" into what is really "no measurement". Holding one series out of a library and
        // asking for it back inside the top few dozen of ~126k candidates is a needle-in-a-haystack
        // task, and a library this size does not put enough of a haystack behind it.
        Console.WriteLine("  NOT A RESULT: no variant placed a single held-out series in the top");
        Console.WriteLine($"  {limit}, so these rows compare nothing. Raise --limit to widen the window,");
        Console.WriteLine("  or use `spread --real`, which measures this library without needing a hit.");
        Console.WriteLine();
    }

    Console.WriteLine("  per-holdout reciprocal ranks written to .artifacts/eval/rr-<variant>-reco.csv");
    Console.WriteLine(
        $"  paired stats: python distribution/eval-compare.py {string.Join(' ', rows.Take(2).Select(r => r.Name))} reco");
    return 0;
}

Dictionary<long, double> SeedWeights(Profile profile, TasteTuning tuning)
{
    var weights = new Dictionary<long, double>();
    foreach (var entry in profile.Entries)
    {
        var weight = TasteWeights.Weight(entry.Signal, today, tuning);
        if (Math.Abs(weight - TasteWeights.Neutral) > 1e-9)
        {
            weights[entry.MangaBakaId] = weight;
        }
    }

    return weights;
}

async Task<IReadOnlyList<MangaBakaRecommendation>> Recommend(
    Profile profile, Variant variant, HashSet<long>? exclude)
{
    var seeds = profile.Entries.Select(e => e.MangaBakaId).ToList();
    var weights = SeedWeights(profile, variant.Tuning);

    return await RecommenderFor(variant.Graph, variant.CoReadTuning).GetSimilarAsync(
        seeds,
        exclude ?? seeds.ToHashSet(),
        limit,
        RecommendationFilters.None,
        obscurity: 0,
        seedWeights: weights.Count > 0 ? weights : null,
        diversity: diversity,
        coGraph: variant.CoGraph,
        coRead: variant.CoRead);
}

static double Mean(IReadOnlyCollection<double> values) => values.Count == 0 ? 0 : values.Average();

file record ProfileEntry(long MangaBakaId, SeriesReadSignal Signal);

file record Profile(string Name, IReadOnlyList<ProfileEntry> Entries);

file record SpreadRow(string Name, double Genres, double Authors, double Tags, double Cohesion, double Overlap, int Weighted, double MinWeight, double MaxWeight, double CoRec, double CoRead, double Popularity);

file record LooRow(string Name, double Mrr, double RecallAt10, double RecallAtLimit);

/// <summary>
/// Builds the profile shape this feature is most likely to be wrong about: a handful of series read
/// to the very end that all sit close together in embedding space, plus a long tail the reader opened
/// once and left. A uniform seeding averages the two into something bland; a behavioural one pushes
/// hard toward the core, and the question `spread` answers is how hard is too hard.
/// </summary>
file static class Synthetic
{
    public static List<Profile> Build(
        VectorIndex index, EmbeddingStore store, int count, int librarySize, int coreSize, int seed)
    {
        var rng = new Random(seed);
        var profiles = new List<Profile>();

        // Anchor on series anyone has heard of. A random row from the long tail is usually a
        // one-volume doujin whose neighbours say nothing about taste.
        var popular = Enumerable.Range(0, index.Count)
            .Where(r => index.PopularityAt(r) is > 0 and <= 20_000)
            .ToArray();
        if (popular.Length < coreSize * 2)
        {
            return profiles;
        }

        var plan = index.Plan(RecommendationFilters.None);

        for (var p = 0; p < count; p++)
        {
            var anchor = index.IdAt(popular[rng.Next(popular.Length)]);
            if (store.GetVector(anchor) is not { } vector)
            {
                continue;
            }

            var core = index.Search(vector, plan, coreSize, CancellationToken.None)
                .Select(hit => index.IdAt(hit.Row))
                .ToList();
            if (core.Count == 0)
            {
                continue;
            }

            var entries = new List<ProfileEntry>();
            var seen = new HashSet<long>();
            var now = DateTime.UtcNow;

            foreach (var id in core)
            {
                if (!seen.Add(id))
                {
                    continue;
                }

                var chapters = 40 + rng.Next(160);
                entries.Add(new ProfileEntry(id, new SeriesReadSignal(
                    Completed: chapters,
                    Downloaded: chapters,
                    Seconds: chapters * 600L,
                    LastReadAt: now.AddDays(-rng.Next(60)))));
            }

            while (entries.Count < librarySize)
            {
                var id = index.IdAt(popular[rng.Next(popular.Length)]);
                if (!seen.Add(id))
                {
                    continue;
                }

                var chapters = 1 + rng.Next(3);
                entries.Add(new ProfileEntry(id, new SeriesReadSignal(
                    Completed: chapters,
                    Downloaded: 40 + rng.Next(160),
                    Seconds: chapters * 240L,
                    LastReadAt: now.AddDays(-rng.Next(700)))));
            }

            profiles.Add(new Profile($"synthetic-{p}", entries));
        }

        return profiles;
    }
}

/// <summary>
/// How concentrated a set of picks is. All four numbers come off the vector index, so this needs no
/// dump query and no labels — which is the whole reason `spread` is the mode that can be trusted on
/// any install.
/// </summary>
file static class Spread
{
    public static (double Genres, double Authors, double Tags, double Cohesion) Measure(
        VectorIndex index, IReadOnlyCollection<long> ids)
    {
        var rows = new List<int>(ids.Count);
        foreach (var id in ids)
        {
            if (index.TryGetRow(id, out var row))
            {
                rows.Add(row);
            }
        }

        if (rows.Count == 0)
        {
            return (0, 0, 0, 0);
        }

        var genres = new HashSet<int>();
        var authors = new HashSet<int>();
        var tags = new HashSet<int>();
        foreach (var row in rows)
        {
            genres.UnionWith(index.GenresAt(row));
            authors.UnionWith(index.AuthorsAt(row));
            foreach (var (tagId, _) in TagMath.Unpack(index.TagsAt(row)))
            {
                tags.Add(tagId);
            }
        }

        var pairs = 0;
        var cosineSum = 0.0;
        for (var i = 0; i < rows.Count; i++)
        {
            for (var j = i + 1; j < rows.Count; j++)
            {
                cosineSum += index.CosineBetween(rows[i], rows[j]);
                pairs++;
            }
        }

        return (genres.Count, authors.Count, tags.Count, pairs > 0 ? cosineSum / pairs : 0);
    }

    public static double Jaccard(IReadOnlyCollection<long> a, IReadOnlyCollection<long> b)
    {
        if (a.Count == 0 && b.Count == 0)
        {
            return 1;
        }

        var union = new HashSet<long>(a);
        union.UnionWith(b);
        var intersection = a.Count(b.Contains);
        return union.Count == 0 ? 1 : (double)intersection / union.Count;
    }
}

/// <summary>
/// Reads one user's reading history straight out of maki.db. Raw SQL rather than EF so this tool does
/// not have to drag the data layer in; the conditions are the ones ReadCounts and
/// BehavioralTasteService apply, written out here because there is no shared query to call.
/// </summary>
file static class History
{
    public static int? BusiestUser(string dbPath)
    {
        using var conn = Open(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT p.UserId
            FROM ChapterProgress p
            JOIN Chapters c ON c.Id = p.ChapterId AND c.ChapterFileId IS NOT NULL
            WHERE p.Completed = 1
            GROUP BY p.UserId
            ORDER BY COUNT(*) DESC
            LIMIT 1
            """;
        return cmd.ExecuteScalar() is long id ? (int)id : null;
    }

    public static Profile Load(string dbPath, int userId)
    {
        using var conn = Open(dbPath);

        var downloaded = new Dictionary<long, int>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT SeriesId, COUNT(*) FROM Chapters WHERE ChapterFileId IS NOT NULL GROUP BY SeriesId";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                downloaded[reader.GetInt64(0)] = reader.GetInt32(1);
            }
        }

        // Every library series with a catalogue id is a seed, exactly as RecommendationService seeds
        // the whole library — not just the ones that were read. This matters: the unread majority
        // sits at neutral weight and dilutes the weighted minority, and a profile made only of read
        // series would overstate how much the weighting can move the centroid.
        var entries = new List<ProfileEntry>();
        var read = new HashSet<long>();
        using (var cmd = conn.CreateCommand())
        {
            // Incognito = 2 is IncognitoMode.Full, excluded exactly as BehavioralTasteService excludes
            // it. ScrobbleOnly (1) stays, for the same reason it stays there.
            cmd.CommandText =
                """
                SELECT s.Id, s.MangaBakaId, COUNT(*), SUM(p.ReadSeconds), MAX(p.UpdatedAt)
                FROM ChapterProgress p
                JOIN Chapters c ON c.Id = p.ChapterId AND c.ChapterFileId IS NOT NULL
                JOIN Series s ON s.Id = p.SeriesId
                WHERE p.UserId = $user
                  AND p.Completed = 1
                  AND s.MangaBakaId IS NOT NULL
                  AND s.Incognito <> 2
                GROUP BY s.Id
                """;
            cmd.Parameters.AddWithValue("$user", userId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var seriesId = reader.GetInt64(0);
                var lastRead = reader.IsDBNull(4)
                    ? (DateTime?)null
                    : DateTime.TryParse(
                        reader.GetString(4), CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                        ? parsed
                        : null;

                var mangaBakaId = reader.GetInt64(1);
                read.Add(mangaBakaId);
                entries.Add(new ProfileEntry(mangaBakaId, new SeriesReadSignal(
                    Completed: reader.GetInt32(2),
                    Downloaded: downloaded.GetValueOrDefault(seriesId),
                    Seconds: reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
                    LastReadAt: lastRead)));
            }
        }

        // The rest of the library, unread and therefore unweighted. Incognito is not filtered here:
        // a fully-incognito series is still a seed in production, it just never earns a weight, and
        // the query above is where that exclusion already happened.
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT MangaBakaId FROM Series WHERE MangaBakaId IS NOT NULL";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var mangaBakaId = reader.GetInt64(0);
                if (read.Add(mangaBakaId))
                {
                    entries.Add(new ProfileEntry(mangaBakaId, new SeriesReadSignal(0, 0, 0, null)));
                }
            }
        }

        return new Profile($"user-{userId}", entries);
    }

    private static Microsoft.Data.Sqlite.SqliteConnection Open(string dbPath)
    {
        var conn = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        conn.Open();
        return conn;
    }
}

/// <summary>
/// Variant syntax: <c>name:key=value,key=value</c>, keys being <see cref="TasteTuning"/>,
/// <see cref="RecoGraphTuning"/> or <see cref="CoReadTuning"/> property names, case-insensitively.
/// Graph keys carry a <c>graph</c> prefix (<c>graphweight</c>, <c>graphdegreepenalty</c>, ...) and
/// co-read keys a <c>coread</c> one (<c>coreadweight</c>, <c>coreadmininjectedscore</c>, ...).
///
/// <para>
/// <b>Prefix order matters and is not alphabetical.</b> <c>coread</c> is tested before <c>graph</c>
/// only because no co-read key happens to start with "graph"; the reverse is not true of any key
/// either, so the two namespaces are disjoint. Adding a key that collides would need this rewritten
/// rather than extended.
/// </para>
///
/// <para>
/// Four shorthands are built in. <c>uniform</c> turns every taste channel off, which is the
/// behaviour that predates behavioural seeding and the baseline everything is read against.
/// <c>nograph</c> and <c>nocoread</c> keep the taste defaults but switch off the vote graph and the
/// reading graph respectively, which are the baselines <em>those</em> features have to be read
/// against — and the only honest way to see what each moved, since both are on by default.
/// <c>nocrowd</c> switches off both at once, which is what the recommender did before either
/// existed.
/// </para>
/// </summary>
file static class Variants
{
    public static Variant Parse(string spec)
    {
        var (name, overrides) = spec.Split(':', 2) is [var n, var rest] ? (n, rest) : (spec, string.Empty);

        var tuning = name.ToLowerInvariant() switch
        {
            "uniform" => TasteTuning.Uniform,
            _ => TasteTuning.Default,
        };

        var graph = RecoGraphTuning.Default;
        var coReadTuning = CoReadTuning.Default;
        var noCrowd = string.Equals(name, "nocrowd", StringComparison.OrdinalIgnoreCase);
        var coGraph = !noCrowd && !string.Equals(name, "nograph", StringComparison.OrdinalIgnoreCase);
        var coRead = !noCrowd && !string.Equals(name, "nocoread", StringComparison.OrdinalIgnoreCase);

        foreach (var pair in overrides.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2)
            {
                throw new InvalidOperationException($"Malformed override '{pair}' (want key=value).");
            }

            var key = parts[0].Trim();
            var value = parts[1].Trim();
            if (key.StartsWith("coread", StringComparison.OrdinalIgnoreCase) && key.Length > 6)
            {
                coReadTuning = ApplyCoRead(coReadTuning, key[6..], value);
            }
            else if (key.StartsWith("graph", StringComparison.OrdinalIgnoreCase) && key.Length > 5)
            {
                graph = ApplyGraph(graph, key[5..], value);
            }
            else
            {
                tuning = Apply(tuning, key, value);
            }
        }

        return new Variant(name, tuning, graph, coGraph, coReadTuning, coRead);
    }

    private static CoReadTuning ApplyCoRead(CoReadTuning coRead, string key, string value)
    {
        double D() => double.Parse(value, CultureInfo.InvariantCulture);
        int I() => int.Parse(value, CultureInfo.InvariantCulture);

        return key.ToLowerInvariant() switch
        {
            "weight" => coRead with { Weight = D() },
            "minstrength" => coRead with { MinStrength = D() },
            "maxinjected" => coRead with { MaxInjected = I() },
            "mininjectedscore" => coRead with { MinInjectedScore = D() },
            _ => throw new InvalidOperationException($"Unknown co-read tuning key 'coread{key}'."),
        };
    }

    private static RecoGraphTuning ApplyGraph(RecoGraphTuning graph, string key, string value)
    {
        double D() => double.Parse(value, CultureInfo.InvariantCulture);
        int I() => int.Parse(value, CultureInfo.InvariantCulture);

        return key.ToLowerInvariant() switch
        {
            "weight" => graph with { Weight = D() },
            "degreepenalty" => graph with { DegreePenalty = D() },
            "degreesmoothing" => graph with { DegreeSmoothing = D() },
            "minvotes" => graph with { MinVotes = I() },
            "maxinjected" => graph with { MaxInjected = I() },
            "mininjectedscore" => graph with { MinInjectedScore = D() },
            _ => throw new InvalidOperationException($"Unknown graph tuning key 'graph{key}'."),
        };
    }

    private static TasteTuning Apply(TasteTuning tuning, string key, string value)
    {
        double D() => double.Parse(value, CultureInfo.InvariantCulture);

        return key.ToLowerInvariant() switch
        {
            "depthweight" => tuning with { DepthWeight = D() },
            "ratioweight" => tuning with { RatioWeight = D() },
            "engageweight" => tuning with { EngageWeight = D() },
            "depthsaturationchapters" => tuning with { DepthSaturationChapters = D() },
            "engagesaturationminutes" => tuning with { EngageSaturationMinutes = D() },
            "recencyhalflifedays" => tuning with { RecencyHalfLifeDays = D() },
            "recencyfloor" => tuning with { RecencyFloor = D() },
            "minweight" => tuning with { MinWeight = D() },
            "maxweight" => tuning with { MaxWeight = D() },
            "neutralsignal" => tuning with { NeutralSignal = D() },
            "ratingblendalpha" => tuning with { RatingBlendAlpha = D() },
            // Deliberately refused rather than accepted-and-ignored. The multiplier is inert unless a
            // caller passes a real typeAffinity to TasteWeights.Weight, and nothing does yet — a sweep
            // over this key would report "no effect" for a channel that was never connected.
            "typeaffinityweight" => throw new InvalidOperationException(
                "typeaffinityweight is not wired up: no caller computes a per-type affinity yet, so "
                + "this key would score identically at every value."),
            "weightquantum" => tuning with { WeightQuantum = D() },
            _ => throw new InvalidOperationException($"Unknown tuning key '{key}'."),
        };
    }
}

file sealed record Variant(
    string Name,
    TasteTuning Tuning,
    RecoGraphTuning Graph,
    bool CoGraph,
    CoReadTuning CoReadTuning,
    bool CoRead);

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
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
                $"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
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
