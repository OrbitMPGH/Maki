#:project ../src/Maki.Metadata/Maki.Metadata.csproj
#:package Microsoft.Data.Sqlite@10.0.9
#:package SQLitePCLRaw.bundle_e_sqlite3@3.0.3

// Groups the readers the co-read fetcher already collected into cohorts, and ships what each
// cohort finished and scored. Two surfaces read it: a "readers like you" score beside MangaBaka's
// aggregate in the Discover detail card, and a Discover rail seeded from what a reader's own
// cohorts read more than average.
//
// Run:
//   dotnet run distribution/build-reader-cohorts.cs
//   dotnet run distribution/build-reader-cohorts.cs -- --cohorts 24
//   dotnet run distribution/build-reader-cohorts.cs -- --fold-out 0/4 --out .artifacts/reader-cohorts-fold0.db
//
// WHY COHORTS AND NOT A PER-USER MODEL
// "Readers like you" is a neighbourhood question, and the neighbourhood is 35,000 real people
// whose per-title rows must never leave the machine that fetched them. Computing the neighbourhood
// HERE, once, and shipping only group aggregates is what makes the feature expressible at all: the
// artifact has no user axis, so no join restores a person and cohort membership is never written
// down. The instance places its own reader against those groups locally.
//
// WHY LIFT IS THE POINT, AND WHY THE RAW RATE IS NOT
// A cohort's completion RATE is dominated by whatever everybody reads: Berserk is finished by a
// large share of every cohort, so ranking on rate returns the same famous list to every reader.
// Measured on a predicted-score ranking of the same data, the top 40 was 87.6% identical across
// users and its median popularity rank was 688 of ~126,000. This tool therefore ships the global
// row alongside the cohort rows so the serving side can divide - what survives is what a cohort
// reads MORE than average, which is the only part that says anything about a person.
//
// SCORES ARE RAW HERE, DELIBERATELY, UNLIKE build-taste-vectors.cs
// That tool centres a score on its reader's own average, because it is learning a preference. This
// one reports a rating a human will read as a rating, so a cohort mean is the plain mean of the
// POINT_100 scores its members gave. Centring would produce a number that is no longer a score and
// cannot be shown beside MangaBaka's.
//
// FLOORS ARE A NOISE FLOOR, NOT ANONYMITY
// The source is public AniList lists, so a cell floor makes nobody anonymous who was not already
// public, and it does not stop an adversary differencing two published releases. What it does is
// stop a mean over three people being displayed as though it were an average. The claim carrying
// the privacy weight is the shape of the file, plus graph-artifact.cs's personal-data gate.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Maki.Metadata.Embedding;
using Microsoft.Data.Sqlite;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

var workPath = Path.Combine(".artifacts", "coread-graph.db");
var dumpPath = Path.Combine(".artifacts", "mangabaka.full.db");
var tastePath = Path.Combine(".artifacts", "taste-vectors.db");
var outPath = Path.Combine(".artifacts", "reader-cohorts.db");

var cohortCount = 24;
var iterations = 25;

// A reader with fewer placeable finishes than this has no stable direction to cluster on. They
// still count toward the global rows, which is what the taste page reads.
var minReaderItems = 5;

// A cohort smaller than this is a handful of people wearing a group's name. Its members are merged
// into their next-best cohort rather than dropped, so their reading is not lost.
var minCohortReaders = 200;

var minCohortCompletions = 5;
var minCohortRaters = 5;
var minGlobalCompletions = 3;
var minGlobalRaters = 5;

var foldOut = -1;
var foldCount = 0;
var seed = 20260902;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--work": workPath = args[++i]; break;
        case "--dump": dumpPath = args[++i]; break;
        case "--taste": tastePath = args[++i]; break;
        case "--out": outPath = args[++i]; break;
        case "--cohorts": cohortCount = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--iterations": iterations = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--min-reader-items": minReaderItems = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--min-cohort-readers": minCohortReaders = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--min-cohort-completions": minCohortCompletions = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--min-cohort-raters": minCohortRaters = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--min-global-completions": minGlobalCompletions = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--min-global-raters": minGlobalRaters = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--rng": seed = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        // Hold a slice of READERS out, so eval-reader-cohorts.cs can grade this artifact without it
        // having grouped the very readers it is being asked to predict.
        case "--fold-out":
            var parts = args[++i].Split('/');
            if (parts.Length != 2
                || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out foldOut)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out foldCount)
                || foldCount < 2 || foldOut < 0 || foldOut >= foldCount)
            {
                Console.WriteLine($"error: --fold-out wants k/n with n >= 2 and 0 <= k < n, not '{args[i]}'.");
                return 2;
            }

            break;
        default:
            Console.WriteLine($"error: unknown argument '{args[i]}'");
            return 2;
    }
}

if (cohortCount < 2)
{
    Console.WriteLine("error: --cohorts wants at least 2.");
    return 2;
}

foreach (var (name, path) in new[]
         {
             ("working database", workPath), ("dump", dumpPath), ("taste vectors", tastePath),
         })
{
    if (!File.Exists(path))
    {
        Console.WriteLine($"error: no {name} at {path}");
        return 2;
    }
}

Console.WriteLine($"work     : {workPath}");
Console.WriteLine($"dump     : {dumpPath}");
Console.WriteLine($"taste    : {tastePath}");
Console.WriteLine($"cohorts  : {cohortCount}, {iterations} k-means iterations, rng {seed}");
Console.WriteLine(
    $"floors   : cohort {minCohortCompletions} completions / {minCohortRaters} raters, " +
    $"global {minGlobalCompletions} / {minGlobalRaters}, cohort size {minCohortReaders}");
if (foldCount > 0)
{
    Console.WriteLine($"fold     : holding reader fold {foldOut} of {foldCount} OUT");
}

var clock = Stopwatch.StartNew();

// -------------------------------------------------------------------------------------------------
// Load: cross-reference, item space, and every COMPLETED row that resolves
// -------------------------------------------------------------------------------------------------

var crossRef = CrossReference(dumpPath);
Console.WriteLine(
    $"cross-ref: {crossRef.Count:N0} AniList ids map to recommendable series ({clock.Elapsed.TotalSeconds:F0}s)");

var (dims, tasteFold, tasteVectors) = LoadTaste(tastePath);
if (dims <= 0 || tasteVectors.Count == 0)
{
    Console.WriteLine($"error: {tastePath} carries no usable vectors; build it before this.");
    return 1;
}

Console.WriteLine(
    $"taste    : {tasteVectors.Count:N0} item vectors, {dims} dims, trainingFold '{tasteFold}' " +
    $"({clock.Elapsed.TotalSeconds:F0}s)");

// A fold-limited cohort build against an all-readers item space is leaky in a way nothing else
// would report: the clustering is honest, but the space the clustering happens in was fitted with
// the held-out readers in it. Refuse rather than produce a number somebody would trust.
if (foldCount > 0 && (tasteFold.Equals("all", StringComparison.OrdinalIgnoreCase)
                      || tasteFold.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                          .Contains(foldOut.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal)))
{
    Console.WriteLine(
        $"error: --fold-out {foldOut}/{foldCount} needs an item space that also held fold {foldOut} out,");
    Console.WriteLine($"       but {tastePath} reports trainingFold '{tasteFold}'.");
    Console.WriteLine($"       Build one with `build-taste-vectors.cs --fold-out {foldOut}/{foldCount}` and pass --taste.");
    return 1;
}

// Dense slots so the per-cohort accumulator can be a flat array rather than a dictionary keyed by a
// tuple. The item axis is MangaBaka ids, which is what every other artifact keys on.
var itemSlot = new Dictionary<long, int>(120_000);
var itemIds = new List<long>(120_000);
var userSlot = new Dictionary<int, int>(40_000);

// (reader, item, score), one flat list rather than per-user lists: 6.4M rows is ~50 MB this way and
// several hundred as a dictionary of lists.
var rows = new List<(int User, int Item, short Score)>(7_000_000);

using (var conn = new SqliteConnection($"Data Source={workPath};Mode=ReadOnly;Pooling=False"))
{
    conn.Open();
    using var cmd = conn.CreateCommand();

    // COMPLETED only, for the reason build-taste-vectors.cs settled on it: the question both
    // surfaces answer is "what did people finish, and what did they think of it". A PLANNING row
    // answers neither.
    cmd.CommandText = "SELECT user_id, media_id, score FROM user_entry WHERE status = 'COMPLETED'";
    cmd.CommandTimeout = 1800;
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        var user = (int)reader.GetInt64(0);
        if (foldCount > 0 && UserFold.Of(user, foldCount) == foldOut)
        {
            continue;
        }

        if (!crossRef.TryGetValue(reader.GetInt64(1), out var mangaBakaId))
        {
            continue;
        }

        if (!itemSlot.TryGetValue(mangaBakaId, out var item))
        {
            itemSlot[mangaBakaId] = item = itemIds.Count;
            itemIds.Add(mangaBakaId);
        }

        if (!userSlot.TryGetValue(user, out var u))
        {
            userSlot[user] = u = userSlot.Count;
        }

        var score = reader.GetInt32(2);
        rows.Add((u, item, (short)(score is > 0 and <= 100 ? score : 0)));
    }
}

var items = itemIds.Count;
var readers = userSlot.Count;
Console.WriteLine(
    $"rows     : {rows.Count:N0} completed over {readers:N0} readers x {items:N0} series " +
    $"({clock.Elapsed.TotalSeconds:F0}s)");

if (readers == 0 || items == 0)
{
    Console.WriteLine("error: nothing resolvable in the working database.");
    return 1;
}

// -------------------------------------------------------------------------------------------------
// Global rows. Over every reader, placed or not: this is also the reader baseline the taste page
// reads, and there is no reason to narrow that to the clusterable population.
// -------------------------------------------------------------------------------------------------

var globalCompletions = new int[items];
var globalRaters = new int[items];
var globalScoreSum = new long[items];

foreach (var (_, item, score) in rows)
{
    globalCompletions[item]++;
    if (score > 0)
    {
        globalRaters[item]++;
        globalScoreSum[item] += score;
    }
}

Console.WriteLine($"global   : aggregated ({clock.Elapsed.TotalSeconds:F0}s)");

// -------------------------------------------------------------------------------------------------
// Reader centroids, in the item space the behavioural artifact already defines
// -------------------------------------------------------------------------------------------------

// Reusing taste-vectors.db rather than fitting a second space: it is a measured artifact, it is
// already the app's notion of "these two series go together", and a reader's mean item vector is
// exactly what the factorization was fitted to predict.
var vectorBySlot = new float[items][];
for (var item = 0; item < items; item++)
{
    vectorBySlot[item] = tasteVectors.TryGetValue(itemIds[item], out var vec) ? vec : null!;
}

var centroids = new float[readers][];
var placeable = new int[readers];

foreach (var (user, item, _) in rows)
{
    if (vectorBySlot[item] is not { } vec)
    {
        continue;
    }

    var target = centroids[user] ??= new float[dims];
    for (var d = 0; d < dims; d++)
    {
        target[d] += vec[d];
    }

    placeable[user]++;
}

var clusterable = new List<int>(readers);
for (var user = 0; user < readers; user++)
{
    if (placeable[user] < minReaderItems || centroids[user] is null)
    {
        centroids[user] = null!;
        continue;
    }

    if (!Normalize(centroids[user]))
    {
        // A reader whose finishes cancel each other out has no direction, which is a real answer
        // rather than an error. They keep contributing to the global rows.
        centroids[user] = null!;
        continue;
    }

    clusterable.Add(user);
}

Console.WriteLine(
    $"centroids: {clusterable.Count:N0} of {readers:N0} readers placeable " +
    $"( >= {minReaderItems} finishes with a vector ) ({clock.Elapsed.TotalSeconds:F0}s)");

if (clusterable.Count < cohortCount * minCohortReaders)
{
    Console.WriteLine(
        $"error: {clusterable.Count:N0} placeable readers cannot fill {cohortCount} cohorts of " +
        $"{minCohortReaders}. Lower --cohorts or --min-cohort-readers.");
    return 1;
}

// -------------------------------------------------------------------------------------------------
// Spherical k-means
// -------------------------------------------------------------------------------------------------

var assignment = KMeans(
    centroids, clusterable, cohortCount, iterations, dims, seed,
    (pass, moved) => Console.WriteLine($"  iteration {pass}/{iterations}: {moved:N0} readers moved"));

// Merge undersized cohorts into their members' next-best surviving cohort, then renumber so the
// shipped ids are contiguous.
var sizes = new int[cohortCount];
foreach (var user in clusterable)
{
    sizes[assignment[user]]++;
}

var survivors = Enumerable.Range(0, cohortCount).Where(c => sizes[c] >= minCohortReaders).ToArray();
if (survivors.Length == 0)
{
    Console.WriteLine($"error: no cohort reached {minCohortReaders} readers. Lower --cohorts.");
    return 1;
}

if (survivors.Length < cohortCount)
{
    Console.WriteLine(
        $"cohorts  : {cohortCount - survivors.Length} under {minCohortReaders} readers, merging their members");
}

// Normalized before the merge compares against them: an unnormalized mean is longer for a bigger
// cluster, so the nearest surviving cohort would be decided by size rather than by direction.
var mergeMeans = CohortMeans(centroids, clusterable, assignment, cohortCount, dims);
foreach (var mean in mergeMeans)
{
    Normalize(mean);
}

var renumber = new int[cohortCount];
Array.Fill(renumber, -1);
for (var c = 0; c < survivors.Length; c++)
{
    renumber[survivors[c]] = c;
}

foreach (var user in clusterable)
{
    if (renumber[assignment[user]] >= 0)
    {
        continue;
    }

    var best = survivors[0];
    var bestScore = float.NegativeInfinity;
    foreach (var candidate in survivors)
    {
        var score = Dot(centroids[user], mergeMeans[candidate], dims);
        if (score > bestScore)
        {
            bestScore = score;
            best = candidate;
        }
    }

    assignment[user] = best;
}

var finalCount = survivors.Length;
var cohortOf = new int[readers];
Array.Fill(cohortOf, -1);
var cohortReaders = new int[finalCount];
foreach (var user in clusterable)
{
    var c = renumber[assignment[user]];
    cohortOf[user] = c;
    cohortReaders[c]++;
}

Console.WriteLine(
    $"cohorts  : {finalCount} kept, sizes {string.Join(", ", cohortReaders.OrderDescending())} " +
    $"({clock.Elapsed.TotalSeconds:F0}s)");

// -------------------------------------------------------------------------------------------------
// Per-cohort rows. Flat arrays rather than a dictionary: 24 x ~100k cells is ~40 MB and every
// increment is an indexed add.
// -------------------------------------------------------------------------------------------------

var cells = (long)finalCount * items;
if (cells > int.MaxValue)
{
    Console.WriteLine($"error: {finalCount} cohorts x {items:N0} items overflows a flat accumulator.");
    return 1;
}

var cohortCompletions = new int[cells];
var cohortRaters = new int[cells];
var cohortScoreSum = new long[cells];

foreach (var (user, item, score) in rows)
{
    var c = cohortOf[user];
    if (c < 0)
    {
        continue;
    }

    var cell = ((long)c * items) + item;
    cohortCompletions[cell]++;
    if (score > 0)
    {
        cohortRaters[cell]++;
        cohortScoreSum[cell] += score;
    }
}

Console.WriteLine($"cells    : aggregated ({clock.Elapsed.TotalSeconds:F0}s)");

// -------------------------------------------------------------------------------------------------
// Write
// -------------------------------------------------------------------------------------------------

// Recomputed over the FINAL assignment rather than reused from the merge pass, which was taken
// before undersized cohorts handed their members over.
var shippedMeans = new float[finalCount][];
for (var c = 0; c < finalCount; c++)
{
    shippedMeans[c] = new float[dims];
}

foreach (var user in clusterable)
{
    var target = shippedMeans[cohortOf[user]];
    var vec = centroids[user];
    for (var d = 0; d < dims; d++)
    {
        target[d] += vec[d];
    }
}

var centroidBlobs = new List<(float Scale, byte[] Blob)>(finalCount);
var quantBuffer = new sbyte[dims];
for (var c = 0; c < finalCount; c++)
{
    var mean = shippedMeans[c];
    Normalize(mean);
    var scale = EmbeddingMath.Quantize(mean, quantBuffer);
    var blob = new byte[dims];
    for (var d = 0; d < dims; d++)
    {
        blob[d] = unchecked((byte)quantBuffer[d]);
    }

    centroidBlobs.Add((scale, blob));
}

var written = Write(
    outPath, itemIds, globalCompletions, globalRaters, globalScoreSum,
    cohortCompletions, cohortRaters, cohortScoreSum, cohortReaders, centroidBlobs,
    finalCount, items, dims, tasteFold, minGlobalCompletions, minGlobalRaters,
    minCohortCompletions, minCohortRaters, minCohortReaders, minReaderItems,
    clusterable.Count, iterations, seed, foldCount, foldOut);

Console.WriteLine();
Console.WriteLine(
    $"done     : {outPath} ({new FileInfo(outPath).Length / 1024.0 / 1024.0:F1} MB, " +
    $"{written.GlobalRows:N0} global rows, {written.CohortRows:N0} cohort rows, " +
    $"{clock.Elapsed.TotalSeconds:F0}s total)");
return 0;

// -------------------------------------------------------------------------------------------------

static bool Normalize(float[] vec)
{
    var norm = 0.0;
    foreach (var value in vec)
    {
        norm += value * (double)value;
    }

    norm = Math.Sqrt(norm);
    if (norm <= 1e-6)
    {
        return false;
    }

    for (var d = 0; d < vec.Length; d++)
    {
        vec[d] = (float)(vec[d] / norm);
    }

    return true;
}

static float Dot(float[] a, float[] b, int dims)
{
    var sum = 0f;
    for (var d = 0; d < dims; d++)
    {
        sum += a[d] * b[d];
    }

    return sum;
}

/// <summary>
/// Mean direction of each cluster's members, unnormalized. Kept separate from the k-means loop so
/// the merge pass and the write can both ask for it without re-running the fit.
/// </summary>
static float[][] CohortMeans(float[][] centroids, List<int> members, int[] assignment, int k, int dims)
{
    var means = new float[k][];
    for (var c = 0; c < k; c++)
    {
        means[c] = new float[dims];
    }

    foreach (var user in members)
    {
        var target = means[assignment[user]];
        var vec = centroids[user];
        for (var d = 0; d < dims; d++)
        {
            target[d] += vec[d];
        }
    }

    return means;
}

/// <summary>
/// Spherical k-means over unit reader centroids. Deterministic given <paramref name="seed"/>, which
/// matters because two builds over one working database have to agree: the serving side caches a
/// reader's cohort weights, and a cohort numbering that moved between builds would make that cache
/// a lie.
/// </summary>
static int[] KMeans(
    float[][] centroids, List<int> members, int k, int iterations, int dims, int seed,
    Action<int, int> report)
{
    var rng = new Random(seed);
    var assignment = new int[centroids.Length];
    Array.Fill(assignment, -1);

    // k-means++ seeding: the first centre uniformly, each later one with probability proportional to
    // its squared distance from the nearest chosen centre. A uniform draw leaves two centres inside
    // the same dense genre often enough to matter at this k.
    var means = new float[k][];
    means[0] = (float[])centroids[members[rng.Next(members.Count)]].Clone();

    var nearest = new float[members.Count];
    Array.Fill(nearest, float.MaxValue);

    for (var c = 1; c < k; c++)
    {
        var total = 0.0;
        for (var m = 0; m < members.Count; m++)
        {
            // Both sides are unit vectors, so squared euclidean distance is 2 - 2cos.
            var distance = Math.Max(0f, 2f - (2f * Dot(centroids[members[m]], means[c - 1], dims)));
            nearest[m] = Math.Min(nearest[m], distance);
            total += nearest[m];
        }

        var target = rng.NextDouble() * total;
        var picked = members.Count - 1;
        for (var m = 0; m < members.Count; m++)
        {
            target -= nearest[m];
            if (target <= 0)
            {
                picked = m;
                break;
            }
        }

        means[c] = (float[])centroids[members[picked]].Clone();
    }

    for (var pass = 1; pass <= iterations; pass++)
    {
        var moved = 0;
        Parallel.ForEach(
            Partitioner.Create(0, members.Count),
            () => 0,
            (range, _, local) =>
            {
                for (var m = range.Item1; m < range.Item2; m++)
                {
                    var user = members[m];
                    var best = 0;
                    var bestScore = float.NegativeInfinity;
                    for (var c = 0; c < k; c++)
                    {
                        var score = Dot(centroids[user], means[c], dims);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = c;
                        }
                    }

                    if (assignment[user] != best)
                    {
                        assignment[user] = best;
                        local++;
                    }
                }

                return local;
            },
            local => Interlocked.Add(ref moved, local));

        var next = CohortMeans(centroids, members, assignment, k, dims);
        for (var c = 0; c < k; c++)
        {
            // An emptied cluster is re-seeded on the member furthest from its own centre rather than
            // left at zero, which would make it absorb everything on the next pass.
            if (!Normalize(next[c]))
            {
                next[c] = (float[])centroids[members[rng.Next(members.Count)]].Clone();
            }
        }

        means = next;
        report(pass, moved);
        if (moved == 0)
        {
            break;
        }
    }

    return assignment;
}

static Dictionary<long, long> CrossReference(string dumpPath)
{
    var map = new Dictionary<long, long>(150_000);
    using var conn = new SqliteConnection($"Data Source={dumpPath};Mode=ReadOnly;Pooling=False");
    conn.Open();
    using var cmd = conn.CreateCommand();
    // Ordered by popularity so the first row to claim an AniList id wins, the same collision rule
    // fetch-coread-graph.cs, build-taste-vectors.cs and eval-reco-labels.cs use.
    cmd.CommandText =
        """
        SELECT source_anilist_id, id
        FROM series
        WHERE state = 'active' AND type != 'novel' AND source_anilist_id IS NOT NULL
        ORDER BY COALESCE(popularity_global_current, 2147483647)
        """;
    cmd.CommandTimeout = 900;
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        map.TryAdd(reader.GetInt64(0), reader.GetInt64(1));
    }

    return map;
}

static (int Dims, string TrainingFold, Dictionary<long, float[]> Vectors) LoadTaste(string tastePath)
{
    using var conn = new SqliteConnection($"Data Source={tastePath};Mode=ReadOnly;Pooling=False");
    conn.Open();

    var dims = 0;
    var trainingFold = "?";
    using (var meta = conn.CreateCommand())
    {
        meta.CommandText = "SELECT key, value FROM meta WHERE key IN ('dimensions', 'trainingFold')";
        using var metaReader = meta.ExecuteReader();
        while (metaReader.Read())
        {
            if (metaReader.GetString(0) == "dimensions")
            {
                int.TryParse(metaReader.GetString(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out dims);
            }
            else
            {
                trainingFold = metaReader.GetString(1);
            }
        }
    }

    var vectors = new Dictionary<long, float[]>(70_000);
    if (dims <= 0)
    {
        return (0, trainingFold, vectors);
    }

    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT id, scale, vec FROM item_vectors";
    cmd.CommandTimeout = 900;
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        var scale = (float)reader.GetDouble(1);
        var blob = (byte[])reader[2];
        // `scale IS NULL OR NOT (scale > 0)` in query form: SQLite stores a NaN as NULL, and a row
        // whose scale went wrong upstream must be skipped rather than dequantized to nonsense.
        if (blob.Length != dims || reader.IsDBNull(1) || !(scale > 0))
        {
            continue;
        }

        if (EmbeddingMath.FromQuantizedBlob(blob, scale) is { } vec)
        {
            vectors[reader.GetInt64(0)] = vec;
        }
    }

    return (dims, trainingFold, vectors);
}

static (int GlobalRows, int CohortRows) Write(
    string outPath, List<long> itemIds,
    int[] globalCompletions, int[] globalRaters, long[] globalScoreSum,
    int[] cohortCompletions, int[] cohortRaters, long[] cohortScoreSum,
    int[] cohortReaders, List<(float Scale, byte[] Blob)> centroids,
    int cohorts, int items, int dims, string tasteFold,
    int minGlobalCompletions, int minGlobalRaters, int minCohortCompletions, int minCohortRaters,
    int minCohortReaders, int minReaderItems, int placedReaders, int iterations, int seed,
    int foldCount, int foldOut)
{
    if (File.Exists(outPath))
    {
        File.Delete(outPath);
    }

    var dir = Path.GetDirectoryName(outPath);
    if (!string.IsNullOrEmpty(dir))
    {
        Directory.CreateDirectory(dir);
    }

    using var conn = new SqliteConnection($"Data Source={outPath};Pooling=False");
    conn.Open();
    Execute(conn, "PRAGMA journal_mode = OFF");
    Execute(conn, "PRAGMA synchronous = OFF");
    Execute(
        conn,
        """
        CREATE TABLE cohort (
            cohort  INTEGER PRIMARY KEY,
            readers INTEGER NOT NULL,
            scale   REAL    NOT NULL,
            vec     BLOB    NOT NULL
        )
        """);
    Execute(
        conn,
        """
        CREATE TABLE cohort_item (
            cohort      INTEGER NOT NULL,
            id          INTEGER NOT NULL,
            completions INTEGER NOT NULL,
            raters      INTEGER NOT NULL,
            mean        REAL,
            PRIMARY KEY (cohort, id)
        ) WITHOUT ROWID
        """);
    Execute(
        conn,
        """
        CREATE TABLE item_global (
            id          INTEGER PRIMARY KEY,
            completions INTEGER NOT NULL,
            raters      INTEGER NOT NULL,
            mean        REAL
        )
        """);
    Execute(conn, "CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL)");

    using (var tx = conn.BeginTransaction())
    {
        using var insert = conn.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = "INSERT INTO cohort (cohort, readers, scale, vec) VALUES ($c, $r, $s, $v)";
        var pc = insert.Parameters.Add("$c", SqliteType.Integer);
        var pr = insert.Parameters.Add("$r", SqliteType.Integer);
        var ps = insert.Parameters.Add("$s", SqliteType.Real);
        var pv = insert.Parameters.Add("$v", SqliteType.Blob);
        for (var c = 0; c < cohorts; c++)
        {
            pc.Value = c;
            pr.Value = cohortReaders[c];
            ps.Value = centroids[c].Scale;
            pv.Value = centroids[c].Blob;
            insert.ExecuteNonQuery();
        }

        tx.Commit();
    }

    var globalRows = 0;
    var completionsKept = new List<int>(items);
    using (var tx = conn.BeginTransaction())
    {
        using var insert = conn.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText =
            "INSERT INTO item_global (id, completions, raters, mean) VALUES ($i, $c, $r, $m)";
        var pi = insert.Parameters.Add("$i", SqliteType.Integer);
        var pc = insert.Parameters.Add("$c", SqliteType.Integer);
        var pr = insert.Parameters.Add("$r", SqliteType.Integer);
        var pm = insert.Parameters.Add("$m", SqliteType.Real);
        for (var item = 0; item < items; item++)
        {
            if (globalCompletions[item] < minGlobalCompletions)
            {
                continue;
            }

            // Completions and raters carry their own floors because they are different populations:
            // a series plenty of people finished and few scored still belongs in the reader
            // baseline, it just has no mean to offer.
            var rated = globalRaters[item] >= minGlobalRaters;
            pi.Value = itemIds[item];
            pc.Value = globalCompletions[item];
            pr.Value = rated ? globalRaters[item] : 0;
            pm.Value = rated ? globalScoreSum[item] / (double)globalRaters[item] : DBNull.Value;
            insert.ExecuteNonQuery();
            globalRows++;
            completionsKept.Add(globalCompletions[item]);
        }

        tx.Commit();
    }

    var cohortRows = 0;
    using (var tx = conn.BeginTransaction())
    {
        using var insert = conn.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText =
            "INSERT INTO cohort_item (cohort, id, completions, raters, mean) VALUES ($k, $i, $c, $r, $m)";
        var pk = insert.Parameters.Add("$k", SqliteType.Integer);
        var pi = insert.Parameters.Add("$i", SqliteType.Integer);
        var pc = insert.Parameters.Add("$c", SqliteType.Integer);
        var pr = insert.Parameters.Add("$r", SqliteType.Integer);
        var pm = insert.Parameters.Add("$m", SqliteType.Real);
        for (var c = 0; c < cohorts; c++)
        {
            for (var item = 0; item < items; item++)
            {
                var cell = ((long)c * items) + item;
                var completions = cohortCompletions[cell];
                var raters = cohortRaters[cell];
                if (completions < minCohortCompletions && raters < minCohortRaters)
                {
                    continue;
                }

                var rated = raters >= minCohortRaters;
                pk.Value = c;
                pi.Value = itemIds[item];
                pc.Value = completions;
                pr.Value = rated ? raters : 0;
                pm.Value = rated ? cohortScoreSum[cell] / (double)raters : DBNull.Value;
                insert.ExecuteNonQuery();
                cohortRows++;
            }
        }

        tx.Commit();
    }

    // The lift denominator saturates on a handful of megahits, so the serving side scales
    // completions against a high percentile rather than the max. Computed here because this is the
    // only place that has the whole distribution.
    completionsKept.Sort();
    var p99 = completionsKept.Count == 0
        ? 0
        : completionsKept[Math.Min(completionsKept.Count - 1, (int)(completionsKept.Count * 0.99))];

    // Which reader folds this artifact was BUILT from. eval-reader-cohorts.cs refuses to grade an
    // artifact whose training folds include the fold being evaluated, so this is enforcement rather
    // than documentation.
    var trainedOn = foldCount == 0
        ? "all"
        : string.Join(',', Enumerable.Range(0, foldCount).Where(f => f != foldOut));

    using (var tx = conn.BeginTransaction())
    {
        using var meta = conn.CreateCommand();
        meta.Transaction = tx;
        meta.CommandText = "INSERT INTO meta (key, value) VALUES ($k, $v)";
        var pk = meta.Parameters.Add("$k", SqliteType.Text);
        var pv = meta.Parameters.Add("$v", SqliteType.Text);
        foreach (var (k, v) in new (string, string)[]
                 {
                     ("schemaVersion", "1"),
                     ("generatedAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
                     ("cohortCount", cohorts.ToString(CultureInfo.InvariantCulture)),
                     ("dimensions", dims.ToString(CultureInfo.InvariantCulture)),
                     ("itemCount", globalRows.ToString(CultureInfo.InvariantCulture)),
                     ("cohortItemCount", cohortRows.ToString(CultureInfo.InvariantCulture)),
                     ("trainedReaders", placedReaders.ToString(CultureInfo.InvariantCulture)),
                     ("trainingFold", trainedOn),
                     // Which folds the ITEM SPACE saw. A cohort artifact is only as held-out as the
                     // vectors its clustering ran in, and nothing else on the file would say so.
                     ("tasteTrainingFold", tasteFold),
                     ("completionP99", p99.ToString(CultureInfo.InvariantCulture)),
                     // Two artifacts of the same cohort count are not comparable if these differ,
                     // and nothing else on the file would say so.
                     ("minCohortCompletions", minCohortCompletions.ToString(CultureInfo.InvariantCulture)),
                     ("minCohortRaters", minCohortRaters.ToString(CultureInfo.InvariantCulture)),
                     ("minGlobalCompletions", minGlobalCompletions.ToString(CultureInfo.InvariantCulture)),
                     ("minGlobalRaters", minGlobalRaters.ToString(CultureInfo.InvariantCulture)),
                     ("minCohortReaders", minCohortReaders.ToString(CultureInfo.InvariantCulture)),
                     ("minReaderItems", minReaderItems.ToString(CultureInfo.InvariantCulture)),
                     ("iterations", iterations.ToString(CultureInfo.InvariantCulture)),
                     ("rng", seed.ToString(CultureInfo.InvariantCulture)),
                     ("statuses", "COMPLETED"),
                     ("source", "anilist-lists-cohorts"),
                 })
        {
            pk.Value = k;
            pv.Value = v;
            meta.ExecuteNonQuery();
        }

        tx.Commit();
    }

    Execute(conn, "VACUUM");
    return (globalRows, cohortRows);
}

static void Execute(SqliteConnection conn, string sql)
{
    using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.CommandTimeout = 3600;
    cmd.ExecuteNonQuery();
}

/// <summary>
/// Which evaluation fold a reader belongs to. MUST stay byte-identical to the copies in
/// build-taste-vectors.cs and eval-reco-labels.cs: a builder that partitions readers differently
/// from the grader produces an artifact that trained on part of the evaluation set while honestly
/// reporting that it did not. <c>HashCode.Combine</c> cannot be used - .NET randomizes its seed per
/// process.
/// </summary>
file static class UserFold
{
    public static int Of(long userId, int folds)
    {
        var hash = 2166136261u;
        var value = (ulong)userId;
        for (var i = 0; i < 8; i++)
        {
            hash = (hash ^ (byte)(value >> (i * 8))) * 16777619u;
        }

        return (int)(hash % (uint)folds);
    }
}
