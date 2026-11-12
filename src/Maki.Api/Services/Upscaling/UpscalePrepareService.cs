using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Quality;
using Maki.Core.Reading;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services.Upscaling;

/// <summary>What the last plan held and still has to make, for the settings page.</summary>
public sealed record PrepareSnapshot(int Chapters, int Pages, int Waiting)
{
    public static readonly PrepareSnapshot Empty = new(0, 0, 0);
}

/// <summary>
/// "Prepare chapters ahead". Every few minutes, and soon after a nudge, works out which chapters each
/// reader is likely to open next and hands their missing pages to <see cref="UpscaleQueue"/> as its
/// lowest-priority list, so they are upscaled while nobody is reading. Their cached pages are held
/// out of the cache's eviction while they stay planned.
/// <para>
/// A series counts when the reader set it to Reading, read it within the configured days and reads it
/// with Enhance on Always and a server model that is enabled and downloaded. Auto is left out: what it
/// upscales depends on the screen. A reader's own number for a series (<c>UserSeriesState.PrepareChapters</c>)
/// takes that many chapters, ignores the days and does not use up one of the series slots; 0 leaves
/// the series out. Those series are planned first. The next chapter <see cref="ContinueReadingService"/> names is
/// taken from the page the reader stopped at, and the chapters after it whole, skipping any whose
/// file tier the reader's skip list names. Chapters are planned breadth first (every series' first
/// chapter, then every second one, readers in turn) until the budget runs out, so a short budget drops
/// the least recently read series' later chapters first.
/// </para>
/// </summary>
public sealed class UpscalePrepareService(
    IServiceScopeFactory scopes,
    IAppSettings settings,
    UpscaleModelStore store,
    UpscaleQueue queue,
    UpscaleCache cache,
    TimeProvider clock,
    ILogger<UpscalePrepareService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan FirstRun = TimeSpan.FromMinutes(2);

    /// <summary>Used for a page not made yet when the cache has nothing to average.</summary>
    internal const long FallbackPageBytes = 1_500_000;

    private readonly SemaphoreSlim _nudge = new(0, 1);
    private volatile bool _planned;

    public PrepareSnapshot Last { get; private set; } = PrepareSnapshot.Empty;

    /// <summary>Plans again in a little while: a chapter was finished or a setting changed.</summary>
    public void Nudge()
    {
        try
        {
            _nudge.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await WaitAsync(FirstRun, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await PlanAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Planning the chapters to prepare ahead failed");
                }

                await WaitAsync(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (await _nudge.WaitAsync(timeout, ct))
        {
            await Task.Delay(Settle, ct);
        }
    }

    /// <summary>One chapter to prepare: whose it is, which model, and the first page still to read.</summary>
    internal sealed record Target(int UserId, int ChapterId, string Model, int FromPage);

    /// <summary>Works out the plan and hands it to the queue and the cache.</summary>
    internal async Task<PrepareSnapshot> PlanAsync(CancellationToken ct)
    {
        var options = await UpscaleOptions.LoadAsync(settings, ct);
        var prepare = options.PrepareAhead;
        var models = options.Models.Where(store.IsReady).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
        if (!options.Enabled || !prepare.Enabled || models.Count == 0)
        {
            if (_planned)
            {
                queue.SetBackground([]);
                cache.Hold([]);
                await cache.EnforceCapAsync(ct);
                _planned = false;
            }

            return Last = PrepareSnapshot.Empty;
        }

        await cache.EnsureSeededAsync();
        List<(int UserId, bool AllRootFolders)> users;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
            users = (await db.Users.AsNoTracking()
                    .Where(u => !u.Disabled && !u.PendingSetup)
                    .OrderBy(u => u.Id)
                    .Select(u => new { u.Id, u.AllRootFolders })
                    .ToListAsync(ct))
                .Select(u => (u.Id, u.AllRootFolders))
                .ToList();
        }

        var perUser = new List<List<List<Target>>>();
        foreach (var (userId, allRootFolders) in users)
        {
            try
            {
                var series = await TargetsForAsync(userId, allRootFolders, prepare, models, ct);
                if (series.Count > 0)
                {
                    perUser.Add(series);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not plan the chapters to prepare for user {UserId}", userId);
            }
        }

        var ordered = BreadthFirst(perUser);
        var budget = prepare.Mb * 1024L * 1024L;
        var (_, _, average) = cache.HeldStats();
        var guess = average > 0 ? average : FallbackPageBytes;
        var held = new HashSet<UpscaleWorkKey>();
        var missing = new List<(UpscaleWorkKey Key, ReaderService.PageSlice Slice)>();
        long used = 0;
        var chapters = 0;
        using (var scope = scopes.CreateScope())
        {
            var reader = scope.ServiceProvider.GetRequiredService<ReaderService>();
            foreach (var target in ordered)
            {
                var slice = await reader.PageSliceAsync(target.ChapterId, ct);
                if (slice is null)
                {
                    continue;
                }

                var from = Math.Clamp(target.FromPage, 0, slice.PageCount - 1);
                var keys = Enumerable.Range(from, slice.PageCount - from)
                    .Select(page => new UpscaleWorkKey(slice.ChapterFileId, slice.ArchiveVersion, slice.StartPage + page, target.Model))
                    .Where(key => !held.Contains(key))
                    .ToList();
                var sizes = keys.Select(key => cache.SizeOf(key)).ToList();
                var size = sizes.Sum(s => s ?? guess);
                if (used > 0 && used + size > budget)
                {
                    break;
                }

                used += size;
                chapters++;
                for (var i = 0; i < keys.Count; i++)
                {
                    held.Add(keys[i]);
                    if (sizes[i] is null)
                    {
                        missing.Add((keys[i], slice));
                    }
                }
            }
        }

        cache.Hold(held);
        queue.SetBackground(missing);
        await cache.EnforceCapAsync(ct);
        _planned = true;
        return Last = new PrepareSnapshot(chapters, held.Count, missing.Count);
    }

    /// <summary>
    /// Each series' chapters in turn: the first chapter of every reader's every series, then the second,
    /// readers taking turns within each round.
    /// </summary>
    internal static List<Target> BreadthFirst(IReadOnlyList<List<List<Target>>> perUser)
    {
        var result = new List<Target>();
        var depth = perUser.SelectMany(u => u).Select(s => s.Count).DefaultIfEmpty(0).Max();
        for (var round = 0; round < depth; round++)
        {
            var longest = perUser.Select(u => u.Count).DefaultIfEmpty(0).Max();
            for (var s = 0; s < longest; s++)
            {
                foreach (var series in perUser)
                {
                    if (s < series.Count && round < series[s].Count)
                    {
                        result.Add(series[s][round]);
                    }
                }
            }
        }

        return result;
    }

    /// <summary>One reader's series to prepare, most recently read first, each with its chapters in reading order.</summary>
    private async Task<List<List<Target>>> TargetsForAsync(
        int userId, bool allRootFolders, PrepareOptions prepare, IReadOnlySet<string> models, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        db.Scope.SetUser(userId, allRootFolders);

        var states = await db.UserSeriesStates.AsNoTracking()
            .Where(s => s.Status == ReadingStatus.Reading)
            .Where(s => s.PrepareChapters == null || s.PrepareChapters > 0)
            .Join(db.Series, s => s.SeriesId, x => x.Id,
                (s, x) => new { s.SeriesId, x.Type, s.ReaderPrefsJson, s.ReadingProfileId, s.PrepareChapters })
            .ToListAsync(ct);
        if (states.Count == 0)
        {
            return [];
        }

        var ids = states.Select(s => s.SeriesId).ToList();
        var since = clock.GetUtcNow().UtcDateTime.AddDays(-prepare.Days);
        var lastRead = await db.ChapterProgress.AsNoTracking()
            .OwnedByScopeUser(db)
            .Where(p => ids.Contains(p.SeriesId) && !p.Watched)
            .GroupBy(p => p.SeriesId)
            .Select(g => new { SeriesId = g.Key, At = g.Max(p => p.UpdatedAt) })
            .ToDictionaryAsync(g => g.SeriesId, g => g.At, ct);

        var profiles = new ReadingProfileService(db, new FixedUserSettings(db, userId));
        var eligible = new List<(int SeriesId, string Model, ReaderPrefsSpec Prefs, int Chapters)>();
        var slots = prepare.Series;
        var candidates = states
            .Where(s => s.PrepareChapters is not null || lastRead.GetValueOrDefault(s.SeriesId) >= since)
            .OrderBy(s => s.PrepareChapters is null)
            .ThenByDescending(s => lastRead.GetValueOrDefault(s.SeriesId));
        foreach (var state in candidates)
        {
            if (state.PrepareChapters is null && slots == 0)
            {
                break;
            }

            var prefs = (await profiles.ResolveForAsync(state.Type, state.ReaderPrefsJson, state.ReadingProfileId, ct)).Prefs;
            if (ServerModel(prefs) is { } model && models.Contains(model))
            {
                eligible.Add((state.SeriesId, model, prefs, state.PrepareChapters ?? prepare.Chapters));
                if (state.PrepareChapters is null)
                {
                    slots--;
                }
            }
        }

        if (eligible.Count == 0)
        {
            return [];
        }

        var next = await scope.ServiceProvider.GetRequiredService<ContinueReadingService>()
            .NextForAsync(eligible.Select(e => e.SeriesId).ToList(), ct);
        var unreadIds = next.Values.SelectMany(n => n.UnreadChapterIds).ToList();
        var chapters = (await db.Chapters.AsNoTracking()
                .Where(c => unreadIds.Contains(c.Id) && c.ChapterFile != null)
                .Select(c => new { c.Id, c.SeriesId, c.Number, c.Volume, c.ChapterFile!.Tier })
                .ToListAsync(ct))
            .GroupBy(c => c.SeriesId)
            .ToDictionary(g => g.Key, g => ChapterOrder.Sort(g, c => c.Number, c => c.Volume, c => c.Id).ToList());
        var resume = await db.ChapterProgress.AsNoTracking()
            .OwnedByScopeUser(db)
            .Where(p => unreadIds.Contains(p.ChapterId) && !p.Completed && p.UnreadAt == null)
            .ToDictionaryAsync(p => p.ChapterId, p => p.PageIndex, ct);

        var result = new List<List<Target>>();
        foreach (var (seriesId, model, prefs, depth) in eligible)
        {
            if (!next.TryGetValue(seriesId, out var first) || !chapters.TryGetValue(seriesId, out var ordered))
            {
                continue;
            }

            var start = ordered.FindIndex(c => c.Id == first.ChapterId);
            if (start < 0)
            {
                continue;
            }

            var skip = prefs.EnhanceSkipTiers ?? ReaderPrefsSpec.DefaultEnhanceSkipTiers;
            var targets = ordered.Skip(start).Take(depth)
                .Where(c => !skip.Contains(QualitySnapshot.TierName(c.Tier)))
                .Select(c => new Target(userId, c.Id, model, resume.GetValueOrDefault(c.Id)))
                .ToList();
            if (targets.Count > 0)
            {
                result.Add(targets);
            }
        }

        return result;
    }

    /// <summary>The model a series' settings ask the server for when every page is enhanced, else null.</summary>
    internal static string? ServerModel(ReaderPrefsSpec prefs) =>
        prefs.Enhance == "always" && prefs.EnhanceMethod.StartsWith("server:", StringComparison.Ordinal)
            ? prefs.EnhanceMethod["server:".Length..]
            : null;

    /// <summary>One user's settings for code running outside their request.</summary>
    private sealed class FixedUserSettings(MakiDbContext db, int userId) : IUserSettings
    {
        public int UserId => userId;

        public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
            UserSettingsStore.GetAsync(db, userId, key, ct);

        public Task<Dictionary<string, string>> GetManyAsync(IReadOnlyCollection<string> keys, CancellationToken ct = default) =>
            UserSettingsStore.GetManyAsync(db, userId, keys, ct);

        public Task SetAsync(string key, string? value, CancellationToken ct = default) =>
            UserSettingsStore.SetAsync(db, userId, key, value, ct);
    }
}
