using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

public sealed class RecommendationFeedbackPruneService(
    IServiceScopeFactory scopes, ILogger<RecommendationFeedbackPruneService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
                var now = DateTime.UtcNow;
                await db.RecommendationFeedbackEvents.IgnoreQueryFilters()
                    .Where(x => x.OccurredAtUtc < now.AddDays(-90)).ExecuteDeleteAsync(stoppingToken);
                await db.RecommendationMutationReceipts.IgnoreQueryFilters()
                    .Where(x => x.ExpiresAtUtc < now).ExecuteDeleteAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Could not prune recommendation feedback history"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
