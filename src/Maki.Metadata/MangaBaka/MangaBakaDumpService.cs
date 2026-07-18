using System.Globalization;
using System.Security.Cryptography;
using Maki.Core.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ZstdSharp;

namespace Maki.Metadata.MangaBaka;

/// <summary>
/// Maintains the local copy of the MangaBaka database dump: nightly snapshots published
/// at api.mangabaka.org/v1/database/ (~350 MB zst, ~3 GB unpacked). Downloads only when
/// the published SHA1 changes, builds an FTS5 title index, and atomically swaps the file
/// into place so readers never see a half-written database.
/// </summary>
public class MangaBakaDumpService(
    IHttpClientFactory httpClientFactory,
    MangaBakaDumpOptions options,
    IAppSettings settings,
    ILogger<MangaBakaDumpService> logger)
{
    public const string HttpClientName = "mangabaka-dump";
    public const string SearchTableName = "maki_search";

    private const string DumpPath = "v1/database/series.sqlite.zst";

    public record DumpStatus(bool Present, long? SizeBytes, DateTime? RefreshedAt);

    public async Task<DumpStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var info = new FileInfo(options.DatabasePath);
        DateTime? refreshedAt = DateTime.TryParse(
            await settings.GetAsync(SettingKeys.MangaBakaDumpRefreshedAt, ct),
            CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
        return new DumpStatus(info.Exists, info.Exists ? info.Length : null, refreshedAt);
    }

    /// <summary>Downloads and installs the dump if its checksum changed; returns true when a new dump was installed.</summary>
    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);

        // Published as "<hex sha1>  <filename>" over the compressed file.
        var sha1Line = await client.GetStringAsync(DumpPath + ".sha1", ct);
        var expectedSha1 = sha1Line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].Trim();

        var installedSha1 = await settings.GetAsync(SettingKeys.MangaBakaDumpSha1, ct);
        if (string.Equals(expectedSha1, installedSha1, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(options.DatabasePath))
        {
            logger.LogDebug("MangaBaka dump unchanged ({Sha1}); skipping download", expectedSha1);
            return false;
        }

        Directory.CreateDirectory(options.StagingDirectory);
        var stagingPath = Path.Combine(options.StagingDirectory, "mangabaka.db.partial");

        try
        {
            var actualSha1 = await DownloadAndDecompressAsync(client, stagingPath, ct);
            if (!string.Equals(actualSha1, expectedSha1, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"MangaBaka dump checksum mismatch: expected {expectedSha1}, got {actualSha1}");
            }

            PrepareStagedDatabase(stagingPath);
            await SwapIntoPlaceAsync(stagingPath, ct);
        }
        catch
        {
            TryDelete(stagingPath);
            throw;
        }

        await settings.SetAsync(SettingKeys.MangaBakaDumpSha1, expectedSha1, ct);
        await settings.SetAsync(SettingKeys.MangaBakaDumpRefreshedAt, DateTime.UtcNow.ToString("O"), ct);
        logger.LogInformation("MangaBaka local database installed at {Path} ({Sha1})", options.DatabasePath, expectedSha1);
        return true;
    }

    /// <summary>Streams the zst dump to disk, decompressing on the fly while hashing the compressed bytes.</summary>
    private async Task<string> DownloadAndDecompressAsync(HttpClient client, string stagingPath, CancellationToken ct)
    {
        logger.LogInformation("Downloading MangaBaka database dump (~350 MB)…");
        using var response = await client.GetAsync(DumpPath, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var hashing = new HashingReadStream(source, sha1);
        await using var decompressed = new DecompressionStream(hashing);
        await using (var output = File.Create(stagingPath))
        {
            await decompressed.CopyToAsync(output, ct);
        }

        return Convert.ToHexStringLower(sha1.GetHashAndReset());
    }

    /// <summary>Sanity-checks the staged database and builds the FTS5 index over all title variants.</summary>
    private void PrepareStagedDatabase(string stagingPath)
    {
        using var conn = new SqliteConnection($"Data Source={stagingPath};Pooling=False");
        conn.Open();

        var count = (long)Scalar(conn, "SELECT COUNT(*) FROM series")!;
        if (count < 1000)
        {
            throw new InvalidOperationException($"MangaBaka dump looks broken: only {count} series rows");
        }

        logger.LogInformation("Building MangaBaka search index over {Count} series…", count);
        BuildSearchIndex(conn);
    }

    /// <summary>Indexes every title variant of non-merged series into the FTS5 search table.</summary>
    internal static void BuildSearchIndex(SqliteConnection conn)
    {
        Execute(conn, $"DROP TABLE IF EXISTS {SearchTableName}");
        Execute(conn, $"CREATE VIRTUAL TABLE {SearchTableName} USING fts5(title, series_id UNINDEXED, tokenize='unicode61 remove_diacritics 2')");

        using var tx = conn.BeginTransaction();
        foreach (var column in new[] { "title", "native_title", "romanized_title" })
        {
            Execute(conn, $"""
                INSERT INTO {SearchTableName} (series_id, title)
                SELECT id, [{column}] FROM series
                WHERE state = 'active' AND [{column}] IS NOT NULL
                """, tx);
        }

        // The titles column holds every alternative title as JSON: [{"title": …, "language": …}, …]
        Execute(conn, $"""
            INSERT INTO {SearchTableName} (series_id, title)
            SELECT s.id, json_extract(je.value, '$.title')
            FROM series s, json_each(s.titles) je
            WHERE s.state = 'active' AND s.titles IS NOT NULL
              AND json_extract(je.value, '$.title') IS NOT NULL
            """, tx);
        tx.Commit();
    }

    private async Task SwapIntoPlaceAsync(string stagingPath, CancellationToken ct)
    {
        // Readers use Pooling=False, but an in-flight query may still hold the old file
        // open for a moment — retry the move instead of failing the whole refresh.
        SqliteConnection.ClearAllPools();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(stagingPath, options.DatabasePath, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }

    private static object? Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 600;
        return cmd.ExecuteScalar();
    }

    private static void Execute(SqliteConnection conn, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        cmd.CommandTimeout = 600; // FTS population scans the full 3 GB table
        cmd.ExecuteNonQuery();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Pass-through read stream that feeds every byte it serves into an IncrementalHash.</summary>
internal sealed class HashingReadStream(Stream inner, IncrementalHash hash) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        if (read > 0)
        {
            hash.AppendData(buffer, offset, read);
        }

        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var read = await inner.ReadAsync(buffer, ct);
        if (read > 0)
        {
            hash.AppendData(buffer.Span[..read]);
        }

        return read;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
