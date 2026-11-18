using Maki.Core.Sources;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

public sealed record ChapterFoldMissingDto(string Mapping, decimal Number);

public sealed record ChapterFoldFloorDto(
    decimal Floor,
    string Language,
    string State,
    bool Fold,
    string Reason,
    IReadOnlyDictionary<string, IReadOnlyList<decimal>> Parts,
    IReadOnlyList<string> Wholes,
    IReadOnlyDictionary<string, string> Aliases,
    IReadOnlyList<PageRatio> Ratios,
    IReadOnlyList<ChapterFoldMissingDto> MissingCounts,
    string? Nominated,
    IReadOnlyList<PageCheck> PageChecks,
    IReadOnlyDictionary<string, IReadOnlyList<decimal>> Held,
    IReadOnlyList<ChapterFoldMissingDto> MissingPages);

public sealed record ChapterFoldSeriesDto(int SeriesId, string Title, IReadOnlyList<ChapterFoldFloorDto> Floors);

/// <param name="Folded">Source runs of parts already folded into their whole chapter (<see cref="Maki.Core.Entities.ChapterFold"/>).</param>
public sealed record ChapterFoldLibraryDto(
    int SeriesChecked,
    int Folded,
    int SeriesWithFolds,
    IReadOnlyDictionary<string, int> FloorsByState,
    int MissingCounts,
    IReadOnlyDictionary<string, int> MissingCountsBySource,
    int MissingPages,
    IReadOnlyDictionary<string, int> MissingPagesBySource,
    IReadOnlyList<ChapterFoldSeriesDto> Series);

/// <summary>One mapping's stored view of one chapter, with what a page count request needs.</summary>
public sealed record FoldLink(
    int SeriesId,
    int MappingId,
    string MappingName,
    string SourceName,
    string SourceSeriesId,
    int ChapterId,
    string SourceChapterId,
    string? NumberRaw,
    decimal Number,
    int? Volume,
    string? Title,
    string Language,
    DateTime? ReleaseDate,
    string? Url,
    int? PageCount,
    string? PageHashes,
    string? StripHashes)
{
    public SourceChapter ToSourceChapter() =>
        new(SourceName, SourceSeriesId, SourceChapterId, NumberRaw, Number, Volume, Title, Language, ReleaseDate, Url);
}

/// <summary>A decided floor of one series and language, and the links a pending one waits on for a page count or page hashes.</summary>
/// <param name="MappingIds">The series' mapping names as the decision uses them, to their ids.</param>
public sealed record FoldFloor(
    int SeriesId, string Title, string Language, FloorDecision Decision, IReadOnlyList<FoldLink> Missing, IReadOnlyList<FoldLink> MissingPages,
    IReadOnlyDictionary<string, int> MappingIds);

/// <summary>
/// What <see cref="ChapterReconciler"/> would do to each series, read from what the last syncs stored
/// (each link's page count and page hashes, each mapping's publisher numbers). Nothing is changed: this is the
/// look before any fold is acted on. Only enabled mappings of switched-on sources on series still in
/// the library count, as in sync.
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
        var (floors, seriesChecked) = await DecideAsync(seriesId, ct);
        var series = floors
            .GroupBy(f => (f.SeriesId, f.Title))
            .OrderBy(g => g.Key.SeriesId)
            .Select(g => new ChapterFoldSeriesDto(g.Key.SeriesId, g.Key.Title, [.. g.Select(f => new ChapterFoldFloorDto(
                f.Decision.Floor, f.Language, f.Decision.State.ToString(), f.Decision.Fold, f.Decision.Reason,
                f.Decision.Parts, f.Decision.Wholes, f.Decision.Aliases, f.Decision.Ratios,
                [.. f.Missing.Select(m => new ChapterFoldMissingDto(m.MappingName, m.Number))],
                f.Decision.Nominated, f.Decision.PageChecks, f.Decision.Held,
                [.. f.MissingPages.Select(m => new ChapterFoldMissingDto(m.MappingName, m.Number))]))]))
            .ToList();
        var missing = floors.SelectMany(f => f.Missing).DistinctBy(m => (m.MappingId, m.ChapterId)).ToList();
        var missingPages = floors.SelectMany(f => f.MissingPages).DistinctBy(m => (m.MappingId, m.ChapterId)).ToList();

        return new ChapterFoldLibraryDto(
            seriesChecked,
            await db.ChapterFolds.CountAsync(f => seriesId == null || f.SeriesId == seriesId, ct),
            series.Count(s => s.Floors.Any(f => f.Fold)),
            floors.GroupBy(f => f.Decision.State.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            missing.Count,
            missing.GroupBy(m => m.SourceName).OrderByDescending(g => g.Count()).ToDictionary(g => g.Key, g => g.Count()),
            missingPages.Count,
            missingPages.GroupBy(m => m.SourceName).OrderByDescending(g => g.Count()).ToDictionary(g => g.Key, g => g.Count()),
            series);
    }

    /// <summary>
    /// A link's stored hashes for the page check. An empty strip means none could be made, which is a
    /// strip comparison that cannot happen rather than one still to fetch: it stands as an empty strip.
    /// </summary>
    private static PageEvidence? Evidence(FoldLink link) =>
        ChapterPageMatch.Parse(link.PageHashes) is { } pages
            ? new PageEvidence(pages, link.StripHashes is null ? null : ChapterPageMatch.ParseStrip(link.StripHashes) ?? [])
            : null;

    /// <summary>Every floor that is not plain, per series and language, with the links each pending one waits on.</summary>
    public async Task<(IReadOnlyList<FoldFloor> Floors, int SeriesChecked)> DecideAsync(int? seriesId, CancellationToken ct)
    {
        var disabled = await sourceAvailability.DisabledAsync(ct);
        var mappings = await db.SourceMappings.AsNoTracking()
            .Where(m => m.Enabled && m.Series!.ArchivedAt == null && !disabled.Contains(m.SourceName))
            .Where(m => seriesId == null || m.SeriesId == seriesId)
            .Select(m => new { m.Id, m.SeriesId, m.SourceName, m.SourceSeriesId, m.Priority, m.PublisherNumbers, m.Series!.Title })
            .ToListAsync(ct);
        var mappingIds = mappings.Select(m => m.Id).ToList();
        var links = await db.ChapterSourceLinks.AsNoTracking()
            .Where(l => mappingIds.Contains(l.SourceMappingId) && l.Chapter!.Number != null)
            .Select(l => new
            {
                l.SourceMappingId, l.ChapterId, l.SourceChapterId, l.NumberRaw, Number = l.Chapter!.Number!.Value,
                l.Volume, l.Title, l.Chapter.Language, l.ReleaseDate, l.Url, l.PageCount, l.PageHashes, l.StripHashes,
            })
            .ToListAsync(ct);
        var linksByMapping = links.ToLookup(l => l.SourceMappingId);

        var floors = new List<FoldFloor>();
        var seriesChecked = 0;
        foreach (var series in mappings.GroupBy(m => m.SeriesId).OrderBy(g => g.Key))
        {
            seriesChecked++;
            // Mapping order decides ties in the rules, so it is fixed: priority, then id.
            var ordered = series.OrderBy(m => m.Priority).ThenBy(m => m.Id).ToList();
            var names = ordered.ToDictionary(
                m => m.Id,
                m => ordered.Count(o => o.SourceName == m.SourceName) > 1 ? $"{m.SourceName}#{m.Id}" : m.SourceName);
            var publisher = ordered.SelectMany(m => PublisherNumbering.Parse(m.PublisherNumbers)).Distinct().ToList();
            var idsByName = names.ToDictionary(n => n.Value, n => n.Key);
            foreach (var language in ordered.SelectMany(m => linksByMapping[m.Id]).Select(l => l.Language).Distinct().Order())
            {
                // Per-volume numbering can list one number twice; the first one stands for it here.
                var byMapping = ordered.ToDictionary(
                    m => names[m.Id],
                    m => linksByMapping[m.Id]
                        .Where(l => l.Language == language)
                        .DistinctBy(l => l.Number)
                        .OrderBy(l => l.Number)
                        .Select(l => new FoldLink(series.Key, m.Id, names[m.Id], m.SourceName, m.SourceSeriesId, l.ChapterId,
                            l.SourceChapterId, l.NumberRaw, l.Number, l.Volume, l.Title, language, l.ReleaseDate, l.Url, l.PageCount,
                            l.PageHashes, l.StripHashes))
                        .ToList());
                var listings = ordered
                    .Select(m => new ReconcileListing(names[m.Id],
                        [.. byMapping[names[m.Id]].Select(l => new ReconcileEntry(l.Number, l.Title, l.PageCount))]))
                    .Where(l => l.Entries.Count > 0)
                    .ToList();
                var hashes = byMapping.SelectMany(m => m.Value)
                    .ToDictionary(l => (l.MappingName, l.Number), l => Evidence(l));
                foreach (var d in ChapterReconciler.Decide(listings, publisher).Values.Where(d => d.State != FoldState.Plain))
                {
                    ChapterReconciler.CheckPages(d, (mapping, number) => hashes.GetValueOrDefault((mapping, number)));
                    var missing = ChapterReconciler.MissingCounts(d, listings)
                        .Select(m => byMapping[m.Mapping].First(l => l.Number == m.Number))
                        .ToList();
                    var missingPages = d.MissingPages
                        .Select(m => byMapping[m.Mapping].First(l => l.Number == m.Number))
                        .ToList();
                    floors.Add(new FoldFloor(series.Key, series.First().Title, language, d, missing, missingPages, idsByName));
                }
            }
        }

        return (floors, seriesChecked);
    }
}
