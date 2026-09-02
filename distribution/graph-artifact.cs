#:package Microsoft.Data.Sqlite@10.0.9
#:package SQLitePCLRaw.bundle_e_sqlite3@3.0.3
#:package ZstdSharp.Port@0.8.8

// Validates an exported artifact and packs it into a publishable one.
// Driven by publish-graph.ps1; run directly with:
//   dotnet run distribution/graph-artifact.cs -- <reco|coread|taste> <db> <outDir> <minRows>
//
// One tool for all three because the packing is identical - integrity check, zstd, manifest,
// checksum - and only the table-level validation differs. Copies would be copies to keep in step,
// and the half that would drift is the half that decides what is safe to publish.
//
// `taste` is not a graph. It is the behavioural factor matrix from build-taste-vectors.cs, and it
// is here rather than in a tool of its own for the same reason the second graph was: everything
// except which tables it looks at is the same, including the check that matters most.
//
// The shape is deliberately the same as embeddings-artifact.cs, including the reason for it: under
// Windows PowerShell a native command's stderr line becomes an ErrorRecord, so everything is
// written to stdout and the manifest lands at <outDir>/manifest.json rather than being scraped.
// Exit code is the contract; 0 means the manifest is on disk.
//
// WHAT MAKES THESE PUBLISHABLE, AND WHERE THAT DIFFERS
// The reco graph is an aggregate of public "if you liked X, try Y" submissions keyed by MangaBaka
// id. Nobody is identifiable in it and its fetcher never wrote a per-user row at all.
//
// The co-read graph is different in kind. It is derived from per-user reading lists, and its
// fetcher's working database (coread-graph.db) holds millions of (user, series) rows. That file
// sits in the same folder as the export and differs from it by four characters. **This tool is the
// last check before those rows would leave the machine**, so it refuses any file carrying them,
// before any other check and with no way to override.

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using ZstdSharp;

if (args.Length < 4)
{
    Console.WriteLine("usage: graph-artifact.cs <reco|coread|taste> <db> <outDir> <minRows>");
    return 2;
}

var kind = args[0].Trim().ToLowerInvariant();
if (kind is not ("reco" or "coread" or "taste" or "cohorts"))
{
    Console.WriteLine($"error: unknown artifact '{args[0]}' (expected reco, coread, taste or cohorts)");
    return 2;
}

var isTaste = kind == "taste";
var isCohorts = kind == "cohorts";

var dbPath = args[1];
var outDir = args[2];
var minRows = long.Parse(args[3], CultureInfo.InvariantCulture);

if (!File.Exists(dbPath))
{
    Console.WriteLine($"error: {dbPath} does not exist");
    return 1;
}

using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
conn.Open();

string Scalar(string sql)
{
    using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.CommandTimeout = 600;
    return cmd.ExecuteScalar()?.ToString() ?? string.Empty;
}

long Count(string sql) => long.Parse(Scalar(sql), CultureInfo.InvariantCulture);

bool HasTable(string name) =>
    Count($"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{name}'") > 0;

// PERSONAL DATA FIRST, BEFORE ANYTHING ELSE
// Not folded in with the other problems below, and not reported as one of a list: this is the only
// check here whose failure would be irreversible once the upload finished, so it stops the run on
// its own and says why in its own words.
// Matched by PREFIX as well as by the three names that exist today: a working table nobody
// remembered to list here is exactly the accident this gate is for, and `user_` is what every one
// of them has been called.
var personalTables = Scalar(
    """
    SELECT COALESCE(GROUP_CONCAT(name, ', '), '') FROM sqlite_master
    WHERE type = 'table' AND (name LIKE 'user\_%' ESCAPE '\' OR name = 'pending_user')
    """);

if (!string.IsNullOrEmpty(personalTables))
{
    Console.WriteLine($"error: this database has {personalTables}, which hold per-user reading data.");
    Console.WriteLine("       This is coread-graph.db, the fetcher's working state, not an export.");
    Console.WriteLine("       Run the exporter for this artifact and publish what it writes instead.");
    Console.WriteLine();
    Console.WriteLine("       Nothing has been written. Do not upload this file.");
    return 1;
}

// A corrupt or truncated file must never reach users.
var integrity = Scalar("PRAGMA quick_check");
if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine($"error: quick_check failed: {integrity}");
    return 1;
}

// The working database and the artifact are both SQLite files with similar names sitting in the
// same folder, and packing the wrong one would publish fetch bookkeeping instead of edges. The
// table list is what tells them apart.
var payloadTable = isTaste ? "item_vectors" : isCohorts ? "cohort_item" : "pair";
if (!HasTable(payloadTable))
{
    if (isCohorts)
    {
        Console.WriteLine("error: no 'cohort_item' table. This is not a reader-cohort export.");
        Console.WriteLine("       Run `build-reader-cohorts.cs` and publish what it writes.");
        return 1;
    }

    if (isTaste)
    {
        Console.WriteLine("error: no 'item_vectors' table. This is not a taste-vectors export.");
        Console.WriteLine("       Run `build-taste-vectors.cs` and publish what it writes.");
        return 1;
    }

    var working = kind == "reco" ? "reco-graph.db" : "coread-graph.db";
    var tool = kind == "reco" ? "fetch-reco-graph.cs" : "fetch-coread-graph.cs";
    Console.WriteLine($"error: no 'pair' table. This looks like {working} (the fetcher's working");
    Console.WriteLine($"       state), not the export. Run `{tool} export` first.");
    return 1;
}

var problems = new List<string>();

foreach (var leftover in new[] { "edge", "fetch_state", "cooccurrence", "seed_page" })
{
    if (HasTable(leftover))
    {
        problems.Add($"a '{leftover}' table is present, so this is the fetcher's working database rather than an export");
    }
}

var pairs = isTaste
    ? Count("SELECT COUNT(*) FROM item_vectors")
    : isCohorts
        ? Count("SELECT COUNT(*) FROM cohort_item")
        : Count("SELECT COUNT(*) FROM pair");
var series = isTaste
    ? pairs
    : isCohorts
        ? Count("SELECT COUNT(*) FROM item_global")
        : Count("SELECT COUNT(*) FROM (SELECT a_id AS id FROM pair UNION SELECT b_id FROM pair)");

if (pairs < minRows)
{
    problems.Add($"only {pairs} rows (expected at least {minRows}) - is the build still running?");
}

// Both directions are materialized at load time from this one table, so a row naming the same
// series twice becomes a self-loop that scores a seed against itself.
if (!isTaste && !isCohorts && Count("SELECT COUNT(*) FROM pair WHERE a_id = b_id") > 0)
{
    problems.Add("some rows pair a series with itself; the export folded two remote ids onto one MangaBaka row");
}

// Read by the caches to decide freshness, and by the installers' compatibility gate. A file without
// them is from before the meta table existed and predates anything worth publishing.
var metaPresent = HasTable("meta");
var schemaVersion = metaPresent ? Scalar("SELECT value FROM meta WHERE key = 'schemaVersion'") : "";
var generatedAt = metaPresent ? Scalar("SELECT value FROM meta WHERE key = 'generatedAt'") : "";

if (!metaPresent)
{
    problems.Add("no meta table; re-export with a current fetcher");
}
else
{
    if (string.IsNullOrWhiteSpace(schemaVersion))
    {
        problems.Add("meta has no schemaVersion, so no client could gate on compatibility");
    }

    if (!DateTime.TryParse(generatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
    {
        problems.Add($"meta.generatedAt is not a round-trippable timestamp ('{generatedAt}')");
    }
}

var manifest = isTaste
    ? new JsonObject { ["itemCount"] = pairs }
    : isCohorts
        ? new JsonObject { ["cohortItemCount"] = pairs, ["itemCount"] = series }
        : new JsonObject { ["pairCount"] = pairs, ["seriesCount"] = series };

long aniListPairs = 0, malPairs = 0, reciprocal = 0, users = 0;
long cohorts = 0, cohortReaders = 0;
var providers = string.Empty;

if (isCohorts)
{
    foreach (var table in new[] { "cohort", "item_global" })
    {
        if (!HasTable(table))
        {
            Console.WriteLine($"error: no '{table}' table. This is not a reader-cohort export.");
            return 1;
        }
    }

    cohorts = Count("SELECT COUNT(*) FROM cohort");
    cohortReaders = Count("SELECT COALESCE(SUM(readers), 0) FROM cohort");

    // The serving side packs a cohort id into a byte, one per row over ~190,000 rows, and indexes
    // its own weight array by that id. Both facts break silently if the ids are not 0..n-1.
    if (cohorts is 0 or > 255)
    {
        problems.Add($"{cohorts} cohorts; the row layout carries 1 to 255");
    }
    else if (Count("SELECT COUNT(*) FROM cohort WHERE cohort < 0 OR cohort >= (SELECT COUNT(*) FROM cohort)") > 0)
    {
        problems.Add("cohort ids are not a contiguous 0..n-1 range, so every row's cohort column is a guess");
    }

    if (Count("SELECT COUNT(*) FROM cohort WHERE readers IS NULL OR readers <= 0") > 0)
    {
        problems.Add("some cohorts have no readers, so their completion rates would divide by zero");
    }

    if (Count("SELECT COUNT(*) FROM cohort_item WHERE cohort NOT IN (SELECT cohort FROM cohort)") > 0)
    {
        problems.Add("some cohort rows name a cohort the cohort table does not list");
    }

    if (Count("SELECT COUNT(*) FROM cohort_item WHERE completions IS NULL OR completions <= 0") > 0)
    {
        problems.Add("some cohort rows carry no completions, so no rate could be computed from them");
    }

    if (Count("SELECT COUNT(*) FROM item_global WHERE completions IS NULL OR completions <= 0") > 0)
    {
        problems.Add("some global rows carry no completions, which is the denominator of every lift");
    }

    // `mean IS NOT NULL AND NOT (...)` rather than a plain range test, for the reason spelled out
    // on the co-read branch below: SQLite has no NaN, it stores one as NULL, and a three-valued
    // comparison lets exactly the rows it is meant to catch through. NULL is legitimate here - it
    // means "finished often enough to count, rated too rarely to average" - so it is excluded
    // explicitly rather than by accident.
    foreach (var table in new[] { "cohort_item", "item_global" })
    {
        if (Count($"SELECT COUNT(*) FROM {table} WHERE mean IS NOT NULL AND NOT (mean > 0 AND mean <= 100)") > 0)
        {
            problems.Add($"{table} has means outside the 1-100 score range");
        }
    }

    // The floors are a NOISE floor, not anonymity - the source is public AniList lists and a moving
    // tag can be differenced across releases at any floor. What they buy is that no cell shown to a
    // reader is a mean over three people, so a build that quietly lowered them must not publish.
    var minRaters = metaPresent && long.TryParse(
        Scalar("SELECT value FROM meta WHERE key = 'minCohortRaters'"), NumberStyles.Integer,
        CultureInfo.InvariantCulture, out var declared)
        ? declared
        : 0;

    if (minRaters <= 0)
    {
        problems.Add("meta has no minCohortRaters, so nothing records the floor these cells were cut at");
    }
    else if (Count($"SELECT COUNT(*) FROM cohort_item WHERE raters > 0 AND raters < {minRaters}") > 0)
    {
        problems.Add($"some cohort rows carry a mean over fewer than the {minRaters} raters meta declares");
    }

    // AN EVALUATION BUILD MUST NEVER BE PUBLISHED, for the same reason it must not on the taste
    // artifact: it installs, works, scores slightly worse and gives nobody a reason to look.
    var cohortFold = metaPresent ? Scalar("SELECT value FROM meta WHERE key = 'trainingFold'") : "";
    if (string.IsNullOrWhiteSpace(cohortFold))
    {
        problems.Add("meta has no trainingFold, so nothing records whether this saw every reader");
    }
    else if (!cohortFold.Equals("all", StringComparison.OrdinalIgnoreCase))
    {
        problems.Add(
            $"meta.trainingFold is '{cohortFold}', so this is a fold-limited EVALUATION build "
            + "and is missing readers on purpose");
    }

    // A cohort artifact is only as held-out as the item space its clustering ran in, and that is
    // the one thing about it no other field would reveal.
    var tasteFold = metaPresent ? Scalar("SELECT value FROM meta WHERE key = 'tasteTrainingFold'") : "";
    if (!string.IsNullOrWhiteSpace(tasteFold)
        && !tasteFold.Equals("all", StringComparison.OrdinalIgnoreCase))
    {
        problems.Add(
            $"meta.tasteTrainingFold is '{tasteFold}', so this was clustered inside a fold-limited "
            + "item space");
    }

    // A NUMBER, not the string the meta table stores it as: the installer deserializes this into a
    // long, and System.Text.Json refuses a quoted number by default.
    var trainedReaders = metaPresent && long.TryParse(
        Scalar("SELECT value FROM meta WHERE key = 'trainedReaders'"), NumberStyles.Integer,
        CultureInfo.InvariantCulture, out var readers)
        ? readers
        : 0;

    if (trainedReaders == 0)
    {
        problems.Add("meta has no trainedReaders, so nothing records how many readers this grouped");
    }

    manifest["cohortCount"] = cohorts;
    manifest["trainedReaders"] = trainedReaders;
    manifest["trainingFold"] = cohortFold;
}
else if (isTaste)
{
    var dimensions = metaPresent && int.TryParse(
        Scalar("SELECT value FROM meta WHERE key = 'dimensions'"), NumberStyles.Integer,
        CultureInfo.InvariantCulture, out var dims) && dims > 0
        ? dims
        : 0;

    if (dimensions == 0)
    {
        problems.Add("meta has no usable dimensions, so no client could size the layer");
    }
    else if (Count($"SELECT COUNT(*) FROM item_vectors WHERE length(vec) != {dimensions}") > 0)
    {
        problems.Add($"some vectors are not {dimensions} bytes wide, which would read past their own end");
    }

    // `scale IS NULL OR NOT (scale > 0)`, never `scale <= 0`, for the reason spelled out on the
    // co-read branch below. Zero carries a second meaning here on top of that: it is the serving
    // layer's own "this row has no vector" marker, so a stored zero makes the row invisible rather
    // than wrong.
    if (Count("SELECT COUNT(*) FROM item_vectors WHERE scale IS NULL OR NOT (scale > 0)") > 0)
    {
        problems.Add("some vectors have a missing, zero or negative scale and would be silently skipped");
    }

    // AN EVALUATION BUILD MUST NEVER BE PUBLISHED, and nothing about one looks wrong at runtime.
    // build-taste-vectors.cs --fold-out holds a quarter of the readers back so the eval can grade
    // the model honestly; that artifact installs, works, scores slightly worse, and gives nobody a
    // reason to look. The installer refuses it too, but this is the gate that stops it being
    // uploaded in the first place.
    var trainingFold = metaPresent ? Scalar("SELECT value FROM meta WHERE key = 'trainingFold'") : "";
    if (string.IsNullOrWhiteSpace(trainingFold))
    {
        problems.Add("meta has no trainingFold, so nothing records whether this saw every reader");
    }
    else if (!trainingFold.Equals("all", StringComparison.OrdinalIgnoreCase))
    {
        problems.Add(
            $"meta.trainingFold is '{trainingFold}', so this is a fold-limited EVALUATION build "
            + "and is missing readers on purpose");
    }

    // A NUMBER, not the string the meta table stores it as: the installer deserializes this into a
    // long, and System.Text.Json refuses a quoted number by default.
    var trainedReaders = metaPresent && long.TryParse(
        Scalar("SELECT value FROM meta WHERE key = 'trainedReaders'"), NumberStyles.Integer,
        CultureInfo.InvariantCulture, out var readers)
        ? readers
        : 0;

    if (trainedReaders == 0)
    {
        problems.Add("meta has no trainedReaders, so nothing records how many readers this learned from");
    }

    manifest["dimensions"] = dimensions;
    manifest["trainingFold"] = trainingFold;
    manifest["trainedReaders"] = trainedReaders;
}
else if (kind == "reco")
{
    reciprocal = Count("SELECT COUNT(*) FROM pair WHERE directions = 2");
    aniListPairs = Count("SELECT COUNT(*) FROM pair WHERE anilist_votes > 0");
    malPairs = Count("SELECT COUNT(*) FROM pair WHERE mal_votes > 0");
    providers = metaPresent ? Scalar("SELECT value FROM meta WHERE key = 'providers'") : "";

    if (Count("SELECT COUNT(*) FROM pair WHERE anilist_votes < 0 OR mal_votes < 0") > 0)
    {
        problems.Add("negative vote counts, which log1p cannot take");
    }

    if (aniListPairs == 0 && malPairs == 0)
    {
        problems.Add("every pair has zero votes from both providers, so the whole graph would score zero");
    }

    // The two providers' votes are on incomparable scales and are reconciled at load time by
    // percentile. Claiming a provider that contributed nothing would leave that scale at zero.
    if (providers.Contains("mal", StringComparison.OrdinalIgnoreCase) && malPairs == 0)
    {
        problems.Add("meta.providers names MAL but no pair carries a MAL vote");
    }

    if (providers.Contains("anilist", StringComparison.OrdinalIgnoreCase) && aniListPairs == 0)
    {
        problems.Add("meta.providers names AniList but no pair carries an AniList vote");
    }

    manifest["reciprocalCount"] = reciprocal;
    manifest["providers"] = providers;
}
else
{
    // `strength IS NULL OR NOT (strength > 0)`, never `strength <= 0`: SQLite has no NaN and stores
    // one as NULL, and a three-valued comparison against NULL yields NULL, which the CASE would fall
    // through as false. The naive form passes exactly the rows it is meant to catch.
    if (Count("SELECT COUNT(*) FROM pair WHERE strength IS NULL OR NOT (strength > 0)") > 0)
    {
        problems.Add("some strengths are missing, zero or negative, so those edges carry no evidence");
    }

    users = metaPresent && long.TryParse(
        Scalar("SELECT value FROM meta WHERE key = 'userCount'"), NumberStyles.Integer,
        CultureInfo.InvariantCulture, out var parsed)
        ? parsed
        : 0;

    if (users == 0)
    {
        problems.Add("meta has no userCount, so nothing records how many readers this was built from");
    }

    manifest["userCount"] = users;
}

if (problems.Count > 0)
{
    Console.WriteLine("error: this database is not publishable:");
    foreach (var problem in problems)
    {
        Console.WriteLine($"  - {problem}");
    }

    return 1;
}

conn.Close();
SqliteConnection.ClearAllPools();

Directory.CreateDirectory(outDir);
var stamp = DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
var baseName = kind switch
{
    "reco" => "reco-edges",
    "coread" => "coread-edges",
    "cohorts" => "reader-cohorts",
    _ => "taste-vectors",
};
var archiveName = $"{baseName}-{stamp}.db.zst";
var archivePath = Path.Combine(outDir, archiveName);

Console.WriteLine($"compressing {new FileInfo(dbPath).Length / 1_000_000.0:F0} MB -> {archiveName} …");
using (var source = File.OpenRead(dbPath))
using (var destination = File.Create(archivePath))
using (var compressor = new CompressionStream(destination, level: 10))
{
    source.CopyTo(compressor);
}

static string Sha256(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
}

manifest["schemaVersion"] = int.Parse(schemaVersion, CultureInfo.InvariantCulture);
manifest["generatedAt"] = generatedAt;
manifest["fileName"] = archiveName;
manifest["sizeBytes"] = new FileInfo(archivePath).Length;
manifest["uncompressedBytes"] = new FileInfo(dbPath).Length;
manifest["sha256"] = Sha256(archivePath);
manifest["uncompressedSha256"] = Sha256(dbPath);

var manifestPath = Path.Combine(outDir, "manifest.json");
File.WriteAllText(manifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

Console.WriteLine(isTaste
    ? $"vectors    : {pairs}"
    : isCohorts
        ? $"cohort rows: {pairs} over {series} series"
        : $"pairs      : {pairs} over {series} series");
if (kind == "reco")
{
    Console.WriteLine($"providers  : {providers} (anilist {aniListPairs}, mal {malPairs}, {reciprocal} reciprocal)");
}
else if (kind == "coread")
{
    Console.WriteLine($"built from : {users} readers");
}
else if (isCohorts)
{
    Console.WriteLine($"built from : {cohorts} cohorts over {cohortReaders} readers");
}
else
{
    Console.WriteLine($"built from : {manifest["trainedReaders"]} readers, {manifest["dimensions"]} dims");
}
Console.WriteLine($"wrote {manifestPath}");
return 0;
