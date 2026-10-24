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
/// A source this series has not measured is judged by its record across the library, and one never
/// measured anywhere gets the lowest measured points there are: below the reference is the norm, so
/// scoring it as average would put it ahead of nearly every source that has been measured.
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
            mappings.Select(m => new SourceRanking.Entry(m.Id, m.Priority, scores[m.Id])));
        return new SourceOrderResult(seriesMode, defaultMode, [.. ranked.Select(e => byId[e.MappingId])], scores);
    }

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
