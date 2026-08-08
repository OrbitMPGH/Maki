using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Maki.Api.Controllers;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Kavita;
using Maki.Core.Parsing;
using Maki.Core.Scrobbling;
using Maki.Core.Security;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// The scrobble sync engine: polls Kavita for reading progress, resolves each Kavita
/// series to remote ids on the connected trackers, and pushes forward-only progress
/// updates. Native port of MangaScrobbler, better integrated: Kavita connection
/// settings are shared with the scan/metadata push, and series in Maki's own
/// library match instantly via their stored MangaBaka/AniList/MAL cross-ids.
/// </summary>
public class ScrobbleService(
    IServiceScopeFactory scopeFactory,
    SettingsService settings,
    IUserSettingsStore userSettings,
    KavitaUserResolver kavitaUser,
    KavitaClient kavita,
    AniListTracker anilist,
    MalTracker mal,
    MangaBakaTracker mangaBaka,
    KitsuTracker kitsu,
    ILogger<ScrobbleService> logger)
{
    public const int DefaultIntervalMinutes = 30;

    /// <summary>Polite pacing between remote API calls (AniList is the strictest at ~30/min).</summary>
    private static readonly TimeSpan Pace = TimeSpan.FromSeconds(1.2);

    private readonly IScrobbleTracker[] _trackers = [anilist, mal, mangaBaka, kitsu];
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private (DateTime CheckedAt, bool Ok)? _kavitaPing;

    /// <summary>Cached per-archive page-boundary scans, keyed by ChapterFileId; re-scanned when file size changes.</summary>
    private readonly ConcurrentDictionary<int, (long Size, VolumeChapterProgress.ChapterFileBoundaries Boundaries)>
        _volumeBoundaryCache = new();

    /// <summary>
    /// In-flight OAuth sessions: state → (verifier, redirect URI), plus the user who started the flow.
    /// <para>
    /// Carrying <see cref="OAuthSession.UserId"/> is load-bearing. The provider's callback route is
    /// exempt from authentication — the redirect arrives from AniList or MyAnimeList, not from the
    /// SPA — so the only thing that identifies the flow is the random state. Without the id baked into
    /// the session, the callback would have to guess whose account to store the token under.
    /// </para>
    /// </summary>
    public record OAuthSession(
        int UserId, string Service, string State, string CodeVerifier, string RedirectUri, DateTime CreatedAt);

    /// <summary>Keyed <c>(userId, service)</c> so two people can connect the same tracker at once.</summary>
    private readonly ConcurrentDictionary<(int UserId, string Service), OAuthSession> _oauthSessions = new();

    public bool Running { get; private set; }

    public AniListTracker AniList => anilist;
    public MalTracker Mal => mal;
    public MangaBakaTracker MangaBaka => mangaBaka;
    public KitsuTracker Kitsu => kitsu;
    public IReadOnlyList<IScrobbleTracker> Trackers => _trackers;

    public IScrobbleTracker? FindTracker(string service) =>
        _trackers.FirstOrDefault(t => t.Name == service);

    // ---- OAuth session memory ----

    public OAuthSession StartOAuthSession(int userId, string service, string redirectUri)
    {
        var session = new OAuthSession(
            userId,
            service,
            State: Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)),
            CodeVerifier: Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(40)),
            redirectUri,
            DateTime.UtcNow);
        _oauthSessions[(userId, service)] = session;
        return session;
    }

    /// <summary>
    /// Matches a callback to its session by state alone, across users, then returns the session that
    /// names its owner. Scanning is fine: there are at most a handful of flows in flight, each living
    /// fifteen minutes. The state comparison is what authenticates the callback, so it is the only
    /// thing consulted — a caller cannot ask for somebody else's session by claiming to be them.
    /// </summary>
    public OAuthSession? TakeOAuthSession(string service, string state)
    {
        foreach (var (key, session) in _oauthSessions)
        {
            if (key.Service == service && session.State == state &&
                DateTime.UtcNow - session.CreatedAt < TimeSpan.FromMinutes(15))
            {
                _oauthSessions.TryRemove(key, out _);
                return session;
            }
        }

        return null;
    }

    // ---- status ----

    public async Task<bool> KavitaConnectedAsync(CancellationToken ct = default)
    {
        var url = await settings.GetAsync(SettingKeys.KavitaUrl, ct);
        var apiKey = await settings.GetAsync(SettingKeys.KavitaApiKey, ct);
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(apiKey))
        {
            return false;
        }

        if (_kavitaPing is { } cached && DateTime.UtcNow - cached.CheckedAt < TimeSpan.FromSeconds(60))
        {
            return cached.Ok;
        }

        var ok = await kavita.PingAsync(url, apiKey, ct);
        _kavitaPing = (DateTime.UtcNow, ok);
        return ok;
    }

    public async Task<DateTime?> LastSyncAtAsync(CancellationToken ct = default)
    {
        var raw = await settings.GetAsync(SettingKeys.ScrobbleLastSyncAt, ct);
        return DateTime.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : null;
    }

    /// <summary>Per-user, per-tracker toggle: push reading progress to this service? Unset = on.</summary>
    public async Task<bool> SyncReadingEnabledAsync(int userId, string service, CancellationToken ct = default) =>
        await userSettings.GetAsync(userId, SettingKeys.ScrobbleReadingKey(service), ct) != "false";

    /// <summary>Per-user, per-tracker toggle: push ratings to this service? Unset = on.</summary>
    public async Task<bool> SyncRatingsEnabledAsync(int userId, string service, CancellationToken ct = default) =>
        await userSettings.GetAsync(userId, SettingKeys.ScrobbleRatingsKey(service), ct) != "false";

    public async Task<int> IntervalMinutesAsync(CancellationToken ct = default) =>
        int.TryParse(await settings.GetAsync(SettingKeys.ScrobbleIntervalMinutes, ct), out var m) && m >= 5
            ? m
            : DefaultIntervalMinutes;

    // ---- scheduled tick ----

    /// <summary>
    /// Runs a sync when forced, or when the interval has elapsed and there is something to sync
    /// (a silent no-op otherwise so the log isn't spammed). "Something to sync" is Kavita being
    /// configured — its scan feeds ReadingState/StatsEvents for Rewind — or a tracker being
    /// connected, which the built-in reader's own progress is pushed to.
    /// </summary>
    public async Task TickAsync(bool force, CancellationToken ct = default)
    {
        if (!force)
        {
            var last = await LastSyncAtAsync(ct);
            var interval = TimeSpan.FromMinutes(await IntervalMinutesAsync(ct));
            if (last is { } at && DateTime.UtcNow - at < interval)
            {
                return;
            }

            var kavitaConfigured =
                !string.IsNullOrWhiteSpace(await settings.GetAsync(SettingKeys.KavitaUrl, ct)) &&
                !string.IsNullOrWhiteSpace(await settings.GetAsync(SettingKeys.KavitaApiKey, ct));

            // Cheap to evaluate: the token lookup is one query, no network.
            if (!kavitaConfigured && (await ConnectedUserIdsAsync(ct)).Count == 0)
            {
                return;
            }
        }

        await SyncAsync(ct);
    }

    public async Task<List<IScrobbleTracker>> ActiveTrackersAsync(int userId, CancellationToken ct)
    {
        var active = new List<IScrobbleTracker>();
        foreach (var tracker in _trackers)
        {
            if (await tracker.ConfiguredAsync(ct) && await tracker.AuthenticatedAsync(userId, ct))
            {
                active.Add(tracker);
            }
        }

        return active;
    }

    /// <summary>
    /// Users who hold at least one tracker token, plus every user with per-user tracker credentials
    /// that are exchanged lazily rather than stored (Kitsu's password grant, MangaBaka's PAT). One
    /// query over the token table and one over the settings table, not a probe per user per tracker.
    /// </summary>
    private async Task<List<int>> ConnectedUserIdsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();

        var fromTokens = await db.ScrobbleTokens.Select(t => t.UserId).Distinct().ToListAsync(ct);
        var lazyKeys = new[] { SettingKeys.ScrobbleMangaBakaToken, SettingKeys.ScrobbleKitsuEmail };
        var fromSettings = await db.UserSettings
            .Where(x => lazyKeys.Contains(x.Key) && x.Value.Length > 0)
            .Select(x => x.UserId)
            .Distinct()
            .ToListAsync(ct);

        return fromTokens.Union(fromSettings).Where(id => id != 0).Order().ToList();
    }

    // ---- the sync pass ----

    /// <summary>Runs one full sync pass. Returns a human-readable summary.</summary>
    public async Task<string> SyncAsync(CancellationToken ct = default)
    {
        if (!await _syncLock.WaitAsync(0, ct))
        {
            return "sync already running";
        }

        Running = true;
        try
        {
            return await SyncInnerAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scrobble sync crashed");
            await AddLogAsync(0, "error", "", "", $"sync crashed: {ex.Message}", ct);
            return $"sync crashed: {ex.Message}";
        }
        finally
        {
            Running = false;
            await settings.SetAsync(SettingKeys.ScrobbleLastSyncAt,
                DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), CancellationToken.None);
            _syncLock.Release();
        }
    }

    /// <summary>
    /// One sync pass, over both progress sources, for every user who has something to sync.
    /// <para>
    /// The two passes keep their original division of labour. The Kavita pass reads Kavita's marks,
    /// feeds them into <see cref="ReadingProgressService"/> and pushes the merged result; it runs for
    /// exactly one user, because Kavita is one server behind one API key and everything it reports is
    /// that person's reading. The native pass covers the remainder — series the built-in reader tracks
    /// that Kavita has never reported — resolving remote ids straight from the local cross-ids, and it
    /// runs once per user with a connected tracker.
    /// </para>
    /// <para>
    /// A user is included if they have a tracker connected, and the Kavita-bound user is included even
    /// without one: the Kavita scan is what feeds <c>ReadingState</c>/<c>StatsEvents</c>, so Rewind
    /// works with no tracker at all.
    /// </para>
    /// </summary>
    private async Task<string> SyncInnerAsync(CancellationToken ct)
    {
        var kavitaUrl = await settings.GetAsync(SettingKeys.KavitaUrl, ct);
        var kavitaKey = await settings.GetAsync(SettingKeys.KavitaApiKey, ct);
        var kavitaConfigured = !string.IsNullOrWhiteSpace(kavitaUrl) && !string.IsNullOrWhiteSpace(kavitaKey);
        var kavitaUserId = kavitaConfigured ? await kavitaUser.ResolveAsync(ct) : null;

        var connected = await ConnectedUserIdsAsync(ct);
        var users = kavitaUserId is { } bound && !connected.Contains(bound)
            ? [.. connected, bound]
            : connected;

        if (users.Count == 0)
        {
            // Info, not error: with the built-in reader this is an ordinary configuration, and a
            // red log line every forced run would be noise rather than a problem to fix. Logged
            // against no user (0) — nobody is involved, so nobody's log should own it.
            const string idle = "Nothing to sync — connect Kavita or a tracker";
            await AddLogAsync(0, "info", "", "", idle, ct);
            return idle;
        }

        var summaries = new List<string>();
        foreach (var userId in users.Order())
        {
            ct.ThrowIfCancellationRequested();
            summaries.Add(await SyncUserAsync(userId, kavitaUrl, kavitaKey, userId == kavitaUserId, ct));
        }

        var summary = string.Join(" | ", summaries);
        logger.LogInformation("{Summary}", summary);
        return summary;
    }

    /// <summary>One user's share of a sync pass. Their trackers, their marks, their log lines.</summary>
    private async Task<string> SyncUserAsync(
        int userId, string? kavitaUrl, string? kavitaKey, bool ownsKavita, CancellationToken ct)
    {
        var trackers = await ActiveTrackersAsync(userId, ct);
        var pushEnabled = trackers.Count > 0;

        if (!pushEnabled)
        {
            await AddLogAsync(userId, "info", "", "", "No tracker connected — tracking reading stats only", ct);
        }

        var summary = "";

        if (pushEnabled && ownsKavita)
            summary += "Native: ";

        summary += pushEnabled
            ? await NativePassAsync(userId, trackers, ownsKavita, ct)
            : "";

        if (ownsKavita)
        {
            summary += (summary.Length > 0 ? "; Kavita: " : "") + await KavitaPassAsync(userId, kavitaUrl!, kavitaKey!, trackers, pushEnabled, ct);
        }

        await AddLogAsync(userId, "info", "", "", summary, ct);
        return $"user {userId}: {summary}";
    }

    /// <summary>The Kavita-driven pass: read progress from Kavita, merge it, push the result.</summary>
    private async Task<string> KavitaPassAsync(
        int userId, string kavitaUrl, string kavitaKey, List<IScrobbleTracker> trackers, bool pushEnabled,
        CancellationToken ct)
    {
        List<KavitaClient.KavitaSeriesSummary> seriesList;
        try
        {
            seriesList = await kavita.GetAllSeriesAsync(kavitaUrl, kavitaKey, ct);
        }
        catch (Exception e)
        {
            await AddLogAsync(userId, "error", "kavita", "", e.Message, ct);
            return $"Kavita error: {e.Message}";
        }

        var libraryFilter = ParseLibraryIds(await settings.GetAsync(SettingKeys.ScrobbleLibraryIds, ct));
        var planToRead = await userSettings.GetAsync(userId, SettingKeys.ScrobblePlanToRead, ct) == "true";
        var libraryIndex = await BuildLibraryIndexAsync(ct);

        int updates = 0, errors = 0, skipped = 0, noProgress = 0;

        foreach (var series in seriesList)
        {
            ct.ThrowIfCancellationRequested();
            if (libraryFilter.Count > 0 && !libraryFilter.Contains(series.LibraryId))
            {
                continue;
            }

            if (series.Pages <= 0)
            {
                continue;
            }

            var title = series.Name ?? "";

            // Always read chapter-level progress: Kavita's series-level pagesRead
            // aggregate can be stale (often stuck at 0).
            KavitaProgress.SeriesProgress progress;
            List<KavitaProgress.KavitaVolumeDto> volumesRaw;
            try
            {
                volumesRaw = await kavita.GetVolumesAsync(kavitaUrl, kavitaKey, series.Id, ct);
                progress = KavitaProgress.Compute(volumesRaw);
            }
            catch (Exception e)
            {
                logger.LogWarning("Failed to read progress for '{Title}': {Error}", title, e.Message);
                await AddLogAsync(userId, "error", "kavita", title, $"progress read failed: {e.Message}", ct);
                errors++;
                continue;
            }

            var maxChapter = (decimal)progress.MaxChapter;

            // When a Kavita "volume" is actually one of Maki's own multi-chapter
            // archives (import/rescan grouped several Chapters under one ChapterFile),
            // Kavita only reports one pagesRead counter for the whole thing. Refine the
            // chapter number using the page positions where each chapter starts inside
            // that archive, so a partially-read volume still advances scrobbling.
            var localKey = ScrobbleMatching.NormalizeTitle(title);
            if (!libraryIndex.TryGetValue(localKey, out var localSeries) && series.LocalizedName is { } alt)
            {
                libraryIndex.TryGetValue(ScrobbleMatching.NormalizeTitle(alt), out localSeries);
            }

            if (localSeries is not null)
            {
                var boundaries = await VolumeBoundariesAsync(localSeries.Id, ct);
                if (boundaries.Count > 0)
                {
                    maxChapter = VolumeChapterProgress.Refine(volumesRaw, boundaries, maxChapter);
                }

                // Per-chapter read state is what the UI counts, so it has to be written here and
                // not only by the one-off import — otherwise reading done in Kavita after an import
                // never shows up as read. Uses the payload already fetched above; failure is
                // non-fatal, the next tick retries.
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var externalReads = scope.ServiceProvider.GetRequiredService<ExternalReadSyncService>();
                    var marked = await externalReads.MarkAsync(
                        userId, localSeries.Id, ExternalReadSyncService.ReadChapterNumbers(volumesRaw), ct);
                    if (marked > 0)
                    {
                        logger.LogInformation(
                            "Marked {Count} chapter(s) read from Kavita for '{Title}'", marked, title);
                    }
                }
                catch (Exception e)
                {
                    logger.LogWarning(
                        "Could not record Kavita read state for '{Title}': {Error}", title, e.Message);
                }
            }

            // Scrobble the MERGED marks, not Kavita's raw numbers: a series read in Maki's own
            // reader can be well ahead of what Kavita reports, and the trackers would otherwise
            // never hear about it (ScrobblePlanner is forward-only, so nothing would push). The
            // merge is a Math.Max, so for a Kavita-only series this is exactly Kavita's number.
            var merged = new ReadingProgressService.Marks((double)maxChapter, progress.MaxVolume);
            try
            {
                using var scope = scopeFactory.CreateScope();
                var readingProgress = scope.ServiceProvider.GetRequiredService<ReadingProgressService>();
                merged = await readingProgress.TrackKavitaAsync(
                    userId, series.Id, title, localSeries?.Id, (double)maxChapter, progress.MaxVolume, ct);
            }
            catch (Exception e)
            {
                logger.LogWarning("Reading-stats tracking failed for '{Title}': {Error}", title, e.Message);
            }

            var chapter = (int)Math.Floor(merged.MaxChapter);
            var volume = (int)Math.Floor(merged.MaxVolume);

            ScrobbleStatus? fallbackStatus = null;
            if (chapter <= 0 && volume <= 0)
            {
                if (progress.ReadPages > 0)
                {
                    noProgress++;
                    logger.LogInformation("'{Title}': {Pages} pages read but no fully-read chapter/volume",
                        title, progress.ReadPages);
                }

                if (!planToRead)
                {
                    continue;
                }

                // nothing scrobbable yet — list the series as planning/reading
                fallbackStatus = progress.ReadPages > 0 ? ScrobbleStatus.Reading : ScrobbleStatus.PlanToRead;
            }

            if (!pushEnabled)
            {
                continue;
            }

            // ScrobbleOnly/Full incognito: reading stats above (TrackKavitaAsync) already ran,
            // this only withholds the tracker push.
            if (localSeries is { Incognito: not IncognitoMode.Off })
            {
                continue;
            }

            // figure out which trackers actually need an update before doing any
            // remote matching/lookups
            var pending = new List<IScrobbleTracker>();
            foreach (var tracker in trackers)
            {
                // Per-tracker "scrobble reading" toggle — skip pushing progress to a tracker the
                // user turned reading off for (local Rewind stats above are unaffected).
                if (!await SyncReadingEnabledAsync(userId, tracker.Name, ct))
                {
                    continue;
                }

                var state = await GetSyncStateAsync(userId, series.Id, tracker.Name, ct);
                if (state is not null && string.IsNullOrEmpty(state.Error) &&
                    state.Chapter >= chapter && state.Volume >= volume)
                {
                    skipped++;
                    continue;
                }

                pending.Add(tracker);
            }

            if (pending.Count == 0)
            {
                continue;
            }

            List<string> webLinks;
            try
            {
                webLinks = await kavita.GetWebLinksAsync(kavitaUrl, kavitaKey, series.Id, ct);
            }
            catch
            {
                webLinks = [];
            }

            Dictionary<string, string> mappings;
            try
            {
                mappings = await ResolveAsync(userId, series.Id, title, series.LocalizedName, webLinks,
                    pending.Select(t => t.Name).ToList(), libraryIndex, ct);
            }
            catch (Exception e)
            {
                errors++;
                logger.LogWarning("Matching failed for '{Title}': {Error}", title, e.Message);
                await AddLogAsync(userId, "error", "", title, $"matching failed: {e.Message}", ct);
                continue;
            }

            foreach (var tracker in pending)
            {
                if (!mappings.TryGetValue(tracker.Name, out var remoteId))
                {
                    continue;
                }

                try
                {
                    var changed = await PushAsync(userId, tracker, remoteId,
                        new PushTarget(series.Id, localSeries?.Id, title), chapter, volume,
                        fallbackStatus, ct);
                    if (changed)
                    {
                        updates++;
                    }
                }
                catch (TrackerEntryNotFoundException)
                {
                    // The remote id is dead (AniList entry deleted/merged). Drop the stale mapping so
                    // the next sync re-matches by title, instead of hard-erroring on it every pass.
                    await DeleteMappingAsync(userId, series.Id, tracker.Name, ct);
                    await AddLogAsync(userId, "info", tracker.Name, title,
                        $"remote entry {remoteId} not found — mapping cleared, will re-match next sync", ct);
                    logger.LogInformation(
                        "Cleared stale {Service} mapping for '{Title}' (remote id {RemoteId} not found)",
                        tracker.Name, title, remoteId);
                }
                catch (Exception e)
                {
                    errors++;
                    logger.LogWarning("Update failed for '{Title}' on {Service}: {Error}",
                        title, tracker.Name, e.Message);
                    await AddLogAsync(userId, "error", tracker.Name, title, e.Message, ct);
                    await SaveStateAsync(userId, new PushTarget(series.Id, localSeries?.Id, title),
                        tracker.Name, 0, 0, "", e.Message, ct);
                }

                await Task.Delay(Pace, ct);
            }
        }

        return $"kavita: {updates} updated, {skipped} up-to-date, {errors} errors" +
               (noProgress > 0 ? $", {noProgress} with pages read but no fully-read chapter" : "");
    }

    /// <summary>
    /// The built-in reader's pass: pushes progress for series that have a native
    /// <see cref="ReadingState"/> row that the Kavita pass above did not already cover this tick.
    /// <para>
    /// A row's <see cref="ReadingState.KavitaSeriesId"/> is a one-way adoption stamp — once Kavita
    /// reports a series it is set forever, even after Kavita is unconfigured — so excluding every
    /// adopted row unconditionally would silently orphan it: the Kavita pass stops running the
    /// moment <c>ownsKavita</c> is false, and nothing else would ever push that series again even
    /// as the built-in reader keeps advancing it. So the exclusion only applies when
    /// <paramref name="ownsKavita"/> is true for this tick (the Kavita pass just handled those rows);
    /// otherwise every row with progress is fair game here, adopted or not.
    /// </para>
    /// <para>
    /// Remote ids come straight off the series' own MangaBaka/AniList/MAL/Kitsu columns, the same
    /// resolution <see cref="PushRatingAsync"/> uses. There is deliberately no title-search
    /// fallback and no unmatched-review queue here: a series without cross-ids simply isn't
    /// pushed, and the user fixes that through the existing metadata match UI, which is where
    /// those ids come from in the first place.
    /// </para>
    /// </summary>
    private async Task<string> NativePassAsync(
        int userId, List<IScrobbleTracker> trackers, bool ownsKavita, CancellationToken ct)
    {
        List<NativeProgress> rows;
        List<NativeProgress> zeroProgress;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
            rows = await db.ReadingStates
                .Where(r => r.SeriesId != null && r.MaxChapter > 0 && (!ownsKavita || r.KavitaSeriesId == null))
                .Join(db.Series, r => r.SeriesId, s => s.Id, (r, s) => new { r, s })
                .Where(x => x.s.Incognito == IncognitoMode.Off)
                .Select(x => new NativeProgress(
                    x.s.Id, x.s.Title, x.r.MaxChapter, x.r.MaxVolume,
                    x.s.MalId, x.s.AniListId, x.s.MangaBakaId, x.s.KitsuId))
                .ToListAsync(ct);

            // Series a tracker has never heard of yet — no ReadingState row at all, or one still at
            // zero. Mirrors the Kavita pass's fallbackStatus: nothing scrobbable yet, but worth
            // listing as plan-to-read so a newly added series shows up on the tracker immediately
            // instead of only once the user starts reading it. Only skip a Kavita-adopted zero-progress
            // row when the Kavita pass is actually covering it this tick, same reasoning as above.
            var trackedSeriesIds = rows.Select(r => r.SeriesId).ToHashSet();
            var kavitaTrackedSeriesIds = ownsKavita
                ? await db.ReadingStates
                    .Where(r => r.SeriesId != null && r.KavitaSeriesId != null)
                    .Select(r => r.SeriesId!.Value)
                    .ToListAsync(ct)
                : [];

            zeroProgress = await db.Series
                .Where(s => s.Incognito == IncognitoMode.Off &&
                    (s.MalId != null || s.AniListId != null || s.MangaBakaId != null || s.KitsuId != null))
                .Select(s => new NativeProgress(
                    s.Id, s.Title, 0, 0, s.MalId, s.AniListId, s.MangaBakaId, s.KitsuId))
                .ToListAsync(ct);
            zeroProgress = zeroProgress
                .Where(s => !trackedSeriesIds.Contains(s.SeriesId) && !kavitaTrackedSeriesIds.Contains(s.SeriesId))
                .ToList();
        }

        int updates = 0, errors = 0, skipped = 0;

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            var chapter = (int)Math.Floor(row.MaxChapter);
            var volume = (int)Math.Floor(row.MaxVolume);
            await PushNativeRowAsync(userId, row, chapter, volume, null, trackers, ct, Counters);
        }

        var planToRead = await userSettings.GetAsync(userId, SettingKeys.ScrobblePlanToRead, ct) == "true";
        if (planToRead)
        {
            foreach (var row in zeroProgress)
            {
                ct.ThrowIfCancellationRequested();
                await PushNativeRowAsync(userId, row, 0, 0, ScrobbleStatus.PlanToRead, trackers, ct, Counters);
            }
        }

        return $"reader: {updates} updated, {skipped} up-to-date, {errors} errors";

        void Counters(bool wasUpdate, bool wasSkipped, bool wasError)
        {
            if (wasUpdate) updates++;
            if (wasSkipped) skipped++;
            if (wasError) errors++;
        }
    }

    /// <summary>Pushes one native-pass row to every eligible tracker; reports outcomes via <paramref name="report"/>.</summary>
    private async Task PushNativeRowAsync(
        int userId, NativeProgress row, int chapter, int volume, ScrobbleStatus? fallbackStatus,
        List<IScrobbleTracker> trackers, CancellationToken ct, Action<bool, bool, bool> report)
    {
        foreach (var tracker in trackers)
        {
            if (!await SyncReadingEnabledAsync(userId, tracker.Name, ct))
            {
                continue;
            }

            var remoteId = tracker.Name switch
            {
                "mal" => row.MalId?.ToString(),
                "anilist" => row.AniListId?.ToString(),
                "mangabaka" => row.MangaBakaId?.ToString(),
                "kitsu" => row.KitsuId?.ToString(),
                _ => null,
            };
            if (string.IsNullOrEmpty(remoteId))
            {
                continue;
            }

            var state = await GetSeriesScrobbleStateAsync(userId, row.SeriesId, tracker.Name, ct);
            if (state is not null && string.IsNullOrEmpty(state.Error) &&
                state.Chapter >= chapter && state.Volume >= volume)
            {
                report(false, true, false);
                continue;
            }

            var target = new PushTarget(null, row.SeriesId, row.Title);
            try
            {
                if (await PushAsync(userId, tracker, remoteId, target, chapter, volume, fallbackStatus, ct))
                {
                    report(true, false, false);
                }
            }
            catch (Exception e)
            {
                report(false, false, true);
                logger.LogWarning("Native update failed for '{Title}' on {Service}: {Error}",
                    row.Title, tracker.Name, e.Message);
                await AddLogAsync(userId, "error", tracker.Name, row.Title, e.Message, ct);
                await SaveStateAsync(userId, target, tracker.Name, 0, 0, "", e.Message, ct);
            }

            await Task.Delay(Pace, ct);
        }
    }

    private sealed record NativeProgress(
        int SeriesId, string Title, double MaxChapter, double MaxVolume,
        int? MalId, int? AniListId, int? MangaBakaId, int? KitsuId);

    /// <summary>
    /// Where a push records its result. A Kavita-driven push writes the Kavita-keyed
    /// <see cref="ScrobbleSyncState"/>; a reader-driven one writes <see cref="SeriesScrobbleState"/>.
    /// Kept as two tables because the Kavita key space is shared with ScrobbleMapping and
    /// ScrobbleUnmatched and is reverse-derived by title elsewhere.
    /// </summary>
    private readonly record struct PushTarget(int? KavitaSeriesId, int? SeriesId, string Title);

    /// <summary>Forward-only update of one tracker. Returns true when a write happened.</summary>
    private async Task<bool> PushAsync(
        int userId, IScrobbleTracker tracker, string remoteId, PushTarget target,
        int chapter, int volume, ScrobbleStatus? fallbackStatus, CancellationToken ct)
    {
        var title = target.Title;
        var entry = await tracker.GetEntryAsync(userId, remoteId, ct);
        var plan = ScrobblePlanner.Decide(entry, chapter, volume, fallbackStatus);

        if (!plan.Write)
        {
            await SaveStateAsync(userId, target, tracker.Name, plan.Chapter, plan.Volume,
                StatusName(plan.RecordStatus), null, ct);
            return false;
        }

        await tracker.UpdateAsync(userId, remoteId, plan.Chapter, plan.Volume, plan.PushStatus, ct);
        await SaveStateAsync(userId, target, tracker.Name, plan.Chapter, plan.Volume,
            StatusName(plan.RecordStatus), null, ct);

        var message = chapter <= 0 && volume <= 0
            ? $"added to list [{StatusName(plan.PushStatus)}]"
            : $"-> ch {plan.Chapter}" + (plan.Volume > 0 ? $", vol {plan.Volume}" : "") +
              $" [{StatusName(plan.PushStatus)}]";
        await AddLogAsync(userId, "info", tracker.Name, title, message, ct);
        logger.LogInformation("Updated '{Title}' on {Service}: ch {Chapter} vol {Volume} ({Status})",
            title, tracker.Name, plan.Chapter, plan.Volume, StatusName(plan.PushStatus));
        return true;
    }

    /// <summary>
    /// Fire-and-forget rating push: rating the series shouldn't block the HTTP response on several
    /// seconds of tracker auth-checks, network calls and inter-call pacing. Snapshots the ids it
    /// needs and runs <see cref="PushRatingAsync"/> detached on <see cref="CancellationToken.None"/>
    /// (so the request ending doesn't cancel it); the scrobble log records the outcome. Failures are
    /// swallowed here — PushRatingAsync already logs per-tracker.
    /// </summary>
    public void QueueRatingPush(int userId, Series series, int score)
    {
        if (series.Incognito != IncognitoMode.Off)
        {
            return;
        }

        // Snapshot the scalar ids so the detached task never touches the request-scoped entity after
        // its DbContext is disposed.
        var snapshot = new Series
        {
            Id = series.Id,
            Title = series.Title,
            MalId = series.MalId,
            AniListId = series.AniListId,
            MangaBakaId = series.MangaBakaId,
            KitsuId = series.KitsuId,
        };
        _ = Task.Run(async () =>
        {
            try
            {
                await PushRatingAsync(userId, snapshot, score, CancellationToken.None);
            }
            catch (Exception e)
            {
                logger.LogWarning("Background rating push crashed for '{Title}': {Error}", snapshot.Title, e.Message);
            }
        });
    }

    /// <summary>
    /// Best-effort push of a user rating (1–10, or 0 to clear) to every connected tracker,
    /// resolving the remote id from the series' own stored cross-ids rather than the Kavita
    /// mapping — so rating works even without Kavita. Returns the labels of the trackers that
    /// accepted the write; a per-tracker failure is logged and skipped, never thrown.
    /// </summary>
    public async Task<IReadOnlyList<string>> PushRatingAsync(
        int userId, Series series, int score, CancellationToken ct = default)
    {
        var synced = new List<string>();
        foreach (var tracker in await ActiveTrackersAsync(userId, ct))
        {
            // Per-tracker "sync ratings" toggle.
            if (!await SyncRatingsEnabledAsync(userId, tracker.Name, ct))
            {
                continue;
            }

            var remoteId = tracker.Name switch
            {
                "mal" => series.MalId?.ToString(),
                "anilist" => series.AniListId?.ToString(),
                "mangabaka" => series.MangaBakaId?.ToString(),
                "kitsu" => series.KitsuId?.ToString(),
                _ => null,
            };
            if (string.IsNullOrEmpty(remoteId))
            {
                continue;
            }

            try
            {
                await tracker.UpdateRatingAsync(userId, remoteId, score, ct);
                synced.Add(tracker.Label);
                await AddLogAsync(userId, "info", tracker.Name, series.Title, $"rated {score}/10", ct);
            }
            catch (Exception e)
            {
                logger.LogWarning("Rating push failed for '{Title}' on {Service}: {Error}",
                    series.Title, tracker.Name, e.Message);
                await AddLogAsync(userId, "error", tracker.Name, series.Title, $"rating push failed: {e.Message}", ct);
            }

            await Task.Delay(Pace, ct);
        }

        return synced;
    }

    // ---- rating import (preview → apply) ----

    public record RatingImportItem(int SeriesId, string Title, int? LocalRating, int RemoteScore);

    public sealed class RatingImportState
    {
        public bool Running { get; set; }
        public DateTime? ComputedAt { get; set; }
        public List<RatingImportItem> Items { get; set; } = [];
        public string? Error { get; set; }
    }

    /// <summary>
    /// Last/in-flight rating-import preview per <c>(userId, service)</c> (in-memory, like the OAuth
    /// sessions). Keyed by user as well as service because the preview holds the scores read off one
    /// person's remote list and compares them against that person's local ratings — a single-slot
    /// cache would offer one reader another reader's scores to import.
    /// </summary>
    private readonly ConcurrentDictionary<(int UserId, string Service), RatingImportState> _ratingImports = new();

    public RatingImportState GetRatingImport(int userId, string service) =>
        _ratingImports.GetValueOrDefault((userId, service)) ?? new RatingImportState();

    /// <summary>
    /// Kicks off a detached preview of the scores the user holds on <paramref name="service"/>:
    /// for every library series carrying that service's remote id, fetch the remote entry and
    /// collect the ones whose score differs from the local rating. Results land in
    /// <see cref="GetRatingImport"/> for the UI to poll, then apply.
    /// </summary>
    public void QueueRatingImportPreview(int userId, string service)
    {
        var tracker = FindTracker(service);
        if (tracker is null)
        {
            return;
        }

        var state = new RatingImportState { Running = true };
        _ratingImports[(userId, service)] = state;
        _ = Task.Run(async () =>
        {
            try
            {
                var targets = await LibraryRemoteIdsAsync(userId, service, CancellationToken.None);
                foreach (var (seriesId, title, localRating, remoteId) in targets)
                {
                    try
                    {
                        var entry = await tracker.GetEntryAsync(userId, remoteId, CancellationToken.None);
                        if (entry.Score is { } score && score != localRating)
                        {
                            state.Items.Add(new RatingImportItem(seriesId, title, localRating, score));
                        }
                    }
                    catch (Exception e)
                    {
                        logger.LogWarning("Rating import: fetch failed for '{Title}' on {Service}: {Error}",
                            title, service, e.Message);
                    }

                    await Task.Delay(Pace, CancellationToken.None);
                }
            }
            catch (Exception e)
            {
                state.Error = e.Message;
                logger.LogWarning("Rating import preview crashed for {Service}: {Error}", service, e.Message);
            }
            finally
            {
                state.Running = false;
                state.ComputedAt = DateTime.UtcNow;
            }
        });
    }

    /// <summary>Writes the previewed remote scores for the chosen series to local ratings.</summary>
    public async Task<int> ApplyRatingImportAsync(
        int userId, string service, IReadOnlyCollection<int> seriesIds, CancellationToken ct)
    {
        var wanted = new HashSet<int>(seriesIds);
        var scores = GetRatingImport(userId, service).Items
            .Where(i => wanted.Contains(i.SeriesId))
            .ToDictionary(i => i.SeriesId, i => i.RemoteScore);
        if (scores.Count == 0)
        {
            return 0;
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();

        // Lands in this user's own state rows, creating them on demand — the import is "pull my scores
        // down from the tracker", not "overwrite the library's scores".
        var ids = await db.Series.Where(s => scores.Keys.Contains(s.Id)).Select(s => s.Id).ToListAsync(ct);
        var existing = await db.UserSeriesStates
            .Where(x => x.UserId == userId && ids.Contains(x.SeriesId))
            .ToDictionaryAsync(x => x.SeriesId, ct);

        foreach (var seriesId in ids)
        {
            if (!existing.TryGetValue(seriesId, out var state))
            {
                state = new UserSeriesState { UserId = userId, SeriesId = seriesId };
                db.UserSeriesStates.Add(state);
            }

            state.Rating = scores[seriesId];
            state.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        return ids.Count;
    }

    /// <summary>Library series carrying a remote id for the given tracker: (id, title, localRating, remoteId).</summary>
    private async Task<List<(int SeriesId, string Title, int? LocalRating, string RemoteId)>>
        LibraryRemoteIdsAsync(int userId, string service, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        var rows = await db.Series.AsNoTracking()
            .Select(s => new
            {
                s.Id,
                s.Title,
                Rating = db.UserSeriesStates
                    .Where(u => u.UserId == userId && u.SeriesId == s.Id)
                    .Select(u => u.Rating)
                    .FirstOrDefault(),
                s.MalId,
                s.AniListId,
                s.MangaBakaId,
                s.KitsuId,
            })
            .ToListAsync(ct);

        return rows
            .Select(s => (s.Id, s.Title, s.Rating, RemoteId: service switch
            {
                "mal" => s.MalId?.ToString(),
                "anilist" => s.AniListId?.ToString(),
                "mangabaka" => s.MangaBakaId?.ToString(),
                "kitsu" => s.KitsuId?.ToString(),
                _ => null,
            }))
            .Where(x => !string.IsNullOrEmpty(x.RemoteId))
            .Select(x => (x.Id, x.Title, x.Rating, x.RemoteId!))
            .ToList();
    }

    // ---- matching ----

    /// <summary>Cross-ids of one Maki library series, keyed for Kavita-name lookup.</summary>
    private sealed record LibraryIds(
        int Id, int? MangaBakaId, int? AniListId, int? MalId, int? KitsuId, IncognitoMode Incognito);

    private async Task<Dictionary<string, LibraryIds>> BuildLibraryIndexAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        var rows = await db.Series.AsNoTracking()
            .Select(s => new
            {
                s.Id, s.Title, s.FolderName, s.MangaBakaId, s.AniListId, s.MalId, s.KitsuId, s.Incognito
            })
            .ToListAsync(ct);

        // Kavita parses its series name from file names (filesystem-illegal chars
        // stripped), so index by punctuation-normalized title AND folder name.
        var index = new Dictionary<string, LibraryIds>();
        foreach (var row in rows)
        {
            var ids = new LibraryIds(row.Id, row.MangaBakaId, row.AniListId, row.MalId, row.KitsuId, row.Incognito);
            foreach (var name in new[] { row.Title, row.FolderName })
            {
                var key = ScrobbleMatching.NormalizeTitle(name ?? "");
                if (key.Length > 0)
                {
                    index.TryAdd(key, ids);
                }
            }
        }

        return index;
    }

    /// <summary>
    /// Page boundaries of every multi-chapter volume archive belonging to one Maki
    /// series, keyed by volume number. Only archives where several <see cref="Chapter"/>
    /// rows share one <see cref="ChapterFile"/> (import/rescan grouped them) qualify —
    /// Maki's own per-chapter downloads need no refinement. Results are cached per
    /// ChapterFileId and re-scanned only when the file's size changes.
    /// </summary>
    private async Task<Dictionary<int, VolumeChapterProgress.ChapterFileBoundaries>> VolumeBoundariesAsync(
        int seriesId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        var chapters = await db.Chapters.AsNoTracking()
            .Where(c => c.SeriesId == seriesId && c.ChapterFileId != null)
            .ToListAsync(ct);

        var result = new Dictionary<int, VolumeChapterProgress.ChapterFileBoundaries>();

        var fileIds = chapters.Select(g => g.ChapterFileId).ToList();
        var files = await db.ChapterFiles.AsNoTracking()
            .Where(f => fileIds.Contains(f.Id))
            .Select(f => new { f.Id, f.RelativePath, f.Size })
            .ToDictionaryAsync(f => f.Id, ct);
        var rootFolderPath = await db.Series.AsNoTracking()
            .Where(s => s.Id == seriesId)
            .Select(s => s.RootFolder!.Path)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(rootFolderPath))
        {
            return result;
        }
        
        var groups = chapters
            .GroupBy(c => c.ChapterFileId!.Value)
            .Where(g => g.Count() > 1)
            .Select(g => new
            {
                ChapterFileId = g.Key,
                Volumes = files.Where(x => x.Key == g.Key)
                    .Select(x => ChapterController.VolumeFileLabel(x.Value.RelativePath)).Distinct().ToList(),
            })
            .ToList();

        foreach (var group in groups)
        {
            // A volume-range file (chapters spanning several volume numbers) has no
            // single Kavita "volume" to attach page boundaries to — skip it.
            if (group.Volumes.Count != 1 || !int.TryParse(group.Volumes[0], out var volumeNumber) ||
                !files.TryGetValue(group.ChapterFileId, out var file))
            {
                continue;
            }

            if (_volumeBoundaryCache.TryGetValue(group.ChapterFileId, out var cached) && cached.Size == file.Size)
            {
                result[volumeNumber] = cached.Boundaries;
                continue;
            }

            var absolutePath = Path.Combine(rootFolderPath, file.RelativePath);
            var (totalPages, boundaries) = VolumeChapterScanner.ScanCbzBoundaries(absolutePath);
            if (boundaries.Count == 0)
            {
                continue;
            }

            var entry = new VolumeChapterProgress.ChapterFileBoundaries(totalPages, boundaries);
            _volumeBoundaryCache[group.ChapterFileId] = (file.Size, entry);
            result[volumeNumber] = entry;
        }

        return result;
    }

    /// <summary>
    /// Returns {service: remote_id} for every requested service that could be
    /// resolved. Precedence: saved mapping (incl. manual/ignored) → Maki library
    /// cross-ids → Kavita web links → cross-derivation → strict title search.
    /// Unresolvable services land on the needs-review list.
    /// </summary>
    private async Task<Dictionary<string, string>> ResolveAsync(
        int userId, int kavitaSeriesId, string title, string? altTitle, List<string> webLinks,
        List<string> services, Dictionary<string, LibraryIds> libraryIndex, CancellationToken ct)
    {
        var result = new Dictionary<string, string>();
        var missing = new List<string>();

        foreach (var service in services)
        {
            var mapping = await GetMappingAsync(userId, kavitaSeriesId, service, ct);
            if (mapping is not null)
            {
                if (mapping.RemoteId.Length > 0) // empty remote id == ignored
                {
                    result[service] = mapping.RemoteId;
                }

                continue;
            }

            missing.Add(service);
        }

        if (missing.Count == 0)
        {
            return result;
        }

        // Maki library cross-ids: instant, no remote calls.
        var libraryIds = new Dictionary<string, string>();
        foreach (var name in new[] { title, altTitle })
        {
            var key = ScrobbleMatching.NormalizeTitle(name ?? "");
            if (key.Length > 0 && libraryIndex.TryGetValue(key, out var found))
            {
                if (found.MangaBakaId is { } mb)
                {
                    libraryIds.TryAdd("mangabaka", mb.ToString());
                }

                if (found.AniListId is { } al)
                {
                    libraryIds.TryAdd("anilist", al.ToString());
                }

                if (found.MalId is { } malId)
                {
                    libraryIds.TryAdd("mal", malId.ToString());
                }

                if (found.KitsuId is { } kitsuId)
                {
                    libraryIds.TryAdd("kitsu", kitsuId.ToString());
                }

                break;
            }
        }

        var webLinkIds = ScrobbleMatching.ParseWebLinks(webLinks);
        var ids = new Dictionary<string, string>(libraryIds);
        foreach (var (service, id) in webLinkIds)
        {
            ids.TryAdd(service, id);
        }

        foreach (var (service, id) in result) // known mappings help derivation
        {
            ids.TryAdd(service, id);
        }

        await DeriveIdsAsync(ids, ct);

        foreach (var service in missing.ToList())
        {
            if (!ids.TryGetValue(service, out var id))
            {
                continue;
            }

            var method = libraryIds.ContainsKey(service) ? "library"
                : webLinkIds.ContainsKey(service) ? "weblink"
                : "derived";
            await SaveMappingAsync(userId, kavitaSeriesId, service, id, method, title, ct);
            result[service] = id;
            missing.Remove(service);
            logger.LogInformation("Matched '{Title}' on {Service} via ids -> {RemoteId}", title, service, id);
        }

        foreach (var service in missing)
        {
            var remoteId = await MatchByTitleAsync(userId, kavitaSeriesId, title, altTitle, service, ct);
            if (remoteId is not null)
            {
                result[service] = remoteId;
                ids[service] = remoteId; // a search hit may unlock the rest on the next pass
            }
        }

        return result;
    }

    /// <summary>Fills in missing service ids from the ones we have.</summary>
    private async Task DeriveIdsAsync(Dictionary<string, string> ids, CancellationToken ct)
    {
        // AniList or MAL id -> MangaBaka series (which lists all source ids)
        JsonElement? series = null;
        if (ids.ContainsKey("anilist") &&
            (!ids.ContainsKey("mangabaka") || !ids.ContainsKey("mal") || !ids.ContainsKey("kitsu")))
        {
            series = await mangaBaka.ResolveFromSourceAsync("anilist", ids["anilist"], ct);
        }
        else if (ids.ContainsKey("mal") &&
                 (!ids.ContainsKey("mangabaka") || !ids.ContainsKey("anilist") || !ids.ContainsKey("kitsu")))
        {
            series = await mangaBaka.ResolveFromSourceAsync("my-anime-list", ids["mal"], ct);
        }

        if (series is { ValueKind: JsonValueKind.Object } s)
        {
            if (s.TryGetProperty("id", out var mbId) && mbId.ValueKind is JsonValueKind.Number or JsonValueKind.String)
            {
                ids.TryAdd("mangabaka", mbId.GetRawText().Trim('"'));
            }

            if (s.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object)
            {
                if (SourceId(source, "anilist") is { } anilistId)
                {
                    ids.TryAdd("anilist", anilistId);
                }

                if (SourceId(source, "my_anime_list") is { } malId)
                {
                    ids.TryAdd("mal", malId);
                }

                if (SourceId(source, "kitsu") is { } kitsuId)
                {
                    ids.TryAdd("kitsu", kitsuId);
                }
            }
        }

        // AniList knows MAL ids directly
        if (ids.ContainsKey("anilist") && !ids.ContainsKey("mal"))
        {
            var malId = await anilist.GetMalIdAsync(ids["anilist"], ct);
            if (malId is not null)
            {
                ids["mal"] = malId;
            }
        }

        static string? SourceId(JsonElement source, string service)
        {
            if (!source.TryGetProperty(service, out var entry) || entry.ValueKind != JsonValueKind.Object ||
                !entry.TryGetProperty("id", out var id))
            {
                return null;
            }

            return id.ValueKind switch
            {
                JsonValueKind.Number => ((long)id.GetDouble()).ToString(),
                JsonValueKind.String => id.GetString(),
                _ => null,
            };
        }
    }

    private async Task<string?> MatchByTitleAsync(
        int userId, int kavitaSeriesId, string title, string? altTitle, string service, CancellationToken ct)
    {
        var tracker = FindTracker(service)!;
        IReadOnlyList<ScrobbleCandidate> candidates;
        try
        {
            candidates = await tracker.SearchAsync(userId, title, ct);
            if (candidates.Count == 0 && !string.IsNullOrEmpty(altTitle))
            {
                candidates = await tracker.SearchAsync(userId, altTitle, ct);
            }
        }
        catch (TrackerException e)
        {
            logger.LogWarning("Search on {Service} for '{Title}' failed: {Error}", service, title, e.Message);
            await SaveUnmatchedAsync(userId, kavitaSeriesId, service, title, $"search failed: {e.Message}", [], ct);
            return null;
        }

        var match = ScrobbleMatching.BestCandidate(title, altTitle, candidates);
        if (match is not null)
        {
            await SaveMappingAsync(userId, kavitaSeriesId, service, match.Id, "search", title, ct);
            logger.LogInformation("Matched '{Title}' on {Service} via title search -> {RemoteId} ({MatchTitle})",
                title, service, match.Id, match.Title);
            return match.Id;
        }

        await SaveUnmatchedAsync(userId, kavitaSeriesId, service, title,
            candidates.Count > 0 ? "no confident title match" : "no search results",
            candidates.Take(5).Select(c => new CandidateDto(c.Id, c.Title, c.Url)).ToList(), ct);
        logger.LogInformation("No confident match for '{Title}' on {Service} ({Count} candidates)",
            title, service, candidates.Count);
        return null;
    }

    // ---- persistence helpers ----

    public record CandidateDto(string Id, string Title, string Url);

    // Every helper below filters on UserId in the predicate rather than leaning on the global query
    // filter: the scopes they open are fresh, and therefore unrestricted, which is exactly what lets
    // the background tick act for a user who is not making a request.
    private async Task<ScrobbleMapping?> GetMappingAsync(
        int userId, int kavitaSeriesId, string service, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        return await db.ScrobbleMappings.AsNoTracking()
            .FirstOrDefaultAsync(
                m => m.UserId == userId && m.KavitaSeriesId == kavitaSeriesId && m.Service == service, ct);
    }

    public async Task SaveMappingAsync(
        int userId, int kavitaSeriesId, string service, string remoteId, string method, string title,
        CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        var existing = await db.ScrobbleMappings
            .FirstOrDefaultAsync(
                m => m.UserId == userId && m.KavitaSeriesId == kavitaSeriesId && m.Service == service, ct);
        if (existing is null)
        {
            db.ScrobbleMappings.Add(new ScrobbleMapping
            {
                UserId = userId,
                KavitaSeriesId = kavitaSeriesId,
                Service = service,
                RemoteId = remoteId,
                Method = method,
                Title = title,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        else
        {
            existing.RemoteId = remoteId;
            existing.Method = method;
            existing.Title = title;
            existing.UpdatedAt = DateTime.UtcNow;
        }

        await db.ScrobbleUnmatched
            .Where(u => u.UserId == userId && u.KavitaSeriesId == kavitaSeriesId && u.Service == service)
            .ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
    }

    private async Task SaveUnmatchedAsync(
        int userId, int kavitaSeriesId, string service, string title, string reason,
        List<CandidateDto> candidates, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        var existing = await db.ScrobbleUnmatched
            .FirstOrDefaultAsync(
                u => u.UserId == userId && u.KavitaSeriesId == kavitaSeriesId && u.Service == service, ct);
        var json = JsonSerializer.Serialize(candidates);
        if (existing is null)
        {
            db.ScrobbleUnmatched.Add(new ScrobbleUnmatched
            {
                UserId = userId,
                KavitaSeriesId = kavitaSeriesId,
                Service = service,
                Title = title,
                Reason = reason,
                CandidatesJson = json,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        else
        {
            existing.Title = title;
            existing.Reason = reason;
            existing.CandidatesJson = json;
            existing.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task<ScrobbleSyncState?> GetSyncStateAsync(
        int userId, int kavitaSeriesId, string service, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        return await db.ScrobbleSyncStates.AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.UserId == userId && s.KavitaSeriesId == kavitaSeriesId && s.Service == service, ct);
    }

    private async Task<SeriesScrobbleState?> GetSeriesScrobbleStateAsync(
        int userId, int seriesId, string service, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        return await db.SeriesScrobbleStates.AsNoTracking()
            .FirstOrDefaultAsync(s => s.UserId == userId && s.SeriesId == seriesId && s.Service == service, ct);
    }

    /// <summary>Records a push result in whichever state table the target names.</summary>
    private async Task SaveStateAsync(
        int userId, PushTarget target, string service, int chapter, int volume, string status,
        string? error, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();

        if (target.KavitaSeriesId is int kavitaSeriesId)
        {
            var existing = await db.ScrobbleSyncStates
                .FirstOrDefaultAsync(
                    s => s.UserId == userId && s.KavitaSeriesId == kavitaSeriesId && s.Service == service, ct);
            if (existing is null)
            {
                db.ScrobbleSyncStates.Add(new ScrobbleSyncState
                {
                    UserId = userId,
                    KavitaSeriesId = kavitaSeriesId,
                    Service = service,
                    Chapter = chapter,
                    Volume = volume,
                    Status = status,
                    Title = target.Title,
                    SyncedAt = DateTime.UtcNow,
                    Error = error,
                });
            }
            else
            {
                existing.Chapter = chapter;
                existing.Volume = volume;
                existing.Status = status;
                existing.Title = target.Title;
                existing.SyncedAt = DateTime.UtcNow;
                existing.Error = error;
            }
        }
        else if (target.SeriesId is int seriesId)
        {
            var existing = await db.SeriesScrobbleStates
                .FirstOrDefaultAsync(s => s.UserId == userId && s.SeriesId == seriesId && s.Service == service, ct);
            if (existing is null)
            {
                db.SeriesScrobbleStates.Add(new SeriesScrobbleState
                {
                    UserId = userId,
                    SeriesId = seriesId,
                    Service = service,
                    Chapter = chapter,
                    Volume = volume,
                    Status = status,
                    SyncedAt = DateTime.UtcNow,
                    Error = error,
                });
            }
            else
            {
                existing.Chapter = chapter;
                existing.Volume = volume;
                existing.Status = status;
                existing.SyncedAt = DateTime.UtcNow;
                existing.Error = error;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Deletes the mapping and sync state so the series re-matches from scratch.</summary>
    public async Task DeleteMappingAsync(int userId, int kavitaSeriesId, string service, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        await db.ScrobbleMappings
            .Where(m => m.UserId == userId && m.KavitaSeriesId == kavitaSeriesId && m.Service == service)
            .ExecuteDeleteAsync(ct);
        await db.ScrobbleSyncStates
            .Where(s => s.UserId == userId && s.KavitaSeriesId == kavitaSeriesId && s.Service == service)
            .ExecuteDeleteAsync(ct);
    }

    /// <param name="userId">
    /// Whose activity log the line belongs to. The cap is applied per user, so one busy account cannot
    /// push another's history out of its own log.
    /// </param>
    public async Task AddLogAsync(
        int userId, string level, string service, string title, string message, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        db.ScrobbleLog.Add(new ScrobbleLogEntry
        {
            UserId = userId,
            Timestamp = DateTime.UtcNow,
            Level = level,
            Service = service,
            Title = title,
            Message = message,
        });
        await db.SaveChangesAsync(ct);
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM ScrobbleLog WHERE UserId = {0} AND Id NOT IN " +
            "(SELECT Id FROM ScrobbleLog WHERE UserId = {0} ORDER BY Id DESC LIMIT 500)",
            [userId], ct);
    }

    public static string StatusName(ScrobbleStatus status) => status switch
    {
        ScrobbleStatus.Reading => "reading",
        ScrobbleStatus.Completed => "completed",
        ScrobbleStatus.PlanToRead => "plan_to_read",
        _ => "other",
    };

    private static HashSet<int> ParseLibraryIds(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(x => int.TryParse(x, out _))
        .Select(int.Parse)
        .ToHashSet();
}
