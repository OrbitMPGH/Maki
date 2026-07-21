#:package Microsoft.Data.Sqlite@10.0.9
#:package ZstdSharp.Port@0.8.8

// Validates a local embeddings.db and packs it into a publishable artifact.
// Driven by publish-embeddings.ps1; run directly with:
//   dotnet run distribution/embeddings-artifact.cs -- <db> <outDir> <expectedDims> <minRows>
//
// Everything here is shaped around one Windows PowerShell quirk: it wraps every stderr line from
// a native command in an ErrorRecord, which under $ErrorActionPreference = "Stop" turns a progress
// message into a fatal error, and otherwise prints red "NativeCommandError" noise over a run that
// succeeded. So all output goes to stdout, and the manifest goes to <outDir>/manifest.json rather
// than being scraped from it. Exit code is the contract; 0 means the manifest is on disk.

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using ZstdSharp;

if (args.Length < 4)
{
    Console.WriteLine("usage: embeddings-artifact.cs <db> <outDir> <expectedDims> <minRows>");
    return 2;
}

var dbPath = args[0];
var outDir = args[1];
var expectedDims = int.Parse(args[2], CultureInfo.InvariantCulture);
var minRows = int.Parse(args[3], CultureInfo.InvariantCulture);
// Vectors are stored int8 with a per-row scale: one byte per dimension.
var expectedVecBytes = expectedDims;

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

// A corrupt or truncated file must never reach users.
var integrity = Scalar("PRAGMA quick_check");
if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine($"error: quick_check failed: {integrity}");
    return 1;
}

var vectorRows = long.Parse(Scalar("SELECT COUNT(*) FROM series_vectors"), CultureInfo.InvariantCulture);
var tagRows = long.Parse(Scalar("SELECT COUNT(*) FROM series_tags"), CultureInfo.InvariantCulture);
var vocabRows = long.Parse(Scalar("SELECT COUNT(*) FROM tag_vocab"), CultureInfo.InvariantCulture);
var minVec = long.Parse(Scalar("SELECT COALESCE(MIN(length(vec)), 0) FROM series_vectors"), CultureInfo.InvariantCulture);
var maxVec = long.Parse(Scalar("SELECT COALESCE(MAX(length(vec)), 0) FROM series_vectors"), CultureInfo.InvariantCulture);

var problems = new List<string>();

if (vectorRows < minRows)
{
    problems.Add($"only {vectorRows} vectors (expected at least {minRows}) — is the indexing pass still running?");
}

// Mixed widths mean a model change is mid-migration: half the rows are from the previous model
// and would be discarded by every client that installed this.
if (minVec != maxVec)
{
    problems.Add($"mixed vector widths ({minVec} and {maxVec} bytes) — the re-embed after a model change hasn't finished");
}
else if (maxVec != expectedVecBytes)
{
    problems.Add($"vectors are {maxVec} bytes, expected {expectedVecBytes} ({expectedDims} dims) — the DB and the source tree disagree about the model");
}

// The vocabulary is written at the end of a pass; empty means the pass never completed, and the
// tag channel would be dead for everyone who installed it.
if (vocabRows == 0)
{
    problems.Add("tag_vocab is empty — the indexing pass hasn't finished");
}

if (tagRows == 0)
{
    problems.Add("series_tags is empty — the indexing pass hasn't finished");
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
var archiveName = $"embeddings-{expectedDims}d-{stamp}.db.zst";
var archivePath = Path.Combine(outDir, archiveName);

Console.WriteLine($"compressing {new FileInfo(dbPath).Length / 1_000_000.0:F0} MB → {archiveName} …");
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
    ["dimensions"] = expectedDims,
    ["quantized"] = true,
    ["rowCount"] = vectorRows,
    ["tagRowCount"] = tagRows,
    ["vocabRowCount"] = vocabRows,
    ["generatedAt"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
    ["fileName"] = archiveName,
    ["sizeBytes"] = new FileInfo(archivePath).Length,
    ["uncompressedBytes"] = new FileInfo(dbPath).Length,
    ["sha256"] = Sha256(archivePath),
    ["uncompressedSha256"] = Sha256(dbPath),
};

var manifestPath = Path.Combine(outDir, "manifest.json");
File.WriteAllText(manifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"wrote {manifestPath}");
return 0;
