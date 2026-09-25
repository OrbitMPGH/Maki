using System.Text.RegularExpressions;
using Maki.Core.Entities;

namespace Maki.Core.Recommendations;

public enum AnimeResumeBasis { AllSeasons, SeasonCount }

/// <param name="AnimeTitle">Title of the last TV entry the frontier rests on, so the callout names the anime it trusted.</param>
/// <param name="CoveredTo">Last chapter the watched anime adapts. Reading resumes after it.</param>
/// <param name="NextLabel">The first season span the reader has not finished, when they are not caught up.</param>
public record AnimeResume(
    string AnimeTitle,
    IReadOnlyList<string> Services,
    double? Score,
    AnimeResumeBasis Basis,
    decimal CoveredTo,
    string? CoveredLabel,
    string? NextLabel,
    decimal? NextFrom,
    decimal? NextTo);

/// <summary>
/// Works out which manga chapter the reader's watched anime ends at, by lining up their finished TV
/// entries against MangaBaka's season spans. Every uncertain case returns null or a lower chapter:
/// telling someone to skip chapters the anime never covered is worse than showing nothing.
/// </summary>
public static class AnimeResumeResolver
{
    private static readonly string[] TvFormats = ["TV", "TV_SHORT", "ONA"];
    private static readonly string[] FilmFormats = ["MOVIE", "OVA"];
    private const int SplitCourGapDays = 31;

    private static readonly Regex PartNumber = new(
        @"(?<![a-z])(?:p|part\s*|cour\s*)(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private sealed class TvGroup
    {
        public List<AnimeWatchedSeason> Parts { get; } = [];
        public AnimeWatchedSeason Last => Parts[^1];
        public bool Finished => Parts.All(IsFinished);
    }

    private record Frontier(decimal CoveredTo, string? CoveredLabel);

    public static AnimeResume? Resolve(
        IReadOnlyList<AnimeWatchedSeason> seasons,
        IReadOnlyList<AnimeSpan> spans,
        decimal? lastKnownChapter)
    {
        var seasonSpans = SeasonSpans(spans);
        if (seasonSpans.Count == 0) return null;

        var groups = TvGroups(seasons);
        if (groups is null) return null;

        var slotEnds = SlotEnds(seasonSpans);
        if (groups.Count > slotEnds[^1]) return null;

        var k = FinishedPrefixLength(groups);
        if (k == 0) return null;

        var caughtUp = k >= slotEnds[^1];
        if (!caughtUp && HasOverlap(seasonSpans)) return null;

        Frontier? frontier;
        AnimeSpan? next = null;
        var backing = k;
        if (caughtUp)
        {
            frontier = CaughtUpFrontier(seasonSpans);
        }
        else
        {
            var j = 0;
            while (slotEnds[j] <= k) j++;
            if (j == 0) return null;

            var covered = seasonSpans[j - 1];
            frontier = covered.OpenEnded || covered.To is null ? null : new Frontier(covered.To.Value, covered.Label);
            next = seasonSpans[j];
            backing = slotEnds[j - 1];
        }
        if (frontier is null) return null;

        var lastFinished = groups[backing - 1].Last;
        var nextTv = backing < groups.Count ? groups[backing].Parts[0] : null;
        frontier = ExtendWithFilms(frontier, spans, seasons, lastFinished, nextTv);

        if (lastKnownChapter is { } known && frontier.CoveredTo > known) return null;

        var finishedRows = groups.Take(backing).SelectMany(g => g.Parts).ToList();
        return new AnimeResume(
            lastFinished.Title,
            ServicesOf(finishedRows),
            AverageScore(finishedRows),
            caughtUp ? AnimeResumeBasis.AllSeasons : AnimeResumeBasis.SeasonCount,
            frontier.CoveredTo,
            frontier.CoveredLabel,
            next?.Label,
            next?.From,
            next?.To);
    }

    private static List<AnimeSpan> SeasonSpans(IReadOnlyList<AnimeSpan> spans)
    {
        var seasons = SpansOfKind(spans, AnimeSpanKind.Season);
        return seasons.Count > 0 ? seasons : SpansOfKind(spans, AnimeSpanKind.Other);
    }

    private static List<AnimeSpan> SpansOfKind(IReadOnlyList<AnimeSpan> spans, AnimeSpanKind kind) =>
        spans.Where(s => s.Kind == kind && s.From is not null).OrderBy(s => s.From).ToList();

    /// <summary>
    /// A span labelled from one part to a later part of the same season, "S3P1" to "S3P2", stands
    /// for that many tracker entries, which aired months apart. Returns each season span's last slot,
    /// counted from 1.
    /// </summary>
    private static int[] SlotEnds(List<AnimeSpan> seasonSpans)
    {
        var ends = new int[seasonSpans.Count];
        var total = 0;
        for (var i = 0; i < seasonSpans.Count; i++)
        {
            total += SlotsOf(seasonSpans[i]);
            ends[i] = total;
        }
        return ends;
    }

    private static int SlotsOf(AnimeSpan span)
    {
        if (span.EndLabel is null || !TryPart(span.Label, out var startSeason, out var startPart)
            || !TryPart(span.EndLabel, out var endSeason, out var endPart))
        {
            return 1;
        }
        if (endPart < startPart || startSeason != endSeason) return 1;
        return endPart - startPart + 1;
    }

    private static bool TryPart(string label, out string season, out int part)
    {
        season = "";
        part = 0;
        var match = PartNumber.Match(label);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out part)) return false;
        season = Regex.Replace(label[..match.Index], @"\s+", " ").Trim().ToLowerInvariant();
        return true;
    }

    /// <summary>
    /// The reader's TV entries in air order, with split cours folded into one entry. Null when any
    /// entry is missing a format or date, a lone one included: until a sync fills it in, it could be
    /// a film or a spinoff as easily as the first season.
    /// </summary>
    private static List<TvGroup>? TvGroups(IReadOnlyList<AnimeWatchedSeason> seasons)
    {
        var rows = seasons.Where(s => s.Format is null || TvFormats.Contains(s.Format)).ToList();
        if (rows.Count == 0) return [];
        if (rows.Any(r => r.Format is null || r.StartDate is null)) return null;

        var groups = new List<TvGroup>();
        foreach (var row in rows.OrderBy(r => r.StartDate))
        {
            var current = groups.Count > 0 ? groups[^1] : null;
            if (current is not null && ContinuesSplitCour(current.Last, row))
            {
                current.Parts.Add(row);
                continue;
            }
            var group = new TvGroup();
            group.Parts.Add(row);
            groups.Add(group);
        }
        return groups;
    }

    private static bool ContinuesSplitCour(AnimeWatchedSeason previous, AnimeWatchedSeason next) =>
        previous.EndDate is { } end && next.StartDate <= end.AddDays(SplitCourGapDays);

    private static bool IsFinished(AnimeWatchedSeason row) =>
        row.Status == AnimeWatchStatus.Completed
        || (row.Status == AnimeWatchStatus.Watching && row.Episodes > 0 && row.Progress >= row.Episodes);

    private static int FinishedPrefixLength(List<TvGroup> groups)
    {
        var k = 0;
        while (k < groups.Count && groups[k].Finished) k++;
        return k;
    }

    /// <summary>
    /// Overlapping season spans mean remakes of the same chapters, so counting seasons says nothing.
    /// Sharing one boundary chapter is not an overlap: MangaBaka splits seasons at a page.
    /// </summary>
    private static bool HasOverlap(List<AnimeSpan> seasonSpans)
    {
        for (var i = 1; i < seasonSpans.Count; i++)
        {
            var previous = seasonSpans[i - 1];
            if (previous.To is null || seasonSpans[i].From < previous.To) return true;
        }
        return false;
    }

    private static Frontier? CaughtUpFrontier(List<AnimeSpan> seasonSpans)
    {
        AnimeSpan? furthest = null;
        foreach (var span in seasonSpans)
        {
            if (span.OpenEnded || span.To is null) return null;
            if (furthest is null || span.To > furthest.To) furthest = span;
        }
        return furthest is null ? null : new Frontier(furthest.To!.Value, furthest.Label);
    }

    /// <summary>
    /// A film that picks up right where the TV run stopped (Mugen Train after Demon Slayer S1) moves
    /// the frontier, but only when the reader finished a movie or OVA that aired between the last
    /// TV entry they finished and the next one.
    /// </summary>
    private static Frontier ExtendWithFilms(
        Frontier frontier,
        IReadOnlyList<AnimeSpan> spans,
        IReadOnlyList<AnimeWatchedSeason> seasons,
        AnimeWatchedSeason lastFinished,
        AnimeWatchedSeason? nextTv)
    {
        if (lastFinished.StartDate is not { } after) return frontier;

        var films = SpansOfKind(spans, AnimeSpanKind.Film);
        var watched = seasons
            .Where(s => IsWatchedFilm(s, after, nextTv?.StartDate))
            .OrderBy(s => s.StartDate)
            .ToList();

        while (watched.Count > 0)
        {
            var film = films.FirstOrDefault(f =>
                !f.OpenEnded && f.To > frontier.CoveredTo
                && f.From >= frontier.CoveredTo && f.From <= frontier.CoveredTo + 1);
            if (film is null) break;

            watched.RemoveAt(0);
            films.Remove(film);
            frontier = new Frontier(film.To!.Value, film.Label);
        }
        return frontier;
    }

    private static bool IsWatchedFilm(AnimeWatchedSeason row, DateOnly after, DateOnly? before) =>
        row.Status == AnimeWatchStatus.Completed
        && row.Format is not null && FilmFormats.Contains(row.Format)
        && row.StartDate is { } start
        && start > after
        && (before is null || start < before);

    private static List<string> ServicesOf(List<AnimeWatchedSeason> rows)
    {
        var services = new List<string>();
        foreach (var row in rows)
        {
            foreach (var service in row.Services)
            {
                if (!services.Contains(service)) services.Add(service);
            }
        }
        return services;
    }

    private static double? AverageScore(List<AnimeWatchedSeason> rows)
    {
        var scores = rows.Where(r => r.Score is not null).Select(r => (double)r.Score!.Value).ToList();
        return scores.Count == 0 ? null : scores.Average();
    }
}
