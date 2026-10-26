using Maki.Core.Entities;

namespace Maki.Data;

public static class SeriesQueries
{
    /// <summary>Series that are not archived. Anything that talks to a source or the download queue starts here.</summary>
    public static IQueryable<Series> Active(this IQueryable<Series> q) => q.Where(s => s.ArchivedAt == null);
}
