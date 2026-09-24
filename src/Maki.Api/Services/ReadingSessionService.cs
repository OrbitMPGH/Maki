using Maki.Core.Entities;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Stitches the built-in reader's time reports into <see cref="ReadingSession"/> rows.
/// <para>
/// This happens server-side, by gap, because the client has nothing better to offer: its reading
/// clock resets on every chapter change and it sends no session id, so a sitting that spans ten
/// chapters arrives as ten unrelated streams of deltas. A report landing within
/// <see cref="Gap"/> of the user's latest sitting extends it, anything later starts a new one.
/// </para>
/// <para>
/// Two tabs reporting at once can both read the same latest row and one extension overwrites the
/// other, or both insert. That race is accepted: the cost is a few seconds of active time or a
/// split sitting, and closing it would need a lock on every page turn.
/// </para>
/// </summary>
public class ReadingSessionService(MakiDbContext db)
{
    /// <summary>
    /// Silence longer than this ends a sitting. The client heartbeats every 60s and goes idle after
    /// five minutes, so ten leaves room for a short break without merging separate sittings.
    /// </summary>
    internal static readonly TimeSpan Gap = TimeSpan.FromMinutes(GapMinutes);

    internal const int GapMinutes = 10;

    public async Task RecordAsync(int userId, int seconds, bool completedChapter, DateTime nowUtc,
        CancellationToken ct)
    {
        if (seconds <= 0 && !completedChapter)
        {
            return;
        }

        // Explicit user and no filters: the ambient scope is not always the reader's own.
        var latest = await db.ReadingSessions
            .IgnoreQueryFilters()
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.EndedAt)
            .FirstOrDefaultAsync(ct);

        var landed = Stitch(latest, userId, seconds, completedChapter, nowUtc);
        if (landed is null)
        {
            return;
        }

        if (!ReferenceEquals(landed, latest))
        {
            db.ReadingSessions.Add(landed);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Applies one report of <paramref name="seconds"/> ending at <paramref name="nowUtc"/> to the
    /// user's latest sitting. Returns <paramref name="latest"/> when extended, a new unsaved row when
    /// the gap was too long, or null when there was nothing to record (a zero-second completion
    /// with no sitting close enough to credit it to).
    /// </summary>
    internal static ReadingSession? Stitch(ReadingSession? latest, int userId, int seconds,
        bool completedChapter, DateTime nowUtc)
    {
        seconds = Math.Max(0, seconds);
        var start = nowUtc.AddSeconds(-seconds);

        if (latest is not null && latest.EndedAt >= start - Gap)
        {
            if (nowUtc > latest.EndedAt)
            {
                latest.EndedAt = nowUtc;
            }

            // Overlapping reports (two tabs, backfill chunks) must widen the span too, or active
            // time could outgrow it.
            if (start < latest.StartedAt)
            {
                latest.StartedAt = start;
            }

            latest.ActiveSeconds += seconds;
            if (completedChapter)
            {
                latest.ChaptersCompleted++;
            }

            return latest;
        }

        if (seconds == 0)
        {
            return null;
        }

        return new ReadingSession
        {
            UserId = userId,
            StartedAt = start,
            EndedAt = nowUtc,
            ActiveSeconds = seconds,
            ChaptersCompleted = completedChapter ? 1 : 0
        };
    }
}
