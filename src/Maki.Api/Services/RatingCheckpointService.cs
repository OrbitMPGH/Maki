using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// When to ask "how is it so far?" (design in <c>docs/design-provisional-ratings.md</c>). A
/// checkpoint is armed only by a genuine chapter completion, never by reading state on a page load:
/// a Kavita or Mihon import, or chapters marked watched from an anime, would otherwise arm every
/// series at once. Each is a condition re-checked on every completion, so one the daily cap held
/// back arms on that series' next completion instead of being lost.
/// <para>
/// Every query runs on the caller's scoped context, so it only ever sees that reader's rows.
/// </para>
/// </summary>
public static class RatingCheckpointService
{
    public const int HookedChapters = 10;
    public const double HookedShare = 0.15;
    public const int HookedMinChapters = 3;
    public const int MaxDismissals = 3;
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(30);
    public const double StaleShare = 0.25;

    /// <summary>
    /// Called after a chapter completion. Arms at most one checkpoint, and never two on one UTC day.
    /// Runs on every completion, so the daily cap, the cheapest check that usually says no, goes first.
    /// </summary>
    /// <param name="completed">The chapter just finished, which decides Volume end; null skips that checkpoint.</param>
    public static async Task ArmAsync(MakiDbContext db, int userId, Series series, DateTime nowUtc,
        CancellationToken ct, Chapter? completed = null)
    {
        if (series.Incognito == IncognitoMode.Full)
        {
            return;
        }

        var today = nowUtc.Date;
        if (await db.UserSeriesStates.AnyAsync(s => s.LastRatingCheckpointAt >= today, ct))
        {
            return;
        }

        var state = await db.UserSeriesStates.FirstOrDefaultAsync(s => s.SeriesId == series.Id, ct);
        if (state is { Rating: not null } ||
            state is { PendingRatingCheckpoint: not RatingCheckpoints.None } ||
            state?.RatingPromptDismissals >= MaxDismissals)
        {
            return;
        }

        var prompts = await db.UserSettings
            .Where(s => s.Key == SettingKeys.RatingPrompts || s.Key == SettingKeys.RatingPromptsVolumes)
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        if (prompts.GetValueOrDefault(SettingKeys.RatingPrompts) == "off" ||
            await FinishedAsync(db, userId, series.Id, ct))
        {
            return;
        }

        var volumes = completed is { Volume: not null } &&
            prompts.GetValueOrDefault(SettingKeys.RatingPromptsVolumes) == "on";
        var checkpoint = await DueAsync(db, userId, series, state, nowUtc, volumes ? completed : null, ct);
        if (checkpoint == RatingCheckpoints.None)
        {
            return;
        }

        if (state is null)
        {
            state = new UserSeriesState { UserId = userId, SeriesId = series.Id };
            db.UserSeriesStates.Add(state);
        }

        state.PendingRatingCheckpoint = checkpoint;
        state.LastRatingCheckpointAt = nowUtc;
        state.UpdatedAt = nowUtc;
        await db.SaveChangesAsync(ct);
    }

    private static async Task<RatingCheckpoints> DueAsync(MakiDbContext db, int userId, Series series,
        UserSeriesState? state, DateTime nowUtc, Chapter? volumeChapter, CancellationToken ct)
    {
        var seen = state?.RatingCheckpointsSeen ?? RatingCheckpoints.None;

        if (!seen.HasFlag(RatingCheckpoints.Hooked) && state?.ProvisionalRating is null)
        {
            var genuine = await ReadCounts.ReadFor(db, userId).CountAsync(p => p.SeriesId == series.Id, ct);
            if (genuine >= HookedThreshold(series.TotalChapters))
            {
                return RatingCheckpoints.Hooked;
            }
        }

        if (state?.LastRatingCheckpointAt is { } last && nowUtc - last < StaleAfter)
        {
            return RatingCheckpoints.None;
        }

        var wanted = await db.Chapters.CountAsync(c => c.SeriesId == series.Id &&
            (c.Wanted || c.ChapterFileId != null || c.FileRemovedAt != null), ct);
        if (wanted == 0)
        {
            return RatingCheckpoints.None;
        }

        var candidate = RatingCheckpoints.None;
        if (series.Status != SeriesStatus.Completed)
        {
            // Position, not evidence: ReadCounts.Read includes watched chapters, the same count the
            // reader's end screen shows, so a reader who watched the anime first can still catch up.
            var read = await ReadCounts.Read(db).CountAsync(p => p.SeriesId == series.Id, ct);
            if (read >= wanted)
            {
                candidate = RatingCheckpoints.CaughtUp;
            }
        }

        if (candidate == RatingCheckpoints.None && volumeChapter is not null &&
            await EndsVolumeAsync(db, series.Id, volumeChapter, ct))
        {
            candidate = RatingCheckpoints.VolumeEnd;
        }

        return candidate != RatingCheckpoints.None && await StaleAsync(db, series.Id, state, wanted, nowUtc, ct)
            ? candidate
            : RatingCheckpoints.None;
    }

    /// <summary>
    /// The chapter is a whole-volume entry, or the highest-numbered chapter its volume has. Chapters
    /// with no volume never end one, which is most webtoons.
    /// </summary>
    private static async Task<bool> EndsVolumeAsync(MakiDbContext db, int seriesId, Chapter chapter,
        CancellationToken ct)
    {
        if (chapter.Volume is not { } volume)
        {
            return false;
        }

        if (chapter.Number is null)
        {
            return Chapter.IsVolumeEntry(chapter);
        }

        // In memory: SQLite cannot aggregate a decimal column in SQL.
        var numbers = await db.Chapters
            .Where(c => c.SeriesId == seriesId && c.Volume == volume && c.Number != null)
            .Select(c => c.Number!.Value)
            .ToListAsync(ct);
        return numbers.Count > 0 && chapter.Number.Value >= numbers.Max();
    }

    /// <summary>
    /// About ten chapters, or 15% of a known total when that comes sooner, never under three.
    /// </summary>
    public static int HookedThreshold(int? totalChapters) =>
        totalChapters is > 0
            ? Math.Max(HookedMinChapters, Math.Min(HookedChapters, (int)Math.Ceiling(totalChapters.Value * HookedShare)))
            : HookedChapters;

    /// <summary>
    /// No "so far" score, one older than 30 days, or one set more than a quarter of the wanted
    /// chapters ago. Measured in chapter numbers, the only position the score records.
    /// </summary>
    private static async Task<bool> StaleAsync(MakiDbContext db, int seriesId, UserSeriesState? state, int wanted,
        DateTime nowUtc, CancellationToken ct)
    {
        if (state?.ProvisionalRating is null || state.ProvisionalRatedAt is not { } ratedAt ||
            nowUtc - ratedAt > StaleAfter || state.ProvisionalAtChapter is not { } at)
        {
            return true;
        }

        var highest = await HighestReadChapterAsync(db, seriesId, ct) ?? at;
        return highest - at >= wanted * StaleShare;
    }

    /// <summary>The highest chapter number the caller has read, watched ones included; null for none.</summary>
    public static async Task<double?> HighestReadChapterAsync(MakiDbContext db, int seriesId, CancellationToken ct)
    {
        // In memory: SQLite cannot aggregate a decimal column in SQL.
        var numbers = await ReadCounts.Read(db)
            .Where(p => p.SeriesId == seriesId)
            .Join(db.Chapters, p => p.ChapterId, c => c.Id, (_, c) => c.Number)
            .Where(n => n != null)
            .ToListAsync(ct);
        return numbers.Count == 0 ? null : (double)numbers.Max()!.Value;
    }

    /// <summary>
    /// The reader finished the series, so a score is a verdict rather than a "so far" one. The flag
    /// sticks through a re-read, which is what keeps re-reading from asking "how is it going?".
    /// </summary>
    public static Task<bool> FinishedAsync(MakiDbContext db, int userId, int seriesId, CancellationToken ct) =>
        db.ReadingStates.AnyAsync(r => r.UserId == userId && r.SeriesId == seriesId && r.Finished, ct);

    /// <summary>The reader rated the series, either kind: the waiting checkpoint is answered.</summary>
    public static void Answered(UserSeriesState state)
    {
        state.RatingCheckpointsSeen |= state.PendingRatingCheckpoint;
        state.PendingRatingCheckpoint = RatingCheckpoints.None;
        state.RatingPromptDismissals = 0;
    }

    public static void Dismissed(UserSeriesState state)
    {
        if (state.PendingRatingCheckpoint == RatingCheckpoints.None)
        {
            return;
        }

        state.RatingCheckpointsSeen |= state.PendingRatingCheckpoint;
        state.PendingRatingCheckpoint = RatingCheckpoints.None;
        state.RatingPromptDismissals++;
    }

    /// <summary>The wire name of a checkpoint: "hooked", "caughtUp", "volumeEnd", or null for none.</summary>
    public static string? WireName(RatingCheckpoints checkpoint) => checkpoint switch
    {
        RatingCheckpoints.Hooked => "hooked",
        RatingCheckpoints.CaughtUp => "caughtUp",
        RatingCheckpoints.VolumeEnd => "volumeEnd",
        _ => null,
    };
}
