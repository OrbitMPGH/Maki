using System.Globalization;
using System.Text.RegularExpressions;
using Maki.Core.Entities;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// One-time renumber of Baozi Manhua chapters synced before chapter numbers came from the site's own
/// label instead of the zero-based <c>chapter_slot</c>. <c>ChapterSyncService</c> matches incoming
/// chapters on <c>(Number, Language)</c>, so left alone the next sync would create a new, correctly
/// numbered, empty row beside each old one instead of correcting it in place.
/// <para>
/// The target number is read from the link's stored label (<see cref="ChapterSourceLink.NumberRaw"/>),
/// not computed as slot + 1, because slots also count unnumbered extras. A row is rewritten only while
/// its Number still equals its slot, which keeps the repair idempotent and leaves rows a newer sync
/// already rematched alone. Only a series whose sole source mapping is Baozi qualifies, and a series
/// where a target collides with a chapter not being rewritten is skipped whole.
/// </para>
/// <para>
/// Marker-gated in AppConfig like <see cref="SeriesIdentityRepairService"/>, and runs at startup
/// before Kestrel and Quartz so it cannot overlap a live sync.
/// </para>
/// </summary>
public partial class BaoziChapterRenumberRepairService(
    MakiDbContext db,
    ILogger<BaoziChapterRenumberRepairService> logger)
{
    public const string MarkerKey = "library.baoziChapterRenumberRepairDone";

    /// <summary><see cref="Maki.Sources.BaoziManhua.BaoziManhuaSource.Name"/>, not a shared constant, so kept in sync here.</summary>
    private const string BaoziSourceName = "baozimanhua";

    /// <summary>Same pattern as <c>BaoziManhuaSource.ChapterLabelNumberRegex</c>.</summary>
    [GeneratedRegex(@"第(\d+(?:\.\d+)?)[话話]")]
    private static partial Regex ChapterLabelNumberRegex();

    private sealed record Rewrite(Chapter Chapter, decimal? Target, string Label);

    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        if (await db.AppConfig.AnyAsync(c => c.Key == MarkerKey, ct))
        {
            return;
        }

        var (shifted, series, skipped) = await RepairAsync(ct);

        logger.LogInformation(
            "Baozi chapter renumber repair complete: renumbered {Count} chapter(s) across {Series} series, skipped {Skipped}",
            shifted, series, skipped);
    }

    /// <summary>
    /// Renumbers every qualifying series and writes the marker in the same transaction. Returns
    /// chapters renumbered, series renumbered and series skipped.
    /// </summary>
    public async Task<(int Shifted, int Series, int Skipped)> RepairAsync(CancellationToken ct = default)
    {
        var mappings = await db.SourceMappings.IgnoreQueryFilters()
            .Select(m => new { m.Id, m.SeriesId, m.SourceName })
            .ToListAsync(ct);

        var shiftedChapters = 0;
        var skippedSeries = 0;
        var renumberedSeriesIds = new List<int>();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var group in mappings.GroupBy(m => m.SeriesId))
        {
            var seriesId = group.Key;
            var sourceNames = group.Select(m => m.SourceName).Distinct().ToList();
            if (sourceNames.Count != 1 || sourceNames[0] != BaoziSourceName)
            {
                continue;
            }

            var mappingIds = group.Select(m => m.Id).ToHashSet();
            var links = await db.ChapterSourceLinks.IgnoreQueryFilters()
                .Where(l => mappingIds.Contains(l.SourceMappingId))
                .Select(l => new { l.ChapterId, l.SourceChapterId, l.NumberRaw })
                .ToListAsync(ct);
            if (links.Count == 0)
            {
                continue;
            }

            var allChapters = await db.Chapters.IgnoreQueryFilters()
                .Where(c => c.SeriesId == seriesId)
                .ToListAsync(ct);
            var chaptersById = allChapters.ToDictionary(c => c.Id);

            var rewrites = new List<Rewrite>();
            var rematched = false;
            foreach (var byChapter in links.GroupBy(l => l.ChapterId))
            {
                if (!chaptersById.TryGetValue(byChapter.Key, out var chapter) || chapter.Number is null)
                {
                    continue;
                }

                var parsed = byChapter
                    .Select(l => (Slot: ParseSlot(l.SourceChapterId), Label: l.NumberRaw?.Trim() ?? string.Empty))
                    .Distinct()
                    .ToList();
                if (parsed.Count != 1 || parsed[0].Slot is not { } slot)
                {
                    continue;
                }

                if (chapter.Number != slot)
                {
                    rematched = true;
                    continue;
                }

                var label = parsed[0].Label;
                var labelMatch = ChapterLabelNumberRegex().Match(label);
                if (labelMatch.Success)
                {
                    var target = decimal.Parse(labelMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                    if (target != chapter.Number)
                    {
                        rewrites.Add(new Rewrite(chapter, target, label));
                    }
                }
                else if (label.Length > 0)
                {
                    // An unnumbered extra: the fixed source lists it as a one-shot titled by its
                    // label, so the row takes that shape or the next sync duplicates it.
                    rewrites.Add(new Rewrite(chapter, null, label));
                }
            }

            if (rematched)
            {
                logger.LogWarning(
                    "Baozi chapter renumber repair: series {SeriesId} has chapters a newer sync already rematched by label; their files may not match their numbers and this repair cannot fix that",
                    seriesId);
            }

            if (rewrites.Count == 0)
            {
                continue;
            }

            var rewrittenIds = rewrites.Select(r => r.Chapter.Id).ToHashSet();
            var collides = false;
            foreach (var byLanguage in rewrites.GroupBy(r => r.Chapter.Language))
            {
                var fixedNumbers = allChapters
                    .Where(c => c.Language == byLanguage.Key && c.Number != null && !rewrittenIds.Contains(c.Id))
                    .Select(c => c.Number!.Value)
                    .ToHashSet();
                var targets = byLanguage.Where(r => r.Target is not null).Select(r => r.Target!.Value).ToList();

                if (targets.Any(fixedNumbers.Contains) || targets.Distinct().Count() != targets.Count)
                {
                    collides = true;
                    break;
                }
            }

            if (collides)
            {
                skippedSeries++;
                logger.LogInformation(
                    "Baozi chapter renumber repair skipped series {SeriesId}: a renumbered chapter would collide with an existing chapter",
                    seriesId);
                continue;
            }

            foreach (var rewrite in rewrites.OrderByDescending(r => r.Chapter.Number))
            {
                var chapter = rewrite.Chapter;
                chapter.Number = rewrite.Target;
                if (rewrite.Target is null)
                {
                    chapter.IsOneShot = true;
                    chapter.Title ??= rewrite.Label;
                }

                shiftedChapters++;
            }

            renumberedSeriesIds.Add(seriesId);
        }

        db.AppConfig.Add(new AppConfigEntry { Key = MarkerKey, Value = DateTime.UtcNow.ToString("O") });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        if (renumberedSeriesIds.Count > 0)
        {
            logger.LogInformation(
                "Baozi chapter renumber repair: files and ComicInfo still carry the old numbers in series {SeriesIds}; run Rename on them",
                string.Join(", ", renumberedSeriesIds));
        }

        return (shiftedChapters, renumberedSeriesIds.Count, skippedSeries);
    }

    private static decimal? ParseSlot(string sourceChapterId)
    {
        var parts = sourceChapterId.Split('_', 2);
        return parts.Length == 2 && decimal.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var slot)
            ? slot
            : null;
    }
}
