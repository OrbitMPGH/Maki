using Maki.Api.Auth;
using Maki.Api.Hubs;
using Maki.Api.Localization;
using Maki.Core.Entities;
using Maki.Core.Security;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Runs the operations <see cref="SeriesBulkOperations"/> queues, one series at a time. Each series
/// gets its own scope set up as the caller (user, data scope, language), with the account, permissions,
/// root folders and content rating read again from the database, so a series the caller can no longer
/// see is refused as gone and an account that lost the action stops the job. Any other failure is
/// recorded against that series without stopping the rest. Progress goes to the caller's own SignalR
/// group as <c>seriesBulkProgress</c>.
/// </summary>
public sealed class SeriesBulkWorker(
    SeriesBulkOperations operations,
    IServiceScopeFactory scopeFactory,
    EventBroadcaster events,
    ILogger<SeriesBulkWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PushEvery = TimeSpan.FromMilliseconds(500);

    /// <summary>Pushes carry only the first few failures; the status endpoint has the full list.</summary>
    private const int PushedFailures = 20;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(LaneAsync(files: true, stoppingToken), LaneAsync(files: false, stoppingToken));

    private async Task LaneAsync(bool files, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            SeriesBulkOperation operation;
            try
            {
                operation = await operations.ReadAsync(files, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await RunAsync(operation, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Bulk {Action} operation {Id} stopped", operation.Action, operation.Id);
                operation.Finish(cancelled: false);
                await PushAsync(operation);
            }
        }
    }

    internal async Task RunAsync(SeriesBulkOperation operation, CancellationToken stoppingToken)
    {
        operation.Started();
        await PushAsync(operation);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, operation.Cancel.Token);
        var ct = linked.Token;
        var lastPush = DateTime.UtcNow;
        foreach (var seriesId in operation.SeriesIds)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            string? title, error;
            bool revoked;
            try
            {
                (title, error, revoked) = await RunOneAsync(operation, seriesId, ct);
            }
            catch (OperationCanceledException) when (operation.Cancel.IsCancellationRequested
                && !stoppingToken.IsCancellationRequested)
            {
                // Stopped mid-series: that series is not counted either way.
                break;
            }

            operation.Record(seriesId, title, error);
            if (revoked)
            {
                logger.LogWarning("Bulk {Action} operation {Id} stopped: user {UserId} may no longer run it",
                    operation.Action, operation.Id, operation.Caller.UserId);
                break;
            }

            if (DateTime.UtcNow - lastPush >= PushEvery)
            {
                lastPush = DateTime.UtcNow;
                await PushAsync(operation);
            }
        }

        stoppingToken.ThrowIfCancellationRequested();
        operation.Finish(cancelled: operation.Cancel.IsCancellationRequested);
        await PushAsync(operation);
    }

    /// <summary>The permission the single endpoint behind each action asks for.</summary>
    private static MakiPermission Required(SeriesBulkAction action) => action switch
    {
        SeriesBulkAction.SearchMissing => MakiPermission.DownloadChapters,
        _ => MakiPermission.EditMetadata,
    };

    /// <returns><c>Revoked</c> when the caller may no longer run the action, which ends the operation.</returns>
    private async Task<(string? Title, string? Error, bool Revoked)> RunOneAsync(
        SeriesBulkOperation operation, int seriesId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var caller = operation.Caller;
        services.GetRequiredService<RequestLocaleContext>().SetRequested(caller.Locale, null);
        var localizer = services.GetRequiredService<ILocalizer>();
        var db = services.GetRequiredService<MakiDbContext>();

        // As the request pipeline loads it (CurrentUserMiddleware), so a revocation mid-job applies to
        // the next series rather than the next request.
        var account = await db.Users.AsNoTracking()
            .Where(u => u.Id == caller.UserId)
            .Select(u => new { u.UserName, u.Permissions, u.AllRootFolders, u.MaxContentRating, u.Disabled, u.PendingSetup })
            .FirstOrDefaultAsync(ct);
        if (account is null || account.Disabled || account.PendingSetup ||
            !account.Permissions.Grants(Required(operation.Action)))
        {
            return (null, localizer.Get("error.seriesBulk.accessRevoked"), true);
        }

        IReadOnlySet<int> folders = account.AllRootFolders
            ? new HashSet<int>()
            : (await db.UserRootFolders.Where(g => g.UserId == caller.UserId).Select(g => g.RootFolderId).ToListAsync(ct))
            .ToHashSet();
        if (operation.Action == SeriesBulkAction.Move && !account.AllRootFolders &&
            operation.RootFolderId is { } target && !folders.Contains(target))
        {
            return (null, localizer.Get("error.seriesBulk.accessRevoked"), true);
        }

        services.GetRequiredService<CurrentUserContext>().Set(caller.UserId, account.UserName ?? caller.UserName,
            account.Permissions, account.AllRootFolders, folders, account.MaxContentRating);
        if (!caller.ScopeUnrestricted)
        {
            services.GetRequiredService<DataScope>().SetUser(caller.UserId, account.AllRootFolders);
        }

        Series? series = null;
        try
        {
            // Through the scoped db.Series, so the root-folder filter applies again at run time.
            series = await db.Series.Include(s => s.RootFolder).FirstOrDefaultAsync(s => s.Id == seriesId, ct);
            if (series is null)
            {
                return (null, localizer.Get("error.seriesRename.notFound"), false);
            }

            var key = operation.Action switch
            {
                SeriesBulkAction.Refresh => await RefreshAsync(services, series, ct),
                SeriesBulkAction.Rescan => await RescanAsync(services, series, ct),
                SeriesBulkAction.RefreshMetadata => await RefreshMetadataAsync(services, db, series, ct),
                SeriesBulkAction.SearchMissing => await SearchMissingAsync(services, db, series, caller.UserId, ct),
                SeriesBulkAction.Rename => await RenameAsync(services, series, ct),
                SeriesBulkAction.Move => await MoveAsync(services, operation, series, ct),
                _ => throw new InvalidOperationException($"Unknown bulk action {operation.Action}"),
            };
            return (series.Title, key, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Bulk {Action} failed for series {SeriesId}", operation.Action, seriesId);
            return (series?.Title, localizer.Get("error.server.unexpected"), false);
        }
    }

    private static async Task<string?> RefreshAsync(IServiceProvider services, Series series, CancellationToken ct)
    {
        if (series.ArchivedAt is not null)
        {
            return Text(services, "error.series.archived");
        }

        await services.GetRequiredService<ChapterSyncService>().SyncSeriesAsync(series.Id, ct);
        return null;
    }

    private async Task<string?> RescanAsync(IServiceProvider services, Series series, CancellationToken ct)
    {
        if (series.RootFolder is null)
        {
            return Text(services, "error.series.noRootFolder");
        }

        if (series.ArchivedAt is null)
        {
            try
            {
                await services.GetRequiredService<ChapterSyncService>().SyncSeriesAsync(series.Id, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Chapter sync failed during rescan of series {Id}", series.Id);
            }
        }

        var result = await services.GetRequiredService<CbzLinkService>().RescanSeriesAsync(series, ct);
        return result.RootUnavailable ? Text(services, "error.series.rootUnavailable") : null;
    }

    private static async Task<string?> RefreshMetadataAsync(
        IServiceProvider services, MakiDbContext db, Series series, CancellationToken ct)
    {
        var refresh = services.GetRequiredService<SeriesMetadataRefreshService>();
        if (await refresh.LookupAsync(series, ct) is not { } metadata)
        {
            return Text(services, "error.series.metadataRefreshFailed");
        }

        await refresh.ApplyAsync(series, metadata, includeCover: true, SeriesMetadataField.None, ct,
            reloadSeries: () => SeriesMetadataLocks.ReloadAsync(db, series, ct));
        if (db.Entry(series).State == EntityState.Detached)
        {
            return Text(services, "error.seriesRename.notFound");
        }

        await db.SaveChangesAsync(ct);
        await refresh.PublishChangesAsync(ct);
        if (series.RootFolder is { } rootFolder)
        {
            services.GetRequiredService<KavitaScanService>()
                .QueuePush(Path.Combine(rootFolder.Path, series.FolderName), series.Id);
        }

        return null;
    }

    private static async Task<string?> SearchMissingAsync(
        IServiceProvider services, MakiDbContext db, Series series, int userId, CancellationToken ct)
    {
        if (series.ArchivedAt is not null)
        {
            return Text(services, "error.series.archived");
        }

        var chapters = await db.Chapters.Where(c => c.SeriesId == series.Id).ToListAsync(ct);
        var result = await services.GetRequiredService<DownloadQueueService>().EnqueueChaptersAsync(
            Chapter.NextWanted(chapters, int.MaxValue), DownloadOrigin.Manual, userId, ct);
        var queued = result.Queued.Select(item => item.Id).ToList();
        await services.GetRequiredService<DownloadBatchNotifier>().QueuedAsync(series.Id, series.Title, queued);
        return result.Error is { } key ? Text(services, key) : null;
    }

    private static async Task<string?> RenameAsync(IServiceProvider services, Series series, CancellationToken ct)
    {
        var result = await services.GetRequiredService<SeriesRenameService>().RenameAsync(series.Id, ct);
        return result.Error;
    }

    private static async Task<string?> MoveAsync(
        IServiceProvider services, SeriesBulkOperation operation, Series series, CancellationToken ct)
    {
        // Already there is what the caller asked for, so it is not a failure.
        if (series.RootFolderId == operation.RootFolderId)
        {
            return null;
        }

        var result = await services.GetRequiredService<SeriesMoveService>()
            .MoveAsync(series.Id, operation.RootFolderId!.Value, operation.MoveFiles, ct);
        return result.Failure switch
        {
            SeriesMoveFailure.None => null,
            SeriesMoveFailure.NotFound => Text(services, "error.seriesRename.notFound"),
            _ => Text(services, result.ErrorKey!, result.Args),
        };
    }

    private static string Text(IServiceProvider services, string key, object? args = null) =>
        services.GetRequiredService<ILocalizer>().Get(key, args);

    private Task PushAsync(SeriesBulkOperation operation) =>
        events.SeriesBulkProgress(operation.Caller.UserId, operation.Snapshot(PushedFailures));
}
