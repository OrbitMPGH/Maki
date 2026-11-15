using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Maki.Metadata.Ease;

/// <summary>Where the EASE weight table lives.</summary>
public record EaseOptions(string DatabasePath);

/// <summary>
/// Loads an <see cref="EaseTable"/> on first use and hands the same instance to every request.
/// Only the eval harness constructs one today. An absent or unreadable file returns null, and
/// <c>SemanticRecommender</c> then behaves exactly as it does without the channel.
/// </summary>
public sealed class EaseCache(EaseOptions options, ILogger<EaseCache> logger)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly LoadFailureMemo _failed = new();
    private volatile EaseTable? _table;

    public string DatabasePath => options.DatabasePath;

    public async Task<EaseTable?> GetAsync(CancellationToken ct = default)
    {
        if (_table is { } cached)
        {
            return cached;
        }

        await _lock.WaitAsync(ct);
        try
        {
            if (_table is { } raced)
            {
                return raced;
            }

            if (!File.Exists(options.DatabasePath) || _failed.ShouldSkip(options.DatabasePath))
            {
                return null;
            }

            var observed = _failed.Observe(options.DatabasePath);
            var loaded = Load();
            if (loaded is null)
            {
                _failed.Record(observed);
            }

            _table = loaded;
            return loaded;
        }
        finally
        {
            _lock.Release();
        }
    }

    private EaseTable? Load()
    {
        var started = DateTime.UtcNow;
        try
        {
            using var conn = new SqliteConnection($"Data Source={options.DatabasePath};Mode=ReadOnly;Pooling=False");
            conn.Open();
            var rows = new List<(long, long, float)>(2_000_000);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT seed_id, item_id, weight FROM weight";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add((reader.GetInt64(0), reader.GetInt64(1), reader.IsDBNull(2) ? 0 : reader.GetFloat(2)));
                }
            }

            var table = EaseTable.Build(rows);
            if (table.Count == 0)
            {
                return null;
            }

            logger.LogInformation(
                "Loaded EASE table: {Seeds} seeds, {Weights} weights in {Elapsed:F0}ms",
                table.Count, table.WeightCount, (DateTime.UtcNow - started).TotalMilliseconds);
            return table;
        }
        catch (SqliteException ex)
        {
            logger.LogWarning(ex, "Could not read the EASE table at {Path}", options.DatabasePath);
            return null;
        }
    }
}
