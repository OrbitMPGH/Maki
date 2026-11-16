using System.Collections.Concurrent;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>Asks for another release once a torrent row failed as stalled.</summary>
public interface IStalledDownloadResearch
{
    /// <summary>Starts the search in the background and returns at once; a series already being searched is skipped.</summary>
    void Request(int seriesId);
}

/// <summary>
/// Runs the volume search for a series whose torrent stalled (<see cref="TorrentUpgradeService.SearchSeriesAsync"/>,
/// with the instance switches and the series' upgrade eligibility as the daily search applies them, minus the
/// weekly interval, plus the missing-chapter scope). The stalled release was declined before this runs, so the search takes the next best one.
/// Off the poll's thread, in its own scope, because an indexer search can take most of a minute.
/// </summary>
public sealed class StalledDownloadResearch(
    IServiceScopeFactory scopeFactory,
    IHostApplicationLifetime lifetime,
    ILogger<StalledDownloadResearch> logger) : IStalledDownloadResearch
{
    internal static readonly TimeSpan Deadline = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<int, byte> _running = new();

    public void Request(int seriesId)
    {
        if (_running.TryAdd(seriesId, 0))
        {
            _ = Task.Run(() => RunAsync(seriesId), CancellationToken.None);
        }
    }

    internal async Task RunAsync(int seriesId)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
            deadline.CancelAfter(Deadline);
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
            if (!await db.Series.AnyAsync(s => s.Id == seriesId && s.ArchivedAt == null, deadline.Token))
            {
                return;
            }

            var result = await scope.ServiceProvider.GetRequiredService<TorrentUpgradeService>()
                .SearchSeriesAsync(seriesId, deadline.Token, missing: true);
            if (result.Grabbed is { } grabbed)
            {
                logger.LogInformation("Grabbed another release for series {SeriesId} after a stalled torrent (queue row {Row})",
                    seriesId, grabbed);
            }
            else
            {
                logger.LogInformation("No replacement grabbed for series {SeriesId} after a stalled torrent ({Reason})",
                    seriesId, result.Reason ?? (result.ProposalId is null ? "nothing better found" : "proposed"));
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Search after a stalled torrent for series {SeriesId} did not finish", seriesId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Search after a stalled torrent for series {SeriesId} failed", seriesId);
        }
        finally
        {
            _running.TryRemove(seriesId, out _);
        }
    }
}
