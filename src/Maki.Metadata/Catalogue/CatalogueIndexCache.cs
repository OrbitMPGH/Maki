using Maki.Metadata.MangaBaka;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Maki.Metadata.Catalogue;

/// <summary>The pair of in-memory catalogue indexes, always built and replaced together.</summary>
public sealed record CatalogueIndexes(CreditIndex Credits, FuzzyTermIndex Terms);

/// <summary>
/// Owns the process-wide <see cref="CreditIndex"/> and <see cref="FuzzyTermIndex"/>: builds both
/// from the dump on first use and hands the same instances to every search. Modelled on
/// <c>VectorIndexCache</c>, for the same reason, and the indexes are immutable once built so
/// readers need no lock.
///
/// <para>
/// Staleness is the dump file's write time and length rather than an explicit invalidation call.
/// <c>MangaBakaDumpService.SwapIntoPlaceAsync</c> moves a freshly written staging file over the old
/// one, so both genuinely change, and a <c>FileInfo</c> per search costs nothing next to the search
/// itself. That keeps the dump service from having to know this cache exists.
/// </para>
///
/// <para>
/// A cold build is about 3.5 s of the two scans combined, which is why
/// <c>DiscoverCacheWarmJob</c> triggers it at startup and again after a new dump installs, rather
/// than letting it land on whichever keystroke happens to arrive first.
/// </para>
/// </summary>
public sealed class CatalogueIndexCache(
    MangaBakaDumpOptions dumpOptions, ILogger<CatalogueIndexCache> logger)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile CatalogueIndexes? _indexes;
    private long _stampTicks;
    private long _stampLength;

    /// <summary>Drops the cached indexes so the next read rebuilds. Cheap; safe any time.</summary>
    public void Invalidate()
    {
        _indexes = null;
        logger.LogDebug("Catalogue indexes invalidated");
    }

    /// <summary>
    /// The indexes, building them if needed. Null when there is no dump to read, or when reading it
    /// failed: every caller treats that as "this feature is off", never as an error.
    /// </summary>
    public async Task<CatalogueIndexes?> GetAsync(CancellationToken ct = default)
    {
        var info = new FileInfo(dumpOptions.DatabasePath);
        if (!info.Exists)
        {
            return null;
        }

        var ticks = info.LastWriteTimeUtc.Ticks;
        var length = info.Length;
        if (_indexes is { } cached && ticks == _stampTicks && length == _stampLength)
        {
            return cached;
        }

        await _lock.WaitAsync(ct);
        try
        {
            if (_indexes is { } raced && ticks == _stampTicks && length == _stampLength)
            {
                return raced;
            }

            var built = await Task.Run(() => Build(ct), ct);
            if (built is null)
            {
                return null;
            }

            _indexes = built;
            _stampTicks = ticks;
            _stampLength = length;
            return built;
        }
        finally
        {
            _lock.Release();
        }
    }

    private CatalogueIndexes? Build(CancellationToken ct)
    {
        try
        {
            // Pooling=False for the same reason every other reader here uses it: the nightly swap
            // has to be able to replace the file.
            using var conn = new SqliteConnection(
                $"Data Source={dumpOptions.DatabasePath};Mode=ReadOnly;Pooling=False");
            conn.Open();

            var credits = CreditIndex.Build(conn, logger, ct);
            var terms = FuzzyTermIndex.Build(conn, MangaBakaDumpService.SearchTableName, logger, ct);
            return new CatalogueIndexes(credits, terms);
        }
        catch (SqliteException ex)
        {
            // An older or half-written dump missing a column this needs. Creator search and typo
            // tolerance go quiet; ordinary search carries on.
            logger.LogWarning(ex, "Could not build the catalogue indexes from the dump");
            return null;
        }
    }
}
