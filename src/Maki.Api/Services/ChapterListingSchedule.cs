using System.Collections.Concurrent;

namespace Maki.Api.Services;

/// <summary>How much of a source's chapter list a sync asks for.</summary>
public enum ChapterListingDepth
{
    /// <summary>The whole list, every time. Manual refreshes, adds, re-matches and recoveries.</summary>
    Full,

    /// <summary>
    /// Only the newest pages of an <see cref="Maki.Core.Sources.INewestFirstChapterSource"/>, once the
    /// mapping had a full listing within <see cref="ChapterListingSchedule.FullEvery"/>. The monitored
    /// refresh asks for this.
    /// </summary>
    NewestFirst,
}

/// <summary>
/// When each newest-first mapping last had its whole list walked. A partial listing never removes a
/// link, so re-uploads under new ids and deleted chapters land only on a full walk, which comes back
/// once a day per mapping. In memory: after a restart every mapping's first sync walks it all.
/// </summary>
public sealed class ChapterListingSchedule(TimeProvider time)
{
    public static readonly TimeSpan FullEvery = TimeSpan.FromDays(1);

    private readonly ConcurrentDictionary<int, DateTimeOffset> _fullAt = new();

    public bool FullDue(int mappingId)
    {
        var now = time.GetUtcNow();
        return !_fullAt.TryGetValue(mappingId, out var at) || at > now || now - at >= FullEvery;
    }

    public void Walked(int mappingId) => _fullAt[mappingId] = time.GetUtcNow();
}
