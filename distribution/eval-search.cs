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

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Maki.Core.Configuration;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Microsoft.Extensions.Logging;

// A file-based app builds with the trimming-friendly defaults, under which System.Text.Json
// refuses reflection-based serialization. VectorIndexCache reads the dump's genre and author JSON
// arrays that way, so without this the index build throws before a single query runs. Must happen
// before anything touches a JsonSerializerOptions.
AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", true);

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
        // Only meaningful with --explain: the scored run is deliberately unfiltered, since the
        // labelled set says nothing about which filters a user would have had set.
        case "--tags":
            filterTags = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            break;
        default:
            variantArgs.Add(args[i]);
            break;
    }
}

var variants = variantArgs.Count > 0
    ? variantArgs.Select(Variants.Parse).ToList()
    : [Variants.Parse("baseline"), Variants.Parse("default")];

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
Console.WriteLine($"queries  : {queriesPath}");
Console.WriteLine($"limit    : top {limit} per query");
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
var localStore = new MangaBakaLocalStore(dumpOptions, new NoSettings(), new ConsoleLogger<MangaBakaLocalStore>());

var warm = Stopwatch.StartNew();
if (await cache.GetAsync() is not { } index)
{
    Console.WriteLine("error: the vector index is empty — nothing embedded yet.");
    return 1;
}

Console.WriteLine($"index    : {index.Count} series, built in {warm.Elapsed.TotalSeconds:F1}s");
Console.WriteLine();

var items = EvalQueries.Load(queriesPath);

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
    var reciprocal = new double[items.Length];
    var ranks = new int[items.Length];
    var clock = Stopwatch.StartNew();

    for (var i = 0; i < items.Length; i++)
    {
        var hits = await searcher.SearchAsync(items[i].Query, null, limit);
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

    return (variant, byClass, Metrics.From(ranks), reciprocal);
}

async Task Explain(Variant variant, string query)
{
    var searcher = Build(variant);
    var filters = filterTags is null ? null : new RecommendationFilters(Tags: filterTags);
    var hits = await searcher.SearchAsync(query, filters, limit);
    Console.WriteLine(
        $"=== {variant.Name}: \"{query}\"" +
        $"{(filterTags is null ? string.Empty : $" tags={string.Join("+", filterTags)}")} ({hits.Count} hits)");
    for (var i = 0; i < hits.Count; i++)
    {
        Console.WriteLine($"  {i + 1,3}. {hits[i].Title}");
    }

    Console.WriteLine();
}

SemanticSearcher Build(Variant variant) => new(
    options, dumpOptions, store, cache, embedder, localStore, variant.Tuning,
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
    Console.WriteLine($"paired against '{baseline.Variant.Name}' (premise class only)");
    var premise = items.Select((item, i) => (item.Class, Index: i)).Where(x => x.Class == "premise").Select(x => x.Index).ToList();
    foreach (var candidate in results.Skip(1))
    {
        var better = premise.Count(i => candidate.Rr[i] > baseline.Rr[i]);
        var worse = premise.Count(i => candidate.Rr[i] < baseline.Rr[i]);
        var delta = premise.Average(i => candidate.Rr[i] - baseline.Rr[i]);
        Console.WriteLine(
            $"  {candidate.Variant.Name,-22} better on {better,3}, worse on {worse,3}, unchanged on " +
            $"{premise.Count - better - worse,3}   mean ΔRR {delta,+7:F3}");
    }

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
    public sealed record Item(string Query, string Expected, string Class);

    public static Item[] Load(string path)
    {
        var items = new List<Item>();
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = raw.Split('\t');
            if (parts.Length != 3)
            {
                throw new InvalidOperationException(
                    $"Malformed line in {Path.GetFileName(path)} (want 3 tab-separated fields): {raw}");
            }

            items.Add(new Item(parts[0].Trim(), parts[1].Trim(), parts[2].Trim()));
        }

        return [.. items];
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
