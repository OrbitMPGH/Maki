using Maki.Core.Entities;

namespace Maki.Api.Services;

/// <summary>
/// In-process locks for work that reads a series' files on disk and rewrites its ChapterFile rows
/// from what it saw: rescan, import linking, download placement, rename, move and delete. Each is
/// check-then-act over the folder and the rows, and two of them interleaving on one series
/// duplicated rows or lost them. Not reentrant: never call into another locked entry point while
/// holding the same series.
/// </summary>
public static class SeriesLocks
{
    private static readonly KeyedAsyncLock<int> Series = new();
    private static readonly KeyedAsyncLock<int> ProviderIds = new();
    private static readonly KeyedAsyncLock<int> FolderNames = new();

    public static Task<IDisposable> SeriesAsync(int seriesId, CancellationToken ct) =>
        Series.AcquireAsync(seriesId, ct);

    /// <summary>
    /// Held from "is this MangaBaka id already in the library?" through inserting the row, so an add,
    /// an import and an import list racing on one work cannot each insert their own copy.
    /// </summary>
    public static Task<IDisposable> ProviderIdAsync(int mangaBakaId, CancellationToken ct) =>
        ProviderIds.AcquireAsync(mangaBakaId, ct);

    /// <summary>
    /// Process-wide, held from reading which folder names are taken through saving the series that
    /// claims one. Two different works can standardize to the same name, and the provider-id lock
    /// does not span them. Never take another lock while holding this one.
    /// </summary>
    public static Task<IDisposable> FolderNamesAsync(CancellationToken ct) =>
        FolderNames.AcquireAsync(0, ct);

    /// <summary>
    /// Queue items a worker is holding right now. Queued, resolving and parked items are not in it:
    /// nothing has claimed them, and they cascade away with their series or chapter. A torrent sits
    /// in Downloading inside the client for as long as it takes, so only its import counts; an
    /// unattended import never writes Importing, so it is read from the importer's own registry, as
    /// long as the row has not been settled: a hung import must not block a delete once the user has
    /// removed its row.
    /// </summary>
    public static IQueryable<DownloadQueueItem> InFlight(IQueryable<DownloadQueueItem> queue)
    {
        var importing = TorrentImportService.AutomaticImportIds();
        return queue.Where(q => q.Status == QueueStatus.Importing ||
                                (importing.Contains(q.Id) &&
                                 q.Status != QueueStatus.Cancelled &&
                                 q.Status != QueueStatus.Failed &&
                                 q.Status != QueueStatus.Completed) ||
                                (q.Protocol == AcquisitionProtocol.Scraper &&
                                 (q.Status == QueueStatus.FetchingPages ||
                                  q.Status == QueueStatus.Downloading ||
                                  q.Status == QueueStatus.Validating ||
                                  q.Status == QueueStatus.Packaging)));
    }
}

internal sealed class KeyedAsyncLock<TKey> where TKey : notnull
{
    private readonly Dictionary<TKey, Entry> _entries = new();

    public async Task<IDisposable> AcquireAsync(TKey key, CancellationToken ct)
    {
        Entry entry;
        lock (_entries)
        {
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                _entries[key] = entry;
            }

            entry.Users++;
        }

        try
        {
            await entry.Gate.WaitAsync(ct);
        }
        catch
        {
            Leave(key, entry);
            throw;
        }

        return new Releaser(this, key, entry);
    }

    private void Leave(TKey key, Entry entry)
    {
        lock (_entries)
        {
            if (--entry.Users == 0)
            {
                _entries.Remove(key);
            }
        }
    }

    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public int Users;
    }

    private sealed class Releaser(KeyedAsyncLock<TKey> owner, TKey key, Entry entry) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                entry.Gate.Release();
                owner.Leave(key, entry);
            }
        }
    }
}
