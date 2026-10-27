using System.Collections.Concurrent;
using System.Text.Json;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Download;
using Maki.Core.Entities;
using Maki.Core.Images;
using Maki.Core.Paths;
using Maki.Data;
using Microsoft.EntityFrameworkCore;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Removes imported torrents from the download client (<see cref="DownloadQueueItem.ClientRemovalPendingAt"/>).
/// <para>
/// Its own job rather than part of <see cref="CompletedDownloadJob"/>: that poll stops listing the
/// client once no torrent is in flight, which is exactly when an imported one is waiting here. Nothing
/// about a removal is urgent, so a few minutes between runs is fine.
/// </para>
/// </summary>
[DisallowConcurrentExecution]
public class TorrentRemovalJob(
    MakiDbContext db,
    IAppSettings settings,
    TorrentClientRegistry torrentClients,
    ILogger<TorrentRemovalJob> logger) : IJob
{
    /// <summary>A removal the client keeps refusing is dropped after this long, and the row ages out as usual.</summary>
    internal static readonly TimeSpan GiveUpAfter = TimeSpan.FromDays(30);

    // Per row, so a client that stays down logs one line per torrent rather than one every run.
    internal static readonly ConcurrentDictionary<int, byte> FailureLogged = new();

    public Task Execute(IJobExecutionContext context) => RunAsync(context.CancellationToken);

    public async Task RunAsync(CancellationToken ct)
    {
        var pending = await db.DownloadQueue
            .Where(q => q.ClientRemovalPendingAt != null)
            .OrderBy(q => q.ClientRemovalPendingAt)
            .ToListAsync(ct);
        if (pending.Count == 0)
        {
            return;
        }

        // With no client configured the fingerprint never matches, so those rows settle rather than wait.
        var client = await DownloadClientSettingsStore.LoadAsync(settings, ct);
        var fingerprint = DownloadClientSettingsStore.Fingerprint(client);
        var pathMap = (From: await settings.GetAsync(SettingKeys.DownloadClientPathMapFrom, ct),
            To: await settings.GetAsync(SettingKeys.DownloadClientPathMapTo, ct));

        // The snapshot keeps a later setting from widening a removal, never from narrowing one: turning
        // removal or file deletion off still reaches rows that are waiting.
        var removing = TorrentRemovalModes.Parse(
            await settings.GetAsync(SettingKeys.DownloadClientRemoveCompleted, ct)) == TorrentRemovalModes.AfterImport;
        var deletingData = await settings.GetAsync(SettingKeys.DownloadClientRemoveData, ct) != "false";
        var roots = await db.RootFolders.IgnoreQueryFilters().Select(r => r.Path).ToListAsync(ct);
        var driver = torrentClients.For(client.Kind);

        foreach (var item in pending)
        {
            var info = ReleaseInfoOf(item);
            if (info?.TorrentHash is not { } hash || !removing || item.ClientRemovalMode != TorrentRemovalModes.AfterImport)
            {
                await SettleAsync(item, removed: false, ct);
                continue;
            }

            if (item.ClientRemovalClient != fingerprint)
            {
                logger.LogInformation(
                    "Not removing torrent '{Title}': the download client changed since it was imported", item.Title);
                await SettleAsync(item, removed: false, ct);
                continue;
            }

            if (DateTime.UtcNow - item.ClientRemovalPendingAt > GiveUpAfter)
            {
                logger.LogWarning(
                    "Gave up removing torrent '{Title}' from {Client} after {Days} days", item.Title,
                    client.Kind.DisplayName(), (int)GiveUpAfter.TotalDays);
                await SettleAsync(item, removed: false, ct);
                continue;
            }

            // Asked per row, just before the call, so a grab of the same release made while this run works
            // through earlier rows still holds its torrent.
            if (await StillNeededAsync(item, hash, ct))
            {
                continue;
            }

            // Files Maki cannot see, or that share a folder with the library, are files Maki cannot vouch
            // for; the torrent still goes.
            var mapped = info.ContentPath is null ? null : PathRemapper.Map(info.ContentPath, pathMap.From, pathMap.To);
            var deleteData = item.ClientRemovalDeleteData && deletingData && mapped is not null &&
                (Directory.Exists(mapped) || File.Exists(mapped)) && !roots.Any(root => Overlaps(mapped, root));

            TorrentRemoval result;
            try
            {
                result = await driver.RemoveAsync(client, hash, deleteData, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException ||
                                       (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                if (FailureLogged.TryAdd(item.Id, 0))
                {
                    logger.LogWarning("Could not remove torrent '{Title}' from {Client} ({Error}); trying again later",
                        item.Title, client.Kind.DisplayName(), ex.Message);
                }

                continue;
            }

            switch (result)
            {
                case TorrentRemoval.Removed:
                    logger.LogInformation("Removed torrent '{Title}' from {Client}{Data}", item.Title,
                        client.Kind.DisplayName(), deleteData ? " with its files" : ", keeping its files");
                    if (deleteData)
                    {
                        await ReleaseHardSkipsAsync(hash, ct);
                    }

                    break;
                case TorrentRemoval.HeldOutsideLabel:
                    logger.LogInformation(
                        "Left torrent '{Title}' in {Client}: it was moved out of Maki's category or label",
                        item.Title, client.Kind.DisplayName());
                    break;
            }

            await SettleAsync(item, removed: result == TorrentRemoval.Removed, ct);
        }
    }

    /// <summary>
    /// Whether another row may still need the torrent: one in flight or failed on the same hash (a failed one
    /// can be retried), or a hashless grab in flight for the same series, which can claim a torrent by name.
    /// </summary>
    private Task<bool> StillNeededAsync(DownloadQueueItem item, string hash, CancellationToken ct) =>
        db.DownloadQueue.AnyAsync(q =>
            q.Id != item.Id &&
            q.Protocol == AcquisitionProtocol.Torrent &&
            q.Status != QueueStatus.Completed &&
            q.Status != QueueStatus.Cancelled &&
            q.ReleaseInfoJson != null &&
            (q.ReleaseInfoJson.Contains(hash) ||
             (q.Status != QueueStatus.Failed && q.SeriesId == item.SeriesId &&
              q.ReleaseInfoJson.Contains("\"TorrentHash\":null"))), ct);

    /// <summary>
    /// Conditional, so a row deleted since it was read (history cleared, series deleted) is not
    /// resurrected or failed on: deleting the row is how a removal gets cancelled.
    /// </summary>
    private async Task SettleAsync(DownloadQueueItem item, bool removed, CancellationToken ct)
    {
        FailureLogged.TryRemove(item.Id, out _);
        DateTime? removedAt = removed ? DateTime.UtcNow : null;
        await db.DownloadQueue
            .Where(q => q.Id == item.Id && q.ClientRemovalPendingAt != null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(q => q.ClientRemovalPendingAt, (DateTime?)null)
                .SetProperty(q => q.ClientRemovedAt, removedAt), ct);
    }

    /// <summary>
    /// Page conversion stamps a hardlinked import <see cref="PlanAction.HardSkipName"/> and never looks
    /// at it again. With the client's copy deleted the library file stands alone, so it gets a fresh look.
    /// </summary>
    private Task<int> ReleaseHardSkipsAsync(string hash, CancellationToken ct) =>
        db.ChapterFiles
            .Where(f => f.ReleaseHash == hash && f.CheckedWith == PlanAction.HardSkipName)
            .ExecuteUpdateAsync(s => s
                .SetProperty(f => f.ConversionCheckedAtUtc, (DateTime?)null)
                .SetProperty(f => f.CheckedWith, (string?)null), ct);

    /// <summary>One path is the other or lies inside it, compared without regard to case or separator.</summary>
    private static bool Overlaps(string a, string b)
    {
        static string Norm(string p) => Path.GetFullPath(p).Replace('\\', '/').TrimEnd('/') + "/";
        var (x, y) = (Norm(a), Norm(b));
        return x.StartsWith(y, StringComparison.OrdinalIgnoreCase) || y.StartsWith(x, StringComparison.OrdinalIgnoreCase);
    }

    private static ReleaseInfo? ReleaseInfoOf(DownloadQueueItem item) =>
        item.ReleaseInfoJson is null ? null : JsonSerializer.Deserialize<ReleaseInfo>(item.ReleaseInfoJson);
}
