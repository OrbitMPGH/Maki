using Maki.Api.Services;
using Maki.Data;
using Maki.Metadata.Catalogue;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Microsoft.EntityFrameworkCore;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Pre-warms <see cref="DiscoverService"/>'s rail caches (both the main browse set and the
/// per-genre set) so the first Discover visit after startup or a dump refresh doesn't pay
/// for the full-table scans itself. No-ops quietly when the local MangaBaka database isn't
/// available. Stable key so <see cref="MangaBakaDumpRefreshJob"/> can trigger it right after
/// installing a new dump.
///
/// <para>
/// The rail caches are keyed by content-rating ceiling, so warming has to cover every ceiling
/// somebody can actually arrive with rather than one fixed value. It reads the distinct ceilings
/// off the usable accounts instead of warming all four: most instances use one, and warming
/// ceilings nobody holds would multiply the scan cost for cache entries no request ever reads.
/// </para>
/// </summary>
[DisallowConcurrentExecution]
public class DiscoverCacheWarmJob(
    DiscoverService discover,
    VectorIndexCache searchIndex,
    CatalogueIndexCache catalogueIndex,
    IServiceScopeFactory scopeFactory,
    ArtifactBuildGate gate,
    ILogger<DiscoverCacheWarmJob> logger) : IJob
{
    public static readonly JobKey Key = new("discover-cache-warm");

    public const string ScheduledTriggerName = "discover-cache-warm-trigger";

    public async Task Execute(IJobExecutionContext context)
    {
        try
        {
            // One heavy build at a time across every job here; see ArtifactBuildGate.
            using var build = await gate.EnterAsync(nameof(DiscoverCacheWarmJob), context.CancellationToken);

            foreach (var ceiling in await CeilingsInUseAsync(context.CancellationToken))
            {
                await discover.GetFeedsAsync(refresh: true, ceiling, context.CancellationToken);
                await discover.GetGenreFeedsAsync(refresh: true, ceiling, context.CancellationToken);
            }

            // A run triggered after a dump install, and the first scheduled run after startup, build
            // both indexes so the cost never lands on the first keystroke. Later scheduled runs only
            // refresh an index somebody already has loaded: building one for an instance nobody is
            // browsing costs ~9s of CPU and an RSS spike, only for the idle unload to drop it again.
            var everything = context.Trigger.Key.Name != ScheduledTriggerName || context.PreviousFireTimeUtc is null;

            // Search's in-memory vector index takes ~8s to build over ~100k series; do it here so
            // the first natural-language query doesn't wear it.
            if (everything || searchIndex.IsLoaded)
            {
                if (!searchIndex.IsCurrent)
                {
                    build.MarkBuilt();
                }

                await searchIndex.GetAsync(context.CancellationToken);
            }

            // Same reasoning for the credit and title-vocabulary indexes: about 9s of scanning the
            // dump, which would otherwise land on whichever keystroke arrived first.
            if (everything || catalogueIndex.IsLoaded)
            {
                if (!catalogueIndex.IsCurrent)
                {
                    build.MarkBuilt();
                }

                await catalogueIndex.GetAsync(context.CancellationToken);
            }
        }
        catch (LocalCatalogueUnavailableException)
        {
            // No local MangaBaka database - nothing to warm.
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // Shutdown. Not a failure: the catch below would log one, and rethrowing would make
            // Quartz log a job error on every restart that happened to land mid-run.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Discover cache warm-up failed");
            context.ReportFailure(ex);
        }
    }

    /// <summary>
    /// The distinct content-rating ceilings held by accounts that can sign in. Disabled and
    /// not-yet-claimed rows are excluded: they cannot request a rail, so warming for them is pure
    /// waste. Always includes <see cref="ContentRating.Default"/> so a fresh instance with no users
    /// yet still gets one warm set.
    /// </summary>
    private async Task<IReadOnlyList<string>> CeilingsInUseAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();

        var stored = await db.Users
            .Where(u => !u.Disabled && !u.PendingSetup)
            .Select(u => u.MaxContentRating)
            .Distinct()
            .ToListAsync(ct);

        // Resolved through Allowed() so unset or unrecognised values collapse onto the same key
        // DiscoverService.Ceiling would give them, rather than warming an entry nothing looks up.
        return stored
            .Select(r => ContentRating.Allowed(r)[^1])
            .Append(ContentRating.Default)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
