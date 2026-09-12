using Maki.Core.Io;
using Maki.Core;
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
    // Publish the indexes and their file stamp together so readers cannot mix generations.
    private sealed record CacheEntry(CatalogueIndexes Indexes, long StampTicks, long StampLength);
    private volatile CacheEntry? _entry;
    private readonly IdleStamp _idle = new();

    /// <summary>Whether the artifact is currently in memory, for the memory diagnostics.</summary>
    public bool IsLoaded => _entry is not null;

    /// <summary>How long since anything last read it. Meaningless while unloaded.</summary>
    public TimeSpan IdleFor => _idle.Idle;

    /// <summary>Drops the cached indexes so the next read rebuilds. Cheap; safe any time.</summary>
    public void Invalidate()
    {
        _entry = null;
        logger.LogDebug("Catalogue indexes invalidated");
    }

    /// <summary>
    /// Drops the built indexes when nothing has read them for <paramref name="idleFor"/>, and
    /// reports whether it did.
    ///
    /// <para>
    /// The largest of the unloadable artifacts at ~52 MB, and also the slowest to rebuild - about
    /// nine seconds of scanning the dump - so the idle window wants to be long enough that a person
    /// browsing never meets it. No lock, for the reason given on <see cref="Invalidate"/>'s
    /// neighbours: what is handed out is immutable and a reader holds its own reference.
    /// </para>
    /// </summary>
    public bool ReleaseIfIdle(TimeSpan idleFor)
    {
        var idle = _idle.Idle;
        if (_entry is null || idle < idleFor)
        {
            return false;
        }

        _entry = null;
        logger.LogInformation(
            "Unloaded the catalogue indexes after {Minutes:F0} idle minute(s); they rebuild on next use",
            idle.TotalMinutes);
        return true;
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
        if (_entry is { } cached && ticks == cached.StampTicks && length == cached.StampLength)
        {
            _idle.Touch();
            return cached.Indexes;
        }

        await _lock.WaitAsync(ct);
        try
        {
            // Another caller may have rebuilt against a newer dump while this one waited. Read
            // the stamp again so an old observation cannot trigger a redundant rebuild.
            info.Refresh();
            if (!info.Exists)
            {
                return null;
            }

            ticks = info.LastWriteTimeUtc.Ticks;
            length = info.Length;
            if (_entry is { } raced && ticks == raced.StampTicks && length == raced.StampLength)
            {
                _idle.Touch();
                return raced.Indexes;
            }

            if (_entry is not null)
            {
                logger.LogInformation("Rebuilding catalogue indexes because the dump file changed");
            }

            var built = await Task.Run(() => Build(ct), ct);
            if (built is null)
            {
                return null;
            }

            _entry = new CacheEntry(built, ticks, length);
            _idle.Touch();
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
        finally
        {
            // Two full scans of a multi-gigabyte dump, and nothing reads those pages again until
            // the next rebuild. Ordinary searches re-cache the handful of pages they touch.
            PageCache.DropAfterScan(dumpOptions.DatabasePath);
        }
    }
}
