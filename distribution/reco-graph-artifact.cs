#:package Microsoft.Data.Sqlite@10.0.9
#:package SQLitePCLRaw.bundle_e_sqlite3@3.0.3
#:package ZstdSharp.Port@0.8.8

// Validates an exported reco-edges.db and packs it into a publishable artifact.
// Driven by publish-reco-graph.ps1; run directly with:
//   dotnet run distribution/reco-graph-artifact.cs -- <db> <outDir> <minPairs>
//
// The shape is deliberately the same as embeddings-artifact.cs, including the reason for it: under
// Windows PowerShell a native command's stderr line becomes an ErrorRecord, so everything is
// written to stdout and the manifest lands at <outDir>/manifest.json rather than being scraped.
// Exit code is the contract; 0 means the manifest is on disk.
//
// WHAT MAKES THIS PUBLISHABLE
// The pair table is an aggregate of public "if you liked X, try Y" submissions keyed by MangaBaka
// id, with vote counts. No user is identifiable in it and no per-user row was ever written by the
// fetcher that produces it. The co-read fetcher is a different matter and its working database
// must never be packed by this tool; see fetch-coread-graph.cs.

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using ZstdSharp;

if (args.Length < 3)
{
    Console.WriteLine("usage: reco-graph-artifact.cs <db> <outDir> <minPairs>");
    return 2;
}

var dbPath = args[0];
var outDir = args[1];
var minPairs = long.Parse(args[2], CultureInfo.InvariantCulture);

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

// A corrupt or truncated file must never reach users.
var integrity = Scalar("PRAGMA quick_check");
if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine($"error: quick_check failed: {integrity}");
    return 1;
}

var problems = new List<string>();

// The working database and the artifact are both SQLite files with similar names sitting in the
// same folder, and packing the wrong one would publish fetch bookkeeping instead of edges. The
// table list is what tells them apart.
if (Count("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'pair'") == 0)
{
    Console.WriteLine("error: no 'pair' table. This looks like reco-graph.db (the fetcher's working");
    Console.WriteLine("       state), not the exported reco-edges.db. Run `fetch-reco-graph.cs export` first.");
    return 1;
}

if (Count("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'edge'") > 0)
{
    problems.Add("an 'edge' table is present, so this is the fetcher's working database rather than an export");
}

var pairs = Count("SELECT COUNT(*) FROM pair");
var series = Count("SELECT COUNT(*) FROM (SELECT a_id AS id FROM pair UNION SELECT b_id FROM pair)");
var reciprocal = Count("SELECT COUNT(*) FROM pair WHERE directions = 2");
var aniListPairs = Count("SELECT COUNT(*) FROM pair WHERE anilist_votes > 0");
var malPairs = Count("SELECT COUNT(*) FROM pair WHERE mal_votes > 0");

if (pairs < minPairs)
{
    problems.Add($"only {pairs} pairs (expected at least {minPairs}) - is the fetch still running?");
}

// Both directions are materialized at load time from this one table, so a row naming the same
// series twice becomes a self-loop that scores a seed against itself.
if (Count("SELECT COUNT(*) FROM pair WHERE a_id = b_id") > 0)
{
    problems.Add("some rows pair a series with itself; the export folded two remote ids onto one MangaBaka row");
}

if (Count("SELECT COUNT(*) FROM pair WHERE anilist_votes < 0 OR mal_votes < 0") > 0)
{
    problems.Add("negative vote counts, which log1p cannot take");
}

if (aniListPairs == 0 && malPairs == 0)
{
    problems.Add("every pair has zero votes from both providers, so the whole graph would score zero");
}

// Read by RecoGraphCache to decide freshness, and by the installer's compatibility gate. A file
// without them is from before the meta table existed and predates anything worth publishing.
var metaPresent = Count("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'meta'") > 0;
var schemaVersion = metaPresent ? Scalar("SELECT value FROM meta WHERE key = 'schemaVersion'") : "";
var generatedAt = metaPresent ? Scalar("SELECT value FROM meta WHERE key = 'generatedAt'") : "";
var providers = metaPresent ? Scalar("SELECT value FROM meta WHERE key = 'providers'") : "";

if (!metaPresent)
{
    problems.Add("no meta table; re-export with a current fetch-reco-graph.cs");
}
else
{
    if (string.IsNullOrWhiteSpace(schemaVersion))
    {
        problems.Add("meta has no schemaVersion, so no client could gate on compatibility");
    }

    if (!DateTime.TryParse(
            generatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
    {
        problems.Add($"meta.generatedAt is not a round-trippable timestamp ('{generatedAt}')");
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
var archiveName = $"reco-edges-{stamp}.db.zst";
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

var manifest = new JsonObject
{
    ["schemaVersion"] = int.Parse(schemaVersion, CultureInfo.InvariantCulture),
    ["pairCount"] = pairs,
    ["seriesCount"] = series,
    ["reciprocalCount"] = reciprocal,
    ["providers"] = providers,
    ["generatedAt"] = generatedAt,
    ["fileName"] = archiveName,
    ["sizeBytes"] = new FileInfo(archivePath).Length,
    ["uncompressedBytes"] = new FileInfo(dbPath).Length,
    ["sha256"] = Sha256(archivePath),
    ["uncompressedSha256"] = Sha256(dbPath),
};

var manifestPath = Path.Combine(outDir, "manifest.json");
File.WriteAllText(manifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

Console.WriteLine($"pairs      : {pairs} ({reciprocal} reciprocal) over {series} series");
Console.WriteLine($"providers  : {providers} (anilist {aniListPairs}, mal {malPairs})");
Console.WriteLine($"wrote {manifestPath}");
return 0;
