using Maki.Core.Entities;
using Maki.Core.Tags;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// The one evaluator for auto-tag rules. Every path that creates a library series calls
/// <see cref="ApplyOnAddAsync"/> once the row is saved; existing series are only tagged through
/// <see cref="ApplyToExistingAsync"/>, which an admin asks for per rule.
/// </summary>
public class AutoTagRuleService(MakiDbContext db, TagService tags, ILogger<AutoTagRuleService> logger)
{
    /// <summary>
    /// Tags a freshly added series with every enabled rule it matches. Never throws: a rule that
    /// cannot be applied is logged, and the add it rides on has already committed.
    /// </summary>
    public async Task ApplyOnAddAsync(Series series, string addedVia, int? addedBy, CancellationToken ct)
    {
        try
        {
            var rules = await db.AutoTagRules.AsNoTracking().Where(r => r.Enabled).ToListAsync(ct);
            if (rules.Count == 0)
            {
                return;
            }

            var subject = AutoTagSubject.For(series, addedVia, addedBy is > 0 ? addedBy : null);
            var tagIds = rules
                .Where(r => ConditionGroups.Matches(ConditionGroups.Parse(r.ConditionsJson), subject))
                .Select(r => r.TagId)
                .ToList();
            if (tagIds.Count > 0)
            {
                await tags.AddToSeriesAsync(series.Id, tagIds, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not apply auto-tag rules to {Title}", series.Title);
        }
    }

    /// <summary>The visible series the rule matches that do not carry its tag yet.</summary>
    public async Task<IReadOnlyList<int>> MatchingExistingAsync(AutoTagRule rule, CancellationToken ct)
    {
        var groups = ConditionGroups.Parse(rule.ConditionsJson);
        if (groups.Count == 0)
        {
            return [];
        }

        // Scoped: a caller restricted to some root folders only tags the series they can see.
        var series = await db.Series
            .AsNoTracking()
            .Where(s => s.ArchivedAt == null)
            .Where(s => !db.SeriesTags.Any(t => t.SeriesId == s.Id && t.TagId == rule.TagId))
            .Select(s => new { s.Id, s.Genres, s.Type, s.ContentRating, s.RootFolderId })
            .ToListAsync(ct);
        if (series.Count == 0)
        {
            return [];
        }

        var provenance = groups.SelectMany(g => g).Any(c => c.Field is AutoTagFields.AddedVia or AutoTagFields.AddedBy)
            ? await ProvenanceAsync(db, null, ct)
            : [];

        return series
            .Where(s =>
            {
                var (via, by) = provenance.GetValueOrDefault(s.Id);
                return ConditionGroups.Matches(groups,
                    new AutoTagSubject(s.Genres, s.Type, s.ContentRating, s.RootFolderId, via, by));
            })
            .Select(s => s.Id)
            .ToList();
    }

    public async Task<int> ApplyToExistingAsync(AutoTagRule rule, CancellationToken ct)
    {
        var ids = await MatchingExistingAsync(rule, ct);
        if (ids.Count == 0)
        {
            return 0;
        }

        var rows = ids.Select(id => new SeriesTag { SeriesId = id, TagId = rule.TagId }).ToList();
        db.SeriesTags.AddRange(rows);
        try
        {
            await db.SaveChangesAsync(ct);
            return ids.Count;
        }
        catch (DbUpdateException e) when (DbErrors.IsUniqueViolation(e))
        {
            // Somebody tagged one of them meanwhile; the slow path skips what is already there.
            foreach (var row in rows)
            {
                db.Entry(row).State = EntityState.Detached;
            }

            var added = 0;
            foreach (var seriesId in ids)
            {
                added += await tags.AddToSeriesAsync(seriesId, [rule.TagId], ct);
            }

            return added;
        }
    }

    /// <summary>
    /// How each series came in, as far as anything recorded it: the adder's <c>UserSeriesState</c>
    /// (written by every add except a library import, and skipped for a series added fully
    /// incognito), then the import batch record for a library import. A series with neither matches
    /// no "added via" or "added by" condition.
    /// </summary>
    /// <param name="seriesId">One series, or null for every series.</param>
    public static async Task<Dictionary<int, (string? Via, int? By)>> ProvenanceAsync(
        MakiDbContext db, int? seriesId, CancellationToken ct)
    {
        var result = new Dictionary<int, (string? Via, int? By)>();
        var imported = await db.ImportBatchFolders
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(f => f.CreatedSeries && f.SeriesId != null && (seriesId == null || f.SeriesId == seriesId))
            .Select(f => new { SeriesId = f.SeriesId!.Value, f.UserId })
            .ToListAsync(ct);
        foreach (var row in imported)
        {
            result[row.SeriesId] = (AutoTagAddedVia.LibraryImport, row.UserId);
        }

        var added = await db.UserSeriesStates
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.AddedToLibraryAtUtc != null && (seriesId == null || s.SeriesId == seriesId))
            .OrderBy(s => s.AddedToLibraryAtUtc)
            .Select(s => new { s.SeriesId, s.UserId, s.AddedFrom })
            .ToListAsync(ct);
        foreach (var row in added)
        {
            result.TryAdd(row.SeriesId, (AutoTagAddedVia.FromAddedFrom(row.AddedFrom), row.UserId));
        }

        return result;
    }
}
