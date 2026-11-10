using System.Collections.Concurrent;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

public enum VolumeHoldReason { None, Searching, Proposal, Downloading }

/// <summary>
/// What volume first holds back for a series. <see cref="Through"/> null holds every chapter;
/// otherwise only chapters numbered up to it, so chapters past the release's last volume still
/// download from sources.
/// </summary>
public sealed record VolumeHold(VolumeHoldReason Reason, decimal? Through = null, int? ProposalId = null, DateTime? Until = null)
{
    public static readonly VolumeHold None = new(VolumeHoldReason.None);

    public bool Active => Reason != VolumeHoldReason.None;

    public bool Holds(Chapter chapter) => Active && (Through is not { } through || chapter.Number <= through);
}

/// <summary>
/// The volume-first searches running in this process, read by <see cref="VolumeFirstGate"/>. A search
/// counts as running for at most <see cref="Deadline"/> from its start, whatever happens to it, so a
/// search that hangs or whose release was never disposed cannot hold a series for ever.
/// </summary>
public sealed class VolumeFirstSearches(TimeProvider time)
{
    /// <summary>The longest a series waits on its search; the match worker cancels the search here too.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<int, Run> running = new();

    public bool IsRunning(int seriesId) =>
        running.TryGetValue(seriesId, out var run) && time.GetUtcNow() - run.Started < Deadline;

    public IDisposable Begin(int seriesId)
    {
        var run = new Run(this, seriesId, time.GetUtcNow());
        running[seriesId] = run;
        return run;
    }

    private sealed class Run(VolumeFirstSearches owner, int seriesId, DateTimeOffset started) : IDisposable
    {
        public DateTimeOffset Started { get; } = started;

        // Only the latest run for a series clears it, so an older one ending never ends a newer hold.
        public void Dispose() => owner.running.TryRemove(new KeyValuePair<int, Run>(seriesId, this));
    }
}

/// <summary>
/// New chapters the monitored refresh left alone because a volume release covered them, so the refresh
/// can queue them once the hold ends without the release having brought them. In memory only: after a
/// restart they stay wanted and wait for Download all wanted or Smart Download.
/// </summary>
public sealed class VolumeFirstDeferrals
{
    private readonly ConcurrentDictionary<int, HashSet<int>> held = new();

    public void Add(int seriesId, IEnumerable<int> chapterIds)
    {
        var ids = chapterIds.ToList();
        if (ids.Count == 0)
        {
            return;
        }

        held.AddOrUpdate(seriesId, _ => [.. ids], (_, set) =>
        {
            lock (set)
            {
                set.UnionWith(ids);
            }

            return set;
        });
    }

    public IReadOnlyList<int> SeriesIds => [.. held.Keys];

    public IReadOnlyList<int> For(int seriesId)
    {
        if (!held.TryGetValue(seriesId, out var set))
        {
            return [];
        }

        lock (set)
        {
            return [.. set];
        }
    }

    /// <summary>Forgets chapters that were queued, downloaded or are no longer wanted.</summary>
    public void Settle(int seriesId, IEnumerable<int> chapterIds)
    {
        if (!held.TryGetValue(seriesId, out var set))
        {
            return;
        }

        lock (set)
        {
            set.ExceptWith(chapterIds);
            if (set.Count == 0)
            {
                held.TryRemove(new KeyValuePair<int, HashSet<int>>(seriesId, set));
            }
        }
    }
}

/// <summary>
/// With "volume first" on, automatic source downloads (monitor refresh and Smart Download) wait while a
/// volume release may still bring the same chapters: while the series' volume search runs, while a
/// proposal waits for an answer (<see cref="ProposalHold"/>) and while a release downloads
/// (<see cref="DownloadHold"/>). Only chapters the release covers wait; anything past its last volume
/// goes on as usual. Downloads started by hand, request approvals and health repairs are never held.
/// </summary>
public class VolumeFirstGate(
    MakiDbContext db, IAppSettings settings, VolumeFirstSearches searches, TorrentUpgradeService torrents,
    TimeProvider time)
{
    public static readonly TimeSpan ProposalHold = TimeSpan.FromHours(48);
    public static readonly TimeSpan DownloadHold = TimeSpan.FromHours(72);

    public async Task<VolumeHold> CheckAsync(int seriesId, CancellationToken ct)
    {
        if (await settings.GetAsync(SettingKeys.DownloadsVolumeFirst, ct) != "true")
        {
            return VolumeHold.None;
        }

        if (searches.IsRunning(seriesId))
        {
            return new VolumeHold(VolumeHoldReason.Searching);
        }

        var now = time.GetUtcNow().UtcDateTime;
        var proposal = await db.TorrentProposals.AsNoTracking()
            .Where(p => p.SeriesId == seriesId && p.Status == TorrentProposalStatus.Pending && p.CreatedAtUtc > now - ProposalHold)
            .OrderByDescending(p => p.CreatedAtUtc)
            .Select(p => new { p.Id, p.Title, p.CreatedAtUtc })
            .FirstOrDefaultAsync(ct);
        if (proposal is not null)
        {
            return new VolumeHold(VolumeHoldReason.Proposal,
                await torrents.CoveredThroughAsync(seriesId, proposal.Title, ct), proposal.Id,
                proposal.CreatedAtUtc + ProposalHold);
        }

        var download = await db.DownloadQueue.AsNoTracking()
            .Where(q => q.SeriesId == seriesId && q.Protocol == AcquisitionProtocol.Torrent &&
                        q.Status != QueueStatus.Completed && q.Status != QueueStatus.Failed &&
                        q.Status != QueueStatus.Cancelled && q.QueuedAt > now - DownloadHold)
            .OrderByDescending(q => q.QueuedAt)
            .Select(q => new { q.Title, q.QueuedAt })
            .FirstOrDefaultAsync(ct);
        return download is null
            ? VolumeHold.None
            : new VolumeHold(VolumeHoldReason.Downloading,
                download.Title is { Length: > 0 } title ? await torrents.CoveredThroughAsync(seriesId, title, ct) : null,
                Until: download.QueuedAt + DownloadHold);
    }
}
