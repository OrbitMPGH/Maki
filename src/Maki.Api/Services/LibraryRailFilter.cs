using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Metadata.MangaBaka;

namespace Maki.Api.Services;

/// <summary>The fields of a library series a custom rail filters and sorts on.</summary>
public sealed record LibraryRailRow(
    int Id,
    long? MangaBakaId,
    string Title,
    string SortTitle,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Tags,
    string? ContentRating,
    int? Year,
    SeriesStatus Status,
    string? Type,
    int? TotalChapters,
    DateTime Added);

/// <summary>
/// A catalogue filter evaluated against a library series' own metadata, for series the vector
/// index has no row for (or when it is not loaded). Exact names only: subtags and centrality need
/// the index's tag tree and weights, so here they read as a plain name match, the same compromise
/// <see cref="RecommendationFilters.MatchesNames"/> makes. A constrained field the series has no
/// value for fails the test, like an unknown year falls out of a bounded range in the index.
/// </summary>
public static class LibraryRailFilter
{
    public static bool MatchesLocal(LibraryRailRow s, RecommendationFilters f)
    {
        if (f.YearMin is int ymin && (s.Year is not int y1 || y1 < ymin))
        {
            return false;
        }

        if (f.YearMax is int ymax && (s.Year is not int y2 || y2 > ymax))
        {
            return false;
        }

        if (f.MinChapters is int cmin && (s.TotalChapters is not int c1 || c1 < cmin))
        {
            return false;
        }

        if (f.MaxChapters is int cmax && (s.TotalChapters is not int c2 || c2 > cmax))
        {
            return false;
        }

        // The score lives in the dump, not on the series.
        if (f.MinRating is not null)
        {
            return false;
        }

        if (f.Types is { Count: > 0 } types && !types.Contains(s.Type ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (f.Statuses is { Count: > 0 } statuses &&
            !statuses.Any(st => MangaBakaProvider.MapStatus(st) is var mapped && mapped != SeriesStatus.Unknown && mapped == s.Status))
        {
            return false;
        }

        if (f.ContentRatings is { Count: > 0 } ratings &&
            !ratings.Contains(s.ContentRating ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        // MatchesNames covers the rules only, so the legacy lists are applied here as the implicit
        // "all" rule they stand for.
        if (f.Genres is { Count: > 0 } genres && !genres.All(g => s.Genres.Contains(g, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (f.Tags is { Count: > 0 } tags && !tags.All(t => s.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        return f.MatchesNames(s.Genres.ToList(), s.Tags.ToList());
    }

    /// <param name="lastRead">When the caller last touched each series, for <see cref="CustomRailSorts.Read"/>.</param>
    /// <param name="popularity">A series' catalogue popularity rank, 1 highest, or null when unknown.</param>
    public static IReadOnlyList<int> Order(
        IEnumerable<LibraryRailRow> rows,
        string? sort,
        IReadOnlyDictionary<int, DateTime> lastRead,
        Func<LibraryRailRow, int?> popularity)
    {
        var byTitle = StringComparer.OrdinalIgnoreCase;
        string TitleOf(LibraryRailRow r) => string.IsNullOrWhiteSpace(r.SortTitle) ? r.Title : r.SortTitle;

        IEnumerable<LibraryRailRow> ordered = sort switch
        {
            CustomRailSorts.Title => rows.OrderBy(TitleOf, byTitle),
            CustomRailSorts.Read => rows
                .OrderByDescending(r => lastRead.TryGetValue(r.Id, out var at) ? at : DateTime.MinValue)
                .ThenByDescending(r => r.Added),
            CustomRailSorts.Popular => rows
                .OrderBy(r => popularity(r) ?? int.MaxValue)
                .ThenBy(TitleOf, byTitle),
            _ => rows.OrderByDescending(r => r.Added),
        };

        return ordered.Select(r => r.Id).ToList();
    }
}
