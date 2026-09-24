using Maki.Api.Dtos;
using Maki.Core.Entities;
using Maki.Core.Security;
using Maki.Data;
using Maki.Metadata.MangaBaka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Maki.Api.Services;

/// <summary>
/// Where a reader stands over all time: how they read, what is left, what they are partway through,
/// which creators they keep coming back to, and how their ratings sit against the community's.
/// <para>
/// Per-user rows are read with filters off and an explicit user, because an admin may be looking at
/// somebody else. The <c>Series</c> reads keep the caller's filter, same as
/// <see cref="ActivityStatsService"/>: a series outside the caller's root folders never contributes a
/// title or a cover. Fully incognito series are left out everywhere, since their progress rows are
/// still written.
/// </para>
/// </summary>
public class StatsStandingService(
    MakiDbContext db,
    ReadingBehaviourService behaviour,
    UserMetricsService metrics,
    MangaBakaLocalStore mangaBaka,
    ICurrentUser currentUser,
    IMemoryCache cache,
    TimeProvider clock)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    private const int BacklogTop = 8;
    private const int MidwayTop = 6;
    private const int CreatorsTop = 8;
    private const int CreatorMinSeries = 2;
    private const int RatedTop = 12;
    private const int MinRatedPairs = 3;

    private sealed record SeriesRow(
        int Id, string Title, string? CoverPath, DateTime? LastMetadataRefresh,
        string? AuthorStory, string? AuthorArt, int? MangaBakaId);

    private sealed record ProgressRow(int SeriesId, int ChapterId, bool Watched, DateTime UpdatedAt);

    /// <param name="userId">Resolved through <see cref="UserViewResolver"/>; no permission check here.</param>
    public async Task<StatsStandingDto> GetAsync(int userId, CancellationToken ct)
    {
        var key = $"standing:{currentUser.UserId}:{userId}";
        if (cache.TryGetValue<StatsStandingDto>(key, out var hit) && hit is not null)
        {
            return hit;
        }

        var readingBehaviour = await BehaviourAsync(userId, ct);
        var pace = readingBehaviour.MedianSecondsPerChapter;

        var series = await db.Series.AsNoTracking()
            .Where(s => s.Incognito != IncognitoMode.Full)
            .Select(s => new SeriesRow(
                s.Id, s.Title, s.CoverPath, s.LastMetadataRefresh, s.AuthorStory, s.AuthorArt, s.MangaBakaId))
            .ToDictionaryAsync(s => s.Id, ct);

        if (userId != currentUser.UserId)
        {
            // The named lists come from the target's folders; the caller only sees titles in theirs.
            readingBehaviour = readingBehaviour with
            {
                Savoured = [.. readingBehaviour.Savoured.Where(b => series.ContainsKey(b.SeriesId))],
                Devoured = [.. readingBehaviour.Devoured.Where(b => series.ContainsKey(b.SeriesId))],
                Abandoned = [.. readingBehaviour.Abandoned.Where(b => series.ContainsKey(b.SeriesId))]
            };
        }

        var held = await db.Chapters.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.ChapterFileId != null)
            .GroupBy(c => c.SeriesId)
            .Select(g => new { SeriesId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.SeriesId, x => x.Count, ct);

        // ReadCounts.Read semantics for the target: completed on a downloaded chapter, watched
        // included, because a watched run is not left to read.
        var progress = (await db.ChapterProgress.AsNoTracking().IgnoreQueryFilters()
                .Where(p => p.UserId == userId && p.Completed && p.UnreadAt == null &&
                            db.Chapters.IgnoreQueryFilters().Any(c => c.Id == p.ChapterId && c.ChapterFileId != null))
                .Select(p => new ProgressRow(p.SeriesId, p.ChapterId, p.Watched, p.UpdatedAt))
                .ToListAsync(ct))
            .Where(p => series.ContainsKey(p.SeriesId))
            .ToList();

        // Last touch includes chapters still in progress, not only finished ones: somebody halfway
        // through a long chapter yesterday is reading the series now. Watched ticks and imports
        // (PageCount 0) are not reading and must not make a series look current.
        var lastTouched = await db.ChapterProgress.AsNoTracking().IgnoreQueryFilters()
            .Where(p => p.UserId == userId && p.UnreadAt == null && !p.Watched && p.PageCount > 0)
            .GroupBy(p => p.SeriesId)
            .Select(g => new { SeriesId = g.Key, At = g.Max(p => p.UpdatedAt) })
            .ToDictionaryAsync(x => x.SeriesId, x => x.At, ct);

        var perSeries = progress
            .GroupBy(p => p.SeriesId)
            .Select(g =>
            {
                var read = g.Select(p => p.ChapterId).Distinct().Count();
                var have = Math.Max(read, held.GetValueOrDefault(g.Key));
                return (SeriesId: g.Key, Read: read, Held: have, Unread: have - read,
                    LastReadAt: lastTouched.GetValueOrDefault(g.Key),
                    ReadNotWatched: g.Count(p => !p.Watched));
            })
            .ToList();

        string? Cover(SeriesRow s) => SeriesDto.CoverUrlFor(s.Id, s.CoverPath, s.LastMetadataRefresh);

        // ---- backlog ----
        // Totals cover everything the reader holds and has not read, started or not. The named list
        // sticks to started series, or it would just be the biggest things nobody has opened.
        var readBySeries = perSeries.ToDictionary(p => p.SeriesId, p => p.Read);
        var libraryUnread = held
            .Where(h => series.ContainsKey(h.Key))
            .Select(h => Math.Max(0, h.Value - readBySeries.GetValueOrDefault(h.Key)))
            .Where(u => u > 0)
            .ToList();
        var unreadTotal = libraryUnread.Sum();
        var withUnread = perSeries.Where(p => p.Unread > 0).ToList();
        var backlog = new BacklogDto(
            unreadTotal,
            libraryUnread.Count,
            pace is double secs ? unreadTotal * secs / 3600 : null,
            [.. withUnread
                .OrderByDescending(p => p.Unread).ThenBy(p => series[p.SeriesId].Title)
                .Take(BacklogTop)
                .Select(p => new BacklogSeriesDto(
                    p.SeriesId, series[p.SeriesId].Title, Cover(series[p.SeriesId]), p.Read, p.Unread))]);

        // ---- midway ----
        var recentAfter = clock.GetUtcNow().UtcDateTime - ActivityStatsService.DroppedAfter;
        var midway = withUnread
            .Where(p => p.ReadNotWatched > 0 && p.LastReadAt >= recentAfter)
            .OrderByDescending(p => p.LastReadAt)
            .Take(MidwayTop)
            .Select(p => new MidwaySeriesDto(
                p.SeriesId, series[p.SeriesId].Title, Cover(series[p.SeriesId]), p.Read, p.Held,
                pace is double secs ? (int)Math.Round(p.Unread * secs) : null,
                DateTime.SpecifyKind(p.LastReadAt, DateTimeKind.Utc)))
            .ToList();

        // ---- creators ----
        // ReadCounts.ReadFor semantics: a watched season is not a creator somebody reads.
        var creators = Creators(perSeries
            .Where(p => p.ReadNotWatched > 0)
            .Select(p => (series[p.SeriesId].AuthorStory, series[p.SeriesId].AuthorArt, p.ReadNotWatched)));

        var ratings = await RatingsAsync(userId, series, ct);
        var fullyRead = (await metrics.GetAsync(userId, ct)).SeriesFullyRead;

        var dto = new StatsStandingDto(readingBehaviour, backlog, midway, creators, ratings, fullyRead);
        cache.Set(key, dto, CacheFor);
        return dto;
    }

    /// <summary>
    /// Behaviour counts what the target can see. For somebody else, that is their own folder grants,
    /// read off their account rather than borrowed from the admin asking, so the aggregate numbers
    /// are scoped to the target's own folders; only the named lists are trimmed to the caller's.
    /// </summary>
    private async Task<ReadingBehaviour> BehaviourAsync(int userId, CancellationToken ct)
    {
        var allRootFolders = userId == currentUser.UserId
            ? currentUser.AllRootFolders
            : await db.Users.AsNoTracking().IgnoreQueryFilters()
                .Where(u => u.Id == userId)
                .Select(u => u.AllRootFolders)
                .FirstOrDefaultAsync(ct);
        return await behaviour.GetAsync(userId, allRootFolders, refresh: false, ct);
    }

    /// <summary>
    /// Credits are comma-joined strings. Somebody credited for both story and art on one series
    /// counts that series once.
    /// </summary>
    internal static List<CreatorReturnDto> Creators(
        IEnumerable<(string? AuthorStory, string? AuthorArt, int ChaptersRead)> readSeries)
    {
        var byName = new Dictionary<string, (string Name, bool Story, bool Art, int Series, int Chapters)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var (story, art, chapters) in readSeries)
        {
            var credits = Split(story).Select(n => (Name: n, Story: true))
                .Concat(Split(art).Select(n => (Name: n, Story: false)))
                .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var credit in credits)
            {
                var isStory = credit.Any(c => c.Story);
                var isArt = credit.Any(c => !c.Story);
                var current = byName.TryGetValue(credit.Key, out var c0)
                    ? c0
                    : (Name: credit.Key, Story: false, Art: false, Series: 0, Chapters: 0);
                byName[credit.Key] = (current.Name, current.Story || isStory, current.Art || isArt,
                    current.Series + 1, current.Chapters + chapters);
            }
        }

        return [.. byName.Values
            .Where(c => c.Series >= CreatorMinSeries)
            .OrderByDescending(c => c.Series).ThenByDescending(c => c.Chapters).ThenBy(c => c.Name)
            .Take(CreatorsTop)
            .Select(c => new CreatorReturnDto(c.Name, c.Story, c.Art, c.Series, c.Chapters))];

        static IEnumerable<string> Split(string? credits) =>
            (credits ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private async Task<RatingGapDto?> RatingsAsync(
        int userId, Dictionary<int, SeriesRow> series, CancellationToken ct)
    {
        var rated = (await db.UserSeriesStates.AsNoTracking().IgnoreQueryFilters()
                .Where(s => s.UserId == userId && s.Rating != null)
                .Select(s => new { s.SeriesId, Rating = s.Rating!.Value })
                .ToListAsync(ct))
            .Where(r => series.TryGetValue(r.SeriesId, out var s) && s.MangaBakaId != null)
            .ToList();
        if (rated.Count < MinRatedPairs || !await mangaBaka.IsAvailableAsync(ct))
        {
            return null;
        }

        var community = (await mangaBaka.GetByIdsAsync(
                [.. rated.Select(r => (long)series[r.SeriesId].MangaBakaId!.Value).Distinct()], ct: ct))
            .Where(m => m.Rating != null)
            .ToDictionary(m => m.ProviderId, m => m.Rating!.Value / 10.0);

        return RatingGap(rated
            .Select(r => (Row: series[r.SeriesId], r.Rating))
            .Where(r => community.ContainsKey(r.Row.MangaBakaId!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .Select(r => new RatedSeriesDto(
                r.Row.Id, r.Row.Title, SeriesDto.CoverUrlFor(r.Row.Id, r.Row.CoverPath, r.Row.LastMetadataRefresh),
                r.Rating,
                community[r.Row.MangaBakaId!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)]))
            .ToList());
    }

    /// <summary>Null under three pairs: a mean of one or two ratings is an anecdote.</summary>
    internal static RatingGapDto? RatingGap(IReadOnlyList<RatedSeriesDto> pairs)
    {
        if (pairs.Count < MinRatedPairs)
        {
            return null;
        }

        return new RatingGapDto(
            pairs.Average(p => p.Yours - p.Community),
            pairs.Count,
            [.. pairs
                .OrderByDescending(p => Math.Abs(p.Yours - p.Community)).ThenBy(p => p.Title)
                .Take(RatedTop)]);
    }
}
