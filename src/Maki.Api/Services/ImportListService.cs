using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Maki.Api.Localization;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Inbox;
using Maki.Core.Notifications;
using Maki.Core.Scrobbling;
using Maki.Core.Security;
using Maki.Data;
using Maki.Metadata.MangaBaka;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

public record ImportListRunResult(
    int Added, int Requested, int Skipped, int AlreadyPresent, int Errors, bool DumpUnavailable = false)
{
    public static readonly ImportListRunResult Empty = new(0, 0, 0, 0, 0);

    public static ImportListRunResult operator +(ImportListRunResult a, ImportListRunResult b) => new(
        a.Added + b.Added, a.Requested + b.Requested, a.Skipped + b.Skipped,
        a.AlreadyPresent + b.AlreadyPresent, a.Errors + b.Errors, a.DumpUnavailable || b.DumpUnavailable);
}

/// <summary>
/// Sonarr-style import lists: pulls each user's tracker list, maps the entries to MangaBaka ids and
/// adds what the library is missing. A user without <see cref="MakiPermission.AddSeries"/> gets a
/// <see cref="SeriesRequest"/> filed for them instead.
/// <para>
/// Every entry the run has dealt with lands in <see cref="ImportListSkip"/>, which is also the
/// provenance record that stops a deleted series from coming straight back (see that entity).
/// </para>
/// </summary>
public class ImportListService(
    IServiceScopeFactory scopeFactory,
    IAppSettings settings,
    IUserSettingsStore userSettings,
    IEnumerable<IScrobbleTracker> trackers,
    MangaBakaLocalStore store,
    InboxService inbox,
    IUserLocaleResolver locales,
    IMessageCatalog catalog,
    ILogger<ImportListService> logger)
{
    public const int DefaultIntervalMinutes = 360;
    public const int MinIntervalMinutes = 15;
    public const string AddedFrom = "importlist";

    /// <summary>An Unmatched row older than this is dropped and its entry resolved again.</summary>
    public static readonly TimeSpan UnmatchedRetryAfter = TimeSpan.FromDays(7);

    private readonly IScrobbleTracker[] _trackers = [.. trackers];
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _userLocks = new();
    private readonly SemaphoreSlim _tickLock = new(1, 1);

    public IReadOnlyList<IScrobbleTracker> Trackers => _trackers;

    public IScrobbleTracker? FindTracker(string service) => _trackers.FirstOrDefault(t => t.Name == service);

    public async Task<bool> EnabledAsync(CancellationToken ct = default) =>
        await settings.GetAsync(SettingKeys.ImportListEnabled, ct) != "false";

    public async Task<int> IntervalMinutesAsync(CancellationToken ct = default) =>
        int.TryParse(await settings.GetAsync(SettingKeys.ImportListIntervalMinutes, ct), out var m) && m >= MinIntervalMinutes
            ? m
            : DefaultIntervalMinutes;

    public async Task<DateTime?> LastRunAtAsync(CancellationToken ct = default) =>
        DateTime.TryParse(await settings.GetAsync(SettingKeys.ImportListLastRunAt, ct), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : null;

    /// <summary>The same id for the same entry every time, so a retried add replays instead of duplicating.</summary>
    public static Guid MutationId(int userId, string service, string remoteId) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"importlist:{userId}:{service}:{remoteId}")).AsSpan(0, 16));

    /// <summary>Runs every user's enabled lists once the configured interval has elapsed.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        if (!await EnabledAsync(ct))
        {
            return;
        }

        var interval = TimeSpan.FromMinutes(await IntervalMinutesAsync(ct));
        if (await LastRunAtAsync(ct) is { } last && DateTime.UtcNow - last < interval)
        {
            return;
        }

        if (!await _tickLock.WaitAsync(0, ct))
        {
            return;
        }

        try
        {
            var state = new RunState();
            foreach (var userId in await UsersWithEnabledListsAsync(ct))
            {
                try
                {
                    if (await RunUserAsync(userId, null, full: false, state, ct) is null)
                    {
                        logger.LogDebug("Import list tick skipped user {UserId}: a manual run is in progress", userId);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Import list run failed for user {UserId}", userId);
                }
            }
        }
        finally
        {
            await settings.SetAsync(SettingKeys.ImportListLastRunAt,
                DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), CancellationToken.None);
            _tickLock.Release();
        }
    }

    private async Task<List<int>> UsersWithEnabledListsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        var rows = await db.UserSettings.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.Key == SettingKeys.ImportListPrefs)
            .Select(s => new { s.UserId, s.Value })
            .ToListAsync(ct);
        return
        [
            .. rows.Where(r => ImportListPrefs.Parse(r.Value).Values.Any(p => p.Enabled))
                .Select(r => r.UserId).Distinct().Order(),
        ];
    }

    /// <summary>State shared by every run inside one scheduled tick.</summary>
    private sealed class RunState
    {
        public bool DumpNoticeLogged { get; set; }
    }

    private SemaphoreSlim Gate(int userId) => _userLocks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// One user's import lists. With <paramref name="service"/> null, every tracker the user enabled;
    /// naming one runs that tracker even when its scheduled run is off, since somebody asked for it.
    /// <paramref name="full"/> ignores <see cref="ImportListTrackerPrefs.MaxPerRun"/> for a user who
    /// can add series; requests stay capped. Returns null when a run for this user is already in progress.
    /// </summary>
    public Task<ImportListRunResult?> RunUserAsync(int userId, string? service, bool full, CancellationToken ct) =>
        RunUserAsync(userId, service, full, new RunState(), ct);

    private async Task<ImportListRunResult?> RunUserAsync(
        int userId, string? service, bool full, RunState state, CancellationToken ct)
    {
        var gate = Gate(userId);
        if (!await gate.WaitAsync(0, ct))
        {
            return null;
        }

        try
        {
            return await RunUserInnerAsync(userId, service, full, state, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Starts a full run off the request and returns its task, or null when a run for this user is
    /// already in progress. The outcome reaches the user through the inbox and the last-run record.
    /// </summary>
    public Task<ImportListRunResult>? StartFullRun(int userId, string? service)
    {
        var gate = Gate(userId);
        if (!gate.Wait(0))
        {
            return null;
        }

        return Task.Run(async () =>
        {
            try
            {
                return await RunUserInnerAsync(userId, service, full: true, new RunState(), CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Full import list run failed for user {UserId}", userId);
                return ImportListRunResult.Empty with { Errors = 1 };
            }
            finally
            {
                gate.Release();
            }
        });
    }

    private sealed record UserInfo(int Id, string UserName, MakiPermission Permissions, bool AllRootFolders);

    private async Task<ImportListRunResult> RunUserInnerAsync(
        int userId, string? service, bool full, RunState state, CancellationToken ct)
    {
        UserInfo? user;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
            user = await db.Users.AsNoTracking()
                .Where(u => u.Id == userId && !u.Disabled)
                .Select(u => new UserInfo(u.Id, u.UserName ?? string.Empty, u.Permissions, u.AllRootFolders))
                .FirstOrDefaultAsync(ct);
        }

        if (user is null || !user.Permissions.Grants(MakiPermission.UseTrackers))
        {
            return ImportListRunResult.Empty;
        }

        var prefs = ImportListPrefs.Parse(await userSettings.GetAsync(userId, SettingKeys.ImportListPrefs, ct));
        var targets = service is null
            ? _trackers.Where(t => ImportListPrefs.For(prefs, t.Name).Enabled)
            : _trackers.Where(t => t.Name == service);

        var total = ImportListRunResult.Empty;
        foreach (var tracker in targets)
        {
            ImportListRunResult result;
            try
            {
                result = await RunTrackerAsync(user, tracker, ImportListPrefs.For(prefs, tracker.Name), full, state, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Import list {Service} failed for user {UserId}", tracker.Name, userId);
                result = ImportListRunResult.Empty with { Errors = 1 };
            }

            await userSettings.SetAsync(userId, SettingKeys.ImportListLastRunKey(tracker.Name),
                new ImportListLastRun(DateTime.UtcNow, result.Added, result.Requested, result.Skipped, result.Errors,
                    result.DumpUnavailable).Serialize(),
                CancellationToken.None);

            if (full || result.Added + result.Requested > 0 || result.Errors > 0)
            {
                inbox.Raise(InboxEventType.ImportListFinished, new InboxMessage(
                        Key: "inbox.importList.finished",
                        Params: InboxMessage.Args(new
                        {
                            tracker = tracker.Label,
                            added = result.Added,
                            requested = result.Requested,
                            skipped = result.Skipped,
                            errors = result.Errors,
                        }),
                        Level: result.Errors > 0 ? NotificationLevel.Warning : NotificationLevel.Info,
                        Url: "/scrobble"),
                    InboxAudience.User(userId));
            }

            total += result;
        }

        return total;
    }

    /// <summary>One MangaBaka id to import, with any further list entries that resolved to the same id.</summary>
    private sealed record Candidate(RemoteListEntry Entry, int MangaBakaId)
    {
        public List<RemoteListEntry> Duplicates { get; } = [];

        public IEnumerable<RemoteListEntry> All => [Entry, .. Duplicates];
    }

    private async Task<ImportListRunResult> RunTrackerAsync(
        UserInfo user, IScrobbleTracker tracker, ImportListTrackerPrefs prefs, bool full, RunState state,
        CancellationToken ct)
    {
        if (!await tracker.ConfiguredAsync(ct) || !await tracker.AuthenticatedAsync(user.Id, ct))
        {
            return ImportListRunResult.Empty;
        }

        var entries = (await tracker.ListAsync(user.Id, prefs.ParsedStatuses(), ct))
            .Where(e => !string.IsNullOrWhiteSpace(e.RemoteId))
            .DistinctBy(e => e.RemoteId)
            .ToList();

        int added = 0, requested = 0, skipped = 0, present = 0, errors = 0;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();

        var retryBefore = DateTime.UtcNow - UnmatchedRetryAfter;
        await db.ImportListSkips.IgnoreQueryFilters()
            .Where(x => x.UserId == user.Id && x.Service == tracker.Name &&
                        x.Reason == ImportListSkipReason.Unmatched && x.CreatedAt < retryBefore)
            .ExecuteDeleteAsync(ct);

        var handled = await db.ImportListSkips.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.UserId == user.Id && x.Service == tracker.Name)
            .ToDictionaryAsync(x => x.RemoteId, x => x.Reason, ct);

        var fresh = new List<RemoteListEntry>();
        foreach (var entry in entries)
        {
            if (handled.TryGetValue(entry.RemoteId, out var reason))
            {
                if (reason == ImportListSkipReason.Added) present++;
                else skipped++;
                continue;
            }

            fresh.Add(entry);
        }

        var resolved = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in fresh)
        {
            if (entry.MangaBakaId is > 0 and <= int.MaxValue)
            {
                resolved[entry.RemoteId] = (int)entry.MangaBakaId.Value;
            }
        }

        var unresolved = fresh.Where(e => !resolved.ContainsKey(e.RemoteId)).ToList();
        if (unresolved.Count > 0)
        {
            if (!await store.IsAvailableAsync(ct))
            {
                // Not an error and not recorded as unmatched: nothing is wrong with the list, and the
                // entries may well match once the local database is downloaded.
                if (!state.DumpNoticeLogged)
                {
                    state.DumpNoticeLogged = true;
                    logger.LogInformation(
                        "Import lists skipped: the local MangaBaka database is not downloaded yet, so tracker ids cannot be resolved");
                }

                return ImportListRunResult.Empty with { DumpUnavailable = true };
            }

            await ResolveAsync(MangaBakaLocalStore.ExternalSource.AniList, e => e.AniListId);
            await ResolveAsync(MangaBakaLocalStore.ExternalSource.MyAnimeList, e => e.MalId);
            await ResolveAsync(MangaBakaLocalStore.ExternalSource.Kitsu, e => e.KitsuId);

            var unmatched = unresolved.Where(e => !resolved.ContainsKey(e.RemoteId)).ToList();
            await RecordAsync(user.Id, tracker.Name, unmatched, ImportListSkipReason.Unmatched, null, ct);
            skipped += unmatched.Count;
        }

        var candidates = new List<Candidate>();
        var byId = new Dictionary<int, Candidate>();
        foreach (var entry in fresh)
        {
            if (!resolved.TryGetValue(entry.RemoteId, out var id)) continue;
            if (byId.TryGetValue(id, out var first))
            {
                first.Duplicates.Add(entry);
            }
            else
            {
                byId[id] = new Candidate(entry, id);
                candidates.Add(byId[id]);
            }
        }

        if (candidates.Count > 0)
        {
            var ids = candidates.Select(c => c.MangaBakaId).ToList();
            var inLibrary = await db.Series.IgnoreQueryFilters()
                .Where(s => s.MangaBakaId != null && ids.Contains(s.MangaBakaId.Value))
                .Select(s => s.MangaBakaId!.Value)
                .ToListAsync(ct);
            var idStrings = ids.Select(i => i.ToString(CultureInfo.InvariantCulture)).ToList();
            var pending = await db.SeriesRequests.IgnoreQueryFilters()
                .Where(r => r.Kind == SeriesRequestKind.NewSeries &&
                            (r.Status == SeriesRequestStatus.Pending || r.Status == SeriesRequestStatus.Processing) &&
                            r.MetadataProviderId != null && idStrings.Contains(r.MetadataProviderId))
                .Select(r => r.MetadataProviderId!)
                .ToListAsync(ct);
            var drop = inLibrary.ToHashSet();
            drop.UnionWith(pending.Select(p => int.Parse(p, CultureInfo.InvariantCulture)));

            // Recorded as Added, not merely counted: the row is what lets a later delete of the series
            // flip this user's entry to Removed. Without it the entry would be re-added straight away.
            foreach (var candidate in candidates.Where(c => drop.Contains(c.MangaBakaId)))
            {
                present += await RecordAsync(user.Id, tracker.Name, candidate.All, ImportListSkipReason.Added,
                    candidate.MangaBakaId, ct);
            }

            candidates.RemoveAll(c => drop.Contains(c.MangaBakaId));
        }

        // A full run lifts the cap only for adds. Requests stay capped, so "sync everything" cannot
        // drop hundreds of them on the admins at once.
        var canAdd = user.Permissions.Grants(MakiPermission.AddSeries);
        var batch = full && canAdd ? candidates : candidates.Take(prefs.MaxPerRun).ToList();
        if (batch.Count == 0)
        {
            return new(added, requested, skipped, present, errors);
        }

        var rootFolderId = canAdd ? await RootFolderForAsync(db, user, prefs.RootFolderId, ct) : null;
        if (canAdd && rootFolderId is null)
        {
            logger.LogWarning(
                "Import list {Service} for user {UserId}: no root folder available, nothing added",
                tracker.Name, user.Id);
            return new(added, requested, skipped, present, errors + 1);
        }

        var note = canAdd
            ? null
            : catalog.GetFor(await locales.DefaultAsync(ct), "notify.importList.requestNote",
                new { tracker = tracker.Label });

        foreach (var candidate in batch)
        {
            try
            {
                var outcome = canAdd
                    ? await AddAsync(user, tracker, prefs, rootFolderId!.Value, candidate, ct)
                    : await RequestAsync(user, candidate, note!, ct);

                switch (outcome)
                {
                    case Outcome.Added or Outcome.Requested or Outcome.Present:
                        var recorded = await RecordAsync(user.Id, tracker.Name, candidate.All,
                            ImportListSkipReason.Added, candidate.MangaBakaId, ct);
                        if (outcome == Outcome.Added) added++;
                        else if (outcome == Outcome.Requested) requested++;
                        else present++;
                        present += recorded - 1;
                        break;
                    case Outcome.Unmatched:
                        skipped += await RecordAsync(user.Id, tracker.Name, candidate.All,
                            ImportListSkipReason.Unmatched, null, ct);
                        break;
                    case Outcome.Removed:
                        skipped += await RecordAsync(user.Id, tracker.Name, candidate.All,
                            ImportListSkipReason.Removed, candidate.MangaBakaId, ct);
                        break;
                    default:
                        errors++;
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Import list {Service} could not import {Title} ({RemoteId}) for user {UserId}",
                    tracker.Name, candidate.Entry.Title, candidate.Entry.RemoteId, user.Id);
                errors++;
            }
        }

        return new(added, requested, skipped, present, errors);

        async Task ResolveAsync(MangaBakaLocalStore.ExternalSource source, Func<RemoteListEntry, long?> key)
        {
            var pendingEntries = unresolved.Where(e => !resolved.ContainsKey(e.RemoteId) && key(e) is > 0).ToList();
            if (pendingEntries.Count == 0) return;

            var map = await store.GetIdsByExternalIdsAsync(
                source, pendingEntries.Select(e => key(e)!.Value).Distinct().ToList(), ct);
            foreach (var entry in pendingEntries)
            {
                if (map.TryGetValue(key(entry)!.Value, out var mangaBakaId) && mangaBakaId is > 0 and <= int.MaxValue)
                {
                    resolved[entry.RemoteId] = (int)mangaBakaId;
                }
            }
        }
    }

    private enum Outcome { Added, Requested, Present, Unmatched, Removed, Failed }

    private async Task<Outcome> AddAsync(
        UserInfo user, IScrobbleTracker tracker, ImportListTrackerPrefs prefs, int rootFolderId,
        Candidate candidate, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var creation = scope.ServiceProvider.GetRequiredService<SeriesCreationService>();
        var result = await creation.CreateAsync(
            candidate.MangaBakaId.ToString(CultureInfo.InvariantCulture), rootFolderId, prefs.Monitored,
            prefs.MonitorNewItems, ct,
            deferSourceMatching: true,
            attributedUserId: user.Id,
            addedFrom: AddedFrom,
            clientMutationId: MutationId(user.Id, tracker.Name, candidate.Entry.RemoteId));

        if (result.Series is not null)
        {
            logger.LogInformation("Import list {Service} added {Title} for user {UserId}",
                tracker.Name, result.Series.Title, user.Id);
            return Outcome.Added;
        }

        return result.Error switch
        {
            SeriesCreationError.AlreadyInLibrary => Outcome.Present,
            SeriesCreationError.MetadataNotFound => Outcome.Unmatched,
            SeriesCreationError.OperationResultGone => Outcome.Removed,
            _ => Outcome.Failed,
        };
    }

    private async Task<Outcome> RequestAsync(UserInfo user, Candidate candidate, string note, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var submitter = scope.ServiceProvider.GetRequiredService<SeriesRequestSubmitter>();
        var request = new SeriesRequest
        {
            UserId = user.Id,
            Kind = SeriesRequestKind.NewSeries,
            Status = SeriesRequestStatus.Pending,
            Note = note,
            Created = DateTime.UtcNow,
        };

        if (await submitter.FillNewSeriesAsync(
                request, candidate.MangaBakaId.ToString(CultureInfo.InvariantCulture), ct) is { } failed)
        {
            return failed.Error == SeriesRequestSubmitError.SeriesAlreadyExists ? Outcome.Present : Outcome.Unmatched;
        }

        var submitted = await submitter.SubmitAsync(request, user.UserName, ct);
        return submitted.Error is null ? Outcome.Requested : Outcome.Present;
    }

    /// <summary>
    /// The preferred root folder when the user can still see it, otherwise the first one they can.
    /// </summary>
    public static async Task<int?> RootFolderForAsync(
        MakiDbContext db, int userId, bool allRootFolders, int? preferred, CancellationToken ct)
    {
        var visible = allRootFolders
            ? await db.RootFolders.AsNoTracking().OrderBy(r => r.Id).Select(r => r.Id).ToListAsync(ct)
            : await db.UserRootFolders.AsNoTracking()
                .Where(g => g.UserId == userId)
                .Join(db.RootFolders, g => g.RootFolderId, r => r.Id, (g, r) => r.Id)
                .OrderBy(id => id)
                .ToListAsync(ct);

        if (preferred is { } id && visible.Contains(id)) return id;
        return visible.Count > 0 ? visible[0] : null;
    }

    private static Task<int?> RootFolderForAsync(MakiDbContext db, UserInfo user, int? preferred, CancellationToken ct) =>
        RootFolderForAsync(db, user.Id, user.AllRootFolders, preferred, ct);

    /// <summary>Upserts one row per entry and returns how many entries it recorded.</summary>
    private async Task<int> RecordAsync(
        int userId, string service, IEnumerable<RemoteListEntry> entries, ImportListSkipReason reason,
        int? mangaBakaId, CancellationToken ct)
    {
        var list = entries.ToList();
        if (list.Count == 0)
        {
            return 0;
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        var remoteIds = list.Select(e => e.RemoteId).ToList();
        var existing = await db.ImportListSkips.IgnoreQueryFilters()
            .Where(x => x.UserId == userId && x.Service == service && remoteIds.Contains(x.RemoteId))
            .ToDictionaryAsync(x => x.RemoteId, ct);

        foreach (var entry in list)
        {
            if (!existing.TryGetValue(entry.RemoteId, out var row))
            {
                row = new ImportListSkip { UserId = userId, Service = service, RemoteId = entry.RemoteId };
                db.ImportListSkips.Add(row);
                existing[entry.RemoteId] = row;
            }

            row.Title = entry.Title;
            row.Reason = reason;
            row.MangaBakaId = mangaBakaId;
            row.CreatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        return list.Count;
    }

    /// <summary>
    /// Called when a series is deleted: every import list that added it marks its entry Removed, so the
    /// next run does not add it straight back.
    /// </summary>
    public static async Task MarkRemovedAsync(MakiDbContext db, int mangaBakaId, CancellationToken ct) =>
        await db.ImportListSkips.IgnoreQueryFilters()
            .Where(x => x.MangaBakaId == mangaBakaId && x.Reason == ImportListSkipReason.Added)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.Reason, ImportListSkipReason.Removed), ct);
}
