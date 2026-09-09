using Maki.Core.Entities;
using Maki.Core.Security;
using Maki.Data;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>Small Discover rows for recurring interests outside the library's dominant themes.</summary>
public class SideInterestRailService(
    IServiceScopeFactory scopeFactory,
    RecommendationService recommendations,
    VectorIndexCache vectorIndex,
    EmbeddingStore embeddings)
{
    internal sealed record Seed(long Id, string Title, IReadOnlyList<string> Genres, IReadOnlyList<string> Tags,
        IReadOnlyList<string>? CoreTags = null);
    internal sealed record Interest(string Name, bool IsTag, IReadOnlyList<Seed> Seeds);

    internal async Task<IReadOnlyList<Seed>> ReadSeedsAsync(ICurrentUser scope, CancellationToken ct)
    {
        if (scope.UserId <= 0) return [];
        using var child = scopeFactory.CreateScope();
        var db = child.ServiceProvider.GetRequiredService<MakiDbContext>();
        db.Scope.SetUser(scope.UserId, scope.AllRootFolders);
        var allowed = ContentRating.Allowed(scope.MaxContentRating);
        var rows = await db.Series.AsNoTracking()
            .Where(s => s.MangaBakaId != null && s.Incognito != IncognitoMode.Full
                && allowed.Contains(s.ContentRating ?? ContentRating.Safe)
                && !db.UserSeriesStates.Any(u => u.SeriesId == s.Id && u.Rating < 5))
            .Select(s => new { s.MangaBakaId, s.Title, s.Genres, s.Tags })
            .ToListAsync(ct);
        return rows.Select(s => new Seed(s.MangaBakaId!.Value, s.Title, s.Genres, s.Tags))
            .OrderBy(s => s.Id).DistinctBy(s => s.Id).ToList();
    }

    internal static IReadOnlyList<Interest> SelectInterests(IReadOnlyList<Seed> library)
    {
        var seeds = library.DistinctBy(s => s.Id).ToList();
        if (seeds.Count < 6) return [];
        var themes = seeds.SelectMany(s =>
                s.Tags.Select(t => (Name: t.Trim(), IsTag: true, Seed: s))
                    .Concat(s.Genres.Select(g => (Name: g.Trim(), IsTag: false, Seed: s))))
            .Where(t => t.Name.Length > 0)
            .GroupBy(t => (Name: t.Name.ToLowerInvariant(), t.IsTag))
            .Select(g => new Interest(g.First().Name, g.Key.IsTag,
                g.Select(t => t.Seed).DistinctBy(s => s.Id).OrderBy(s => s.Id).ToList()))
            .ToList();
        var dominantCounts = themes.Where(t => t.Seeds.Count >= seeds.Count * 0.5)
            .SelectMany(t => t.Seeds).GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.Count());
        // Prefer pockets furthest from the dominant themes, then recurring support. Frequency is
        // measured per distinct work, so duplicate tags and duplicate library mappings cannot vote twice.
        // Only core story tags can name a row. Keep all occurrences for the minority ceiling, so
        // a ubiquitous tag with a few core assignments cannot masquerade as a smaller interest.
        var candidates = themes.Where(t => t.IsTag && t.Seeds.Count <= seeds.Count * 0.35)
            .Select(t => t with { Seeds = t.Seeds.Where(s => s.CoreTags?.Contains(
                t.Name, StringComparer.OrdinalIgnoreCase) == true).ToList() })
            .Where(t => t.Seeds.Count >= 2)
            .OrderBy(t => t.Seeds.Average(s => dominantCounts.GetValueOrDefault(s.Id)))
            .ThenByDescending(t => t.IsTag)
            .ThenByDescending(t => t.Seeds.Count)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var selected = new List<Interest>();
        foreach (var candidate in candidates)
        {
            // A genre and its usual tag often describe the same pocket. Give that space to another
            // interest when at least half of the smaller supporting set overlaps an existing row.
            if (selected.Any(t => t.Seeds.Count(s => candidate.Seeds.Any(x => x.Id == s.Id))
                >= Math.Min(t.Seeds.Count, candidate.Seeds.Count) * 0.5)) continue;
            selected.Add(candidate);
            if (selected.Count == 2) break;
        }
        return selected;
    }

    internal static IReadOnlyList<string> CoreStoryTags(byte[]? blob, IReadOnlyDictionary<int, TagInfo> vocabulary) =>
        TagMath.Unpack(blob)
            .Where(t => t.Class == TagMath.Core && vocabulary.TryGetValue(t.Id, out var info)
                && !info.IsSpoiler && TagMath.IsStoryCategory(info.Category))
            .Select(t => vocabulary[t.Id].Name)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public async Task<IReadOnlyList<DiscoverRail>> GetAsync(
        ICurrentUser scope, bool refresh, CancellationToken ct = default)
    {
        var library = await ReadSeedsAsync(scope, ct);
        if (library.Count < 6 || await vectorIndex.GetAsync(ct) is not { } index) return [];
        var vocabulary = embeddings.GetVocab();
        var interests = SelectInterests(library.Select(s =>
        {
            var coreTags = index.TryGetRow(s.Id, out var row)
                ? CoreStoryTags(index.TagsAt(row), vocabulary) : [];
            // Include indexed core names even when the library's flat metadata is older.
            return s with { CoreTags = coreTags, Tags = s.Tags.Concat(coreTags).ToList() };
        }).ToList());
        var rails = new List<DiscoverRail>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var interest in interests)
        {
            var filters = new RecommendationFilters(
                Genres: interest.IsTag ? null : [interest.Name],
                Tags: interest.IsTag ? [interest.Name] : null,
                ContentRatings: ContentRating.Allowed(scope.MaxContentRating));
            var plan = index.Plan(filters);
            if (plan.Impossible) continue;
            var ids = interest.Seeds.Select(s => s.Id).ToList();
            var result = await recommendations.GetAsync(
                new RecommendationRequest(SeedIds: ids, Filters: filters, Refresh: refresh), scope, ct);
            // Recheck against the index because the recommender's SQL fallback cannot filter tags.
            // Relations are omitted: a small interest row should not fill with sequels of its seeds.
            var items = result.Similar.Where(item => !seen.Contains(item.ProviderId)
                    && long.TryParse(item.ProviderId, out var id) && index.TryGetRow(id, out var row)
                    && index.Matches(row, plan))
                .DistinctBy(item => item.ProviderId).Take(20).ToList();
            if (items.Count < 3) continue;
            foreach (var item in items) seen.Add(item.ProviderId);
            rails.Add(new DiscoverRail(
                $"side-interest-{(interest.IsTag ? "tag" : "genre")}-{interest.Name.ToLowerInvariant()}",
                $"Another side of your library: {interest.Name}", "SideInterest", null, items,
                Subtitle: $"A smaller thread shared by {interest.Seeds.Count} titles, including " +
                    string.Join(" and ", interest.Seeds.Take(2).Select(s => s.Title)),
                SeedIds: ids, Filters: filters));
        }
        return rails;
    }
}
