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
    IReadOnlyList<ChapterFoldMissingDto> MissingPages,
    IReadOnlyDictionary<string, IReadOnlyList<decimal>> Waiting);

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
    string? Parts = null)
{
    public SourceChapter ToSourceChapter() =>
        new(SourceName, SourceSeriesId, SourceChapterId, NumberRaw, Number, Volume, Title, Language, ReleaseDate, Url);

    /// <summary>The uploads the link stands for: each part of a folded chapter in reading order, else the link itself.</summary>
    public IReadOnlyList<SourceChapter> ToSourceChapters() =>
        Maki.Core.Entities.ChapterFoldPart.Parse(Parts) is { Count: > 0 } parts
            ? [.. parts.Select(p => new SourceChapter(SourceName, SourceSeriesId, p.SourceChapterId, p.NumberRaw, p.Number, Volume, Title, Language, ReleaseDate, Url))]
            : [ToSourceChapter()];
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
                [.. f.MissingPages.Select(m => new ChapterFoldMissingDto(m.MappingName, m.Number))], f.Decision.Waiting))]))
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
    private static PageEvidence? Evidence(string? pageHashes, string? stripHashes) =>
        ChapterPageMatch.Parse(pageHashes) is { } pages
            ? new PageEvidence(pages, stripHashes is null ? null : ChapterPageMatch.ParseStrip(stripHashes) ?? [])
            : null;

    /// <summary>Wanted links per evidence query, which keeps each query's parameter list short.</summary>
    private const int EvidenceChunk = 500;

    /// <summary>
    /// The stored hashes of only the links a merged floor's page check reads. Loading them for every
    /// link held the whole library's hash text while most floors never look at it.
    /// </summary>
    private async Task<Dictionary<(int MappingId, int ChapterId), PageEvidence?>> EvidenceAsync(
        HashSet<(int MappingId, int ChapterId)> wanted, CancellationToken ct)
    {
        var evidence = new Dictionary<(int MappingId, int ChapterId), PageEvidence?>();
        if (wanted.Count == 0)
        {
            return evidence;
        }

        foreach (var chunk in wanted.OrderBy(w => w.ChapterId).Chunk(EvidenceChunk))
        {
            var chapterIds = chunk.Select(w => w.ChapterId).Distinct().ToList();
            var mappingIds = chunk.Select(w => w.MappingId).Distinct().ToList();
            var rows = await db.ChapterSourceLinks.AsNoTracking()
                .Where(l => chapterIds.Contains(l.ChapterId) && mappingIds.Contains(l.SourceMappingId) && l.PageHashes != null)
                .Select(l => new { l.SourceMappingId, l.ChapterId, l.PageHashes, l.StripHashes })
                .ToListAsync(ct);
            foreach (var row in rows)
            {
                if (wanted.Contains((row.SourceMappingId, row.ChapterId)))
                {
                    evidence[(row.SourceMappingId, row.ChapterId)] = Evidence(row.PageHashes, row.StripHashes);
                }
            }
        }

        return evidence;
    }

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
                l.Volume, l.Title, l.Chapter.Language, l.ReleaseDate, l.Url, l.PageCount, l.Parts,
            })
            .ToListAsync(ct);
        var linksByMapping = links.ToLookup(l => l.SourceMappingId);

        var decided = new List<(int SeriesId, string Title, string Language, FloorDecision Decision,
            Dictionary<string, List<FoldLink>> ByMapping, List<ReconcileListing> Listings,
            Dictionary<(string Mapping, decimal Number), FoldLink> ByKey, Dictionary<string, int> IdsByName)>();
        var wanted = new HashSet<(int MappingId, int ChapterId)>();
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
                            l.Parts))
                        .ToList());
                var listings = ordered
                    .Select(m => new ReconcileListing(names[m.Id],
                        [.. byMapping[names[m.Id]].Select(l => new ReconcileEntry(l.Number, l.Title, l.PageCount))]))
                    .Where(l => l.Entries.Count > 0)
                    .ToList();
                var byKey = byMapping.SelectMany(m => m.Value).ToDictionary(l => (l.MappingName, l.Number));
                foreach (var d in ChapterReconciler.Decide(listings, publisher).Values.Where(d => d.State != FoldState.Plain))
                {
                    decided.Add((series.Key, series.First().Title, language, d, byMapping, listings, byKey, idsByName));
                    if (d.State != FoldState.Merged)
                    {
                        continue;
                    }

                    // Exactly what CheckPages asks for: each part, and each whole at the floor.
                    var asked = d.Parts.SelectMany(p => p.Value.Select(n => (p.Key, n))).Concat(d.Wholes.Select(w => (w, d.Floor)));
                    foreach (var key in asked)
                    {
                        if (byKey.TryGetValue(key, out var link))
                        {
                            wanted.Add((link.MappingId, link.ChapterId));
                        }
                    }
                }
            }
        }

        var evidence = await EvidenceAsync(wanted, ct);
        var floors = new List<FoldFloor>(decided.Count);
        foreach (var (seriesKey, title, language, d, byMapping, listings, byKey, idsByName) in decided)
        {
            ChapterReconciler.CheckPages(d, (mapping, number) =>
                byKey.TryGetValue((mapping, number), out var link) ? evidence.GetValueOrDefault((link.MappingId, link.ChapterId)) : null);
            var missing = ChapterReconciler.MissingCounts(d, listings)
                .Select(m => byMapping[m.Mapping].First(l => l.Number == m.Number))
                .ToList();
            var missingPages = d.MissingPages
                .Select(m => byMapping[m.Mapping].First(l => l.Number == m.Number))
                .ToList();
            floors.Add(new FoldFloor(seriesKey, title, language, d, missing, missingPages, idsByName));
        }

        return (floors, seriesChecked);
    }
}
