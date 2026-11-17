using Maki.Core.Sources;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

public sealed record ChapterFoldFloorDto(
    decimal Floor,
    string Language,
    string State,
    bool Fold,
    string Reason,
    IReadOnlyDictionary<string, IReadOnlyList<decimal>> Parts,
    IReadOnlyList<string> Wholes,
    IReadOnlyDictionary<string, string> Aliases,
    IReadOnlyList<PageRatio> Ratios);

public sealed record ChapterFoldSeriesDto(int SeriesId, string Title, IReadOnlyList<ChapterFoldFloorDto> Floors);

public sealed record ChapterFoldLibraryDto(
    int SeriesChecked,
    int SeriesWithFolds,
    IReadOnlyDictionary<string, int> FloorsByState,
    IReadOnlyList<ChapterFoldSeriesDto> Series);

/// <summary>
/// What <see cref="ChapterReconciler"/> would do to each series, read from what the last syncs stored
/// (each link's page count, each mapping's publisher numbers). Nothing is changed: this is the
/// look before any fold is acted on. Only enabled mappings of switched-on sources count, as in sync.
/// </summary>
public class ChapterFoldPreviewService(MakiDbContext db, SourceAvailability sourceAvailability)
{
    public async Task<ChapterFoldSeriesDto?> ForSeriesAsync(int seriesId, CancellationToken ct)
    {
        var title = await db.Series.Where(s => s.Id == seriesId).Select(s => s.Title).FirstOrDefaultAsync(ct);
        if (title is null)
        {
            return null;
        }

        return (await PreviewAsync(seriesId, ct)).Series.FirstOrDefault() ?? new ChapterFoldSeriesDto(seriesId, title, []);
    }

    public Task<ChapterFoldLibraryDto> ForLibraryAsync(CancellationToken ct) => PreviewAsync(null, ct);

    private async Task<ChapterFoldLibraryDto> PreviewAsync(int? seriesId, CancellationToken ct)
    {
        var disabled = await sourceAvailability.DisabledAsync(ct);
        var mappings = await db.SourceMappings.AsNoTracking()
            .Where(m => m.Enabled && m.Series!.ArchivedAt == null && !disabled.Contains(m.SourceName))
            .Where(m => seriesId == null || m.SeriesId == seriesId)
            .Select(m => new { m.Id, m.SeriesId, m.SourceName, m.Priority, m.PublisherNumbers, m.Series!.Title })
            .ToListAsync(ct);
        var mappingIds = mappings.Select(m => m.Id).ToList();
        var links = await db.ChapterSourceLinks.AsNoTracking()
            .Where(l => mappingIds.Contains(l.SourceMappingId) && l.Chapter!.Number != null)
            .Select(l => new { l.SourceMappingId, Number = l.Chapter!.Number!.Value, l.Chapter.Language, l.Title, l.PageCount })
            .ToListAsync(ct);
        var linksByMapping = links.ToLookup(l => l.SourceMappingId);

        var result = new List<ChapterFoldSeriesDto>();
        var byState = new Dictionary<string, int>();
        var checkedSeries = 0;
        foreach (var series in mappings.GroupBy(m => m.SeriesId).OrderBy(g => g.Key))
        {
            checkedSeries++;
            // Mapping order decides ties in the rules, so it is fixed: priority, then id.
            var ordered = series.OrderBy(m => m.Priority).ThenBy(m => m.Id).ToList();
            var names = ordered.ToDictionary(
                m => m.Id,
                m => ordered.Count(o => o.SourceName == m.SourceName) > 1 ? $"{m.SourceName}#{m.Id}" : m.SourceName);
            var publisher = ordered.SelectMany(m => PublisherNumbering.Parse(m.PublisherNumbers)).Distinct().ToList();
            var floors = new List<ChapterFoldFloorDto>();
            foreach (var language in ordered.SelectMany(m => linksByMapping[m.Id]).Select(l => l.Language).Distinct().Order())
            {
                var listings = ordered
                    .Select(m => new ReconcileListing(names[m.Id], [.. linksByMapping[m.Id]
                        .Where(l => l.Language == language)
                        // Per-volume numbering can list one number twice; the first one stands for it here.
                        .DistinctBy(l => l.Number)
                        .OrderBy(l => l.Number)
                        .Select(l => new ReconcileEntry(l.Number, l.Title, l.PageCount))]))
                    .Where(l => l.Entries.Count > 0)
                    .ToList();
                foreach (var d in ChapterReconciler.Decide(listings, publisher).Values.Where(d => d.State != FoldState.Plain))
                {
                    var state = d.State.ToString();
                    byState[state] = byState.GetValueOrDefault(state) + 1;
                    floors.Add(new ChapterFoldFloorDto(
                        d.Floor, language, state, d.Fold, d.Reason, d.Parts, d.Wholes, d.Aliases, d.Ratios));
                }
            }

            if (floors.Count > 0)
            {
                result.Add(new ChapterFoldSeriesDto(series.Key, series.First().Title, floors));
            }
        }

        return new ChapterFoldLibraryDto(
            checkedSeries,
            result.Count(s => s.Floors.Any(f => f.Fold)),
            byState,
            result);
    }
}
