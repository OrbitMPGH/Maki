using Maki.Core.Entities;
using Maki.Core.Sources;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

public record ResolvedChapterSource(SourceMapping Mapping, ISource Source, string SourceChapterId);

/// <summary>
/// Finds which of a chapter's enabled source mappings actually has it, by listing each source's
/// current chapters until one matches. Shared by <see cref="DownloadQueueService"/> (resolves once at
/// enqueue time, so the queue's Source column is right immediately and dispatch never has to guess)
/// and <see cref="ChapterDownloadProcessor"/> (re-resolves only if the persisted id 404s by the time
/// the item is actually downloaded — a source can re-upload a chapter under a new id in the meantime).
/// </summary>
public class ChapterSourceResolver(
    SourceRegistry sourceRegistry,
    SourceAvailability sourceAvailability,
    SourceChapterListCache chapterLists)
{
    /// <summary>
    /// Cheap, DB-only precheck: does this series have any enabled mapping at all? Lets a caller reject
    /// the obviously-hopeless case synchronously, before <see cref="ResolveAsync"/>'s per-chapter,
    /// per-mapping network lookups.
    /// </summary>
    public async Task<bool> HasEnabledMappingAsync(MakiDbContext db, int seriesId, CancellationToken ct)
    {
        var disabledSources = await sourceAvailability.DisabledAsync(ct);
        return await db.SourceMappings
            .AnyAsync(m => m.SeriesId == seriesId && m.Enabled && !disabledSources.Contains(m.SourceName), ct);
    }

    /// <summary>
    /// Resolves the best available mapping for <paramref name="chapter"/>. <paramref name="preferMappingId"/>,
    /// when given, is tried first regardless of priority — used when re-confirming a mapping the item
    /// was already assigned rather than starting the search over from scratch.
    /// <paramref name="excludeMappingIds"/> drops candidates the caller has already ruled out; without it
    /// a caller's own narrowing means nothing, since a preferred mapping that doesn't list the chapter
    /// falls through to the priority order and can land straight back on a mapping that just failed.
    /// </summary>
    public async Task<ResolvedChapterSource> ResolveAsync(
        MakiDbContext db, Chapter chapter, int? preferMappingId, CancellationToken ct,
        IReadOnlyCollection<int>? excludeMappingIds = null)
    {
        var disabledSources = await sourceAvailability.DisabledAsync(ct);
        var query = db.SourceMappings
            .Where(m => m.SeriesId == chapter.SeriesId && m.Enabled && !disabledSources.Contains(m.SourceName));

        if (excludeMappingIds is { Count: > 0 })
        {
            query = query.Where(m => !excludeMappingIds.Contains(m.Id));
        }

        var mappings = await query
            .OrderBy(m => m.Id == preferMappingId ? -1 : m.Priority)
            .ToListAsync(ct);

        if (mappings.Count == 0)
        {
            throw new InvalidOperationException("Series has no enabled source mappings");
        }

        var errors = new List<string>();
        foreach (var mapping in mappings)
        {
            var source = sourceRegistry.Find(mapping.SourceName);
            if (source is null)
            {
                continue;
            }

            try
            {
                var sourceChapterId = await ResolveSourceChapterIdAsync(source, mapping, chapter, ct);
                if (sourceChapterId != null)
                {
                    return new ResolvedChapterSource(mapping, source, sourceChapterId);
                }

                errors.Add($"{source.Name}: chapter not listed");
            }
            catch (Exception ex)
            {
                errors.Add($"{source.Name}: {ex.Message}");
            }
        }

        throw new InvalidOperationException(
            $"Chapter {chapter.Number} unavailable on all sources ({string.Join("; ", errors)})");
    }

    /// <summary>
    /// The queue stores our Chapter, not the source's chapter id, so look it up in the source's
    /// current chapter list. Keeps the queue robust when a source re-uploads chapters under new ids.
    /// <para>
    /// Goes through <see cref="SourceChapterListCache"/> rather than calling the source directly:
    /// resolution is per chapter but the listing is per series, so a bulk enqueue would otherwise
    /// issue one full catalog listing per queued chapter against the same rate-limited source.
    /// </para>
    /// </summary>
    private async Task<string?> ResolveSourceChapterIdAsync(
        ISource source, SourceMapping mapping, Chapter chapter, CancellationToken ct)
    {
        var chapters = await chapterLists.GetAsync(source, mapping.SourceSeriesId, mapping.LanguageFilter, ct);

        var match = chapter.Number is not null
            ? chapters.FirstOrDefault(c => c.Number == chapter.Number && c.Volume == chapter.Volume)
              ?? chapters.FirstOrDefault(c => c.Number == chapter.Number)
            : chapters.FirstOrDefault(c => c.Number is null &&
                string.Equals(c.Title, chapter.Title, StringComparison.OrdinalIgnoreCase));

        return match?.SourceChapterId;
    }
}
