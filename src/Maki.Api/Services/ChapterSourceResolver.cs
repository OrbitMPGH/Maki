using Maki.Core.Entities;
using Maki.Core.Http;
using Maki.Core.Sources;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

public record ResolvedChapterSource(SourceMapping Mapping, ISource Source, string SourceChapterId);

/// <summary>No mapping resolved and at least one was skipped because its source rate-limited the listing.</summary>
public sealed class SourceRateLimitedException(string sourceName, TimeSpan? retryAfter)
    : RateLimitException($"Rate limited by {sourceName} while finding the chapter", retryAfter)
{
    public string SourceName { get; } = sourceName;
}

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
    SourceChapterListCache chapterLists,
    SourceOrderService sourceOrder)
{
    /// <summary>Best first under the series' <see cref="SourceOrderMode"/>. See <see cref="SourceOrderService"/>.</summary>
    public async Task<IReadOnlyList<SourceMapping>> OrderAsync(
        MakiDbContext db, int seriesId, IReadOnlyCollection<SourceMapping> mappings, CancellationToken ct) =>
        (await sourceOrder.OrderAsync(db, seriesId, mappings, ct)).Ordered;

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

    /// <summary>Set form of <see cref="HasEnabledMappingAsync"/>: which of these series have an enabled mapping.</summary>
    public async Task<HashSet<int>> SeriesWithEnabledMappingAsync(
        MakiDbContext db, IReadOnlyCollection<int> seriesIds, CancellationToken ct)
    {
        var disabledSources = await sourceAvailability.DisabledAsync(ct);
        var mapped = await db.SourceMappings
            .Where(m => seriesIds.Contains(m.SeriesId) && m.Enabled && !disabledSources.Contains(m.SourceName))
            .Select(m => m.SeriesId)
            .Distinct()
            .ToListAsync(ct);
        return [.. mapped];
    }

    /// <summary>
    /// Resolves the best available mapping for <paramref name="chapter"/>. <paramref name="preferMappingId"/>,
    /// when given, is tried first regardless of priority — used when re-confirming a mapping the item
    /// was already assigned rather than starting the search over from scratch.
    /// <paramref name="excludeMappingIds"/> drops candidates the caller has already ruled out; without it
    /// a caller's own narrowing means nothing, since a preferred mapping that doesn't list the chapter
    /// falls through to the priority order and can land straight back on a mapping that just failed.
    /// <paramref name="onlyPreferred"/> turns that fallthrough off: the user picked this source's copy,
    /// and landing on another source would hand them a file they never chose.
    /// </summary>
    public async Task<ResolvedChapterSource> ResolveAsync(
        MakiDbContext db, Chapter chapter, int? preferMappingId, CancellationToken ct,
        IReadOnlyCollection<int>? excludeMappingIds = null, bool requireExactMatch = false,
        bool onlyPreferred = false)
    {
        var disabledSources = await sourceAvailability.DisabledAsync(ct);
        var query = db.SourceMappings
            .Where(m => m.SeriesId == chapter.SeriesId && m.Enabled && !disabledSources.Contains(m.SourceName));

        if (onlyPreferred && preferMappingId is { } only)
        {
            query = query.Where(m => m.Id == only);
        }

        if (excludeMappingIds is { Count: > 0 })
        {
            query = query.Where(m => !excludeMappingIds.Contains(m.Id));
        }

        var mappings = (await OrderAsync(db, chapter.SeriesId, await query.ToListAsync(ct), ct))
            .OrderBy(m => m.Id == preferMappingId ? 0 : 1)
            .ToList();

        if (mappings.Count == 0)
        {
            throw new InvalidOperationException("Series has no enabled source mappings");
        }

        var errors = new List<string>();
        SourceRateLimitedException? rateLimited = null;
        foreach (var mapping in mappings)
        {
            var source = sourceRegistry.Find(mapping.SourceName);
            if (source is null)
            {
                continue;
            }

            try
            {
                var sourceChapterId = await ResolveSourceChapterIdAsync(source, mapping, chapter, ct, requireExactMatch);
                if (sourceChapterId != null)
                {
                    return new ResolvedChapterSource(mapping, source, sourceChapterId);
                }

                errors.Add($"{source.Name}: chapter not listed");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (RateLimitDetector.IsRateLimit(ex, out var retryAfter))
            {
                // Another mapping may still have it; only when none does is the rate limit the answer,
                // so the caller backs the source off instead of spending a retry attempt on it.
                rateLimited ??= new SourceRateLimitedException(mapping.SourceName, retryAfter);
                errors.Add($"{source.Name}: {ex.Message}");
            }
            catch (Exception ex)
            {
                errors.Add($"{source.Name}: {ex.Message}");
            }
        }

        if (rateLimited is not null)
        {
            throw rateLimited;
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
        ISource source, SourceMapping mapping, Chapter chapter, CancellationToken ct, bool requireExactMatch = false)
    {
        var chapters = await chapterLists.GetAsync(source, mapping.SourceSeriesId, mapping.LanguageFilter, ct);
        if (requireExactMatch)
        {
            var exact = chapters.Where(c => c.Language == chapter.Language &&
                (chapter.Number != null ? c.Number == chapter.Number && (chapter.Volume == null || c.Volume == chapter.Volume) : c.Number == null && c.Title == chapter.Title)).ToList();
            return exact.Count == 1 ? exact[0].SourceChapterId : null;
        }

        // A mapping listing two languages carries both under one number, so language is part of every
        // match; a same-number hit in another language is "not listed", never a stand-in.
        var sameLanguage = chapters.Where(c => c.Language == chapter.Language).ToList();
        var match = chapter.Number is not null
            ? sameLanguage.FirstOrDefault(c => c.Number == chapter.Number && c.Volume == chapter.Volume)
              ?? sameLanguage.FirstOrDefault(c => c.Number == chapter.Number)
            : sameLanguage.FirstOrDefault(c => c.Number is null &&
                string.Equals(c.Title, chapter.Title, StringComparison.OrdinalIgnoreCase));

        return match?.SourceChapterId;
    }
}
