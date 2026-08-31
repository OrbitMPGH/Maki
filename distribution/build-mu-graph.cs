#:package Microsoft.Data.Sqlite@10.0.9
#:package SQLitePCLRaw.bundle_e_sqlite3@3.0.3

// Folds MangaUpdates' two recommendation lists out of the MangaBaka full dump into the same
// pair-table shape reco-edges.db and coread-edges.db already use.
//
// Run:
//   dotnet run distribution/build-mu-graph.cs
//   dotnet run distribution/build-mu-graph.cs -- --dump .artifacts/mangabaka.full.db --out .artifacts/mu-edges.db
//
// WHY THIS EXISTS
// Every label set the recommender is graded against today comes from AniList or MAL. reco-edges.db
// is AniList plus MAL votes, coread-edges.db is AniList reading lists, and `library` mode reads
// those same lists. So a channel derived from AniList behaviour, graded against any of them, is
// partly reading its own answers - and v4 adds exactly such a channel.
//
// MangaUpdates is a different site with a different population, and measured against the two
// shipped artifacts its pairs are almost entirely new:
//
//   category_recommendations   331,736 pairs over 85,910 series, 98.3% in NEITHER existing graph
//   recommendations (human)     29,692 pairs over  7,036 series, 75.7% in neither
//
// THE TWO LISTS ARE NOT THE SAME KIND OF EVIDENCE, AND MUST NOT BE SUMMED
// `recommendations` is human-submitted "if you liked X, try Y" with a submitter count as its
// weight, the same unit reco-edges.db carries. `category_recommendations` is MangaUpdates' own
// derivation from category (tag) votes, with weights in the tens of thousands. That makes it a
// broad, cheap relevance signal AND a partly tag-derived one: it must never be the primary grader
// for a tag-channel change, which is why the two go in separate columns and the eval names them
// separately (`--labels mu` against `--labels mu-human`).
//
// THE FULL DUMP IS REQUIRED
// `source_manga_updates_response_*` exists only in series.full.sqlite.zst. In the standard dump
// those columns are present and 100% NULL, so reading it produces zero pairs and no error. This
// tool checks and refuses rather than writing an empty artifact that reads as "MangaUpdates had
// nothing to say".

using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

// Every count printed here ends up quoted in distribution/CLAUDE.md next to figures from the other
// tools, all of which pin this. A comma-locale machine would produce a table nothing compares to.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

var dumpPath = Path.Combine(".artifacts", "mangabaka.full.db");
var outPath = Path.Combine(".artifacts", "mu-edges.db");
var minWeight = 0L;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--dump":
            dumpPath = args[++i];
            break;
        case "--out":
            outPath = args[++i];
            break;
        // A floor on the raw weight, swept by the eval rather than baked in. Same reasoning as
        // RecoGraphTuning.MinVotes: a floor cannot be applied to a number already through log1p at
        // load time, so the artifact stores raw and the consumer decides.
        case "--min-weight":
            minWeight = long.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        default:
            Console.WriteLine($"error: unknown argument '{args[i]}'");
            Console.WriteLine("usage: build-mu-graph.cs [--dump <path>] [--out <path>] [--min-weight N]");
            return 2;
    }
}

if (!File.Exists(dumpPath))
{
    Console.WriteLine($"error: no dump at {dumpPath}");
    Console.WriteLine("  This tool needs the FULL dump (series.full.sqlite.zst), not the standard one.");
    return 2;
}

Console.WriteLine($"dump     : {dumpPath}");
Console.WriteLine($"out      : {outPath}");

using var src = new SqliteConnection($"Data Source={dumpPath};Mode=ReadOnly;Pooling=False");
src.Open();

// The recommendable population, matching SeriesEmbeddingIndexer.CandidateWhere. A pair pointing at
// a row the index will never carry cannot be returned, so counting it as a label would measure
// index coverage rather than ranking.
const string Recommendable = "state = 'active' AND rating IS NOT NULL AND type != 'novel'";

const string CategoryColumn = "source_manga_updates_response_category_recommendations";
const string HumanColumn = "source_manga_updates_response_recommendations";
const string IdColumn = "source_manga_updates_response_series_id";

foreach (var column in new[] { CategoryColumn, HumanColumn, IdColumn })
{
    if (!HasColumn(column))
    {
        Console.WriteLine($"error: this dump has no '{column}' column.");
        Console.WriteLine("       That column exists only in the MangaBaka FULL dump. Fetch series.full.sqlite.zst.");
        return 1;
    }
}

// Present-but-empty is the actual failure mode, not a missing column: the standard dump declares
// every source_*_response column and leaves all of them NULL, so a shape check alone passes and the
// run writes an empty artifact.
if (Count($"SELECT COUNT(*) FROM series WHERE {IdColumn} IS NOT NULL") == 0)
{
    Console.WriteLine($"error: '{IdColumn}' is present but 100% NULL in this dump.");
    Console.WriteLine("       That is what the STANDARD dump looks like. Fetch series.full.sqlite.zst.");
    return 1;
}

// MangaUpdates numeric id to MangaBaka id. Ordered by popularity so the first row to claim an id
// wins, the same collision rule fetch-coread-graph.cs and eval-reco-labels.cs use for AniList ids.
var clock = System.Diagnostics.Stopwatch.StartNew();
var byMuId = new Dictionary<long, long>(150_000);
using (var cmd = src.CreateCommand())
{
    cmd.CommandText =
        $"""
        SELECT {IdColumn}, id
        FROM series
        WHERE {Recommendable} AND {IdColumn} IS NOT NULL
        ORDER BY COALESCE(popularity_global_current, 2147483647)
        """;
    cmd.CommandTimeout = 600;
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        if (TryId(reader, 0) is { } muId)
        {
            byMuId.TryAdd(muId, reader.GetInt64(1));
        }
    }
}

Console.WriteLine($"cross-ref: {byMuId.Count:N0} MangaUpdates ids map to recommendable series ({clock.Elapsed.TotalSeconds:F0}s)");

// Accumulated in one dictionary rather than partitioned the way fetch-coread-graph.cs has to: this
// is a few hundred thousand pairs read straight off the dump, not 39M generated by a nested loop.
var pairs = new Dictionary<(long A, long B), Edge>(400_000);
var stats = new Stats();

Fold(CategoryColumn, category: true);
Fold(HumanColumn, category: false);

Console.WriteLine();
Console.WriteLine($"category : {stats.CategoryRows:N0} source rows, {stats.CategoryEdges:N0} edges, {stats.CategoryUnmapped:N0} targets not in the recommendable set");
Console.WriteLine($"human    : {stats.HumanRows:N0} source rows, {stats.HumanEdges:N0} edges, {stats.HumanUnmapped:N0} unmapped");
Console.WriteLine($"folded   : {pairs.Count:N0} undirected pairs over {pairs.Keys.SelectMany(k => new[] { k.A, k.B }).Distinct().Count():N0} series");

if (minWeight > 0)
{
    var before = pairs.Count;
    foreach (var key in pairs.Where(p => p.Value.Category < minWeight && p.Value.Human < minWeight).Select(p => p.Key).ToList())
    {
        pairs.Remove(key);
    }

    Console.WriteLine($"floor    : --min-weight {minWeight} dropped {before - pairs.Count:N0} pairs");
}

if (pairs.Count == 0)
{
    Console.WriteLine("error: no pairs survived. Nothing written.");
    return 1;
}

Write();

Console.WriteLine();
Console.WriteLine($"done     : {outPath} ({new FileInfo(outPath).Length / 1024.0 / 1024.0:F1} MB, {clock.Elapsed.TotalSeconds:F0}s total)");
return 0;

// -------------------------------------------------------------------------------------------------

bool HasColumn(string name)
{
    using var cmd = src.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('series') WHERE name = $n";
    cmd.Parameters.AddWithValue("$n", name);
    return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
}

long Count(string sql)
{
    using var cmd = src.CreateCommand();
    cmd.CommandText = sql;
    cmd.CommandTimeout = 600;
    return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
}

/// <summary>
/// Streams one recommendation column and folds it into <c>pairs</c>. Both columns hold the same JSON
/// shape - an array of <c>{weight, series_id, series_name, ...}</c> - and differ only in what the
/// weight means, which is why they land in different columns of the same row rather than being added.
/// </summary>
void Fold(string column, bool category)
{
    using var cmd = src.CreateCommand();
    cmd.CommandText = $"SELECT id, {column} FROM series WHERE {Recommendable} AND {column} IS NOT NULL AND {column} != '[]'";
    cmd.CommandTimeout = 600;
    using var reader = cmd.ExecuteReader();

    var rows = 0;
    var edges = 0;
    var unmapped = 0;
    while (reader.Read())
    {
        var from = reader.GetInt64(0);
        var json = reader.GetString(1);
        rows++;

        List<(long To, long Weight)> targets;
        try
        {
            targets = ParseTargets(json, byMuId, ref unmapped);
        }
        catch (JsonException)
        {
            // One malformed blob is a dump defect, not a reason to lose the other 86,000 rows.
            continue;
        }

        foreach (var (to, weight) in targets)
        {
            if (to == from || weight <= 0)
            {
                continue;
            }

            // Canonical order, matching reco-edges.db: each undirected pair is stored once and
            // `directions` records whether both endpoints listed each other. Consumers materialize
            // both directions at load time (PairGraphBuilder), so storing both here would double
            // the file and the CSR.
            var key = from < to ? (from, to) : (to, from);
            ref var edge = ref System.Runtime.InteropServices.CollectionsMarshal
                .GetValueRefOrAddDefault(pairs, key, out _);

            // Strongest endorsement, not a sum. A pair listed from both ends is not twice as
            // related, and MangaUpdates' two directions carry near-identical weights anyway.
            if (category)
            {
                edge.Category = Math.Max(edge.Category, weight);
                edge.CategoryDirections |= from < to ? 1 : 2;
            }
            else
            {
                edge.Human = Math.Max(edge.Human, weight);
                edge.HumanDirections |= from < to ? 1 : 2;
            }

            edges++;
        }

        // A long silent loop in a distribution tool is itself the bug. 86k rows of JSON parsing is
        // fast but not instant, and two columns run back to back.
        if (rows % 10_000 == 0)
        {
            Console.Write($"\r  {(category ? "category" : "human")}: {rows:N0} rows, {pairs.Count:N0} pairs   ");
        }
    }

    Console.Write("\r".PadRight(64) + "\r");

    if (category)
    {
        (stats.CategoryRows, stats.CategoryEdges, stats.CategoryUnmapped) = (rows, edges, unmapped);
    }
    else
    {
        (stats.HumanRows, stats.HumanEdges, stats.HumanUnmapped) = (rows, edges, unmapped);
    }
}

void Write()
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

    using var dst = new SqliteConnection($"Data Source={outPath};Pooling=False");
    dst.Open();

    Execute(dst, "PRAGMA journal_mode = OFF");
    Execute(dst, "PRAGMA synchronous = OFF");
    Execute(
        dst,
        """
        CREATE TABLE pair (
            a_id            INTEGER NOT NULL,
            b_id            INTEGER NOT NULL,
            category_weight INTEGER NOT NULL DEFAULT 0,
            human_weight    INTEGER NOT NULL DEFAULT 0,
            directions      INTEGER NOT NULL DEFAULT 1,
            PRIMARY KEY (a_id, b_id)
        ) WITHOUT ROWID
        """);
    Execute(dst, "CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL)");

    Console.Write("  writing...   ");
    using (var tx = dst.BeginTransaction())
    {
        using var insert = dst.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText =
            "INSERT INTO pair (a_id, b_id, category_weight, human_weight, directions) VALUES ($a, $b, $c, $h, $d)";
        var pa = insert.Parameters.Add("$a", SqliteType.Integer);
        var pb = insert.Parameters.Add("$b", SqliteType.Integer);
        var pc = insert.Parameters.Add("$c", SqliteType.Integer);
        var ph = insert.Parameters.Add("$h", SqliteType.Integer);
        var pd = insert.Parameters.Add("$d", SqliteType.Integer);

        foreach (var ((a, b), edge) in pairs)
        {
            pa.Value = a;
            pb.Value = b;
            pc.Value = edge.Category;
            ph.Value = edge.Human;
            // How many of the two directions listed the other, over both lists. 2 means each end
            // named the other, which is the corroborated subset.
            pd.Value = System.Numerics.BitOperations.PopCount(
                (uint)(edge.CategoryDirections | edge.HumanDirections));
            insert.ExecuteNonQuery();
        }

        tx.Commit();
    }

    Console.Write("\r".PadRight(64) + "\r");

    // Both directions indexed, the same as the shipped graphs: a lookup is per-seed on the
    // recommendation hot path and the canonical order means half the neighbours sit in b_id.
    Execute(dst, "CREATE INDEX ix_pair_b ON pair (b_id, a_id)");

    var series = pairs.Keys.SelectMany(k => new[] { k.A, k.B }).Distinct().Count();
    using (var tx = dst.BeginTransaction())
    {
        using var meta = dst.CreateCommand();
        meta.Transaction = tx;
        meta.CommandText = "INSERT INTO meta (key, value) VALUES ($k, $v)";
        var pk = meta.Parameters.Add("$k", SqliteType.Text);
        var pv = meta.Parameters.Add("$v", SqliteType.Text);
        foreach (var (k, v) in new (string, string)[]
        {
            ("schemaVersion", "1"),
            ("generatedAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
            ("pairCount", pairs.Count.ToString(CultureInfo.InvariantCulture)),
            ("seriesCount", series.ToString(CultureInfo.InvariantCulture)),
            ("source", "mangaupdates-dump"),
            ("categoryPairs", pairs.Values.Count(e => e.Category > 0).ToString(CultureInfo.InvariantCulture)),
            ("humanPairs", pairs.Values.Count(e => e.Human > 0).ToString(CultureInfo.InvariantCulture)),
        })
        {
            pk.Value = k;
            pv.Value = v;
            meta.ExecuteNonQuery();
        }

        tx.Commit();
    }

    Console.Write("  vacuuming... ");
    Execute(dst, "VACUUM");
    Console.Write("\r".PadRight(64) + "\r");
}

static void Execute(SqliteConnection conn, string sql)
{
    using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.CommandTimeout = 3600;
    cmd.ExecuteNonQuery();
}

/// <summary>
/// The dump stores MangaUpdates ids as TEXT in some columns and INTEGER in others; read whichever
/// this one is rather than assuming. <c>source_manga_updates_id</c> is a base36 slug and is NOT the
/// same key as <c>source_manga_updates_response_series_id</c>, which is what the recommendation
/// blobs point at.
/// </summary>
static long? TryId(SqliteDataReader reader, int ordinal)
{
    if (reader.IsDBNull(ordinal))
    {
        return null;
    }

    return long.TryParse(reader.GetValue(ordinal).ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
        ? id
        : null;
}

static List<(long To, long Weight)> ParseTargets(string json, Dictionary<long, long> byMuId, ref int unmapped)
{
    var result = new List<(long, long)>();
    using var doc = JsonDocument.Parse(json);
    if (doc.RootElement.ValueKind != JsonValueKind.Array)
    {
        return result;
    }

    foreach (var entry in doc.RootElement.EnumerateArray())
    {
        if (entry.ValueKind != JsonValueKind.Object
            || !entry.TryGetProperty("series_id", out var idElement)
            || !TryLong(idElement, out var muId))
        {
            continue;
        }

        if (!byMuId.TryGetValue(muId, out var mangaBakaId))
        {
            // Usually a novel, an inactive row or an unrated one - all outside the recommendable
            // set by construction, so this counter is expected to be large and is printed rather
            // than warned about.
            unmapped++;
            continue;
        }

        var weight = entry.TryGetProperty("weight", out var w) && TryLong(w, out var parsed) ? parsed : 1;
        result.Add((mangaBakaId, weight));
    }

    return result;
}

static bool TryLong(JsonElement element, out long value)
{
    switch (element.ValueKind)
    {
        case JsonValueKind.Number:
            return element.TryGetInt64(out value);
        case JsonValueKind.String:
            return long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        default:
            value = 0;
            return false;
    }
}

/// <param name="CategoryDirections">
/// Bit 0 set when the lower-id endpoint listed the higher one, bit 1 for the reverse. Kept per list
/// so a pair corroborated within one list is distinguishable from one corroborated across the two.
/// </param>
file struct Edge
{
    public long Category;
    public long Human;
    public int CategoryDirections;
    public int HumanDirections;
}

file sealed class Stats
{
    public int CategoryRows;
    public int CategoryEdges;
    public int CategoryUnmapped;
    public int HumanRows;
    public int HumanEdges;
    public int HumanUnmapped;
}
