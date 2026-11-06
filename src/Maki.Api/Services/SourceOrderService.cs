using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Quality;
using Maki.Core.Sources;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>The order a series' downloads try its sources in, and why.</summary>
/// <param name="SeriesMode">The series' own setting; null follows <paramref name="DefaultMode"/>.</param>
/// <param name="Scores">Per mapping id, only in <see cref="SourceOrderMode.Quality"/>.</param>
public sealed record SourceOrderResult(
    SourceOrderMode? SeriesMode,
    SourceOrderMode DefaultMode,
    IReadOnlyList<SourceMapping> Ordered,
    IReadOnlyDictionary<int, QualityScore> Scores)
{
    public SourceOrderMode Mode => SeriesMode ?? DefaultMode;
}

/// <summary>
/// Decides which source a download tries first. In <see cref="SourceOrderMode.Manual"/> that is the
/// priority the user set. In <see cref="SourceOrderMode.Quality"/> it is <see cref="SourceRanking"/>
/// under the series' upgrade profile, scoring each source on its recent measurements
/// (<see cref="SourceQualitySamples"/>); a series with no profile is ranked under the default tier
/// order at the starter weights, so the mode still means something before any profile is picked.
/// A source this series has not measured ranks after every allowed source it has, whatever its tier:
/// the usual reason is that sampling it failed or hit a rate limit, so a guess must not beat a measurement.
/// Among the unmeasured, one is judged by its record across the library, and one never measured
/// anywhere gets the lowest measured points there are: below the reference is the norm, so scoring it
/// as average would put it ahead of nearly every source that has been measured. Whatever the
/// score, a source that keeps failing its downloads goes last (<see cref="SourceReliability"/>), and so does
/// one whose page counts look like a preview (<see cref="SourcePageCounts"/>).
/// </summary>
public class SourceOrderService(SourceRegistry registry, ChapterFileQualityService quality)
{
    public async Task<SourceOrderMode> DefaultModeAsync(MakiDbContext db, CancellationToken ct) =>
        Parse(await db.AppConfig.AsNoTracking()
            .Where(c => c.Key == SettingKeys.DownloadSourceOrder)
            .Select(c => c.Value)
            .FirstOrDefaultAsync(ct)) ?? SourceOrderMode.Manual;

    /// <summary>Best first. <paramref name="mappings"/> are the candidates the caller already filtered.</summary>
    /// <param name="force">Order under this mode whatever the series is set to, to show what switching would do.</param>
    public async Task<SourceOrderResult> OrderAsync(
        MakiDbContext db, int seriesId, IReadOnlyCollection<SourceMapping> mappings, CancellationToken ct,
        SourceOrderMode? force = null)
    {
        var seriesMode = await db.Series.AsNoTracking()
            .Where(s => s.Id == seriesId)
            .Select(s => s.SourceOrderMode)
            .FirstOrDefaultAsync(ct);
        var defaultMode = await DefaultModeAsync(db, ct);
        var manual = mappings.OrderBy(m => m.Priority).ThenBy(m => m.Id).ToList();
        if ((force ?? seriesMode ?? defaultMode) != SourceOrderMode.Quality || mappings.Count == 0)
        {
            return new SourceOrderResult(seriesMode, defaultMode, manual, new Dictionary<int, QualityScore>());
        }

        var evaluator = await new UpgradeEvaluationService(db, quality).ForSeriesAsync(seriesId, ct)
                        ?? new UpgradeEvaluator(DefaultProfile(), [], quality);
        var estimates = await SourceQualitySamples.EstimatesAsync(db, seriesId, ct);
        var unmeasured = mappings.Where(m => !estimates.ContainsKey(m.Id)).Select(m => m.SourceName).Distinct().ToList();
        var library = unmeasured.Count == 0
            ? []
            : await SourceQualitySamples.LibraryEstimatesAsync(db, ct, SourceQualitySamples.Keep * 5, unmeasured);
        var scores = new Dictionary<int, QualityScore>();
        foreach (var mapping in mappings)
        {
            var group = ChapterFileQualityService.SiteGroup(registry.Find(mapping.SourceName));
            var candidate = evaluator.CandidateFor(mapping.SourceName, group, string.Empty, null, null, null, null, null);
            var estimate = estimates.GetValueOrDefault(mapping.Id)
                           ?? (library.TryGetValue(mapping.SourceName, out var known) ? known.Estimate : null);
            if (estimate is not null)
            {
                scores[mapping.Id] = evaluator.Score(estimate.Apply(candidate));
                continue;
            }

            var score = evaluator.Score(candidate);
            var (resolution, compression) = MeasuredQuality.PessimisticPoints(evaluator.Profile);
            scores[mapping.Id] = score with
            {
                Score = score.Score + resolution + compression,
                ResolutionPoints = resolution,
                CompressionPoints = compression
            };
        }

        var byId = mappings.ToDictionary(m => m.Id);
        var ranked = SourceRanking.Order(evaluator.Profile,
            mappings.Select(m => new SourceRanking.Entry(m.Id, m.Priority, scores[m.Id], estimates.ContainsKey(m.Id))));
        var outcomes = await OutcomesAsync(db, mappings, ct);
        var truncated = SourcePageCounts.Truncated(await db.SourceQualitySamples.AsNoTracking()
            .Where(q => q.SeriesId == seriesId && q.PageCount > 0)
            .Select(q => new SourcePageCounts.Sample(q.SourceMappingId, q.ChapterId, q.PageCount, q.MedianWidth, q.MedianHeight))
            .ToListAsync(ct));
        var ordered = SourceReliability.Demote(
            [.. ranked.Select(e => byId[e.MappingId])],
            m => SourceReliability.IsUnreliable(outcomes[m.Id]) || truncated.Contains(m.Id));
        return new SourceOrderResult(seriesMode, defaultMode, ordered, scores);
    }

    /// <summary>
    /// How each mapping's downloads have gone over the reliability window: on this series if it has been
    /// tried here, otherwise across the library, so a source that fails everywhere is not trusted on a
    /// series it has not failed on yet. Failures are the attempts logged in <see cref="SourceFailure"/>,
    /// not Failed queue rows, which a failover or a retry moves off the source that failed.
    /// </summary>
    private static async Task<Dictionary<int, SourceReliability.Outcome>> OutcomesAsync(
        MakiDbContext db, IReadOnlyCollection<SourceMapping> mappings, CancellationToken ct)
    {
        var names = mappings.Select(m => m.SourceName).Distinct().ToList();
        var since = DateTime.UtcNow - SourceReliability.Window;
        var failed = await db.SourceFailures.AsNoTracking()
            .Where(f => f.OccurredAtUtc >= since && names.Contains(f.SourceMapping!.SourceName))
            .GroupBy(f => new { f.SourceMappingId, f.SourceMapping!.SourceName })
            .Select(g => new Tally(g.Key.SourceMappingId, g.Key.SourceName, 0, g.Count()))
            .ToListAsync(ct);
        var completed = await db.DownloadQueue.AsNoTracking()
            .Where(q => q.Status == QueueStatus.Completed && q.CompletedAt >= since &&
                        q.SourceMapping != null && names.Contains(q.SourceMapping.SourceName))
            .GroupBy(q => new { SourceMappingId = q.SourceMappingId!.Value, q.SourceMapping!.SourceName })
            .Select(g => new Tally(g.Key.SourceMappingId, g.Key.SourceName, g.Count(), 0))
            .ToListAsync(ct);
        List<Tally> counts = [.. failed, .. completed];

        static SourceReliability.Outcome Sum(IEnumerable<Tally> tallies) =>
            tallies.Aggregate(new SourceReliability.Outcome(0, 0), (sum, t) => new(sum.Completed + t.Completed, sum.Failed + t.Failed));

        var result = new Dictionary<int, SourceReliability.Outcome>();
        foreach (var mapping in mappings)
        {
            var here = Sum(counts.Where(c => c.MappingId == mapping.Id));
            result[mapping.Id] = here.Attempts > 0 ? here : Sum(counts.Where(c => c.SourceName == mapping.SourceName));
        }

        return result;
    }

    private sealed record Tally(int MappingId, string SourceName, int Completed, int Failed);

    public static SourceOrderMode? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "manual" => SourceOrderMode.Manual,
        "quality" => SourceOrderMode.Quality,
        _ => null
    };

    public static string Name(SourceOrderMode mode) => mode == SourceOrderMode.Quality ? "quality" : "manual";

    private static UpgradeProfile DefaultProfile()
    {
        var profile = new UpgradeProfile
        {
            ResolutionWeight = UpgradeProfileSeeder.MeasuredWeight,
            CompressionWeight = UpgradeProfileSeeder.MeasuredWeight
        };
        UpgradeProfileDefaults.Normalise(profile);
        return profile;
    }
}
