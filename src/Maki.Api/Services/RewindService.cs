using System.Text.Json;
using Maki.Api.Dtos;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Aggregates the append-only StatsEvents log into the Rewind payload. On-demand and
/// in-memory by design: a heavy year is low tens of thousands of tiny rows (one indexed
/// range query), SQLite's date functions don't translate timezone shifts, and the
/// genre/tag step needs an in-memory join against Series' JSON list columns anyway.
/// </summary>
public class RewindService(MakiDbContext db, IAppSettings appSettings, TimeProvider clock)
{
    /// <summary>A series counts as dropped when its reading mark stalled this long.</summary>
    private static readonly TimeSpan DroppedAfter = TimeSpan.FromDays(90);

    private const int TimelineDayBucketMaxDays = 62;

    public async Task<List<int>> YearsAsync(CancellationToken ct)
    {
        return await db.StatsEvents.AsNoTracking()
            .Select(e => e.Timestamp.Year)
            .Distinct()
            .OrderByDescending(y => y)
            .ToListAsync(ct);
    }

    /// <param name="utcOffsetMinutes">JS getTimezoneOffset() semantics: UTC − local, so
    /// UTC+2 sends −120. Local time = UTC − offset.</param>
    public async Task<RewindStatsDto> StatsAsync(
        DateOnly from, DateOnly to, int utcOffsetMinutes, CancellationToken ct)
    {
        // [from, to] are inclusive local dates; convert the window edges to UTC.
        var utcStart = from.ToDateTime(TimeOnly.MinValue).AddMinutes(utcOffsetMinutes);
        var utcEnd = to.AddDays(1).ToDateTime(TimeOnly.MinValue).AddMinutes(utcOffsetMinutes);

        var events = await db.StatsEvents.AsNoTracking()
            .Where(e => e.Timestamp >= utcStart && e.Timestamp < utcEnd)
            .ToListAsync(ct);

        DateTime Local(DateTime utc) => utc.AddMinutes(-utcOffsetMinutes);

        // ---- totals ----
        int Sum(StatsEventType t) => events.Where(e => e.Type == t).Sum(e => e.Value);
        int Count(StatsEventType t) => events.Count(e => e.Type == t);

        // ---- timeline ----
        var useDayBuckets = to.DayNumber - from.DayNumber + 1 <= TimelineDayBucketMaxDays;
        string Bucket(DateTime utc)
        {
            var local = Local(utc);
            return useDayBuckets ? local.ToString("yyyy-MM-dd") : local.ToString("yyyy-MM");
        }

        var timeline = events
            .Where(e => e.Type is StatsEventType.ChaptersRead or StatsEventType.ChapterDownloaded
                or StatsEventType.SeriesAdded)
            .GroupBy(e => Bucket(e.Timestamp))
            .OrderBy(g => g.Key)
            .Select(g => new RewindTimelinePointDto(
                g.Key,
                g.Where(e => e.Type == StatsEventType.ChaptersRead).Sum(e => e.Value),
                g.Where(e => e.Type == StatsEventType.ChapterDownloaded).Sum(e => e.Value),
                g.Where(e => e.Type == StatsEventType.SeriesAdded).Sum(e => e.Value)))
            .ToList();

        // ---- most/least read ----
        // Key by Maki series when matched, else by Kavita series, else by title, so
        // deleted and unmatched series still aggregate under one entry.
        var readEvents = events
            .Where(e => e.Type is StatsEventType.ChaptersRead or StatsEventType.VolumesRead)
            .ToList();
        var perSeries = readEvents
            .GroupBy(e => e.SeriesId is int sid ? $"s{sid}" : e.KavitaSeriesId is int kid ? $"k{kid}" : e.SeriesTitle)
            .Select(g =>
            {
                var last = g.OrderBy(e => e.Timestamp).Last();
                return new RewindSeriesStatDto(last.SeriesId, last.SeriesTitle, g.Sum(e => e.Value));
            })
            .ToList();
        var topRead = perSeries.OrderByDescending(s => s.Count).ThenBy(s => s.Title).Take(10).ToList();
        var topKeys = topRead.Select(s => (s.SeriesId, s.Title)).ToHashSet();
        var leastRead = perSeries
            .Where(s => s.Count >= 1 && !topKeys.Contains((s.SeriesId, s.Title)))
            .OrderBy(s => s.Count).ThenBy(s => s.Title)
            .Take(5)
            .ToList();

        // ---- favorite genres/tags ----
        // Weight = chapters/volumes read per series; when nothing was read in the window
        // (no Kavita), fall back to series added. Removed series contribute via their
        // snapshot payload.
        var seriesIds = events.Where(e => e.SeriesId != null).Select(e => e.SeriesId!.Value).Distinct().ToList();
        var seriesMeta = await db.Series.AsNoTracking()
            .Where(s => seriesIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Genres, s.Tags })
            .ToDictionaryAsync(s => s.Id, ct);

        var genreWeights = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var tagWeights = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void AddWeights(int? seriesId, string? payloadJson, int weight)
        {
            List<string>? genres = null, tags = null;
            if (seriesId is int sid && seriesMeta.TryGetValue(sid, out var meta))
            {
                (genres, tags) = (meta.Genres, meta.Tags);
            }
            else if (payloadJson is not null)
            {
                try
                {
                    var snap = JsonSerializer.Deserialize<RemovedSeriesSnapshot>(payloadJson);
                    (genres, tags) = (snap?.Genres, snap?.Tags);
                }
                catch (JsonException)
                {
                    // best-effort — a malformed snapshot just doesn't contribute
                }
            }

            foreach (var g in genres ?? [])
            {
                genreWeights[g] = genreWeights.GetValueOrDefault(g) + weight;
            }

            foreach (var t in tags ?? [])
            {
                tagWeights[t] = tagWeights.GetValueOrDefault(t) + weight;
            }
        }

        var weightSource = readEvents.Count > 0
            ? readEvents
            : events.Where(e => e.Type is StatsEventType.SeriesAdded or StatsEventType.SeriesRemoved).ToList();
        foreach (var e in weightSource)
        {
            AddWeights(e.SeriesId, e.PayloadJson, e.Value);
        }

        static List<RewindWeightedNameDto> Top(Dictionary<string, int> weights) => weights
            .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
            .Take(10)
            .Select(kv => new RewindWeightedNameDto(kv.Key, kv.Value))
            .ToList();

        // ---- event lists ----
        List<RewindSeriesEventDto> EventList(StatsEventType type) => events
            .Where(e => e.Type == type)
            .OrderByDescending(e => e.Timestamp)
            .Select(e => new RewindSeriesEventDto(e.SeriesId, e.SeriesTitle, Local(e.Timestamp)))
            .ToList();

        // ---- dropped (computed from ReadingState, not an event — self-heals on resume) ----
        var staleBefore = clock.GetUtcNow().UtcDateTime - DroppedAfter;
        var dropped = (await db.ReadingStates.AsNoTracking()
                .Where(r => !r.Finished && r.MaxChapter > 0 &&
                            r.LastProgressAt < staleBefore &&
                            r.LastProgressAt >= utcStart && r.LastProgressAt < utcEnd)
                .ToListAsync(ct))
            .OrderBy(r => r.LastProgressAt)
            .Select(r => new RewindDroppedSeriesDto(r.SeriesId, r.Title, Local(r.LastProgressAt), r.MaxChapter))
            .ToList();

        var readTrackingAvailable =
            !string.IsNullOrWhiteSpace(await appSettings.GetAsync(SettingKeys.KavitaUrl, ct)) &&
            !string.IsNullOrWhiteSpace(await appSettings.GetAsync(SettingKeys.KavitaApiKey, ct));

        return new RewindStatsDto(
            from, to, readTrackingAvailable,
            new RewindTotalsDto(
                Sum(StatsEventType.ChaptersRead),
                Sum(StatsEventType.VolumesRead),
                Sum(StatsEventType.ChapterDownloaded),
                Count(StatsEventType.SeriesAdded),
                Count(StatsEventType.SeriesRemoved),
                Count(StatsEventType.SeriesFinished),
                dropped.Count),
            timeline,
            topRead,
            leastRead,
            Top(genreWeights),
            Top(tagWeights),
            EventList(StatsEventType.SeriesFinished),
            EventList(StatsEventType.SeriesAdded),
            EventList(StatsEventType.SeriesRemoved),
            dropped);
    }

    private sealed record RemovedSeriesSnapshot(
        [property: System.Text.Json.Serialization.JsonPropertyName("genres")] List<string>? Genres,
        [property: System.Text.Json.Serialization.JsonPropertyName("tags")] List<string>? Tags);
}
