using System.Text.Json;
using System.Text.Json.Serialization;
using Maki.Api.Dtos;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Security;
using Maki.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Maki.Api.Services;

/// <summary>
/// The windowed half of the stats page that <c>stats/activity</c> does not carry: when somebody
/// reads, how long they sit, and what mix of the library they actually read.
/// <para>
/// Same scoping as <see cref="ActivityStatsService"/>: per-user rows are read with filters off and an
/// explicit user, while the <c>Series</c> joins keep the caller's filter so a series outside the
/// caller's root folders contributes nothing.
/// </para>
/// </summary>
public class StatsInsightsService(
    MakiDbContext db,
    IUserSettingsStore userSettings,
    IMemoryCache cache,
    ICurrentUser currentUser)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);

    /// <summary>A genre needs this share of either side before its lean is worth showing.</summary>
    private const double LeanFloor = 0.03;

    private const int LeanPerSide = 5;
    private const int PrimeWindowHours = 3;

    /// <param name="userId">Resolved through <see cref="UserViewResolver"/>; no permission check here.</param>
    /// <param name="utcOffsetMinutes">JS getTimezoneOffset() semantics, used only when the reader has
    /// no stored time zone.</param>
    public async Task<StatsInsightsDto> GetAsync(
        int userId, DateOnly from, DateOnly to, int utcOffsetMinutes, CancellationToken ct)
    {
        var key = $"insights:{currentUser.UserId}:{userId}:{from:yyyy-MM-dd}:{to:yyyy-MM-dd}:{utcOffsetMinutes}";
        if (cache.TryGetValue<StatsInsightsDto>(key, out var hit) && hit is not null)
        {
            return hit;
        }

        var zone = await UserTimeZone.TryResolveAsync(userSettings, userId, ct) ?? FixedOffset(utcOffsetMinutes);
        var utcStart = ToUtc(from.ToDateTime(TimeOnly.MinValue), zone);
        var utcEnd = ToUtc(to.AddDays(1).ToDateTime(TimeOnly.MinValue), zone);

        var events = await db.StatsEvents.AsNoTracking().IgnoreQueryFilters()
            .Where(e => e.UserId == userId && e.Timestamp >= utcStart && e.Timestamp < utcEnd &&
                        (e.Type == StatsEventType.ReadingTime ||
                         e.Type == StatsEventType.ChaptersRead ||
                         e.Type == StatsEventType.VolumesRead))
            .Select(e => new { e.Type, e.Timestamp, e.Value, e.SeriesId, e.SeriesKey })
            .ToListAsync(ct);

        var matrix = new int[7 * 24];
        foreach (var e in events.Where(e => e.Type == StatsEventType.ReadingTime && e.Value > 0))
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(e.Timestamp.AddSeconds(-e.Value / 2.0), DateTimeKind.Utc), zone);
            matrix[Weekday(local.DayOfWeek) * 24 + local.Hour] += e.Value;
        }

        var sittings = await SittingsAsync(userId, utcStart, utcEnd, to.DayNumber - from.DayNumber + 1, ct);

        var reads = events
            .Where(e => e.Type is StatsEventType.ChaptersRead or StatsEventType.VolumesRead)
            .Select(e => new ReadWeight(e.SeriesId, e.SeriesKey, e.Value))
            .ToList();
        var taste = await TasteAsync(reads, ct);

        // Series filter on, in its own query (IgnoreQueryFilters would reach a subquery too): a
        // bookmark in a folder the caller can't see, or in a hidden series, doesn't count.
        var visibleSeries = (await db.Series.AsNoTracking()
                .Where(s => s.Incognito != IncognitoMode.Full)
                .Select(s => s.Id)
                .ToListAsync(ct))
            .ToHashSet();
        var bookmarks = (await db.ReaderBookmarks.AsNoTracking().IgnoreQueryFilters()
                .Where(b => b.UserId == userId && b.CreatedAt >= utcStart && b.CreatedAt < utcEnd)
                .Select(b => b.SeriesId)
                .ToListAsync(ct))
            .Count(visibleSeries.Contains);

        var dto = new StatsInsightsDto(from, to, zone.Id, Rhythm(matrix), sittings, taste, bookmarks);
        cache.Set(key, dto, CacheFor);
        return dto;
    }

    // ---- time zones ----

    internal static TimeZoneInfo FixedOffset(int utcOffsetMinutes)
    {
        var offset = TimeSpan.FromMinutes(-utcOffsetMinutes);
        if (offset == TimeSpan.Zero)
        {
            return TimeZoneInfo.Utc;
        }

        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var id = $"UTC{sign}{offset.Duration():hh\\:mm}";
        return TimeZoneInfo.CreateCustomTimeZone(id, offset, id, id);
    }

    /// <summary>
    /// Local wall-clock time to UTC. A local midnight that a DST jump skips does not exist, so it is
    /// nudged forward an hour rather than letting the conversion throw.
    /// </summary>
    internal static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    internal static int Weekday(DayOfWeek day) => ((int)day + 6) % 7;

    // ---- rhythm ----

    /// <summary>Derives the headline numbers from the weekday-by-hour matrix (Monday = 0).</summary>
    internal static RhythmDto Rhythm(int[] matrix)
    {
        var total = matrix.Sum();
        if (total == 0)
        {
            return new RhythmDto(matrix, 0, null, null, null, null);
        }

        var byHour = new int[24];
        var byDay = new int[7];
        for (var i = 0; i < matrix.Length; i++)
        {
            byHour[i % 24] += matrix[i];
            byDay[i / 24] += matrix[i];
        }

        var (primeStart, primeSeconds) = PrimeWindow(byHour);
        var busiest = Array.IndexOf(byDay, byDay.Max());

        return new RhythmDto(
            matrix,
            total,
            primeStart,
            (double)primeSeconds / total,
            busiest,
            (double)(byDay[5] + byDay[6]) / total);
    }

    /// <summary>
    /// The three consecutive hours holding the most reading, wrapping midnight, so a 23:00 to 02:00
    /// night owl is one window rather than two halves. Ties go to the earliest start.
    /// </summary>
    internal static (int StartHour, int Seconds) PrimeWindow(int[] byHour)
    {
        var best = (StartHour: 0, Seconds: -1);
        for (var start = 0; start < 24; start++)
        {
            var sum = 0;
            for (var k = 0; k < PrimeWindowHours; k++)
            {
                sum += byHour[(start + k) % 24];
            }

            if (sum > best.Seconds)
            {
                best = (start, sum);
            }
        }

        return best;
    }

    // ---- sittings ----

    private async Task<SittingsDto?> SittingsAsync(
        int userId, DateTime utcStart, DateTime utcEnd, int windowDays, CancellationToken ct)
    {
        var sessions = await db.ReadingSessions.AsNoTracking().IgnoreQueryFilters()
            .Where(s => s.UserId == userId && s.StartedAt >= utcStart && s.StartedAt < utcEnd)
            .Select(s => new { s.StartedAt, s.ActiveSeconds, s.ChaptersCompleted })
            .ToListAsync(ct);
        if (sessions.Count == 0)
        {
            return null;
        }

        var longest = sessions.MaxBy(s => s.ActiveSeconds)!;
        return new SittingsDto(
            sessions.Count,
            (int)Math.Round(Median(sessions.Select(s => (double)s.ActiveSeconds))),
            longest.ActiveSeconds,
            DateTime.SpecifyKind(longest.StartedAt, DateTimeKind.Utc),
            sessions.Count / (windowDays / 7.0),
            (int)Math.Round(Median(sessions.Select(s => (double)s.ChaptersCompleted))));
    }

    private static double Median(IEnumerable<double> source)
    {
        var values = source.OrderBy(v => v).ToList();
        var mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2;
    }

    // ---- taste mix ----

    private sealed record ReadWeight(int? SeriesId, string? SeriesKey, int Value);

    internal sealed record SeriesFacts(IReadOnlyList<string> Genres, string? Type, int? Year);

    private async Task<TasteMixDto> TasteAsync(List<ReadWeight> reads, CancellationToken ct)
    {
        var ids = reads.Where(r => r.SeriesId != null).Select(r => r.SeriesId!.Value).Distinct().ToList();
        // Filter on: a series outside the caller's root folders drops out entirely. Fully incognito
        // series should have no read events, but one switched after the fact still has old ones.
        var live = await db.Series.AsNoTracking()
            .Where(s => ids.Contains(s.Id))
            .Select(s => new { s.Id, s.Genres, s.Type, s.Year, s.Incognito })
            .ToListAsync(ct);
        var facts = live
            .Where(s => s.Incognito != IncognitoMode.Full)
            .ToDictionary(s => s.Id, s => new SeriesFacts(s.Genres, s.Type, s.Year));

        // A removed series has lost its SeriesId, so its genres come off the removal snapshot, found
        // by the durable key. Only for rows with no SeriesId: a live id missing from the projection
        // is a series the caller may not see, and must not be looked up another way.
        var removedKeys = reads
            .Where(r => r.SeriesId == null && r.SeriesKey != null)
            .Select(r => r.SeriesKey!)
            .Distinct()
            .ToList();
        var snapshots = new Dictionary<string, SeriesFacts>();
        if (removedKeys.Count > 0)
        {
            var removals = await db.StatsEvents.AsNoTracking().IgnoreQueryFilters()
                .Where(e => e.Type == StatsEventType.SeriesRemoved && e.SeriesKey != null &&
                            removedKeys.Contains(e.SeriesKey) && e.PayloadJson != null)
                .OrderBy(e => e.Timestamp)
                .Select(e => new { e.SeriesKey, e.PayloadJson })
                .ToListAsync(ct);
            foreach (var r in removals)
            {
                snapshots[r.SeriesKey!] = new SeriesFacts(ParseGenres(r.PayloadJson), null, null);
            }
        }

        var weighted = new List<(SeriesFacts Facts, int Weight)>();
        foreach (var r in reads)
        {
            if (r.SeriesId is int id)
            {
                if (facts.TryGetValue(id, out var f))
                {
                    weighted.Add((f, r.Value));
                }
            }
            else if (r.SeriesKey != null && snapshots.TryGetValue(r.SeriesKey, out var snap))
            {
                weighted.Add((snap, r.Value));
            }
        }

        var libraryGenres = await db.Series.AsNoTracking().Select(s => s.Genres).ToListAsync(ct);

        return Taste(weighted, libraryGenres);
    }

    /// <summary>The pure half of the taste mix, over already-scoped rows.</summary>
    internal static TasteMixDto Taste(
        IReadOnlyList<(SeriesFacts Facts, int Weight)> reads, IReadOnlyList<List<string>> libraryGenres)
    {
        var total = reads.Sum(r => r.Weight);

        var readByGenre = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var types = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var demographics = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var eras = new Dictionary<int, int>();
        var undated = 0;
        foreach (var (f, w) in reads)
        {
            foreach (var g in f.Genres.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                readByGenre[g] = readByGenre.GetValueOrDefault(g) + w;
            }

            var type = f.Type ?? "";
            types[type] = types.GetValueOrDefault(type) + w;

            var demographic = f.Genres.FirstOrDefault(TasteInsightsService.DemographicGenres.Contains) ?? "";
            demographics[demographic] = demographics.GetValueOrDefault(demographic) + w;

            if (f.Year is int year)
            {
                var decade = year / 10 * 10;
                eras[decade] = eras.GetValueOrDefault(decade) + w;
            }
            else
            {
                undated += w;
            }
        }

        var libraryByGenre = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var genres in libraryGenres)
        {
            foreach (var g in genres.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                libraryByGenre[g] = libraryByGenre.GetValueOrDefault(g) + 1;
            }
        }

        var lean = new List<GenreLeanDto>();
        if (total > 0 && libraryGenres.Count > 0)
        {
            var shares = readByGenre.Keys.Union(libraryByGenre.Keys, StringComparer.OrdinalIgnoreCase)
                .Select(g => new GenreLeanDto(
                    g,
                    (double)readByGenre.GetValueOrDefault(g) / total,
                    (double)libraryByGenre.GetValueOrDefault(g) / libraryGenres.Count))
                .Where(l => l.ReadShare >= LeanFloor || l.LibraryShare >= LeanFloor)
                .ToList();
            lean.AddRange(shares
                .Where(l => l.ReadShare > l.LibraryShare)
                .OrderByDescending(l => l.ReadShare - l.LibraryShare).ThenBy(l => l.Name)
                .Take(LeanPerSide));
            lean.AddRange(shares
                .Where(l => l.ReadShare < l.LibraryShare)
                .OrderBy(l => l.ReadShare - l.LibraryShare).ThenBy(l => l.Name)
                .Take(LeanPerSide));
        }

        static List<NamedCountDto> Named(Dictionary<string, int> counts) => counts
            .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
            .Select(kv => new NamedCountDto(kv.Key, kv.Value))
            .ToList();

        var eraList = eras
            .OrderBy(kv => kv.Key)
            .Select(kv => new EraBucketDto(kv.Key, kv.Value))
            .ToList();
        if (undated > 0)
        {
            eraList.Add(new EraBucketDto(null, undated));
        }

        return new TasteMixDto(lean, Named(types), Named(demographics), eraList);
    }

    private static List<string> ParseGenres(string? payloadJson)
    {
        if (payloadJson is null)
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<RemovedSnapshot>(payloadJson)?.Genres ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private sealed record RemovedSnapshot([property: JsonPropertyName("genres")] List<string>? Genres);
}
