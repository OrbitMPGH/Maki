using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Mangarr.Core.Configuration;
using Mangarr.Core.Entities;
using Mangarr.Core.Kavita;
using Mangarr.Core.Parsing;
using Mangarr.Core.Scrobbling;
using Mangarr.Data;
using Microsoft.EntityFrameworkCore;

namespace Mangarr.Api.Services;

/// <summary>
/// The scrobble sync engine: polls Kavita for reading progress, resolves each Kavita
/// series to remote ids on the connected trackers, and pushes forward-only progress
/// updates. Native port of MangaScrobbler, better integrated: Kavita connection
/// settings are shared with the scan/metadata push, and series in Mangarr's own
/// library match instantly via their stored MangaBaka/AniList/MAL cross-ids.
/// </summary>
public class ScrobbleService(
    IServiceScopeFactory scopeFactory,
    SettingsService settings,
    KavitaClient kavita,
    AniListTracker anilist,
    MalTracker mal,
    MangaBakaTracker mangaBaka,
    ILogger<ScrobbleService> logger)
{
    public const int DefaultIntervalMinutes = 30;

    /// <summary>Polite pacing between remote API calls (AniList is the strictest at ~30/min).</summary>
    private static readonly TimeSpan Pace = TimeSpan.FromSeconds(1.2);

    private readonly IScrobbleTracker[] _trackers = [anilist, mal, mangaBaka];
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private (DateTime CheckedAt, bool Ok)? _kavitaPing;

    /// <summary>Cached per-archive page-boundary scans, keyed by ChapterFileId; re-scanned when file size changes.</summary>
    private readonly ConcurrentDictionary<int, (long Size, VolumeChapterProgress.ChapterFileBoundaries Boundaries)>
        _volumeBoundaryCache = new();

    /// <summary>In-flight OAuth sessions per service: state → (verifier, redirect URI).</summary>
    public record OAuthSession(string Service, string State, string CodeVerifier, string RedirectUri, DateTime CreatedAt);

    private readonly ConcurrentDictionary<string, OAuthSession> _oauthSessions = new();

    public bool Running { get; private set; }

    public AniListTracker AniList => anilist;
    public MalTracker Mal => mal;
    public MangaBakaTracker MangaBaka => mangaBaka;
    public IReadOnlyList<IScrobbleTracker> Trackers => _trackers;

    public IScrobbleTracker? FindTracker(string service) =>
        _trackers.FirstOrDefault(t => t.Name == service);

    // ---- OAuth session memory ----

    public OAuthSession StartOAuthSession(string service, string redirectUri)
    {
        var session = new OAuthSession(
            service,
            State: Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)),
            CodeVerifier: Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(40)),
            redirectUri,
            DateTime.UtcNow);
        _oauthSessions[service] = session;
        return session;
    }

    public OAuthSession? TakeOAuthSession(string service, string state)
    {
        if (_oauthSessions.TryGetValue(service, out var session) && session.State == state &&
            DateTime.UtcNow - session.CreatedAt < TimeSpan.FromMinutes(15))
        {
            _oauthSessions.TryRemove(service, out _);
            return session;
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

    public async Task<int> IntervalMinutesAsync(CancellationToken ct = default) =>
        int.TryParse(await settings.GetAsync(SettingKeys.ScrobbleIntervalMinutes, ct), out var m) && m >= 5
            ? m
            : DefaultIntervalMinutes;

    // ---- scheduled tick ----

    /// <summary>
    /// Runs a sync when forced, or when the interval has elapsed and at least one
    /// tracker is connected (a silent no-op otherwise so the log isn't spammed).
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

            if ((await ActiveTrackersAsync(ct)).Count == 0)
            {
                return;
            }
        }

        await SyncAsync(ct);
    }

    private async Task<List<IScrobbleTracker>> ActiveTrackersAsync(CancellationToken ct)
    {
        var active = new List<IScrobbleTracker>();
        foreach (var tracker in _trackers)
        {
            if (await tracker.ConfiguredAsync(ct) && await tracker.AuthenticatedAsync(ct))
            {
                active.Add(tracker);
            }
        }

        return active;
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
            await AddLogAsync("error", "", "", $"sync crashed: {ex.Message}", ct);
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

    private async Task<string> SyncInnerAsync(CancellationToken ct)
    {
        var kavitaUrl = await settings.GetAsync(SettingKeys.KavitaUrl, ct);
        var kavitaKey = await settings.GetAsync(SettingKeys.KavitaApiKey, ct);
        if (string.IsNullOrWhiteSpace(kavitaUrl) || string.IsNullOrWhiteSpace(kavitaKey))
        {
            const string msg = "Kavita is not configured (Settings → Kavita)";
            await AddLogAsync("error", "kavita", "", msg, ct);
            return msg;
        }

        var trackers = await ActiveTrackersAsync(ct);
        if (trackers.Count == 0)
        {
            const string msg = "No tracker is connected — nothing to sync";
            await AddLogAsync("warning", "", "", msg, ct);
            return msg;
        }

        List<KavitaClient.KavitaSeriesSummary> seriesList;
        try
        {
            seriesList = await kavita.GetAllSeriesAsync(kavitaUrl, kavitaKey, ct);
        }
        catch (Exception e)
        {
            await AddLogAsync("error", "kavita", "", e.Message, ct);
            return $"Kavita error: {e.Message}";
        }

        var libraryFilter = ParseLibraryIds(await settings.GetAsync(SettingKeys.ScrobbleLibraryIds, ct));
        var planToRead = await settings.GetAsync(SettingKeys.ScrobblePlanToRead, ct) == "true";
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
                await AddLogAsync("error", "kavita", title, $"progress read failed: {e.Message}", ct);
                errors++;
                continue;
            }

            var maxChapter = (decimal)progress.MaxChapter;

            // When a Kavita "volume" is actually one of Mangarr's own multi-chapter
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
            }

            var chapter = (int)Math.Floor(maxChapter);
            var volume = (int)Math.Floor(progress.MaxVolume);
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

            // figure out which trackers actually need an update before doing any
            // remote matching/lookups
            var pending = new List<IScrobbleTracker>();
            foreach (var tracker in trackers)
            {
                var state = await GetSyncStateAsync(series.Id, tracker.Name, ct);
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
                mappings = await ResolveAsync(series.Id, title, series.LocalizedName, webLinks,
                    pending.Select(t => t.Name).ToList(), libraryIndex, ct);
            }
            catch (Exception e)
            {
                errors++;
                logger.LogWarning("Matching failed for '{Title}': {Error}", title, e.Message);
                await AddLogAsync("error", "", title, $"matching failed: {e.Message}", ct);
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
                    var changed = await PushAsync(tracker, remoteId, series.Id, title, chapter, volume,
                        fallbackStatus, ct);
                    if (changed)
                    {
                        updates++;
                    }
                }
                catch (Exception e)
                {
                    errors++;
                    logger.LogWarning("Update failed for '{Title}' on {Service}: {Error}",
                        title, tracker.Name, e.Message);
                    await AddLogAsync("error", tracker.Name, title, e.Message, ct);
                    await SaveSyncStateAsync(series.Id, tracker.Name, 0, 0, "", title, e.Message, ct);
                }

                await Task.Delay(Pace, ct);
            }
        }

        var summary = $"sync done: {updates} updated, {skipped} up-to-date, {errors} errors" +
                      (noProgress > 0 ? $", {noProgress} with pages read but no fully-read chapter" : "");
        logger.LogInformation("{Summary}", summary);
        await AddLogAsync("info", "", "", summary, ct);
        return summary;
    }

    /// <summary>Forward-only update of one tracker. Returns true when a write happened.</summary>
    private async Task<bool> PushAsync(
        IScrobbleTracker tracker, string remoteId, int kavitaSeriesId, string title,
        int chapter, int volume, ScrobbleStatus? fallbackStatus, CancellationToken ct)
    {
        var entry = await tracker.GetEntryAsync(remoteId, ct);
        var plan = ScrobblePlanner.Decide(entry, chapter, volume, fallbackStatus);

        if (!plan.Write)
        {
            await SaveSyncStateAsync(kavitaSeriesId, tracker.Name, plan.Chapter, plan.Volume,
                StatusName(plan.RecordStatus), title, null, ct);
            return false;
        }

        await tracker.UpdateAsync(remoteId, plan.Chapter, plan.Volume, plan.PushStatus, ct);
        await SaveSyncStateAsync(kavitaSeriesId, tracker.Name, plan.Chapter, plan.Volume,
            StatusName(plan.RecordStatus), title, null, ct);

        var message = chapter <= 0 && volume <= 0
            ? $"added to list [{StatusName(plan.PushStatus)}]"
            : $"-> ch {plan.Chapter}" + (plan.Volume > 0 ? $", vol {plan.Volume}" : "") +
              $" [{StatusName(plan.PushStatus)}]";
        await AddLogAsync("info", tracker.Name, title, message, ct);
        logger.LogInformation("Updated '{Title}' on {Service}: ch {Chapter} vol {Volume} ({Status})",
            title, tracker.Name, plan.Chapter, plan.Volume, StatusName(plan.PushStatus));
        return true;
    }

    // ---- matching ----

    /// <summary>Cross-ids of one Mangarr library series, keyed for Kavita-name lookup.</summary>
    private sealed record LibraryIds(int Id, int? MangaBakaId, int? AniListId, int? MalId);

    private async Task<Dictionary<string, LibraryIds>> BuildLibraryIndexAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangarrDbContext>();
        var rows = await db.Series.AsNoTracking()
            .Select(s => new { s.Id, s.Title, s.FolderName, s.MangaBakaId, s.AniListId, s.MalId })
            .ToListAsync(ct);

        // Kavita parses its series name from file names (filesystem-illegal chars
        // stripped), so index by punctuation-normalized title AND folder name.
        var index = new Dictionary<string, LibraryIds>();
        foreach (var row in rows)
        {
            var ids = new LibraryIds(row.Id, row.MangaBakaId, row.AniListId, row.MalId);
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
    /// Page boundaries of every multi-chapter volume archive belonging to one Mangarr
    /// series, keyed by volume number. Only archives where several <see cref="Chapter"/>
    /// rows share one <see cref="ChapterFile"/> (import/rescan grouped them) qualify —
    /// Mangarr's own per-chapter downloads need no refinement. Results are cached per
    /// ChapterFileId and re-scanned only when the file's size changes.
    /// </summary>
    private async Task<Dictionary<int, VolumeChapterProgress.ChapterFileBoundaries>> VolumeBoundariesAsync(
        int seriesId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangarrDbContext>();
        var groups = (await db.Chapters.AsNoTracking()
                .Where(c => c.SeriesId == seriesId && c.ChapterFileId != null)
                .ToListAsync(ct))
            .GroupBy(c => c.ChapterFileId!.Value)
            .Where(g => g.Count() > 1)
            .Select(g => new
            {
                ChapterFileId = g.Key,
                Volumes = g.Select(c => c.Volume).Distinct().ToList()
            })
            .ToList();

        var result = new Dictionary<int, VolumeChapterProgress.ChapterFileBoundaries>();
        if (groups.Count == 0)
        {
            return result;
        }

        var fileIds = groups.Select(g => g.ChapterFileId).ToList();
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

        foreach (var group in groups)
        {
            // A volume-range file (chapters spanning several volume numbers) has no
            // single Kavita "volume" to attach page boundaries to — skip it.
            if (group.Volumes.Count != 1 || group.Volumes[0] is not { } volumeNumber ||
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
    /// resolved. Precedence: saved mapping (incl. manual/ignored) → Mangarr library
    /// cross-ids → Kavita web links → cross-derivation → strict title search.
    /// Unresolvable services land on the needs-review list.
    /// </summary>
    private async Task<Dictionary<string, string>> ResolveAsync(
        int kavitaSeriesId, string title, string? altTitle, List<string> webLinks,
        List<string> services, Dictionary<string, LibraryIds> libraryIndex, CancellationToken ct)
    {
        var result = new Dictionary<string, string>();
        var missing = new List<string>();

        foreach (var service in services)
        {
            var mapping = await GetMappingAsync(kavitaSeriesId, service, ct);
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

        // Mangarr library cross-ids: instant, no remote calls.
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
            await SaveMappingAsync(kavitaSeriesId, service, id, method, title, ct);
            result[service] = id;
            missing.Remove(service);
            logger.LogInformation("Matched '{Title}' on {Service} via ids -> {RemoteId}", title, service, id);
        }

        foreach (var service in missing)
        {
            var remoteId = await MatchByTitleAsync(kavitaSeriesId, title, altTitle, service, ct);
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
        if (ids.ContainsKey("anilist") && (!ids.ContainsKey("mangabaka") || !ids.ContainsKey("mal")))
        {
            series = await mangaBaka.ResolveFromSourceAsync("anilist", ids["anilist"], ct);
        }
        else if (ids.ContainsKey("mal") && (!ids.ContainsKey("mangabaka") || !ids.ContainsKey("anilist")))
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
        int kavitaSeriesId, string title, string? altTitle, string service, CancellationToken ct)
    {
        var tracker = FindTracker(service)!;
        IReadOnlyList<ScrobbleCandidate> candidates;
        try
        {
            candidates = await tracker.SearchAsync(title, ct);
            if (candidates.Count == 0 && !string.IsNullOrEmpty(altTitle))
            {
                candidates = await tracker.SearchAsync(altTitle, ct);
            }
        }
        catch (TrackerException e)
        {
            logger.LogWarning("Search on {Service} for '{Title}' failed: {Error}", service, title, e.Message);
            await SaveUnmatchedAsync(kavitaSeriesId, service, title, $"search failed: {e.Message}", [], ct);
            return null;
        }

        var match = ScrobbleMatching.BestCandidate(title, altTitle, candidates);
        if (match is not null)
        {
            await SaveMappingAsync(kavitaSeriesId, service, match.Id, "search", title, ct);
            logger.LogInformation("Matched '{Title}' on {Service} via title search -> {RemoteId} ({MatchTitle})",
                title, service, match.Id, match.Title);
            return match.Id;
        }

        await SaveUnmatchedAsync(kavitaSeriesId, service, title,
            candidates.Count > 0 ? "no confident title match" : "no search results",
            candidates.Take(5).Select(c => new CandidateDto(c.Id, c.Title, c.Url)).ToList(), ct);
        logger.LogInformation("No confident match for '{Title}' on {Service} ({Count} candidates)",
            title, service, candidates.Count);
        return null;
    }

    // ---- persistence helpers ----

    public record CandidateDto(string Id, string Title, string Url);

    private async Task<ScrobbleMapping?> GetMappingAsync(int kavitaSeriesId, string service, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangarrDbContext>();
        return await db.ScrobbleMappings.AsNoTracking()
            .FirstOrDefaultAsync(m => m.KavitaSeriesId == kavitaSeriesId && m.Service == service, ct);
    }

    public async Task SaveMappingAsync(
        int kavitaSeriesId, string service, string remoteId, string method, string title, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangarrDbContext>();
        var existing = await db.ScrobbleMappings
            .FirstOrDefaultAsync(m => m.KavitaSeriesId == kavitaSeriesId && m.Service == service, ct);
        if (existing is null)
        {
            db.ScrobbleMappings.Add(new ScrobbleMapping
            {
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
            .Where(u => u.KavitaSeriesId == kavitaSeriesId && u.Service == service)
            .ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
    }

    private async Task SaveUnmatchedAsync(
        int kavitaSeriesId, string service, string title, string reason,
        List<CandidateDto> candidates, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangarrDbContext>();
        var existing = await db.ScrobbleUnmatched
            .FirstOrDefaultAsync(u => u.KavitaSeriesId == kavitaSeriesId && u.Service == service, ct);
        var json = JsonSerializer.Serialize(candidates);
        if (existing is null)
        {
            db.ScrobbleUnmatched.Add(new ScrobbleUnmatched
            {
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

    private async Task<ScrobbleSyncState?> GetSyncStateAsync(int kavitaSeriesId, string service, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangarrDbContext>();
        return await db.ScrobbleSyncStates.AsNoTracking()
            .FirstOrDefaultAsync(s => s.KavitaSeriesId == kavitaSeriesId && s.Service == service, ct);
    }

    private async Task SaveSyncStateAsync(
        int kavitaSeriesId, string service, int chapter, int volume, string status,
        string title, string? error, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangarrDbContext>();
        var existing = await db.ScrobbleSyncStates
            .FirstOrDefaultAsync(s => s.KavitaSeriesId == kavitaSeriesId && s.Service == service, ct);
        if (existing is null)
        {
            db.ScrobbleSyncStates.Add(new ScrobbleSyncState
            {
                KavitaSeriesId = kavitaSeriesId,
                Service = service,
                Chapter = chapter,
                Volume = volume,
                Status = status,
                Title = title,
                SyncedAt = DateTime.UtcNow,
                Error = error,
            });
        }
        else
        {
            existing.Chapter = chapter;
            existing.Volume = volume;
            existing.Status = status;
            existing.Title = title;
            existing.SyncedAt = DateTime.UtcNow;
            existing.Error = error;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Deletes the mapping and sync state so the series re-matches from scratch.</summary>
    public async Task DeleteMappingAsync(int kavitaSeriesId, string service, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangarrDbContext>();
        await db.ScrobbleMappings
            .Where(m => m.KavitaSeriesId == kavitaSeriesId && m.Service == service)
            .ExecuteDeleteAsync(ct);
        await db.ScrobbleSyncStates
            .Where(s => s.KavitaSeriesId == kavitaSeriesId && s.Service == service)
            .ExecuteDeleteAsync(ct);
    }

    public async Task AddLogAsync(
        string level, string service, string title, string message, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangarrDbContext>();
        db.ScrobbleLog.Add(new ScrobbleLogEntry
        {
            Timestamp = DateTime.UtcNow,
            Level = level,
            Service = service,
            Title = title,
            Message = message,
        });
        await db.SaveChangesAsync(ct);
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM ScrobbleLog WHERE Id NOT IN (SELECT Id FROM ScrobbleLog ORDER BY Id DESC LIMIT 500)", ct);
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
