using System.Collections.Concurrent;
using System.Threading.Channels;
using Maki.Api.Jobs;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Refreshes every series whose mapping on one source is failing, from the health page's grouped
/// source warning once the site is back. Runs off the request: sixty series on a browser-driven
/// source take minutes, and the page polls <see cref="Progress"/> instead of holding a connection.
/// <para>
/// Each series goes through <see cref="RefreshMonitoredSeriesJob.RefreshSeriesAsync"/>, not a bare
/// sync, so chapters released during the outage are queued and announced exactly as the scheduled
/// refresh would have. A bare sync inserts them as known, and the next scheduled pass then sees
/// nothing new and never queues them.
/// </para>
/// </summary>
public class HealthSourceRecovery(
    IServiceProvider services,
    IServiceScopeFactory scopeFactory,
    ILogger<HealthSourceRecovery> logger) : BackgroundService
{
    public record Status(int Done, int Total);

    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    private readonly ConcurrentDictionary<string, Status> _running = new(StringComparer.OrdinalIgnoreCase);

    public Status? Progress(string source) => _running.GetValueOrDefault(source);

    /// <returns>False when that source is already being refreshed.</returns>
    public bool Enqueue(string source)
    {
        if (!_running.TryAdd(source, new Status(0, 0))) return false;
        _queue.Writer.TryWrite(source);
        return true;
    }

    public static IQueryable<Core.Entities.SourceMapping> Failing(MakiDbContext db, string source) =>
        db.SourceMappings.Where(m => m.SourceName == source && m.Enabled && m.LastError != null);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var source in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await RunAsync(source, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Refreshing failing series on {Source} failed", source);
            }
            finally
            {
                _running.TryRemove(source, out _);
            }
        }
    }

    private async Task RunAsync(string source, CancellationToken ct)
    {
        List<int> seriesIds;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
            seriesIds = await Failing(db, source).Select(m => m.SeriesId).Distinct().ToListAsync(ct);
        }

        var refresh = ActivatorUtilities.CreateInstance<RefreshMonitoredSeriesJob>(services);
        var done = 0;
        _running[source] = new Status(done, seriesIds.Count);
        foreach (var seriesId in seriesIds)
        {
            try
            {
                await refresh.RefreshSeriesAsync(seriesId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Refresh failed for series {SeriesId}", seriesId);
            }

            _running[source] = new Status(++done, seriesIds.Count);
        }

        logger.LogInformation("Refreshed {Count} series failing on {Source}", seriesIds.Count, source);

        // Without this the grouped warning keeps its old count until the next scheduled check.
        using (var scope = scopeFactory.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<HealthMonitor>().RefreshAsync(ct);
        }
    }
}
