using System.Globalization;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// The two opt-in reading status rules: put a Reading series on hold, or drop it, once the reader has
/// gone a set number of days without reading it while something was waiting to be read.
/// <para>
/// The clock starts at the latest of: the last read, the last change the reader made by hand, and the
/// moment the oldest unread downloaded chapter arrived. A series the reader is caught up on is never
/// touched, and when a chapter finally lands after months of waiting the clock starts then, not at
/// the last read. A change a rule made does not restart the clock, so a series paused at 30 days is
/// still dropped at 90.
/// </para>
/// <para>
/// Drop applies to Reading series and to ones the hold rule paused, never to a series the reader put
/// on hold by hand. A read moves either status back to Reading (<c>MakiDbContext</c>).
/// </para>
/// </summary>
public class ReadingStatusAutoService(
    IServiceScopeFactory scopes, ScrobbleService scrobbler, TimeProvider clock,
    ILogger<ReadingStatusAutoService> logger)
{
    public const int MaxDays = 3650;

    /// <summary>How a change reaches the trackers; replaced in tests.</summary>
    internal Action<int, IReadOnlyList<Series>, ReadingStatus> QueuePush { get; init; } =
        (userId, series, status) => scrobbler.QueueStatusPush(userId, series, status);

    public static int? ParseDays(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var days) && days is >= 1 and <= MaxDays
            ? days
            : null;

    /// <summary>Applies both rules for every reader who switched either on.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        List<(int UserId, string Key, string Value)> settings;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
            settings = (await db.UserSettings.AsNoTracking().IgnoreQueryFilters()
                    .Where(s => s.Key == SettingKeys.ReadingAutoHoldDays || s.Key == SettingKeys.ReadingAutoDropDays)
                    .Select(s => new { s.UserId, s.Key, s.Value })
                    .ToListAsync(ct))
                .Select(s => (s.UserId, s.Key, s.Value))
                .ToList();
        }

        foreach (var user in settings.GroupBy(s => s.UserId))
        {
            var hold = ParseDays(user.FirstOrDefault(s => s.Key == SettingKeys.ReadingAutoHoldDays).Value);
            var drop = ParseDays(user.FirstOrDefault(s => s.Key == SettingKeys.ReadingAutoDropDays).Value);
            if (hold is null && drop is null)
            {
                continue;
            }

            try
            {
                await RunForUserAsync(user.Key, hold, drop, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Reading status rules failed for user {UserId}", user.Key);
            }
        }
    }

    /// <summary>One reader's pass. Returns the series it put on hold and the ones it dropped.</summary>
    internal async Task<(List<int> Held, List<int> Dropped)> RunForUserAsync(
        int userId, int? holdDays, int? dropDays, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        var allRootFolders = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.AllRootFolders)
            .FirstOrDefaultAsync(ct);
        db.Scope.SetUser(userId, allRootFolders);

        var candidates = await db.UserSeriesStates.AsNoTracking()
            .Where(s => s.Status == ReadingStatus.Reading || (s.Status == ReadingStatus.OnHold && s.StatusAuto))
            .Join(db.Series.Where(x => x.ArchivedAt == null), s => s.SeriesId, x => x.Id, (s, _) => s)
            .Select(s => new { s.SeriesId, s.Status, s.StatusAuto, s.StatusChangedAt })
            .ToListAsync(ct);
        if (candidates.Count == 0)
        {
            return ([], []);
        }

        var ids = candidates.Select(c => c.SeriesId).ToList();
        var lastRead = (await db.ChapterProgress.AsNoTracking()
                .Where(p => ids.Contains(p.SeriesId) && !p.Watched)
                .GroupBy(p => p.SeriesId)
                .Select(g => new { SeriesId = g.Key, At = g.Max(p => p.UpdatedAt) })
                .ToListAsync(ct))
            .ToDictionary(x => x.SeriesId, x => x.At);
        foreach (var mark in await db.ReadingStates.AsNoTracking()
                     .Where(r => r.SeriesId != null && ids.Contains(r.SeriesId.Value))
                     .Select(r => new { SeriesId = r.SeriesId!.Value, r.LastProgressAt })
                     .ToListAsync(ct))
        {
            if (!lastRead.TryGetValue(mark.SeriesId, out var at) || mark.LastProgressAt > at)
            {
                lastRead[mark.SeriesId] = mark.LastProgressAt;
            }
        }

        var next = await scope.ServiceProvider.GetRequiredService<ContinueReadingService>().NextForAsync(ids, ct);
        var unreadIds = next.Values.SelectMany(n => n.UnreadChapterIds).ToList();
        var firstWaiting = unreadIds.Count == 0
            ? []
            : (await db.Chapters.AsNoTracking()
                .Where(c => unreadIds.Contains(c.Id) && c.ChapterFile != null)
                .Select(c => new { c.SeriesId, c.ChapterFile!.DateAdded })
                .ToListAsync(ct))
            .GroupBy(c => c.SeriesId)
            .ToDictionary(g => g.Key, g => g.Min(c => c.DateAdded));

        var now = clock.GetUtcNow().UtcDateTime;
        List<int> held = [], dropped = [];
        foreach (var c in candidates)
        {
            if (!firstWaiting.TryGetValue(c.SeriesId, out var waitingSince))
            {
                continue; // caught up, or nothing on disk to read
            }

            var since = waitingSince;
            if (lastRead.TryGetValue(c.SeriesId, out var read) && read > since)
            {
                since = read;
            }

            if (!c.StatusAuto && c.StatusChangedAt is { } changed && changed > since)
            {
                since = changed;
            }

            var idle = now - since;
            if (dropDays is { } d && idle >= TimeSpan.FromDays(d))
            {
                dropped.Add(c.SeriesId);
            }
            else if (holdDays is { } h && c.Status == ReadingStatus.Reading && idle >= TimeSpan.FromDays(h))
            {
                held.Add(c.SeriesId);
            }
        }

        var statuses = scope.ServiceProvider.GetRequiredService<ReadingStatusService>();
        var changedHeld = await statuses.SetAsync(userId, held, ReadingStatus.OnHold, auto: true, ct);
        var changedDropped = await statuses.SetAsync(userId, dropped, ReadingStatus.Dropped, auto: true, ct);
        if (changedHeld.Count + changedDropped.Count > 0)
        {
            logger.LogInformation("Reading status rules put {Held} series on hold and dropped {Dropped} for user {UserId}",
                changedHeld.Count, changedDropped.Count, userId);
            await PushAsync(db, userId, changedHeld, ReadingStatus.OnHold, ct);
            await PushAsync(db, userId, changedDropped, ReadingStatus.Dropped, ct);
        }

        return (changedHeld, changedDropped);
    }

    private async Task PushAsync(MakiDbContext db, int userId, List<int> seriesIds, ReadingStatus status, CancellationToken ct)
    {
        if (seriesIds.Count == 0)
        {
            return;
        }

        var series = await db.Series.AsNoTracking().Where(s => seriesIds.Contains(s.Id)).ToListAsync(ct);
        QueuePush(userId, series, status);
    }
}
