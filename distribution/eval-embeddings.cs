#:project ../src/Maki.Metadata/Maki.Metadata.csproj

// Scores an embedding model's retrieval quality on a labelled set that costs nothing to produce, so
// a model change can be argued from numbers instead of taste. Run:
//   dotnet run distribution/eval-embeddings.cs -- <candidate> [pool] [queries]
//   dotnet run distribution/eval-embeddings.cs -p:MakiOnnxGpu=true -- large 20000 1000   (on a GPU)
//
// WHY THIS EXISTS
// The MRR figures quoted in EmbeddingModelProfile (base 0.545, large 0.639) came from a 12-query set
// that lived in a scratch file and was never committed, so they cannot be reproduced or extended to
// a new model. Twelve hand-labelled queries is also too few to separate two decent models: the
// commit that added the tag channel already noted that weights between 0.35 and 0.6 were "inside the
// noise of 12 queries".
//
// THE LABELS ARE FREE
// ~59k series in the full dump carry two independently written descriptions - MangaBaka's own and
// MangaUpdates'. SeriesEmbeddingIndexer.BuildText prefers the MangaUpdates one, so the MangaBaka
// description is text the indexed passage has never seen: a different person's summary of the same
// story. Retrieving the right series from it is a real semantic match with the series id as ground
// truth, at any sample size we like.
//
// WHAT IT IS NOT
// A proxy, and it should be read as one. Real Discover queries are short and thematic ("a wandering
// swordsman in feudal Japan"), not paragraphs, and this measures the dense channel alone -
// SemanticSearcher also fuses FTS5 titles and the tag channel. Hence --mode: `full` uses the whole
// held-out description, `short` uses a random 8-16 word span of it, which is much closer to what
// someone types. Report both; a model that wins on one and loses the other has not won.

using System.Globalization;
using System.Text.Json;
using Maki.Metadata.Embedding;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

// Every number this tool prints is a measurement to be compared against another run, possibly on
// another machine, and quoted into a doc comment beside one. Without this a decimal comma makes two
// identical runs look different: the per-query CSVs already pin the culture where they are written,
// so the FILES were always fine and only the printed tables read as "0,4205". Same reason
// eval-search.cs, eval-reco.cs and eval-reco-labels.cs all pin it; this was the one that did not.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

// Relative to the working directory, not AppContext.BaseDirectory: a file-based app runs out of a
// hashed folder under TEMP, so nothing useful is relative to the binary.
var dumpPath = Environment.GetEnvironmentVariable("MAKI_EVAL_DUMP")
    ?? Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), ".artifacts/mangabaka.full.db"));

var candidateName = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "base";

// `pairs` swaps to the hand-labelled set (see QueryPairs) over the whole recommendable catalogue,
// because those twelve targets are famous series that an arbitrary 10k slice would simply not
// contain - and a target missing from the pool scores zero for every model equally, which looks
// like agreement while measuring nothing.
var pairsMode = args.Length > 1 && args[1].Trim().Equals("pairs", StringComparison.OrdinalIgnoreCase);

// `titles` is the closest thing to a real Discover query with free labels: MangaBaka's `titles`
// column carries every alternate name a series is known by, and the English and romaji ones are
// exactly what somebody types - "AoT", "HQ!!", "Shingeki no Kyojin", "I Level Up Alone". The series
// id is the label, so there are tens of thousands of them and none needed hand-writing.
//
// Read the result knowing what it is: a NAME-matching test, which the dense channel alone is not
// supposed to win. SemanticSearcher fuses an FTS5 title index by RRF precisely because embeddings
// are weak here. It is still valid for comparing models against each other, and it is the metric a
// separate title vector would be trying to move.
var titlesMode = args.Length > 1 && args[1].Trim().Equals("titles", StringComparison.OrdinalIgnoreCase);

// `queries` is the hand-written set in distribution/eval-queries.tsv: 153 searches a real person
// would type, each labelled with the series it means. It exists because every other mode here
// measures a query shape nobody actually types - a whole description, a span cut out of one, or an
// alternate name - while Discover receives short thematic sentences. Its `premise` class is that
// shape, at a sample size the twelve-query set could never reach.
var queriesMode = args.Length > 1 && args[1].Trim().Equals("queries", StringComparison.OrdinalIgnoreCase);

// `dual` embeds the title and the description as TWO vectors instead of the one combined string
// SeriesEmbeddingIndexer builds, then sweeps the weight between them. It answers a specific question:
// the current text formula leads with the title and that measured BETTER than leading with the
// description (MRR 0.393 vs 0.298), which is evidence the title carries real signal - so the natural
// follow-up is whether scoring the two separately beats blending them into one vector.
var dualMode = args.Length > 1 && args[1].Trim().Equals("dual", StringComparison.OrdinalIgnoreCase);

// Defaults to the whole catalogue in every mode. A smaller pool is a materially easier retrieval
// problem and is not what production does; it stays available as an argument for a quick smoke run,
// but it is not the number to draw conclusions from.
var namedMode = pairsMode || titlesMode || queriesMode || dualMode;
var poolSize = namedMode
    ? (args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 200000)
    : (args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 200000);
var queryCount = args.Length > (namedMode ? 3 : 2)
    ? int.Parse(args[namedMode ? 3 : 2], CultureInfo.InvariantCulture)
    : 1000;

if (!Candidates.All.TryGetValue(candidateName, out var profile))
{
    Console.WriteLine($"unknown candidate '{candidateName}'. Known: {string.Join(", ", Candidates.All.Keys)}");
    return 2;
}

if (!File.Exists(dumpPath))
{
    Console.WriteLine($"error: no MangaBaka full dump at {dumpPath}. Set MAKI_EVAL_DUMP or run publish-embeddings.ps1 first.");
    return 2;
}

var workDir = Path.Combine(Path.GetTempPath(), "maki-eval");
Directory.CreateDirectory(workDir);
// Results go next to the other build artifacts (git-ignored) rather than into TEMP: they are the
// input to the paired comparison, and want to outlive a temp sweep.
var resultsDir = Path.Combine(Directory.GetCurrentDirectory(), ".artifacts", "eval");
var options = new EmbeddingOptions(Path.Combine(workDir, "models"), Path.Combine(workDir, "unused.db"), workDir, profile)
{
    Enabled = true,
};

Console.WriteLine($"candidate : {candidateName} ({profile.Version}, {profile.Dimensions} dims)");
Console.WriteLine($"runtime   : {options.Precision} on {options.Provider}, batch {options.BatchSize}");
Console.WriteLine($"pool      : {poolSize} series, {queryCount} queries");
Console.WriteLine();

// A fixed seed everywhere: two candidates must be scored on the identical pool and the identical
// query spans, or the comparison measures the sample rather than the model.
const int Seed = 20260803;

// Body is the indexed description WITHOUT the title glued on, which only the `dual` mode uses: it is
// the half of Passage that a separate description vector would carry.
var pool = new List<(long Id, string Title, string Passage, string Body, string HeldOut, string[] AltTitles, bool Distinct)>(poolSize);
using (var conn = new SqliteConnection($"Data Source={dumpPath};Mode=ReadOnly;Pooling=False"))
{
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandTimeout = 600;
    // Same candidate filter as SeriesEmbeddingIndexer.CandidateWhere, narrowed to rows that carry
    // both descriptions. ORDER BY id keeps the pool identical between runs and between candidates.
    // The pairs eval needs the real candidate set, the same one SeriesEmbeddingIndexer embeds. The
    // held-out eval additionally needs both descriptions present, since the second one *is* the
    // label.
    // Both modes now load the WHOLE recommendable catalogue, exactly what SeriesEmbeddingIndexer
    // embeds. The held-out eval used to narrow the pool to series carrying two descriptions, which
    // made it a 10k-candidate problem when production is 95,745 - roughly 9.5x easier, and the two
    // evals disagreed about bge-large precisely where that mattered. The second description is a
    // requirement of the *query*, not of the haystack, so it is filtered below instead. Keeping the
    // pools identical also lets both modes share one cached vector file per model.
    const string dualDescriptionOnly = "";
    cmd.CommandText =
        "SELECT id, title, source_manga_updates_response_description, description, titles FROM series " +
        "WHERE state = 'active' AND rating IS NOT NULL " +
        "AND type != 'novel' AND description IS NOT NULL AND length(description) > 20 " +
        dualDescriptionOnly +
        $"ORDER BY id LIMIT {poolSize}";
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        var title = reader.IsDBNull(1) ? "" : reader.GetString(1);
        // BuildText prefers the MangaUpdates description where it exists; in pairs mode it may not.
        var mangaUpdates = reader.IsDBNull(2) ? null : Text.Clean(reader.GetString(2));
        var indexed = mangaUpdates is { Length: > 30 } ? mangaUpdates : Text.Clean(reader.GetString(3));
        var heldOut = reader.IsDBNull(3) ? "" : Text.Clean(reader.GetString(3));
        // BuildText's formula. Duplicated rather than referenced because it is internal to
        // Maki.Metadata; if that formula changes, change it here too or the eval stops describing
        // what actually ships.
        var alts = titlesMode && !reader.IsDBNull(4) ? AltTitles.Parse(reader.GetString(4), title) : [];
        // Only a series with a REAL MangaUpdates description can be a held-out query: without one
        // the indexer falls back to MangaBaka's own text, so the passage would literally contain the
        // query and the retrieval would be trivial. The passage pool still holds everything, because
        // that is what production searches.
        var distinct = mangaUpdates is { Length: > 200 };
        pool.Add((reader.GetInt64(0), title, profile.PassagePrefix + $"{title}. {indexed}", indexed, heldOut, alts, distinct));
    }
}

Console.WriteLine($"loaded {pool.Count} series with two descriptions.");
if (pool.Count < queryCount)
{
    Console.WriteLine($"error: pool of {pool.Count} is smaller than the {queryCount} queries asked for.");
    return 2;
}

var embedder = new TextEmbedder(options, new EmbeddingModelStore(new Factory(), options, new ConsoleLogger<EmbeddingModelStore>()), new ConsoleLogger<TextEmbedder>());
if (!await embedder.EnsureReadyAsync())
{
    Console.WriteLine("error: embedder failed to initialize.");
    return 1;
}

if (options.Provider == EmbeddingProvider.Cuda && embedder.ActiveProvider != EmbeddingProvider.Cuda)
{
    Console.WriteLine("error: CUDA requested but the session fell back to CPU; the timings would be meaningless.");
    return 1;
}

// Passage vectors are cached on disk. Embedding the whole catalogue is ~7 minutes for a small model
// and ~20 for a large one, and iterating on the *scoring* (a title matcher, a new metric) does not
// change a single vector. Keyed by candidate, pool size and dimension, so a changed pool or model
// cannot silently reuse the wrong file. Delete .artifacts/eval/vec-*.bin to force a re-embed.
Directory.CreateDirectory(resultsDir);
// The version suffix is load-bearing. The key was candidate + pool + dimension, none of which
// change when the *embedding code* does, so fixing the tokenizer (Qwen needs an appended EOS) left
// the cache happily serving vectors built the old way - the "after" run returned byte-identical
// numbers to the "before" one and looked like the fix had no effect. Bump this on any change to how
// text becomes a vector: tokenization, pooling, prefixes, truncation.
//
//   v2  Qwen's appended EOS sentinel.
//   v3  Restored [CLS]/[SEP] for every WordPiece model. Widening TextEmbedder's tokenizer field to
//       `Tokenizer?` for Qwen re-bound EncodeToIds to the BASE overload, which adds no special
//       tokens, so bge/arctic/e5/gte were all embedded without them and CLS pooling read the first
//       real word. Every v2 file is poisoned; bge-base scored 0/12 on the pairs set against 9/12
//       before and after. See TextEmbedderEncodingTests.
const int CacheVersion = 3;
var vectorCache = Path.Combine(
    resultsDir, $"vec{CacheVersion}-{candidateName}-{pool.Count}x{profile.Dimensions}.bin");
float[][] passages;
if (File.Exists(vectorCache))
{
    passages = ReadVectors(vectorCache, pool.Count, profile.Dimensions);
    Console.WriteLine($"  reused cached passage vectors ({Path.GetFileName(vectorCache)})");
}
else
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    passages = Embed(pool.Select(p => p.Passage).ToList(), "passages");
    Console.WriteLine($"  embedded {pool.Count} passages in {sw.Elapsed.TotalSeconds:F0}s ({pool.Count / sw.Elapsed.TotalSeconds:F0}/s)");
    WriteVectors(vectorCache, passages);
}

Console.WriteLine();

if (titlesMode)
{
    // One alternate per series, so a series listed under fifteen names does not dominate the score.
    var eligibleTitles = Enumerable.Range(0, pool.Count).Where(i => pool[i].AltTitles.Length > 0).ToArray();
    Console.WriteLine($"  {eligibleTitles.Length} of {pool.Count} series carry a usable alternate name.");
    if (eligibleTitles.Length < queryCount)
    {
        Console.WriteLine($"error: only {eligibleTitles.Length} usable alternates, {queryCount} asked for.");
        return 2;
    }

    var pickRng = new Random(Seed);
    var chosen = eligibleTitles.OrderBy(_ => pickRng.Next()).Take(queryCount).ToArray();
    var altRng = new Random(Seed);
    var altQueries = chosen.Select(i => pool[i].AltTitles[altRng.Next(pool[i].AltTitles.Length)]).ToArray();

    var altVectors = Embed(altQueries.Select(q => profile.QueryPrefix + q).ToList(), "alt-title queries");
    Console.Write($"  titles: scoring {chosen.Length} x {passages.Length} ...");
    var titleClock = System.Diagnostics.Stopwatch.StartNew();
    var (tMrr, tSe, tR1, tR10, tR20, tPer) = Score(altVectors, passages, chosen);
    Console.WriteLine($" {titleClock.Elapsed:mm\\:ss}");

    Directory.CreateDirectory(resultsDir);
    File.WriteAllLines(
        Path.Combine(resultsDir, $"rr-{candidateName}-titles.csv"),
        tPer.Select((r, i) => $"{chosen[i]},{r.ToString(CultureInfo.InvariantCulture)}"));

    Console.WriteLine(
        $"titles  MRR@10 {tMrr:F4} +/-{1.96 * tSe:F4}   recall@1 {tR1:P1}   recall@10 {tR10:P1}   recall@20 {tR20:P1}");

    // Abbreviations are the hardest and most realistic slice, and they are where a dense-only
    // channel is expected to fail; splitting them out stops a good average from hiding it.
    var shortIdx = Enumerable.Range(0, chosen.Length).Where(i => altQueries[i].Length <= 8).ToArray();
    if (shortIdx.Length > 20)
    {
        var shortMrr = shortIdx.Average(i => tPer[i]);
        var shortR10 = (double)shortIdx.Count(i => tPer[i] >= 0.1) / shortIdx.Length;
        Console.WriteLine($"        of which {shortIdx.Length} are <=8 chars (abbreviations): MRR {shortMrr:F4}, recall@10 {shortR10:P1}");
    }

    Console.WriteLine();
    foreach (var i in Enumerable.Range(0, Math.Min(8, chosen.Length)))
    {
        var hit = tPer[i] > 0 ? $"rank {(int)Math.Round(1 / tPer[i])}" : "MISS";
        Console.WriteLine($"  {hit,-7} \"{altQueries[i]}\"  ->  {pool[chosen[i]].Title}");
    }

    return 0;
}

if (pairsMode)
{
    var pairQueries = QueryPairs.All.Select(p => profile.QueryPrefix + p.Query).ToList();
    var pairVectors = Embed(pairQueries, "pair queries");
    double total = 0;
    var found = 0;

    for (var q = 0; q < QueryPairs.All.Length; q++)
    {
        var (queryText, targets) = QueryPairs.All[q];
        var ranked = Enumerable.Range(0, passages.Length)
            .Select(i => (Index: i, Score: EmbeddingMath.Cosine(pairVectors[q], passages[i])))
            .OrderByDescending(x => x.Score)
            .Take(10)
            .ToArray();

        var rank = Array.FindIndex(ranked, x => QueryPairs.Matches(pool[x.Index].Title, targets));
        if (rank >= 0)
        {
            total += 1.0 / (rank + 1);
            found++;
        }

        var verdict = rank >= 0 ? $"rank {rank + 1}" : "MISS";
        Console.WriteLine($"  {verdict,-7} \"{queryText}\"");
        Console.WriteLine($"          top3: {string.Join(" | ", ranked.Take(3).Select(x => pool[x.Index].Title))}");
    }

    Console.WriteLine();
    Console.WriteLine($"pairs   MRR@10 {total / QueryPairs.All.Length:F4}   found {found}/{QueryPairs.All.Length}");
    Console.WriteLine("        (n=12, so the interval is roughly +/-0.25; read this as a cross-check, not a verdict)");
    return 0;
}

if (dualMode)
{
    // Two independent vectors per series instead of the one combined string production builds, then
    // a sweep of the weight between them. alpha=0 is description-only, alpha=1 is title-only, and
    // the COMBINED baseline is what SeriesEmbeddingIndexer actually ships - so the experiment only
    // pays off if some alpha beats that baseline, not merely if it beats one of its own endpoints.
    var titleTexts = pool.Select(p => profile.PassagePrefix + p.Title).ToList();
    var bodyTexts = pool.Select(p => profile.PassagePrefix + p.Body).ToList();

    float[][] LoadOrEmbed(string kind, IReadOnlyList<string> texts)
    {
        var path = Path.Combine(resultsDir, $"vec{CacheVersion}-{candidateName}-{kind}-{pool.Count}x{profile.Dimensions}.bin");
        if (File.Exists(path))
        {
            Console.WriteLine($"  reused cached {kind} vectors ({Path.GetFileName(path)})");
            return ReadVectors(path, pool.Count, profile.Dimensions);
        }

        var v = Embed(texts, $"{kind} passages");
        WriteVectors(path, v);
        return v;
    }

    var titleVectors = LoadOrEmbed("title", titleTexts);
    var bodyVectors = LoadOrEmbed("body", bodyTexts);

    var dualRng = new Random(Seed);
    var dualEligible = Enumerable.Range(0, pool.Count).Where(i => pool[i].Distinct && pool[i].HeldOut.Length > 200).ToArray();
    if (dualEligible.Length < queryCount)
    {
        Console.WriteLine($"error: only {dualEligible.Length} eligible queries, {queryCount} asked for.");
        return 2;
    }

    // The same seed and the same mode as the held-out `clean` run, so the baseline printed below is
    // directly comparable to the clean number the other modes report.
    var dualIndices = dualEligible.OrderBy(_ => dualRng.Next()).Take(queryCount).ToArray();
    var spanSeed = new Random(Seed);
    var dualQueries = dualIndices
        .Select(i => profile.QueryPrefix + Text.RandomSpan(Text.StripTitleWords(pool[i].HeldOut, pool[i].Title), spanSeed))
        .ToList();
    var dualVectors = Embed(dualQueries, "clean queries");

    // Two query sets, because one of them cannot answer the question on its own. `clean` strips title
    // words from the query on purpose, so a title vector has nothing to match and the sweep is rigged
    // against it before it starts. The hand-written set is the fair test: its `title` and `alias`
    // classes are exactly the queries a separate title vector exists to serve.
    var sweeps = new List<(string Name, float[][] Queries, int[][] Accepts, string[] Classes)>
    {
        ("held-out clean", dualVectors, [.. dualIndices.Select(i => new[] { i })], [.. dualIndices.Select(_ => "all")]),
    };

    if (File.Exists(EvalQueries.DefaultPath))
    {
        var (handItems, handAccepts) = EvalQueries.Resolve(EvalQueries.DefaultPath, [.. pool.Select(p => p.Title)]);
        if (handAccepts.All(a => a.Length > 0))
        {
            var handVectors = Embed(handItems.Select(i => profile.QueryPrefix + i.Query).ToList(), "hand-written queries");
            sweeps.Add(("hand-written", handVectors, handAccepts, [.. handItems.Select(i => i.Class)]));
        }
        else
        {
            Console.WriteLine("  (skipping the hand-written sweep: some labels do not resolve)");
        }
    }

foreach (var (sweepName, sweepQueries, sweepAccepts, sweepClasses) in sweeps)
{
    Console.WriteLine();
    Console.WriteLine($"--- {sweepName} ({sweepQueries.Length} queries)");
    var (baseMrr, baseSe, baseR1, _, _, basePer) = ScoreSets(sweepQueries, passages, sweepAccepts);
    Console.WriteLine($"combined (what ships)  MRR@10 {baseMrr:F4} +/-{1.96 * baseSe:F4}   recall@1 {baseR1:P1}");
    ReportByClass("  combined", basePer, sweepClasses);
    Console.WriteLine();

    foreach (var alpha in new[] { 0.0, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 1.0 })
    {
        // Blending the two SCORES, not the two vectors: averaging unit vectors and renormalizing is
        // a different (and weaker) operation, and it cannot express "the title matched strongly and
        // the description not at all", which is the case a separate title vector exists to catch.
        var blended = new float[pool.Count][];
        for (var i = 0; i < pool.Count; i++)
        {
            // Fold the weights into the passage side once, so Score's cosine stays a plain dot
            // product. Both inputs are unit length, so this is exactly alpha*cos_t + (1-alpha)*cos_b.
            var v = new float[profile.Dimensions * 2];
            for (var d = 0; d < profile.Dimensions; d++)
            {
                v[d] = (float)(alpha * titleVectors[i][d]);
                v[profile.Dimensions + d] = (float)((1 - alpha) * bodyVectors[i][d]);
            }

            blended[i] = v;
        }

        var wideQueries = new float[sweepQueries.Length][];
        for (var q = 0; q < sweepQueries.Length; q++)
        {
            var v = new float[profile.Dimensions * 2];
            Array.Copy(sweepQueries[q], 0, v, 0, profile.Dimensions);
            Array.Copy(sweepQueries[q], 0, v, profile.Dimensions, profile.Dimensions);
            wideQueries[q] = v;
        }

        var (m, se, r1, r10, _, per) = ScoreSets(wideQueries, blended, sweepAccepts);
        var label = alpha == 0 ? "description only" : alpha == 1 ? "title only" : $"alpha {alpha:F1}";
        var delta = m - baseMrr;
        Console.WriteLine(
            $"  {label,-18} MRR@10 {m:F4} +/-{1.96 * se:F4}   recall@1 {r1:P1}   recall@10 {r10:P1}   " +
            $"vs combined {delta:+0.0000;-0.0000}");
        ReportByClass($"    {label}", per, sweepClasses);
    }
}

    return 0;
}

// Per-class breakdown, printed only when the sweep has more than one class. The whole point of the
// hand-written set here is that a title vector should help `title`/`alias` and be neutral on
// `premise`; an average over all three would hide exactly that.
static void ReportByClass(string prefix, double[] per, string[] classes)
{
    var distinct = classes.Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToArray();
    if (distinct.Length < 2)
    {
        return;
    }

    var parts = distinct.Select(c =>
    {
        var idx = Enumerable.Range(0, classes.Length).Where(i => classes[i] == c).ToArray();
        return $"{c} {idx.Average(i => per[i]):F4}";
    });
    Console.WriteLine($"{prefix,-24} {string.Join("   ", parts)}");
}

if (queriesMode)
{
    var tsv = Environment.GetEnvironmentVariable("MAKI_EVAL_QUERIES")
        ?? Path.Combine(Directory.GetCurrentDirectory(), "distribution", "eval-queries.tsv");
    if (!File.Exists(tsv))
    {
        Console.WriteLine($"error: no query set at {tsv}. Set MAKI_EVAL_QUERIES to point at one.");
        return 2;
    }

    var (items, accepts) = EvalQueries.Resolve(tsv, [.. pool.Select(p => p.Title)]);

    var unresolved = Enumerable.Range(0, items.Length).Where(i => accepts[i].Length == 0).ToArray();
    if (unresolved.Length > 0)
    {
        // Refuse to score rather than report a number that blames the model for a broken label.
        Console.WriteLine($"error: {unresolved.Length} label(s) match no row in the pool:");
        foreach (var i in unresolved.DistinctBy(i => items[i].Expected))
        {
            Console.WriteLine($"  \"{items[i].Expected}\"  (first seen in query \"{items[i].Query}\")");
        }

        return 2;
    }

    Console.WriteLine(
        $"  {items.Length} queries; {items.Select(i => i.Expected).Distinct(StringComparer.Ordinal).Count()} " +
        $"distinct labels, all resolved ({accepts.Sum(a => a.Length) / (double)accepts.Length:F1} acceptable rows each on average).");

    var qVectors = Embed(items.Select(i => profile.QueryPrefix + i.Query).ToList(), "hand-written queries");
    Console.Write($"  queries: scoring {items.Length} x {passages.Length} ...");
    var qClock = System.Diagnostics.Stopwatch.StartNew();
    var (qMrr, qSe, qR1, qR10, qR20, qPer) = ScoreSets(qVectors, passages, accepts);
    Console.WriteLine($" {qClock.Elapsed:mm\\:ss}");

    Directory.CreateDirectory(resultsDir);
    File.WriteAllLines(
        Path.Combine(resultsDir, $"rr-{candidateName}-queries.csv"),
        qPer.Select((r, i) => $"{i},{r.ToString(CultureInfo.InvariantCulture)}"));

    Console.WriteLine();
    Console.WriteLine(
        $"queries MRR@10 {qMrr:F4} +/-{1.96 * qSe:F4}   recall@1 {qR1:P1}   recall@10 {qR10:P1}   recall@20 {qR20:P1}");

    // Per class, because they measure different channels and one average hides the only one that
    // decides anything. A separate CSV per class so the paired test can run on `premise` alone.
    foreach (var cls in items.Select(i => i.Class).Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal))
    {
        var idx = Enumerable.Range(0, items.Length).Where(i => items[i].Class == cls).ToArray();
        var mrr = idx.Average(i => qPer[i]);
        var r1 = idx.Count(i => qPer[i] == 1.0) / (double)idx.Length;
        var r10 = idx.Count(i => qPer[i] >= 0.1) / (double)idx.Length;
        var r20 = idx.Count(i => qPer[i] > 0) / (double)idx.Length;
        var variance = idx.Sum(i => (qPer[i] - mrr) * (qPer[i] - mrr)) / Math.Max(idx.Length - 1, 1);
        Console.WriteLine(
            $"  {cls,-8} n={idx.Length,3}  MRR@10 {mrr:F4} +/-{1.96 * Math.Sqrt(variance / idx.Length):F4}   " +
            $"recall@1 {r1:P1}   recall@10 {r10:P1}   recall@20 {r20:P1}");
        File.WriteAllLines(
            Path.Combine(resultsDir, $"rr-{candidateName}-queries-{cls}.csv"),
            idx.Select(i => $"{i},{qPer[i].ToString(CultureInfo.InvariantCulture)}"));
    }

    Console.WriteLine();
    foreach (var i in Enumerable.Range(0, items.Length).Where(i => items[i].Class == "premise").Take(10))
    {
        var verdict = qPer[i] > 0 ? $"rank {(int)Math.Round(1 / qPer[i])}" : "MISS";
        Console.WriteLine($"  {verdict,-7} \"{items[i].Query}\"  ->  {items[i].Expected}");
    }

    return 0;
}

// Same query indices for every candidate, so the pools line up run to run.
var rng = new Random(Seed);
// Queries come only from series carrying a real second description, since that description IS the
// label. The pool they are searched against stays the whole catalogue.
var eligible = Enumerable.Range(0, pool.Count).Where(i => pool[i].Distinct && pool[i].HeldOut.Length > 200).ToArray();
if (eligible.Length < queryCount)
{
    Console.WriteLine($"error: only {eligible.Length} series carry a usable second description, {queryCount} asked for.");
    return 2;
}

var queryIndices = eligible.OrderBy(_ => rng.Next()).Take(queryCount).ToArray();
Console.WriteLine($"  {eligible.Length} of {pool.Count} series are eligible as queries; sampled {queryCount}.");

foreach (var mode in new[] { "full", "short", "clean" })
{
    var spanRng = new Random(Seed); // reset per mode so spans are stable across candidates
    var texts = queryIndices
        .Select(i => mode switch
        {
            "full" => pool[i].HeldOut,
            "short" => Text.RandomSpan(pool[i].HeldOut, spanRng),
            // Title words removed *before* the span is cut, so the result cannot contain one.
            _ => Text.RandomSpan(Text.StripTitleWords(pool[i].HeldOut, pool[i].Title), spanRng),
        })
        .Select(q => profile.QueryPrefix + q)
        .ToList();

    var queryVectors = Embed(texts, $"{mode} queries");
    // Scoring is the other slow half at this pool size: 2000 queries against 95,745 passages is
    // 191M cosine comparisons per mode, so say so rather than appearing to hang.
    Console.Write($"  {mode}: scoring {queryIndices.Length} x {passages.Length} ...");
    var scoreClock = System.Diagnostics.Stopwatch.StartNew();
    var (mrr, se, r1, r10, r20, perQuery) = Score(queryVectors, passages, queryIndices);
    Console.WriteLine($" {scoreClock.Elapsed:mm\\:ss}");

    // Per-query reciprocal ranks, so two candidates can be compared with a PAIRED test. The
    // independent intervals printed below are the conservative view: every candidate answers the
    // identical queries over the identical pool, so most of the spread is query difficulty that
    // cancels out when the same query is compared across models. Unpaired intervals can overlap
    // while the paired difference is unambiguous.
    Directory.CreateDirectory(resultsDir);
    File.WriteAllLines(
        Path.Combine(resultsDir, $"rr-{candidateName}-{mode}.csv"),
        perQuery.Select((r, i) => $"{queryIndices[i]},{r.ToString(CultureInfo.InvariantCulture)}"));
    // The interval matters more than the point estimate. The 12-query set this replaces reported
    // base 0.545 against large 0.639 and was read as a real gap; at that sample size the interval is
    // roughly +/-0.25, so it never supported the conclusion drawn from it.
    Console.WriteLine(
        $"{mode,-6}  MRR@10 {mrr:F4} +/-{1.96 * se:F4}   recall@1 {r1:P1}   recall@10 {r10:P1}   recall@20 {r20:P1}");
}

return 0;

static void WriteVectors(string path, float[][] vectors)
{
    var staging = path + ".partial";
    using (var stream = File.Create(staging))
    using (var writer = new BinaryWriter(stream))
    {
        foreach (var vector in vectors)
        {
            foreach (var value in vector)
            {
                writer.Write(value);
            }
        }
    }

    // Staged then moved, so an interrupted run cannot leave a truncated cache that a later run
    // would happily read as real vectors.
    File.Move(staging, path, overwrite: true);
}

static float[][] ReadVectors(string path, int count, int dimensions)
{
    var expected = (long)count * dimensions * sizeof(float);
    if (new FileInfo(path).Length != expected)
    {
        throw new InvalidOperationException(
            $"Cached vectors at {path} are {new FileInfo(path).Length} bytes, expected {expected}. Delete it and re-run.");
    }

    using var stream = File.OpenRead(path);
    using var reader = new BinaryReader(stream);
    var vectors = new float[count][];
    for (var i = 0; i < count; i++)
    {
        var vector = new float[dimensions];
        for (var d = 0; d < dimensions; d++)
        {
            vector[d] = reader.ReadSingle();
        }

        vectors[i] = vector;
    }

    return vectors;
}

float[][] Embed(IReadOnlyList<string> texts, string label)
{
    var result = new float[texts.Count][];
    var done = 0;
    var clock = System.Diagnostics.Stopwatch.StartNew();
    var lastReport = TimeSpan.Zero;

    for (var i = 0; i < texts.Count; i += options.BatchSize)
    {
        var slice = texts.Skip(i).Take(options.BatchSize).ToList();
        var vectors = embedder.EmbedBatch(slice);
        for (var j = 0; j < slice.Count; j++)
        {
            result[i + j] = vectors[j];
        }

        done += slice.Count;

        // Report on a time interval rather than a row count: batch size is configurable, so any
        // count-based trigger either spams a small batch or goes silent on a large one. Two seconds
        // is often enough to watch, rare enough not to flood a redirected log.
        if (clock.Elapsed - lastReport < TimeSpan.FromSeconds(2) && done != texts.Count)
        {
            continue;
        }

        lastReport = clock.Elapsed;
        var rate = done / Math.Max(clock.Elapsed.TotalSeconds, 0.001);
        var remaining = TimeSpan.FromSeconds((texts.Count - done) / Math.Max(rate, 0.001));
        Console.Write(
            $"\r  {label}: {done,7}/{texts.Count} ({(double)done / texts.Count,5:P0})  " +
            $"{rate,5:F0}/s  elapsed {clock.Elapsed:mm\\:ss}  eta {remaining:mm\\:ss}    ");
    }

    Console.WriteLine();
    Console.WriteLine($"  {label}: done in {clock.Elapsed:mm\\:ss}");
    return result;
}

// Brute-force cosine against the whole pool. VectorIndex would be faster but quantizes to int8, and
// the point here is to compare models, not to re-measure a quantization already known to cost
// nothing (round-trip cosine 1.0000).
static (double Mrr, double StdErr, double R1, double R10, double R20, double[] PerQuery) Score(
    float[][] queries, float[][] passages, int[] truth) =>
    ScoreSets(queries, passages, [.. truth.Select(t => new[] { t })]);

/// <summary>
/// As <see cref="Score"/>, but each query may have SEVERAL acceptable answers and the first one
/// reached counts. Needed by the hand-written set, where a famous series is many rows in the pool
/// (Naruto 15, Sword Art Online 23, Mobile Suit Gundam 26 - sequels, spin-offs, anthologies) and
/// returning "Naruto: The Seventh Hokage" for "naruto" has plainly not failed. The cost is that a
/// query with 26 acceptable rows is easier than one with 1, so absolute MRR runs optimistic; every
/// candidate faces the identical bias, and the paired comparison is what decides anything.
/// </summary>
static (double Mrr, double StdErr, double R1, double R10, double R20, double[] PerQuery) ScoreSets(
    float[][] queries, float[][] passages, int[][] truth)
{
    // Retrieved depth. MRR is reported @10 by convention, so a hit at rank 11-20 raises recall@20
    // without moving MRR - which is exactly the distinction worth seeing when one model is sharp
    // and another is broad.
    const int Depth = 20;

    var reciprocalRanks = new double[queries.Length];

    // Parallel over queries, each with its own scratch buffer: at 2000 queries against a 10k pool
    // this is 20M cosines per mode, which is minutes single-threaded and seconds here. Each
    // iteration writes only its own slot, so no synchronisation is needed.
    Parallel.For(0, queries.Length, () => new (int Index, float Score)[passages.Length], (q, _, scores) =>
    {
        for (var p = 0; p < passages.Length; p++)
        {
            scores[p] = (p, EmbeddingMath.Cosine(queries[q], passages[p]));
        }

        // A partial top-K scan rather than a full sort: the pool is large and only the head matters.
        var top = new (int Index, float Score)[Depth];
        for (var i = 0; i < Depth; i++)
        {
            var best = -1;
            for (var p = 0; p < scores.Length; p++)
            {
                if (scores[p].Score > float.MinValue && (best < 0 || scores[p].Score > scores[best].Score))
                {
                    best = p;
                }
            }

            top[i] = scores[best];
            scores[best].Score = float.MinValue; // consume it so the next pass finds the runner-up
        }

        var accept = truth[q];
        var rank = Array.FindIndex(top, s => Array.IndexOf(accept, s.Index) >= 0);
        reciprocalRanks[q] = rank >= 0 ? 1.0 / (rank + 1) : 0;

        // Restore the buffer for the next query this thread handles.
        for (var i = 0; i < Depth; i++)
        {
            scores[top[i].Index].Score = top[i].Score;
        }

        return scores;
    }, _ => { });

    var hits1 = reciprocalRanks.Count(r => r == 1.0);
    var hits10 = reciprocalRanks.Count(r => r >= 1.0 / 10);
    var hits20 = reciprocalRanks.Count(r => r > 0);

    // Standard error of the mean over the per-query reciprocal ranks. Reported so a difference can
    // be told from noise, which is the whole reason this harness exists.
    var mean = reciprocalRanks.Average();
    var variance = reciprocalRanks.Sum(r => (r - mean) * (r - mean)) / Math.Max(queries.Length - 1, 1);
    var stdErr = Math.Sqrt(variance / queries.Length);
    return (mean, stdErr, (double)hits1 / queries.Length, (double)hits10 / queries.Length,
        (double)hits20 / queries.Length, reciprocalRanks);
}

/// <summary>
/// The original hand-labelled query set, recovered from the maintainer's scratch harness and kept
/// here so it can never be lost again. It is the eval the shipped MRR figures came from.
///
/// Twelve queries cannot settle a model choice on their own - the 95% interval at n=12 is roughly
/// +/-0.25, which is why `base 0.545 vs large 0.639` was over-read. Its value is as an INDEPENDENT
/// check on the held-out-description eval: the two measure different things (short thematic queries
/// against the whole catalogue, versus paraphrase retrieval), so agreement between them is worth far
/// more than either alone, and disagreement is the most informative outcome available.
/// </summary>
file static class QueryPairs
{
    public static readonly (string Query, string[] Targets)[] All =
    [
        ("revenge story where the hero slowly loses his humanity", ["Berserk"]),
        ("a quiet story about a girl and her motorcycle", ["Jyajya", "One Off", "Bakuon"]),
        ("cooking in another world with a magic skill", ["Campfire Cooking in Another World with My Absurd Skill"]),
        ("an underdog boxing manga", ["Hajime no Ippo"]),
        ("two girls travelling through a ruined empty world", ["Girls' Last Tour", "Touring After the Apocalypse"]),
        ("an unlicensed genius surgeon", ["Black Jack"]),
        ("office worker reincarnated as a slime in a fantasy world", ["That Time I Got Reincarnated as a Slime"]),
        ("high school girls camping outdoors", ["Laid-Back Camp", "Yuru Camp"]),
        ("a wandering swordsman in feudal Japan seeking strength", ["Vagabond"]),
        ("a boy detective shrunk by a poison", ["Detective Conan", "Case Closed"]),
        ("high school volleyball team underdogs", ["Haikyu"]),
        ("giant humanoids devour people inside walled cities", ["Attack on Titan"]),
    ];

    /// <summary>
    /// Matching real MangaBaka titles is fiddlier than it looks, and getting it wrong scores a
    /// correct answer as a miss. Three things this has to survive, all found in the live dump:
    ///
    /// * Romanization variants. "Haikyu!!" and "Haikyuu!!" are both active rows for the same work,
    ///   so a doubled letter cannot be the difference between a hit and a miss. Repeated letters
    ///   are collapsed, which leaves "berserk" and "berserkofglutony" still distinct.
    /// * Subtitles. The only active row for Hajime no Ippo is titled "Hajime no Ippo: Fighting
    ///   Spirit!" - the bare-titled rows are state='merged' and are never indexed. So the part
    ///   before the first colon is compared, which reaches it without letting a bare prefix test
    ///   match anything it likes.
    /// * Punctuation and case. "ATTACK ON TITAN", "Girls' Last Tour", "Yuru Camp△".
    ///
    /// Still equality, never substring: a substring test would let "Berserk" claim "Berserk of
    /// Gluttony" and score a hit for a completely different series. The known cost of the subtitle
    /// rule is that a spin-off sharing a stem before its colon ("Vagabond: Saigo no Manga-ten", an
    /// art book) counts as a hit; the real series is an exact match and normally outranks it.
    /// </summary>
    public static bool Matches(string title, string[] targets)
    {
        var full = Normalize(title);
        var beforeSubtitle = Normalize(title.Split(':')[0]);
        return targets.Any(t => Normalize(t) is var n && (n == full || n == beforeSubtitle));
    }

    public static string NormalizeTitle(string s) => Normalize(s);

    private static string Normalize(string s)
    {
        var kept = s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant);
        var builder = new System.Text.StringBuilder();
        foreach (var c in kept)
        {
            if (builder.Length == 0 || builder[^1] != c)
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}

/// <summary>
/// The hand-written query set in distribution/eval-queries.tsv. See that file's header for what the
/// three classes mean and why only `premise` should decide a model swap.
///
/// A label that resolves to nothing is a HARD ERROR rather than a silent zero. That distinction is
/// the whole reason this loader exists: an unresolvable label scores as a miss for every candidate,
/// which reads exactly like a model failure and is actually a typo. Fifteen of the first draft's
/// labels were wrong because MangaBaka's titles are not the common English ones - "Detective Conan"
/// not "Case Closed", "Omniscient Reader" without "'s Viewpoint", "Gambling Apocalypse Kaiji".
/// </summary>
file static class EvalQueries
{
    public sealed record Item(string Query, string Expected, string Class);

    /// <summary>Default location; override with MAKI_EVAL_QUERIES.</summary>
    public static string DefaultPath =>
        Environment.GetEnvironmentVariable("MAKI_EVAL_QUERIES")
        ?? Path.Combine(Directory.GetCurrentDirectory(), "distribution", "eval-queries.tsv");

    /// <summary>
    /// Loads the set and resolves every label to the pool rows carrying that title, using the same
    /// matcher the pairs eval uses. Returns null accepts for any label that matches nothing, so the
    /// caller can refuse to score rather than blame a model for a broken label.
    /// </summary>
    public static (Item[] Items, int[][] Accepts) Resolve(string path, IReadOnlyList<string> titles)
    {
        var items = Load(path);
        var byLabel = new Dictionary<string, int[]>(StringComparer.Ordinal);
        foreach (var label in items.Select(i => i.Expected).Distinct(StringComparer.Ordinal))
        {
            string[] one = [label];
            byLabel[label] = [.. Enumerable.Range(0, titles.Count).Where(i => QueryPairs.Matches(titles[i], one))];
        }

        return (items, [.. items.Select(i => byLabel[i.Expected])]);
    }

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
                throw new InvalidOperationException($"Malformed line in {Path.GetFileName(path)} (want 3 tab-separated fields): {raw}");
            }

            items.Add(new Item(parts[0].Trim(), parts[1].Trim(), parts[2].Trim()));
        }

        return [.. items];
    }
}

/// <summary>
/// Pulls the alternate names a series is known by out of MangaBaka's <c>titles</c> JSON, keeping the
/// ones an English-speaking user would plausibly type.
///
/// The language filter is the whole trick. The column is mostly localised titles - "Vagabundo",
/// "Angreifender Riese", "Atacul Titanilor" - which are real names but not what this audience
/// searches. Keeping <c>en</c> and the romaji locales leaves the useful shapes: English alternates
/// ("I Level Up Alone"), romaji ("Shingeki no Kyojin") and the abbreviations that are the hardest
/// and most realistic queries of all ("AoT", "HQ!!").
/// </summary>
file static class AltTitles
{
    private static readonly HashSet<string> Languages =
        new(StringComparer.OrdinalIgnoreCase) { "en", "ja-Latn", "ko-Latn", "zh-Latn" };

    public static string[] Parse(string json, string primaryTitle)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var primary = QueryPairs.NormalizeTitle(primaryTitle);
            var found = new List<string>();
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object ||
                    !entry.TryGetProperty("title", out var t) || t.GetString() is not { Length: > 1 } name ||
                    !entry.TryGetProperty("language", out var l) || !Languages.Contains(l.GetString() ?? ""))
                {
                    continue;
                }

                // An alternate that normalizes to the primary title is not a test of anything: the
                // passage literally begins with that string.
                if (QueryPairs.NormalizeTitle(name) == primary || name.Length > 60)
                {
                    continue;
                }

                // Latin script only. A Cyrillic or CJK alternate tagged `en` does happen, and it
                // would measure cross-script transfer rather than the name matching intended here.
                if (!name.All(c => c < 128))
                {
                    continue;
                }

                found.Add(name);
            }

            // Deterministic pick order, so every candidate is scored on the identical queries.
            return found.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

file static class Text
{
    public static string Clean(string t) =>
        System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(t, "<[^>]+>", " ").Replace("&nbsp;", " "), @"\s+", " ").Trim();

    /// <summary>
    /// Drops every word of the title from the text. Measured on a 10k pool, 56.7% of whole held-out
    /// descriptions and 16.9% of random spans contain a title word, so without this the eval is
    /// partly scoring name matching rather than the semantic match it claims to measure. Applied
    /// before the span is cut, so the span cannot reintroduce one.
    /// </summary>
    public static string StripTitleWords(string text, string title)
    {
        var titleWords = System.Text.RegularExpressions.Regex.Matches(title, "[A-Za-z]{4,}")
            .Select(m => m.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (titleWords.Count == 0)
        {
            return text;
        }

        return string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !titleWords.Contains(w.Trim('.', ',', '!', '?', '"', '\'', ':', ';', '(', ')'))));
    }

    /// <summary>A random 8-16 word run, standing in for the length of thing somebody actually types.</summary>
    public static string RandomSpan(string text, Random rng)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var length = Math.Min(rng.Next(8, 17), words.Length);
        var start = Math.Max(0, rng.Next(0, Math.Max(1, words.Length - length)));
        return string.Join(' ', words.Skip(start).Take(length));
    }
}

/// <summary>
/// Models to score. The two shipped ones plus anything being considered. Deliberately separate from
/// EmbeddingModelProfile: a candidate can be measured here without touching production code, and
/// only a winner earns a profile entry.
///
/// Every entry must be BERT-shaped, because TextEmbedder is: WordPiece vocab.txt, a token_type_ids
/// input, CLS pooling and a last_hidden_state output. An XLM-R model (bge-m3, arctic-embed v2.0,
/// gte-multilingual) needs a SentencePiece tokenizer and different special-token ids, which is real
/// work in TextEmbedder before it could be scored at all.
/// </summary>
file static class Candidates
{
    public static readonly Dictionary<string, EmbeddingModelProfile> All = new(StringComparer.OrdinalIgnoreCase)
    {
        ["base"] = EmbeddingModelProfile.Base,

        // bge-large-en-v1.5, the tier Maki used to offer and no longer does (see the note on
        // EmbeddingModelProfile.Base). Defined HERE rather than there, because a retired product
        // option is still a legitimate evaluation candidate and the constant it used to reference
        // is gone: `["large"] = EmbeddingModelProfile.Large` stopped compiling the day the tier was
        // retired, which took this whole tool with it and went unnoticed because nothing builds a
        // file-based app until somebody runs it.
        ["large"] = new(
            Kind: "large",
            FolderName: "bge-large-en-v1.5",
            Dimensions: 1024,
            Version: "bge-large-en-v1.5-q4",
            ModelUrl: "https://huggingface.co/Xenova/bge-large-en-v1.5/resolve/main/onnx/model_quantized.onnx",
            VocabUrl: "https://huggingface.co/BAAI/bge-large-en-v1.5/resolve/main/vocab.txt",
            PrebuiltTag: "embeddings-large-latest"),

        // Both are 335M-parameter BERT-large encoders at 1024 dims, the same class as bge-large, and
        // both publish onnx/model.onnx + onnx/model_quantized.onnx under the exact names ModelUrlFor
        // rewrites. Both also use CLS pooling and the same "Represent this sentence…" query prefix as
        // bge, so nothing in TextEmbedder or SemanticSearcher has to change to score them.
        ["mxbai"] = new(
            Kind: "mxbai",
            FolderName: "mxbai-embed-large-v1",
            Dimensions: 1024,
            Version: "mxbai-embed-large-v1-eval",
            ModelUrl: "https://huggingface.co/mixedbread-ai/mxbai-embed-large-v1/resolve/main/onnx/model_quantized.onnx",
            VocabUrl: "https://huggingface.co/mixedbread-ai/mxbai-embed-large-v1/resolve/main/vocab.txt",
            PrebuiltTag: "unpublished"),

        ["arctic"] = new(
            Kind: "arctic",
            FolderName: "snowflake-arctic-embed-l",
            Dimensions: 1024,
            Version: "snowflake-arctic-embed-l-eval",
            ModelUrl: "https://huggingface.co/Snowflake/snowflake-arctic-embed-l/resolve/main/onnx/model_quantized.onnx",
            VocabUrl: "https://huggingface.co/Snowflake/snowflake-arctic-embed-l/resolve/main/vocab.txt",
            PrebuiltTag: "unpublished"),

        // 768-dim, 110M, bge-base's exact size class. Worth scoring separately from arctic-l: if it
        // beats bge-base then the default tier can be upgraded at no cost to anyone's RAM or
        // download, which is a very different proposition from a better opt-in tier.
        ["arctic-m"] = new(
            Kind: "arctic-m",
            FolderName: "snowflake-arctic-embed-m",
            Dimensions: 768,
            Version: "snowflake-arctic-embed-m-eval",
            ModelUrl: "https://huggingface.co/Snowflake/snowflake-arctic-embed-m/resolve/main/onnx/model_quantized.onnx",
            VocabUrl: "https://huggingface.co/Snowflake/snowflake-arctic-embed-m/resolve/main/vocab.txt",
            PrebuiltTag: "unpublished"),

        // A decoder embedder, and the reason EmbeddingPooling.LastToken and DecoderGraph exist.
        // Reference only, not a shipping candidate: 600M parameters is ~600 MB resident on a user's
        // CPU for every search, against 240 MB for the current default. Its 8B sibling has no ONNX
        // export at all and would be ~8 GB, which is why that one could not be scored even in
        // principle. The instruction prefix is the format Qwen documents for retrieval; passages
        // deliberately get none, which is the asymmetry the model was trained with.
        ["qwen"] = new(
            Kind: "qwen",
            FolderName: "qwen3-embedding-0.6b",
            Dimensions: 1024,
            Version: "qwen3-embedding-0.6b-eval",
            ModelUrl: "https://huggingface.co/onnx-community/Qwen3-Embedding-0.6B-ONNX/resolve/main/onnx/model.onnx",
            VocabUrl: "https://huggingface.co/onnx-community/Qwen3-Embedding-0.6B-ONNX/resolve/main/vocab.json",
            PrebuiltTag: "unpublished")
        {
            Pooling = EmbeddingPooling.LastToken,
            TokenizerKind = EmbeddingTokenizer.ByteLevelBpe,
            Decoder = new DecoderGraph(Layers: 28, KeyValueHeads: 8, HeadDimension: 128),
            MergesUrl = "https://huggingface.co/onnx-community/Qwen3-Embedding-0.6B-ONNX/resolve/main/merges.txt",
            ModelDataUrl = "https://huggingface.co/onnx-community/Qwen3-Embedding-0.6B-ONNX/resolve/main/onnx/model.onnx_data",
            QueryPrefix = "Instruct: Given a web search query, retrieve relevant passages that answer the query\nQuery:",
            PassagePrefix = "",
        },

        // EmbeddingGemma-300m. 768 dims like bge-base, and the reason SentencePiece tokenization and
        // EmbeddingPooling.Pooled exist. Three things about it are not optional:
        //
        //   * The graph exposes BOTH last_hidden_state and sentence_embedding, and only the second is
        //     the model's actual output: after mean pooling it runs two Dense layers (768 to 3072 to
        //     768) and an L2 normalize, all inside the graph. Mean-pooling the hidden states by hand
        //     produces vectors of the right shape that have skipped both projections.
        //   * It declares only input_ids and attention_mask - no token_type_ids - which is why
        //     TextEmbedder now reads the graph's input list instead of assuming the BERT trio.
        //   * The prefixes are a documented format, not a convention. Queries get the task line;
        //     documents get "title: none | text: " unless a title is passed separately, and this
        //     eval folds the title into the passage text exactly as SeriesEmbeddingIndexer does.
        //
        // Download cost for users would be ~309 MB int8 against bge-base's ~110 MB, between the two
        // shipped tiers. 262k vocab means the embedding table alone is 201M of the 300M parameters.
        ["gemma"] = new(
            Kind: "gemma",
            FolderName: "embeddinggemma-300m",
            Dimensions: 768,
            Version: "embeddinggemma-300m-eval",
            ModelUrl: "https://huggingface.co/onnx-community/embeddinggemma-300m-ONNX/resolve/main/onnx/model_quantized.onnx",
            VocabUrl: "https://huggingface.co/onnx-community/embeddinggemma-300m-ONNX/resolve/main/tokenizer.model",
            PrebuiltTag: "unpublished")
        {
            Pooling = EmbeddingPooling.Pooled,
            TokenizerKind = EmbeddingTokenizer.SentencePiece,
            ModelDataUrl = "https://huggingface.co/onnx-community/embeddinggemma-300m-ONNX/resolve/main/onnx/model_quantized.onnx_data",
            BeginOfSequenceToken = 2,
            EndOfSequenceToken = 1,
            QueryPrefix = "task: search result | query: ",
            PassagePrefix = "title: none | text: ",
        },

        // The two mean-pooling families, which is why they needed EmbeddingPooling before they could
        // be scored at all. Their prefixes are not decoration and not interchangeable: e5 was trained
        // on "query: "/"passage: " as a matched pair, so using one side without the other is worse
        // than using neither, while gte was trained symmetrically and any prefix is noise to it.
        ["e5"] = new(
            Kind: "e5",
            FolderName: "e5-large-v2",
            Dimensions: 1024,
            Version: "e5-large-v2-eval",
            ModelUrl: "https://huggingface.co/Xenova/e5-large-v2/resolve/main/onnx/model_quantized.onnx",
            VocabUrl: "https://huggingface.co/Xenova/e5-large-v2/resolve/main/vocab.txt",
            PrebuiltTag: "unpublished")
        {
            Pooling = EmbeddingPooling.Mean,
            QueryPrefix = "query: ",
            PassagePrefix = "passage: ",
        },

        ["gte"] = new(
            Kind: "gte",
            FolderName: "gte-large",
            Dimensions: 1024,
            Version: "gte-large-eval",
            ModelUrl: "https://huggingface.co/Xenova/gte-large/resolve/main/onnx/model_quantized.onnx",
            VocabUrl: "https://huggingface.co/Xenova/gte-large/resolve/main/vocab.txt",
            PrebuiltTag: "unpublished")
        {
            Pooling = EmbeddingPooling.Mean,
            QueryPrefix = "",
            PassagePrefix = "",
        },
    };
}

file sealed class Factory : IHttpClientFactory
{
    public HttpClient CreateClient(string name)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Maki/1.0 (+https://github.com/OrbitMPGH/Maki)");
        return client;
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
            if (ex is not null)
            {
                // The message alone is useless for a model that fails to load: the reason is always
                // in the exception (a missing tokenizer token, a graph input, a CUDA fallback).
                Console.WriteLine($"        {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
