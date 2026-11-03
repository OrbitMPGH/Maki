using System.Net;
using Maki.Api.Services;
using Maki.Core.Sources;
using Maki.Data;
using Maki.Sources.Custom;
using Microsoft.EntityFrameworkCore;
using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Lists each enabled custom source's sample series every six hours, on an hourly tick, so a site
/// that changed its layout shows up in Health instead of as a series that quietly stopped updating.
/// The health monitor's own 15-minute refresh with 10-second probes can't fit a spaced-out chapter
/// list or a FlareSolverr solve. The last check time is the row's, so a restart doesn't re-probe all.
/// </summary>
[DisallowConcurrentExecution]
public class CustomSourceHealthJob(
    SourceRegistry registry,
    SourceAvailability availability,
    HealthMonitor monitor,
    MakiDbContext db,
    ILogger<CustomSourceHealthJob> logger) : IJob
{
    public const string Prefix = "custom:source:";
    public static readonly TimeSpan Every = TimeSpan.FromHours(6);
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromMinutes(3);

    /// <summary>The Health row's link: Manage sources with this source's wizard open on it.</summary>
    public static string Link(string sourceName) => $"/settings?tab=downloads&s=sources&sources=edit:{sourceName}";

    public async Task Execute(IJobExecutionContext context)
    {
        var ct = context.CancellationToken;
        var disabled = await availability.DisabledAsync(ct);
        var sources = registry.All
            .Where(s => s is CustomSource or InvalidCustomSource)
            .Where(s => !disabled.Contains(s.Name, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var checkedAt = await db.HealthChecks.AsNoTracking()
            .Where(r => r.Id.StartsWith(Prefix))
            .ToDictionaryAsync(r => r.Id, r => r.CheckedAt, ct);

        var keep = new HashSet<string>(StringComparer.Ordinal);
        var checks = new List<HealthMonitor.ExternalCheck>();
        foreach (var source in sources)
        {
            var id = Prefix + source.Name;
            keep.Add(id);
            if (checkedAt.TryGetValue(id, out var at) && at > DateTime.UtcNow - Every)
            {
                continue;
            }

            checks.Add(await CheckAsync(source, id, ct));
        }

        await monitor.RecordAsync(Prefix, checks, keep, ct);
    }

    private async Task<HealthMonitor.ExternalCheck> CheckAsync(ISource source, string id, CancellationToken ct)
    {
        HealthMonitor.ExternalCheck Row(string status, string key, string? reason = null) =>
            new(id, "connections", status, key, new { source = source.DisplayName, reason }, Link(source.Name));

        if (source is not CustomSource custom)
        {
            return Row("error", "health.check.customSourceInvalid");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CheckTimeout);
        var sampleId = Uri.TryCreate(custom.Definition.Sample.SeriesUrl, UriKind.Absolute, out var sample)
            ? custom.ResolveSeriesIdFromUrl(sample)
            : null;

        Exception? sampleError = null;
        if (sampleId is not null)
        {
            try
            {
                await custom.ListChaptersAsync(sampleId, ct: timeout.Token);
                return Row("healthy", "health.check.customSourceWorking");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                sampleError = ex;
            }
        }

        // A sample the site took down is not a broken source: one other linked series decides.
        var other = await db.SourceMappings.AsNoTracking()
            .Where(m => m.SourceName == source.Name && m.SourceSeriesId != sampleId)
            .Select(m => m.SourceSeriesId)
            .FirstOrDefaultAsync(ct);
        Exception? otherError = null;
        if (other is not null)
        {
            try
            {
                await custom.ListChaptersAsync(other, ct: timeout.Token);
                return Row("warning", "health.check.customSourceSample");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                otherError = ex;
            }
        }

        if (new[] { sampleError, otherError }.Where(e => e is not null).All(NotFound) && (sampleError ?? otherError) is not null)
        {
            return Row("warning", "health.check.customSourceSample");
        }

        var reason = (otherError ?? sampleError)?.Message;
        logger.LogInformation("Custom source {Source} failed its health check: {Reason}", source.Name, reason);
        return Row("error", "health.check.customSourceFailing", reason);
    }

    private static bool NotFound(Exception? ex) =>
        ex is HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Gone };
}
