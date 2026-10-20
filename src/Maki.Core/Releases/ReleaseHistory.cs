namespace Maki.Core.Releases;

/// <summary>One mapping's view of one chapter, as far as release timing goes.</summary>
/// <param name="Reliable">Whether the source's <see cref="ReleaseDate"/> is a real release time (<c>ISource.ReleaseDatesAreReliable</c>).</param>
public readonly record struct ChapterSighting(int ChapterId, DateTime? ReleaseDate, bool Reliable, DateTime? FirstSeenAt);

/// <summary>Turns per-mapping sightings into the times the schedule and the cadence work from.</summary>
public static class ReleaseHistory
{
    /// <summary>Some sources date a chapter a little ahead of when it opens; anything later than this is ignored.</summary>
    private static readonly TimeSpan FutureTolerance = TimeSpan.FromDays(1);

    /// <summary>
    /// When each chapter came out, as best known: the earliest of a trustworthy source date and the
    /// moment a refresh first saw it. Chapters with neither are left out.
    /// </summary>
    public static List<DateTime> ReleaseTimes(IEnumerable<ChapterSighting> sightings, DateTime now)
    {
        var limit = now + FutureTolerance;
        var earliest = new Dictionary<int, DateTime>();
        foreach (var s in sightings)
        {
            foreach (var candidate in new[] { s.Reliable ? s.ReleaseDate : null, s.FirstSeenAt })
            {
                if (candidate is { } at && at <= limit &&
                    (!earliest.TryGetValue(s.ChapterId, out var known) || at < known))
                {
                    earliest[s.ChapterId] = at;
                }
            }
        }

        return [.. earliest.Values.Order()];
    }

    /// <summary>
    /// The newest chapter's time, for <see cref="RefreshSchedule"/>. Falls back to dates a source
    /// re-stamps when nothing better exists: a re-upload only ever makes such a date newer, so an
    /// old one still says the series has been quiet.
    /// </summary>
    public static DateTime? LastNewChapter(IReadOnlyCollection<ChapterSighting> sightings, DateTime now)
    {
        var trusted = ReleaseTimes(sightings, now);
        if (trusted.Count > 0)
        {
            return Min(trusted[^1], now);
        }

        var fallback = sightings
            .Where(s => s.ReleaseDate is { } at && at <= now + FutureTolerance)
            .Select(s => s.ReleaseDate!.Value)
            .DefaultIfEmpty()
            .Max();
        return fallback == default ? null : Min(fallback, now);
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}
