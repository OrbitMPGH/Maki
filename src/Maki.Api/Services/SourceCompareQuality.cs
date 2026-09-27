using Maki.Api.Dtos;
using Maki.Core.Quality;
using Maki.Core.Sources;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <param name="Tier">Lowercase tier name.</param>
/// <param name="Reason">Why this copy would not be an upgrade, as a reason code; null when it would, or when there is no file.</param>
/// <param name="CurrentTier">The file on disk, null when the chapter has none.</param>
public sealed record ComparePanelQuality(string Tier, int Score, IReadOnlyList<string> MatchedFormats,
    bool IsUpgrade, string? Reason, int? MedianWidth, int? PageCount, string? CurrentTier,
    int? CurrentScore, int? CurrentWidth);

/// <summary>
/// Scores each compare panel's sampled pages against the chapter's file on disk. Runs on the snapshot
/// the client polls, not inside the job: one evaluator per snapshot and no probes, since the job has
/// already measured the pages it sampled.
/// </summary>
public static class SourceCompareQuality
{
    public const string NoProfile = "no_profile";

    public static async Task<CompareSnapshot> FillAsync(MakiDbContext db, UpgradeEvaluationService upgrades,
        SourceRegistry registry, CompareSnapshot snapshot, CancellationToken ct)
    {
        if (snapshot.ChapterNumber is not { } number || !snapshot.Panels.Any(p => KnownWidths(p).Count > 0))
        {
            return snapshot;
        }

        var chapterId = (await db.Chapters.AsNoTracking()
                .Where(c => c.SeriesId == snapshot.SeriesId && c.Number != null)
                .Select(c => new { c.Id, c.Number, c.Language, c.ChapterFileId })
                .ToListAsync(ct))
            .Where(c => c.Number == number)
            .OrderByDescending(c => c.ChapterFileId != null)
            .ThenByDescending(c => c.Language is null or "en")
            .ThenBy(c => c.Id)
            .Select(c => (int?)c.Id)
            .FirstOrDefault();
        var chapter = chapterId is null
            ? null
            : await db.Chapters.AsNoTracking()
                .Include(c => c.ChapterFile)
                .Include(c => c.SourceLinks)
                .FirstOrDefaultAsync(c => c.Id == chapterId, ct);
        var file = chapter?.ChapterFile;
        var language = chapter?.Language;
        var evaluator = await upgrades.ForSeriesAsync(snapshot.SeriesId, ct);
        var current = file is null ? null : evaluator?.Evaluate(file, language);
        var formatNames = evaluator is null
            ? []
            : await db.QualityFormats.AsNoTracking().ToDictionaryAsync(f => f.Id, f => f.Name, ct);
        var fileName = file is null ? string.Empty : Path.GetFileName(file.RelativePath);
        var currentTier = file is null ? null : QualityNames.Tier(file.Tier);

        return snapshot with { Panels = [.. snapshot.Panels.Select(p => p with { Quality = Panel(p) })] };

        ComparePanelQuality? Panel(ComparePanel panel)
        {
            var widths = KnownWidths(panel);
            if (widths.Count == 0)
            {
                return null;
            }

            var width = ChapterFileMeasurer.Median(widths);
            var source = registry.Find(panel.SourceName);
            if (evaluator is null)
            {
                var tier = QualityTierResolver.Resolve(source?.Kind, null, fileName, isVolume: false);
                return new ComparePanelQuality(QualityNames.Tier(tier), 0, [], false, NoProfile, width, panel.PageCount,
                    currentTier, null, file?.MedianWidth);
            }

            var group = chapter?.SourceLinks.FirstOrDefault(l => l.SourceMappingId == panel.MappingId)?.Group
                        ?? ChapterFileQualityService.SiteGroup(source);
            var score = evaluator.Score(evaluator.CandidateFor(panel.SourceName, group, fileName, panel.PageCount, width,
                null, null, language));
            var profile = evaluator.Profile;
            string? reason = null;
            var isUpgrade = false;
            if (file is not null)
            {
                if (!UpgradeTrash.IsReplaceable(file.RelativePath)) reason = UpgradeReasons.UnsupportedFile;
                else if (file.Trusted) reason = "trusted";
                else if (current is not { } now) reason = "unmeasured";
                else if (now.CutoffMet) reason = "cutoff_met";
                else if (QualityScorer.IsUpgrade(profile, now.Score, file.PageCount, false, score, width, panel.PageCount))
                    isUpgrade = true;
                else reason = UpgradeReasons.Explain(profile, file.PageCount, score, width, panel.PageCount);
            }

            return new ComparePanelQuality(QualityNames.Tier(score.Tier), score.Score,
                [.. score.MatchedFormatIds.Select(id => formatNames.GetValueOrDefault(id)).OfType<string>()],
                isUpgrade, reason, width, panel.PageCount, currentTier, current?.Score.Score, file?.MedianWidth);
        }
    }

    private static List<int> KnownWidths(ComparePanel panel) =>
        [.. panel.Pages.Where(p => p?.Width is not null).Select(p => p!.Width!.Value)];
}
