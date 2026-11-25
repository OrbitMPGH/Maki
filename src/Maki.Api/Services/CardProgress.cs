using Maki.Core.Entities;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// What the progress bar on a library cover leads with (<see cref="Maki.Core.Configuration.SettingKeys.UiCardProgress"/>).
/// Maki manages first and reads second, and many people read in another app, so downloads lead
/// unless the reader actually uses Maki's own reader.
/// </summary>
public static class CardProgress
{
    public const string Auto = "auto";
    public const string Downloads = "downloads";
    public const string Reading = "reading";

    /// <summary>Days of built-in reading in the window that make "auto" pick reading.</summary>
    public const int ReaderDays = 3;

    /// <summary>How far back "auto" looks.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(30);

    /// <summary>Anything unknown, including unset, reads as <see cref="Auto"/>.</summary>
    public static string Normalize(string? value) => value is Downloads or Reading ? value : Auto;

    /// <summary>
    /// "reading" when the user read in Maki's reader on at least <see cref="ReaderDays"/> different
    /// days in the last <see cref="Window"/>, else "downloads". Days are the user's local days
    /// (<paramref name="zone"/>), like every other day-based stat, so a session across UTC midnight
    /// counts once. Counted from
    /// <see cref="StatsEventType.ReadingTime"/>, which only the built-in reader emits: read progress
    /// itself also arrives from Kavita and so cannot tell the two apart.
    /// </summary>
    public static async Task<string> ResolveAutoAsync(
        MakiDbContext db, int userId, TimeZoneInfo zone, DateTime nowUtc, CancellationToken ct)
    {
        var since = nowUtc - Window;
        var stamps = await db.VisibleStatsEvents()
            .AsNoTracking()
            .Where(e => e.UserId == userId && e.Type == StatsEventType.ReadingTime && e.Timestamp >= since)
            .Select(e => e.Timestamp)
            .ToListAsync(ct);
        var days = stamps
            .Select(t => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(t, DateTimeKind.Utc), zone).Date)
            .Distinct()
            .Count();
        return days >= ReaderDays ? Reading : Downloads;
    }
}
