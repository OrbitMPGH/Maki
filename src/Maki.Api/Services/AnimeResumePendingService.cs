using Maki.Api.Controllers;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Finishes a "mark the anime watched" that was asked for before the series had chapters, which is
/// every add from the Home rail or the Discover modal: source matching runs after the add returns.
/// <see cref="ChapterSyncService"/> calls this once it has written new chapters, and each reader
/// with a pending mark gets chapters up to it ticked off in their own scope.
/// </summary>
public class AnimeResumePendingService(IServiceScopeFactory scopes, ILogger<AnimeResumePendingService> logger)
{
    public async Task ApplyAsync(int seriesId, CancellationToken ct)
    {
        List<(int UserId, double PendingTo)> pending;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
            pending = (await db.UserSeriesStates.IgnoreQueryFilters().AsNoTracking()
                    .Where(s => s.SeriesId == seriesId && s.AnimeWatchPendingTo != null)
                    .Select(s => new { s.UserId, s.AnimeWatchPendingTo })
                    .ToListAsync(ct))
                .Select(s => (s.UserId, s.AnimeWatchPendingTo!.Value))
                .ToList();
        }

        foreach (var (userId, pendingTo) in pending)
        {
            try
            {
                await ApplyForUserAsync(seriesId, userId, (decimal)pendingTo, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not apply the pending anime watch mark on series {SeriesId} for user {UserId}",
                    seriesId, userId);
            }
        }
    }

    private async Task ApplyForUserAsync(int seriesId, int userId, decimal pendingTo, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        db.Scope.SetUser(userId, allRootFolders: true);

        // In memory: Chapter.Number is REAL and EF's SQLite provider cannot compare decimals.
        var chapterIds = (await db.Chapters.AsNoTracking()
                .Where(c => c.SeriesId == seriesId && c.Number != null)
                .Select(c => new { c.Id, c.Number })
                .ToListAsync(ct))
            .Where(c => c.Number <= pendingTo)
            .OrderBy(c => c.Number)
            .Select(c => c.Id)
            .Take(ReaderController.MaxBulkChapters)
            .ToList();

        // Cleared even with nothing to tick: chapters that all sit past the frontier mean the mark
        // has nothing to land on, and keeping it would tick older chapters whenever they turn up.
        if (chapterIds.Count > 0)
        {
            await scope.ServiceProvider.GetRequiredService<ReaderService>().MarkWatchedAsync(chapterIds, ct);
        }

        var state = await db.UserSeriesStates.FirstAsync(s => s.SeriesId == seriesId, ct);
        state.AnimeWatchPendingTo = null;
        state.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
