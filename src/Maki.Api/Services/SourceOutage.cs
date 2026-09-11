using Maki.Core.Entities;

namespace Maki.Api.Services;

/// <summary>
/// One source that enough series are failing against for the problem to be the source rather than
/// the series. <paramref name="Attempted"/> counts only mappings that have actually been refreshed
/// at least once (or tried and failed): a library holds plenty of mappings the refresh never
/// reaches, and counting those would keep the share below any threshold on a source that is
/// entirely down.
/// </summary>
public record SourceOutage(string SourceName, int Failing, int Attempted, string Sample)
{
    /// <summary>Every mapping we have tried is failing, so nothing suggests the source answers at all.</summary>
    public bool Unavailable => Failing == Attempted;
}

/// <summary>
/// Groups per-series refresh failures into per-source ones. A site that is down produces one
/// failing mapping per series that refreshed against it, and reporting those individually buries
/// every other health check under a wall of rows that all say the same thing.
/// </summary>
public static class SourceOutages
{
    /// <summary>
    /// Below this many failures a source problem is indistinguishable from series that were
    /// deleted, renamed or licensed away on the site, which is a per-series problem with a
    /// per-series fix.
    /// </summary>
    public const int MinimumFailures = 3;

    /// <summary>
    /// Share of the source's attempted mappings that must be failing. Guards the other direction:
    /// three broken series out of four hundred is not an outage.
    /// </summary>
    public const double MinimumShare = 0.5;

    /// <param name="attempted">
    /// Live mappings (enabled, source not globally switched off) that have been refreshed or tried.
    /// </param>
    public static List<SourceOutage> Detect(IEnumerable<SourceMapping> attempted) =>
        attempted
            .GroupBy(m => m.SourceName, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var failing = group.Where(m => m.LastError != null).ToList();
                // The most common message, not the first: an outage says the same thing on every
                // series, and whichever mapping happens to sort first may be the odd one out.
                var sample = failing
                    .GroupBy(m => m.LastError!)
                    .OrderByDescending(errors => errors.Count())
                    .Select(errors => errors.Key)
                    .FirstOrDefault() ?? "";
                return new SourceOutage(group.Key, failing.Count, group.Count(), Shorten(sample));
            })
            .Where(outage => outage.Failing >= MinimumFailures &&
                             outage.Failing >= outage.Attempted * MinimumShare)
            .OrderBy(outage => outage.SourceName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string Shorten(string error) =>
        error.Length <= 160 ? error : error[..159].TrimEnd() + "…";
}
