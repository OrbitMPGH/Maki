using Maki.Core.Entities;
using Maki.Core.Storage;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <param name="FreeBytes">What the volume had free at the last reading.</param>
/// <param name="MinFreeBytes">The root's threshold, already defaulted.</param>
public sealed record DiskHold(int RootFolderId, string Path, long FreeBytes, long MinFreeBytes);

/// <summary>
/// Holds new downloads into a root folder whose volume has less free space than the root's
/// <see cref="RootFolder.MinFreeBytes"/>. Like a pause it never changes a row: claims skip series in a
/// held root and torrent grabs for them are refused, and both pick up again once space returns.
/// <para>
/// Free space is read at most once per <see cref="ReadInterval"/> per root, because a NAS share can
/// take seconds to answer and claims run every few seconds. A download failing with a disk error
/// expires the reading through <see cref="HealthWriteProbe.Expired"/>, so the next claim measures again.
/// A root whose free space cannot be read is not held: an unreachable share already parks its rows.
/// </para>
/// </summary>
public class DownloadDiskHold
{
    public static readonly TimeSpan ReadInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _time;
    private readonly Func<string, long?> _freeSpace;
    private readonly ILogger<DownloadDiskHold> _logger;
    private readonly SemaphoreSlim _refresh = new(1, 1);

    private volatile Snapshot _snapshot = new([], []);
    private long _readAtTicks = -1;
    private volatile bool _expired;
    private int _version;

    private sealed record Snapshot(IReadOnlyList<DiskHold> Holds, IReadOnlyList<int> RootIds);

    public DownloadDiskHold(
        IServiceScopeFactory scopeFactory, TimeProvider time, ILogger<DownloadDiskHold> logger,
        Func<string, long?>? freeSpace = null)
    {
        _scopeFactory = scopeFactory;
        _time = time;
        _logger = logger;
        _freeSpace = freeSpace ?? DiskSpace.AvailableFor;
        HealthWriteProbe.Shared.Expired += Expire;
    }

    /// <summary>Roots held at the last reading.</summary>
    public IReadOnlyList<DiskHold> Current => _snapshot.Holds;

    /// <summary>Ids of the roots held at the last reading, for the claim query.</summary>
    public IReadOnlyList<int> HeldRootIds => _snapshot.RootIds;

    /// <summary>Goes up each time the set of held roots changes, so a poller can tell open pages.</summary>
    public int Version => Volatile.Read(ref _version);

    /// <summary>The next <see cref="RefreshAsync"/> reads every root again, however recent the last reading.</summary>
    public void Expire() => _expired = true;

    /// <summary>
    /// Reads every root's free space when the last reading is older than <see cref="ReadInterval"/> or
    /// was expired. A caller arriving while another reads keeps the previous answer rather than waiting.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var readAt = Interlocked.Read(ref _readAtTicks);
        if (!_expired && readAt >= 0 && now.Ticks - readAt < ReadInterval.Ticks)
        {
            return;
        }

        if (!await _refresh.WaitAsync(0, ct))
        {
            return;
        }

        try
        {
            _expired = false;
            List<RootFolder> roots;
            using (var scope = _scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
                roots = await db.RootFolders.AsNoTracking().ToListAsync(ct);
            }

            var holds = new List<DiskHold>();
            foreach (var root in roots)
            {
                var min = root.EffectiveMinFreeBytes;
                if (min <= 0)
                {
                    continue;
                }

                var path = root.Path;
                if (await Task.Run(() => _freeSpace(path), ct) is { } free && free < min)
                {
                    holds.Add(new DiskHold(root.Id, root.Path, free, min));
                }
            }

            Interlocked.Exchange(ref _readAtTicks, now.Ticks);
            var previous = _snapshot.RootIds;
            var ids = holds.Select(h => h.RootFolderId).Order().ToList();
            _snapshot = new Snapshot(holds, ids);
            if (!previous.SequenceEqual(ids))
            {
                Interlocked.Increment(ref _version);
                foreach (var hold in holds.Where(h => !previous.Contains(h.RootFolderId)))
                {
                    _logger.LogWarning("Holding downloads into {Path}: {Free} bytes free, below {Min}",
                        hold.Path, hold.FreeBytes, hold.MinFreeBytes);
                }

                foreach (var lifted in previous.Where(id => !ids.Contains(id)))
                {
                    _logger.LogInformation("Downloads into root folder {Id} resumed, free space is back", lifted);
                }
            }
        }
        finally
        {
            _refresh.Release();
        }
    }

    /// <summary>The hold on <paramref name="seriesId"/>'s root folder, read fresh when the last reading has aged out.</summary>
    public async Task<DiskHold?> ForSeriesAsync(MakiDbContext db, int seriesId, CancellationToken ct = default)
    {
        await RefreshAsync(ct);
        var holds = Current;
        if (holds.Count == 0)
        {
            return null;
        }

        var rootId = await db.Series.IgnoreQueryFilters()
            .Where(s => s.Id == seriesId)
            .Select(s => (int?)s.RootFolderId)
            .FirstOrDefaultAsync(ct);
        return holds.FirstOrDefault(h => h.RootFolderId == rootId);
    }

    /// <summary>
    /// The torrent grab's gate: refuses a grab for a series whose root folder is held, so a release is
    /// not fetched into a library with no room for it.
    /// </summary>
    public async Task ThrowIfGrabHeldAsync(MakiDbContext db, int seriesId, CancellationToken ct = default)
    {
        if (await ForSeriesAsync(db, seriesId, ct) is not null)
        {
            throw new ReleaseRefusedException(DiskLowKey, "The series' root folder is low on free space");
        }
    }

    public const string DiskLowKey = "error.release.diskLow";
}
