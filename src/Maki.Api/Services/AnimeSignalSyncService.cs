using System.Collections.Concurrent;
using System.Globalization;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Scrobbling;
using Maki.Data;
using Maki.Metadata.MangaBaka;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <param name="Fetched">Entries read off the trackers.</param>
/// <param name="Matched">Entries that now carry a MangaBaka id.</param>
/// <param name="Removed">Stored rows the tracker no longer lists.</param>
/// <param name="Looked">Per-anime relation lookups spent, which is the expensive number.</param>
/// <param name="LookupFailures">
/// Per-anime relation lookups that timed out or hit a network error rather than answering. Those
/// rows are left unstamped so the next pass retries them, and are not counted in <see cref="Looked"/>.
/// </param>
public record AnimeSignalSyncSummary(
    int Fetched, int Matched, int Removed, int Looked, string? Error = null, int LookupFailures = 0);

/// <summary>How far one user's in-flight pass has gotten through its relation lookups.</summary>
public record AnimeSignalSyncProgress(int Looked, int Total);

/// <summary>
/// Pulls each opted-in user's anime list off their connected trackers, matches every entry to the
/// manga it was adapted from, and stores the result for <see cref="SeedWeightService"/> to read.
/// <para>
/// Shaped like <see cref="ScrobbleService"/>'s tick on purpose: a Quartz trigger fires often and
/// this decides whether the interval has elapsed, so changing the interval takes effect without a
/// restart and the manual endpoint can force one pass.
/// </para>
/// </summary>
public class AnimeSignalSyncService(
    IServiceScopeFactory scopeFactory,
    AnimeSignalSources animeSources,
    MangaBakaLocalStore store,
    IAppSettings settings,
    IUserSettingsStore userSettings,
    ILogger<AnimeSignalSyncService> logger)
{
    public const int DefaultIntervalHours = 24;

    /// <summary>
    /// How long a per-anime relation lookup waits before the next one. MyAnimeList has no published
    /// rate limit and an unthrottled few hundred requests is what gets an app registration blocked,
    /// so a new list costs minutes rather than seconds. Only new entries pay it.
    /// </summary>
    private static readonly TimeSpan LookupDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>How many per-anime lookups one user's pass may spend, so a 2,000-entry list lands over several runs.</summary>
    private const int MaxLookupsPerPass = 300;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly HashSet<int> _running = [];
    private readonly ConcurrentDictionary<int, AnimeSignalSyncProgress> _progress = new();

    public bool IsRunning(int userId)
    {
        lock (_running)
        {
            return _running.Contains(userId);
        }
    }

    /// <summary>How far a user's in-flight pass has gotten, or null when nothing is running.</summary>
    public AnimeSignalSyncProgress? Progress(int userId) => _progress.GetValueOrDefault(userId);

    public async Task<bool> EnabledAsync(CancellationToken ct = default) =>
        await settings.GetAsync(SettingKeys.RecommendationsAnimeSignals, ct) != "false";

    public async Task<bool> EnabledForAsync(int userId, CancellationToken ct = default) =>
        await EnabledAsync(ct) &&
        await userSettings.GetAsync(userId, SettingKeys.RecommendationsAnimeSignalsEnabled, ct) == "true";

    public async Task<DateTime?> LastSyncAtAsync(int userId, CancellationToken ct = default) =>
        ParseUtc(await userSettings.GetAsync(userId, SettingKeys.RecommendationsAnimeSignalsLastSync, ct));

    public async Task<int> IntervalHoursAsync(CancellationToken ct = default) =>
        int.TryParse(await settings.GetAsync(SettingKeys.RecommendationsAnimeSignalsIntervalHours, ct),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) && h >= 1
            ? h
            : DefaultIntervalHours;

    /// <summary>Runs a pass over every opted-in user when forced, or when the interval has elapsed.</summary>
    public async Task TickAsync(bool force, CancellationToken ct = default)
    {
        if (!await EnabledAsync(ct))
        {
            return;
        }

        if (!force)
        {
            var last = ParseUtc(await settings.GetAsync(SettingKeys.RecommendationsAnimeSignalsLastSyncAt, ct));
            if (last is { } at && DateTime.UtcNow - at < TimeSpan.FromHours(await IntervalHoursAsync(ct)))
            {
                return;
            }
        }

        if (!await _lock.WaitAsync(0, ct))
        {
            return;
        }

        try
        {
            var users = await OptedInUserIdsAsync(ct);
            var completed = 0;
            foreach (var userId in users)
            {
                try
                {
                    await SyncUserAsync(userId, ct);
                    completed++;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Anime signal sync failed for user {UserId}", userId);
                }
            }

            // Only when something actually ran. Stamping after a pass where every user threw would
            // put the whole instance behind a 24-hour gate on the strength of an outage, and the
            // retry nobody asked for would be a day away.
            if (users.Count == 0 || completed > 0)
            {
                await settings.SetAsync(SettingKeys.RecommendationsAnimeSignalsLastSyncAt,
                    DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), ct);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<int>> OptedInUserIdsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        return await db.UserSettings
            .Where(x => x.Key == SettingKeys.RecommendationsAnimeSignalsEnabled && x.Value == "true")
            .Select(x => x.UserId)
            .Distinct()
            .Where(id => id != 0)
            .Order()
            .ToListAsync(ct);
    }

    /// <summary>
    /// One user's whole pass: fetch, upsert, resolve relations for rows that were never tried, then
    /// resolve the catalogue ids in batch.
    /// </summary>
    public async Task<AnimeSignalSyncSummary> SyncUserAsync(int userId, CancellationToken ct = default)
    {
        lock (_running)
        {
            if (!_running.Add(userId))
            {
                return new AnimeSignalSyncSummary(0, 0, 0, 0, "already running");
            }
        }

        try
        {
            return await RunAsync(userId, ct);
        }
        finally
        {
            lock (_running)
            {
                _running.Remove(userId);
            }

            _progress.TryRemove(userId, out _);
        }
    }

    private async Task<AnimeSignalSyncSummary> RunAsync(int userId, CancellationToken ct)
    {
        if (!await EnabledForAsync(userId, ct))
        {
            return new AnimeSignalSyncSummary(0, 0, 0, 0, "disabled");
        }

        var sources = await animeSources.EnabledAsync(userId, ct);
        if (sources.Count == 0)
        {
            return new AnimeSignalSyncSummary(0, 0, 0, 0, "no connected tracker");
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        db.Scope.SetUser(userId, true);

        // A tracker the reader switched off takes its rows with it, the same way opting out of the
        // whole feature does. Grouping would otherwise keep averaging a list they asked to stop
        // using, and the panel would keep listing it, until they happened to reconnect.
        var enabledNames = sources.Select(AnimeSignalSources.NameOf).ToHashSet(StringComparer.Ordinal);
        var orphaned = await db.AnimeSignals
            .Where(x => x.UserId == userId && !enabledNames.Contains(x.Service))
            .ToListAsync(ct);
        if (orphaned.Count > 0)
        {
            db.AnimeSignals.RemoveRange(orphaned);
            await db.SaveChangesAsync(ct);
        }

        var now = DateTime.UtcNow;
        var fetched = 0;
        var removed = orphaned.Count;
        var looked = 0;
        var lookupFailures = 0;
        var failed = false;
        var fetchedAny = false;
        var progressLooked = 0;
        var progressTotal = 0;

        foreach (var source in sources)
        {
            var service = AnimeSignalSources.NameOf(source);
            IReadOnlyList<AnimeListEntry> entries;
            try
            {
                entries = await source.ListAnimeAsync(userId, ct);
            }
            catch (TrackerException ex)
            {
                logger.LogWarning(ex, "Could not read the {Service} anime list for user {UserId}", service, userId);
                failed = true;
                continue;
            }
            catch (Exception ex) when (IsTransportFailure(ex, ct))
            {
                logger.LogWarning(ex, "Could not read the {Service} anime list for user {UserId}: {Message}",
                    service, userId, ex.Message);
                failed = true;
                continue;
            }

            fetchedAny = true;
            fetched += entries.Count;
            var existing = await db.AnimeSignals
                .Where(x => x.UserId == userId && x.Service == service)
                .ToDictionaryAsync(x => x.AnimeId, ct);

            foreach (var entry in entries)
            {
                // An unscored entry carries no opinion: it neither seeds (unscored Completed) nor
                // avoids (unscored Dropped), so it is not worth storing at all. Dropping it here
                // rather than in AnimeSignalPolicy means a reader who later scores it just gets
                // picked up on the next sync like any other new entry.
                if (entry.Score is null)
                {
                    continue;
                }

                if (!existing.TryGetValue(entry.AnimeId, out var row))
                {
                    row = new AnimeSignal { UserId = userId, Service = service, AnimeId = entry.AnimeId };
                    db.AnimeSignals.Add(row);
                    existing[entry.AnimeId] = row;
                }

                row.Title = entry.Title;
                row.Score = entry.Score;
                row.Status = entry.Status;
                row.UpdatedAtUtc = now;
                // Outside the RelationsResolved branch: this is the anime's own cross-reference,
                // not a manga relation, and an existing row from before the column existed has to
                // pick it up on an ordinary refresh or it never dedupes against the other tracker.
                row.MalAnimeId = entry.MalAnimeId ?? row.MalAnimeId;
                if (entry.RelationsResolved)
                {
                    row.AniListMangaId = entry.AniListMangaId;
                    row.MalMangaId = entry.MalMangaId;
                    row.MatchAttemptedAtUtc = now;
                }
            }

            // A row the tracker no longer lists, or now lists unscored, is treated the same way: not
            // stored. An empty or failed fetch threw above rather than reaching this.
            var listed = entries.Where(e => e.Score is not null).Select(e => e.AnimeId).ToHashSet();
            foreach (var row in existing.Where(x => !listed.Contains(x.Key)).Select(x => x.Value))
            {
                if (row.Id != 0)
                {
                    db.AnimeSignals.Remove(row);
                    removed++;
                }
            }

            await db.SaveChangesAsync(ct);

            // Only rows nobody has asked about yet. A re-sync of a list whose entries were all
            // looked up last week costs one list call and no lookups at all, which is the whole
            // point of storing MatchAttemptedAtUtc.
            var unresolved = await db.AnimeSignals
                .Where(x => x.UserId == userId && x.Service == service && x.MatchAttemptedAtUtc == null)
                .OrderBy(x => x.AnimeId)
                .Take(MaxLookupsPerPass)
                .ToListAsync(ct);
            progressTotal += unresolved.Count;
            if (unresolved.Count > 0)
            {
                _progress[userId] = new AnimeSignalSyncProgress(progressLooked, progressTotal);
            }

            var consecutiveLookupFailures = 0;
            for (var i = 0; i < unresolved.Count; i++)
            {
                var row = unresolved[i];
                ct.ThrowIfCancellationRequested();
                if (i > 0)
                {
                    await Task.Delay(LookupDelay, ct);
                }

                try
                {
                    var related = await source.RelatedMangaAsync(userId, row.AnimeId, ct);
                    row.AniListMangaId = related?.AniListMangaId;
                    row.MalMangaId = related?.MalMangaId;
                }
                catch (TrackerException ex)
                {
                    logger.LogDebug(ex, "No manga relation for {Service} anime {AnimeId}", service, row.AnimeId);
                    row.MatchAttemptedAtUtc = DateTime.UtcNow;
                    looked++;
                    await FinishLookupAsync();
                    continue;
                }
                catch (Exception ex) when (IsTransportFailure(ex, ct))
                {
                    // A slow or unreachable provider, not "this anime has no manga". Leaving
                    // MatchAttemptedAtUtc unset means the next pass tries this row again instead of
                    // silently giving up on it for good.
                    logger.LogWarning("No manga relation for {Service} anime {AnimeId}: {Message}",
                        service, row.AnimeId, ex.Message);
                    lookupFailures++;
                    consecutiveLookupFailures++;
                    if (consecutiveLookupFailures >= 3)
                    {
                        logger.LogInformation(
                            "Stopping {Service} relation lookups for user {UserId} after 3 consecutive failures",
                            service, userId);
                        await FinishLookupAsync();
                        break;
                    }

                    await FinishLookupAsync();
                    continue;
                }

                consecutiveLookupFailures = 0;
                row.MatchAttemptedAtUtc = DateTime.UtcNow;
                looked++;
                await FinishLookupAsync();

                // Saves the row, advances the progress counter, and resolves catalogue ids every 10
                // lookups so a matched title shows up in the panel while the pass is still running -
                // otherwise a 300-lookup pass looks frozen for as long as it takes.
                async Task FinishLookupAsync()
                {
                    await db.SaveChangesAsync(ct);
                    progressLooked++;
                    _progress[userId] = new AnimeSignalSyncProgress(progressLooked, progressTotal);
                    if (progressLooked % 10 == 0)
                    {
                        await ResolveCatalogueIdsAsync(db, userId, ct);
                    }
                }
            }
        }

        var matched = await ResolveCatalogueIdsAsync(db, userId, ct);
        // A failed list fetch leaves the stored rows untouched and unrefreshed, so calling that a
        // sync would hide a broken token behind a fresh-looking timestamp on the panel.
        if (fetchedAny && !failed)
        {
            await userSettings.SetAsync(userId, SettingKeys.RecommendationsAnimeSignalsLastSync,
                DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), ct);
        }
        logger.LogInformation(
            "Anime signals for user {UserId}: {Fetched} entries, {Matched} matched, {Removed} removed, " +
            "{Looked} lookups, {LookupFailures} lookup failures",
            userId, fetched, matched, removed, looked, lookupFailures);
        return new AnimeSignalSyncSummary(fetched, matched, removed, looked,
            failed ? "one or more lists could not be read" : null, lookupFailures);
    }

    /// <summary>
    /// A slow or unreachable provider, not a real failure of this pass: one MAL relation lookup can
    /// hang its own HttpClient timeout (<see cref="TaskCanceledException"/> wrapping a
    /// <see cref="TimeoutException"/>) or drop the connection (<see cref="HttpRequestException"/>),
    /// and neither should take the rest of the list, or the request handling the manual "Sync now",
    /// down with it. A real cancellation of <paramref name="ct"/> must still propagate.
    /// </summary>
    private static bool IsTransportFailure(Exception ex, CancellationToken ct) =>
        ex switch
        {
            HttpRequestException => true,
            OperationCanceledException => !ct.IsCancellationRequested,
            _ => false,
        };

    /// <summary>
    /// Turns the provider manga ids on every row into catalogue ids, in two batched reads of the
    /// dump rather than one per entry. Runs over the whole stored set, not just this pass's rows,
    /// so a dump refresh that added a cross-reference picks up old rows too.
    /// </summary>
    private async Task<int> ResolveCatalogueIdsAsync(MakiDbContext db, int userId, CancellationToken ct)
    {
        var rows = await db.AnimeSignals
            .Where(x => x.UserId == userId && (x.AniListMangaId != null || x.MalMangaId != null))
            .ToListAsync(ct);
        if (rows.Count == 0)
        {
            return 0;
        }

        var byAniList = await store.GetIdsByExternalIdsAsync(
            MangaBakaLocalStore.ExternalSource.AniList,
            rows.Where(x => x.AniListMangaId is > 0).Select(x => x.AniListMangaId!.Value).ToList(), ct);
        var byMal = await store.GetIdsByExternalIdsAsync(
            MangaBakaLocalStore.ExternalSource.MyAnimeList,
            rows.Where(x => x.MalMangaId is > 0).Select(x => x.MalMangaId!.Value).ToList(), ct);

        var matched = 0;
        foreach (var row in rows)
        {
            long? resolved = null;
            if (row.AniListMangaId is { } al && byAniList.TryGetValue(al, out var fromAniList))
            {
                resolved = fromAniList;
            }
            else if (row.MalMangaId is { } mal && byMal.TryGetValue(mal, out var fromMal))
            {
                resolved = fromMal;
            }

            if (row.MangaBakaId != resolved)
            {
                row.MangaBakaId = resolved;
            }

            if (resolved is not null)
            {
                matched++;
            }
        }

        await db.SaveChangesAsync(ct);
        return matched;
    }

    private static DateTime? ParseUtc(string? raw) =>
        DateTime.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : null;
}
