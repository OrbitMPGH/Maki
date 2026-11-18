using Maki.Metadata.Embedding;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Maki.Metadata.PageShare;

/// <summary>Where the page-share table lives, and where its download is staged.</summary>
public record PageShareOptions(string DatabasePath, string? StagingDirectory = null);

/// <summary>
/// Owns the process-wide <see cref="PageFrequency"/> read from <c>page-share.db</c>. A few thousand
/// rows, so unlike <see cref="Ease.EaseCache"/> it has no snapshot and no idle unload: it loads on
/// first use and stays until a new file is swapped in.
/// <para>
/// <b>An absent file is normal, not an error.</b> <see cref="GetAsync"/> returns null and the
/// penalty does nothing.
/// </para>
/// </summary>
public sealed class PageShareCache(PageShareOptions options, ILogger<PageShareCache> logger)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly LoadFailureMemo _failed = new();
    private volatile PageFrequency? _table;

    public string DatabasePath => options.DatabasePath;

    /// <summary>Drops the loaded table so the next request reloads it.</summary>
    public void Invalidate()
    {
        _table = null;
        _failed.Clear();
    }

    /// <summary>
    /// Replaces the table file with <paramref name="stagedPath"/> under the load lock, so a swap never
    /// races a load reading the old file.
    /// </summary>
    public async Task SwapDatabaseAsync(string stagedPath, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            _table = null;
            SqliteConnection.ClearAllPools();
            foreach (var sidecar in new[] { options.DatabasePath + "-wal", options.DatabasePath + "-shm" })
            {
                if (File.Exists(sidecar))
                {
                    File.Delete(sidecar);
                }
            }

            File.Move(stagedPath, options.DatabasePath, overwrite: true);
            _failed.Clear();
            logger.LogInformation("Swapped in a new page-share table at {Path}", options.DatabasePath);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>The table, loading it if needed. Null when there is no file or it cannot be read.</summary>
    public async Task<PageFrequency?> GetAsync(CancellationToken ct = default)
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
            try
            {
                var loaded = PageFrequency.Load(options.DatabasePath);
                if (loaded.Count == 0)
                {
                    _failed.Record(observed);
                    return null;
                }

                logger.LogInformation("Loaded page-share table: {Count} series", loaded.Count);
                _table = loaded;
                return loaded;
            }
            catch (Exception ex) when (ex is SqliteException or IOException or InvalidDataException)
            {
                _failed.Record(observed);
                logger.LogWarning(ex, "Could not read the page-share table at {Path}", options.DatabasePath);
                return null;
            }
        }
        finally
        {
            _lock.Release();
        }
    }
}
