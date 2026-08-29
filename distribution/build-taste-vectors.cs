#:project ../src/Maki.Metadata/Maki.Metadata.csproj
#:package Microsoft.Data.Sqlite@10.0.9
#:package SQLitePCLRaw.bundle_e_sqlite3@3.0.3

// Learns an item vector per series from the reading lists the co-read fetcher already collected,
// and ships it as an artifact the recommender loads beside the text index.
//
// Run:
//   dotnet run distribution/build-taste-vectors.cs
//   dotnet run distribution/build-taste-vectors.cs -- --dims 128 --iterations 15
//   dotnet run distribution/build-taste-vectors.cs -- --fold-out 0/4 --out .artifacts/taste-fold0.db
//
// WHY A VECTOR AND NOT MORE OF THE SAME GRAPH
// coread-edges.db answers "did at least three of 19,667 readers finish both of these?". That is a
// lookup, and it is empty for most of the catalogue: 41,054 of the 126,323 indexed rows, 32.5%.
// Factorizing the same interactions instead gives every item with enough evidence a position in one
// space, so a similarity exists for pairs nobody was ever observed to share. Measured on the
// current working database, items with >= 5 interactions that are also in the index: 89,374, which
// is 71% of the index against the graph's 32.5%.
//
// WHAT IS BEING SPENT THAT THE GRAPH THROWS AWAY
// coread-graph.db holds 11,302,050 rows. The graph is built from the 4,326,424 COMPLETED ones.
// The rest is not noise:
//
//   PLANNING    3,275,937   intent. The thing a recommender is literally trying to predict.
//   CURRENT     2,647,485   in progress, so at minimum "started and did not quit".
//   DROPPED       579,171   ANTI-evidence, and nothing in v3 can express it.
//   PAUSED        462,754   weak.
//   REPEATING      10,279   a re-read. The strongest positive signal a reader can emit.
//
// plus 3,647,125 explicit 0-100 scores across 17,347 readers.
//
// HOW A DISLIKE IS EXPRESSED, GIVEN CONFIDENCE MUST STAY POSITIVE
// This is implicit ALS, where every cell has a preference p in {0,1} and a confidence c >= 1. An
// unobserved cell is p=0 at c=1: "probably not, but we have no idea". A DROPPED row is also p=0 -
// but at HIGH confidence: "we know they saw it and stopped". Same for a title someone finished and
// then scored well below their own average. The negative therefore needs no special case in the
// solver; it is the confidence that carries it, which is exactly what the formulation is for.
//
// SCORES ARE CENTRED PER READER, NEVER USED RAW
// A 70 from someone whose average is 85 is a complaint; the same 70 from someone averaging 60 is
// praise. The distribution is also spiky at multiples of 10 (723,734 rows at exactly 70, 557,631 at
// 80), so a raw threshold lands on a mode and moves a large block of rows at once.

using System.Diagnostics;
using System.Globalization;
using Maki.Metadata.Embedding;
using Microsoft.Data.Sqlite;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

var workPath = Path.Combine(".artifacts", "coread-graph.db");
var dumpPath = Path.Combine(".artifacts", "mangabaka.full.db");
var outPath = Path.Combine(".artifacts", "taste-vectors.db");
var dims = 128;
var iterations = 15;
var lambda = 8.0f;
var alpha = 12.0f;
var minInteractions = 5;
var foldOut = -1;
var foldCount = 0;
var threads = Environment.ProcessorCount;
var seed = 20260829;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--work": workPath = args[++i]; break;
        case "--dump": dumpPath = args[++i]; break;
        case "--out": outPath = args[++i]; break;
        case "--dims": dims = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--iterations": iterations = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--lambda": lambda = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--alpha": alpha = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--min-interactions": minInteractions = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--threads": threads = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--rng": seed = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        // Hold a slice of READERS out, so eval-reco-labels.cs --fold-users can grade this artifact
        // without it having learned from the very lists it is being asked to predict.
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

foreach (var (name, path) in new[] { ("working database", workPath), ("dump", dumpPath) })
{
    if (!File.Exists(path))
    {
        Console.WriteLine($"error: no {name} at {path}");
        return 2;
    }
}

Console.WriteLine($"work     : {workPath}");
Console.WriteLine($"dump     : {dumpPath}");
Console.WriteLine($"factors  : {dims} dims, {iterations} iterations, lambda {lambda}, alpha {alpha}");
Console.WriteLine($"threads  : {threads}");
if (foldCount > 0)
{
    Console.WriteLine($"fold     : holding reader fold {foldOut} of {foldCount} OUT of training");
}

var clock = Stopwatch.StartNew();

// -------------------------------------------------------------------------------------------------
// Load
// -------------------------------------------------------------------------------------------------

var raw = new List<(int User, long Media, float Weight, bool Positive)>(12_000_000);
var userMean = new Dictionary<int, (double Sum, int Count)>();
var userSize = new Dictionary<int, int>();

using (var conn = new SqliteConnection($"Data Source={workPath};Mode=ReadOnly;Pooling=False"))
{
    conn.Open();

    // Two passes rather than one: a reader's score has to be read against their OWN average, and
    // that average is not known until their whole list has been seen.
    using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "SELECT user_id, score FROM user_entry";
        cmd.CommandTimeout = 1800;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var user = (int)reader.GetInt64(0);
            userSize[user] = userSize.GetValueOrDefault(user) + 1;
            var score = reader.GetInt32(1);
            if (score > 0)
            {
                var (sum, count) = userMean.GetValueOrDefault(user);
                userMean[user] = (sum + score, count + 1);
            }
        }
    }

    Console.WriteLine($"readers  : {userSize.Count:N0} lists, {userMean.Count:N0} of them scoring anything ({clock.Elapsed.TotalSeconds:F0}s)");

    using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "SELECT user_id, media_id, score, status FROM user_entry";
        cmd.CommandTimeout = 1800;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var user = (int)reader.GetInt64(0);
            if (foldCount > 0 && UserFold.Of(user, foldCount) == foldOut)
            {
                continue;
            }

            var status = reader.GetString(3);
            var statusWeight = StatusWeight(status);
            if (statusWeight <= 0)
            {
                continue;
            }

            var positive = status != "DROPPED";
            var weight = statusWeight;

            var score = reader.GetInt32(2);
            if (score > 0 && userMean.TryGetValue(user, out var stat) && stat.Count >= 5)
            {
                // Spread is fixed rather than measured per reader: a reader who gives everything
                // 80 has zero variance, and dividing by it turns a one-point difference into a
                // certainty. 15 points is roughly the population's own spread.
                var z = Math.Clamp((score - (stat.Sum / stat.Count)) / 15.0, -2.0, 2.0);
                if (z < -0.75)
                {
                    // Finished it and rated it well under their own bar. That is a dislike, and the
                    // only way to say so here is p=0 at a confidence that grows with how far under.
                    positive = false;
                    weight = statusWeight * (float)Math.Abs(z);
                }
                else
                {
                    weight = statusWeight * (float)(1.0 + (0.5 * z));
                }
            }

            // A reader with 4,000 entries is not 14 times more informative than one with 278; they
            // are a completionist. Same normalization the co-read fold already applies, centred so
            // a typical list comes out near 1 rather than shrinking everything.
            var size = userSize.GetValueOrDefault(user, 1);
            weight *= (float)(Math.Log(1 + 256) / Math.Log(1 + Math.Max(1, size)));

            if (weight > 0)
            {
                raw.Add((user, reader.GetInt64(1), weight, positive));
            }
        }
    }
}

Console.WriteLine($"loaded   : {raw.Count:N0} interactions ({clock.Elapsed.TotalSeconds:F0}s)");

// -------------------------------------------------------------------------------------------------
// Index and prune
// -------------------------------------------------------------------------------------------------

var mediaCount = new Dictionary<long, int>(200_000);
foreach (var (_, media, _, _) in raw)
{
    mediaCount[media] = mediaCount.GetValueOrDefault(media) + 1;
}

var itemSlot = new Dictionary<long, int>(mediaCount.Count);
var itemMedia = new List<long>(mediaCount.Count);
foreach (var (media, count) in mediaCount)
{
    if (count >= minInteractions)
    {
        itemSlot[media] = itemMedia.Count;
        itemMedia.Add(media);
    }
}

var userSlot = new Dictionary<int, int>(userSize.Count);
var kept = new List<(int U, int I, float W, bool P)>(raw.Count);
foreach (var (user, media, weight, positive) in raw)
{
    if (!itemSlot.TryGetValue(media, out var item))
    {
        continue;
    }

    if (!userSlot.TryGetValue(user, out var u))
    {
        userSlot[user] = u = userSlot.Count;
    }

    kept.Add((u, item, weight, positive));
}

raw.Clear();
raw.TrimExcess();

var users = userSlot.Count;
var items = itemMedia.Count;
Console.WriteLine(
    $"matrix   : {users:N0} readers x {items:N0} items ( >= {minInteractions} interactions ), {kept.Count:N0} cells, " +
    $"{kept.Count(k => !k.P):N0} of them negative");

if (items == 0 || users == 0)
{
    Console.WriteLine("error: nothing left to train on.");
    return 1;
}

// Confidence is stored signed: c for a positive cell, -c for a negative one. Both directions of the
// matrix need c and p on every cell, and a parallel bit array would double the random access in the
// hottest loop there is.
var byUser = Csr.Build(users, kept, k => k.U, k => k.I, k => k.P ? 1 + (alpha * k.W) : -(1 + (alpha * k.W)));
var byItem = Csr.Build(items, kept, k => k.I, k => k.U, k => k.P ? 1 + (alpha * k.W) : -(1 + (alpha * k.W)));
kept.Clear();
kept.TrimExcess();

Console.WriteLine($"csr      : built ({clock.Elapsed.TotalSeconds:F0}s)");

// -------------------------------------------------------------------------------------------------
// Train
// -------------------------------------------------------------------------------------------------

var rng = new Random(seed);
var x = new float[users * dims];
var y = new float[items * dims];
// Small random start. Zeros are a stationary point of ALS: YtY stays zero, every solve returns
// zero, and fifteen iterations produce nothing at all.
for (var i = 0; i < x.Length; i++) { x[i] = (float)((rng.NextDouble() - 0.5) * 0.01); }
for (var i = 0; i < y.Length; i++) { y[i] = (float)((rng.NextDouble() - 0.5) * 0.01); }

var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads) };
for (var iteration = 1; iteration <= iterations; iteration++)
{
    var pass = Stopwatch.StartNew();
    Als.Solve(x, y, byUser, users, items, dims, lambda, parallel);
    Als.Solve(y, x, byItem, items, users, dims, lambda, parallel);
    Console.WriteLine($"  iteration {iteration}/{iterations}: {pass.Elapsed.TotalSeconds:F0}s");
}

// -------------------------------------------------------------------------------------------------
// Map, quantize, write
// -------------------------------------------------------------------------------------------------

var crossRef = CrossReference(dumpPath);
Console.WriteLine($"cross-ref: {crossRef.Count:N0} AniList ids map to recommendable series");

var vectors = new List<(long Id, float Scale, byte[] Blob)>(items);
var seen = new HashSet<long>(items);
var buffer = new sbyte[dims];
var unit = new float[dims];
for (var item = 0; item < items; item++)
{
    if (!crossRef.TryGetValue(itemMedia[item], out var mangaBakaId) || !seen.Add(mangaBakaId))
    {
        continue;
    }

    var norm = 0.0;
    for (var d = 0; d < dims; d++)
    {
        norm += y[(item * dims) + d] * (double)y[(item * dims) + d];
    }

    norm = Math.Sqrt(norm);
    if (norm <= 1e-6)
    {
        // A factor that never moved off its random start carries no evidence, and shipping it would
        // put a near-random direction in the index where "no data" is the honest answer.
        continue;
    }

    for (var d = 0; d < dims; d++)
    {
        unit[d] = (float)(y[(item * dims) + d] / norm);
    }

    // Same int8 packing the text index uses, so the serving side needs one dot-product routine and
    // not two.
    var scale = EmbeddingMath.Quantize(unit, buffer);
    var blob = new byte[dims];
    for (var d = 0; d < dims; d++)
    {
        blob[d] = unchecked((byte)buffer[d]);
    }

    vectors.Add((mangaBakaId, scale, blob));
}

Console.WriteLine($"vectors  : {vectors.Count:N0} mapped to MangaBaka ids");

Write(outPath, vectors, dims, foldCount, foldOut, users, items);

Console.WriteLine();
Console.WriteLine($"done     : {outPath} ({new FileInfo(outPath).Length / 1024.0 / 1024.0:F1} MB, {clock.Elapsed.TotalSeconds:F0}s total)");
return 0;

// -------------------------------------------------------------------------------------------------

/// <summary>
/// How much a row of somebody's list is worth before their score is taken into account. DROPPED
/// carries a full weight because a dropped title is a confident statement, not a weak one - the
/// weight says how sure we are, and <c>p = 0</c> says which way.
/// </summary>
static float StatusWeight(string status) => status switch
{
    "REPEATING" => 1.6f,
    "COMPLETED" => 1.0f,
    "CURRENT" => 0.7f,
    "DROPPED" => 1.0f,
    "PAUSED" => 0.3f,
    "PLANNING" => 0.25f,
    _ => 0f,
};

static Dictionary<long, long> CrossReference(string dumpPath)
{
    var map = new Dictionary<long, long>(150_000);
    using var conn = new SqliteConnection($"Data Source={dumpPath};Mode=ReadOnly;Pooling=False");
    conn.Open();
    using var cmd = conn.CreateCommand();
    // Ordered by popularity so the first row to claim an AniList id wins, the same collision rule
    // fetch-coread-graph.cs and eval-reco-labels.cs use.
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

static void Write(
    string outPath, List<(long Id, float Scale, byte[] Blob)> vectors, int dims,
    int foldCount, int foldOut, int users, int items)
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
        CREATE TABLE item_vectors (
            id    INTEGER PRIMARY KEY,
            scale REAL    NOT NULL,
            vec   BLOB    NOT NULL
        )
        """);
    Execute(conn, "CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL)");

    using (var tx = conn.BeginTransaction())
    {
        using var insert = conn.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = "INSERT INTO item_vectors (id, scale, vec) VALUES ($i, $s, $v)";
        var pi = insert.Parameters.Add("$i", SqliteType.Integer);
        var ps = insert.Parameters.Add("$s", SqliteType.Real);
        var pv = insert.Parameters.Add("$v", SqliteType.Blob);
        foreach (var (id, scale, blob) in vectors)
        {
            pi.Value = id;
            ps.Value = scale;
            pv.Value = blob;
            insert.ExecuteNonQuery();
        }

        tx.Commit();
    }

    // Which reader folds this artifact LEARNED from. eval-reco-labels.cs --fold-users refuses to
    // grade an artifact whose training folds include the fold it is evaluating on, so this is not
    // documentation - it is the enforcement.
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
            ("dimensions", dims.ToString(CultureInfo.InvariantCulture)),
            ("itemCount", vectors.Count.ToString(CultureInfo.InvariantCulture)),
            ("trainedItems", items.ToString(CultureInfo.InvariantCulture)),
            ("trainedReaders", users.ToString(CultureInfo.InvariantCulture)),
            ("trainingFold", trainedOn),
            ("source", "anilist-lists-ials"),
        })
        {
            pk.Value = k;
            pv.Value = v;
            meta.ExecuteNonQuery();
        }

        tx.Commit();
    }

    Execute(conn, "VACUUM");
}

static void Execute(SqliteConnection conn, string sql)
{
    using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.CommandTimeout = 3600;
    cmd.ExecuteNonQuery();
}

/// <summary>
/// Which evaluation fold a reader belongs to. MUST stay byte-identical to the copy in
/// eval-reco-labels.cs: a builder that partitions readers differently from the grader produces an
/// artifact that trained on part of the evaluation set while honestly reporting that it did not.
/// <c>HashCode.Combine</c> cannot be used - .NET randomizes its seed per process.
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

/// <summary>Compressed sparse row over the interaction matrix, one instance per direction.</summary>
file sealed class Csr
{
    public required int[] Offsets { get; init; }

    public required int[] Indices { get; init; }

    /// <summary>Confidence, signed: negative means the cell's preference is 0 rather than 1.</summary>
    public required float[] Values { get; init; }

    public static Csr Build<T>(int rows, List<T> cells, Func<T, int> row, Func<T, int> column, Func<T, float> value)
    {
        var offsets = new int[rows + 1];
        foreach (var cell in cells)
        {
            offsets[row(cell) + 1]++;
        }

        for (var i = 0; i < rows; i++)
        {
            offsets[i + 1] += offsets[i];
        }

        var cursor = (int[])offsets.Clone();
        var indices = new int[cells.Count];
        var values = new float[cells.Count];
        foreach (var cell in cells)
        {
            var slot = cursor[row(cell)]++;
            indices[slot] = column(cell);
            values[slot] = value(cell);
        }

        return new Csr { Offsets = offsets, Indices = indices, Values = values };
    }
}

/// <summary>
/// Per-thread working buffers. Allocated once per partition by <c>Parallel.For</c>'s local-state
/// overload rather than per row: at 11M cells the allocation alone would dominate the solve.
/// </summary>
file sealed class Scratch(int dims)
{
    public float[] B { get; } = new float[dims];

    public float[] R { get; } = new float[dims];

    public float[] P { get; } = new float[dims];

    public float[] Ap { get; } = new float[dims];
}

/// <summary>
/// The implicit-ALS half-step, kept as plain statics over <c>float[]</c> rather than closures over
/// spans: this is the only hot loop in the tool and everything about it is deliberate.
/// </summary>
file static class Als
{
    /// <summary>
    /// Holds <paramref name="fixedFactors"/> still and re-solves every row of
    /// <paramref name="solveFor"/>.
    ///
    /// <para>
    /// Conjugate gradient rather than an exact Cholesky. The exact solve is O(d^3) per row on top of
    /// O(nnz * d^2), which at 128 dimensions over 11M cells is the difference between a run that
    /// finishes over lunch and one that does not. Three steps is the standard choice and still
    /// converges across iterations, because each one starts from the previous solution rather than
    /// from scratch.
    /// </para>
    /// </summary>
    public static void Solve(
        float[] solveFor, float[] fixedFactors, Csr matrix, int rows, int otherRows, int dims,
        float lambda, ParallelOptions parallel)
    {
        // The Gramian of everything, computed once per half-step. The confidence of an UNOBSERVED
        // cell is 1, so it contributes identically to every row's normal equations and must not be
        // walked per row - that identity is the whole reason implicit ALS is tractable.
        var gram = new float[dims * dims];
        for (var r = 0; r < otherRows; r++)
        {
            var offset = r * dims;
            for (var a = 0; a < dims; a++)
            {
                var va = fixedFactors[offset + a];
                if (va == 0)
                {
                    continue;
                }

                var gramRow = a * dims;
                for (var b = 0; b < dims; b++)
                {
                    gram[gramRow + b] += va * fixedFactors[offset + b];
                }
            }
        }

        Parallel.For(0, rows, parallel, () => new Scratch(dims), (row, _, scratch) =>
        {
            SolveRow(solveFor, fixedFactors, matrix, row, dims, lambda, gram, scratch);
            return scratch;
        }, _ => { });
    }

    private static void SolveRow(
        float[] solveFor, float[] fixedFactors, Csr matrix, int row, int dims, float lambda,
        float[] gram, Scratch scratch)
    {
        var start = matrix.Offsets[row];
        var end = matrix.Offsets[row + 1];
        var target = row * dims;

        // b = sum over observed cells of c * p * y_i. A negative cell has p = 0, so it contributes
        // nothing here and appears only in the matrix. That is what makes it "confidently not this"
        // rather than "confidently the opposite of this".
        var b = scratch.B;
        Array.Clear(b);
        for (var k = start; k < end; k++)
        {
            var confidence = matrix.Values[k];
            if (confidence <= 0)
            {
                continue;
            }

            var offset = matrix.Indices[k] * dims;
            for (var d = 0; d < dims; d++)
            {
                b[d] += confidence * fixedFactors[offset + d];
            }
        }

        var residual = scratch.R;
        var direction = scratch.P;
        var product = scratch.Ap;

        Multiply(solveFor, target, product, fixedFactors, matrix, start, end, dims, lambda, gram);
        var rr = 0.0;
        for (var d = 0; d < dims; d++)
        {
            residual[d] = b[d] - product[d];
            direction[d] = residual[d];
            rr += residual[d] * (double)residual[d];
        }

        for (var step = 0; step < 3 && rr > 1e-10; step++)
        {
            Multiply(direction, 0, product, fixedFactors, matrix, start, end, dims, lambda, gram);
            var denominator = 0.0;
            for (var d = 0; d < dims; d++)
            {
                denominator += direction[d] * (double)product[d];
            }

            if (denominator <= 1e-12)
            {
                break;
            }

            var stepSize = rr / denominator;
            var next = 0.0;
            for (var d = 0; d < dims; d++)
            {
                solveFor[target + d] += (float)(stepSize * direction[d]);
                residual[d] -= (float)(stepSize * product[d]);
                next += residual[d] * (double)residual[d];
            }

            var beta = next / rr;
            rr = next;
            for (var d = 0; d < dims; d++)
            {
                direction[d] = (float)(residual[d] + (beta * direction[d]));
            }
        }
    }

    /// <summary>
    /// A * v where A = YtY + sum over observed cells of (|c| - 1) y_i y_i^T + lambda * I, applied
    /// without ever materializing A.
    ///
    /// <para>
    /// <c>|c|</c> and not <c>c</c>: a negative cell still tells the solver this row has evidence in
    /// that direction and the factor must account for it. The sign lives in <c>b</c>, where the cell
    /// simply does not appear. Using the signed value here would subtract confidence from the normal
    /// equations and can make A indefinite, at which point conjugate gradient stops being valid.
    /// </para>
    /// </summary>
    private static void Multiply(
        float[] v, int vOffset, float[] result, float[] fixedFactors, Csr matrix, int start, int end,
        int dims, float lambda, float[] gram)
    {
        for (var a = 0; a < dims; a++)
        {
            var sum = 0.0;
            var gramRow = a * dims;
            for (var b = 0; b < dims; b++)
            {
                sum += gram[gramRow + b] * v[vOffset + b];
            }

            result[a] = (float)sum + (lambda * v[vOffset + a]);
        }

        for (var k = start; k < end; k++)
        {
            var offset = matrix.Indices[k] * dims;
            var dot = 0.0;
            for (var d = 0; d < dims; d++)
            {
                dot += fixedFactors[offset + d] * (double)v[vOffset + d];
            }

            var extra = (Math.Abs(matrix.Values[k]) - 1) * dot;
            for (var d = 0; d < dims; d++)
            {
                result[d] += (float)(extra * fixedFactors[offset + d]);
            }
        }
    }
}
