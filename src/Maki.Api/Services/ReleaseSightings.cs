using Maki.Core.Releases;
using Maki.Core.Sources;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Reads every mapping's dates for a set of series, marked with whether their source's dates can
/// be trusted, for <see cref="ReleaseHistory"/>. Disabled mappings are read too: they no longer
/// list anything, but what they listed still happened when it did.
/// </summary>
public static class ReleaseSightings
{
    public static async Task<Dictionary<int, List<ChapterSighting>>> ForSeriesAsync(
        MakiDbContext db, SourceRegistry sources, IReadOnlyCollection<int> seriesIds, CancellationToken ct)
    {
        var rows = await db.ChapterSourceLinks
            .Where(l => seriesIds.Contains(l.SourceMapping!.SeriesId))
            .Where(l => l.ReleaseDate != null || l.FirstSeenAt != null)
            .Select(l => new { l.SourceMapping!.SeriesId, l.SourceMapping.SourceName, l.ChapterId, l.ReleaseDate, l.FirstSeenAt })
            .ToListAsync(ct);

        var reliable = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var bySeries = new Dictionary<int, List<ChapterSighting>>();
        foreach (var row in rows)
        {
            if (!reliable.TryGetValue(row.SourceName, out var trusted))
            {
                // A source that is gone from the registry can't vouch for its dates.
                trusted = sources.Find(row.SourceName)?.ReleaseDatesAreReliable ?? false;
                reliable[row.SourceName] = trusted;
            }

            if (!bySeries.TryGetValue(row.SeriesId, out var list))
            {
                bySeries[row.SeriesId] = list = [];
            }

            list.Add(new ChapterSighting(row.ChapterId, row.ReleaseDate, trusted, row.FirstSeenAt));
        }

        return bySeries;
    }
}
