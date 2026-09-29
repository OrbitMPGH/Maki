using System.Text.Json;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Quality;
using Maki.Core.Sources;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>What happened to one candidate copy during a single-chapter scan.</summary>
/// <param name="Reason">An <see cref="UpgradeReasons"/> code, or a skip code such as <c>probe_budget</c>.</param>
public sealed record UpgradeCandidateOutcome(int MappingId, string SourceName, string SourceChapterId,
    string Reason, bool Probed, int? PageCount, int? MedianWidth, int? Score);

/// <param name="Skipped">How many chapters or candidates were passed over, keyed by reason code.</param>
/// <param name="Candidates">One entry per candidate; filled for single-chapter scans only.</param>
/// <param name="QueuedFromMappingId">The mapping a single-chapter scan queued its upgrade from.</param>
public sealed record UpgradeScanResult(int SeriesScanned, int ChaptersChecked, int CandidatesProbed, int Enqueued,
    IReadOnlyDictionary<string, int> Skipped, IReadOnlyList<UpgradeCandidateOutcome> Candidates,
    int? QueuedFromMappingId);

/// <summary>Another scan holds the single-flight gate.</summary>
public sealed class UpgradeScanBusyException() : Exception("An upgrade scan is already running");

/// <summary>Writes to the <see cref="UpgradeAttempt"/> memo. The caller saves.</summary>
public static class UpgradeAttempts
{
    /// <summary>Updates the row for this candidate and profile version in place, or adds one.</summary>
    public static async Task<UpgradeAttempt> UpsertAsync(MakiDbContext db, int chapterId, int seriesId, int mappingId,
        string sourceChapterId, int profileId, int profileVersion, string reason, bool probed, int? pageCount,
        int? width, int? score, CancellationToken ct)
    {
        var row = db.UpgradeAttempts.Local.FirstOrDefault(Match)
                  ?? await db.UpgradeAttempts.IgnoreQueryFilters().FirstOrDefaultAsync(a =>
                      a.ChapterId == chapterId && a.SourceMappingId == mappingId && a.SourceChapterId == sourceChapterId &&
                      a.ProfileId == profileId && a.ProfileVersion == profileVersion, ct);
        if (row is null)
        {
            row = new UpgradeAttempt
            {
                ChapterId = chapterId,
                SourceMappingId = mappingId,
                SourceChapterId = sourceChapterId,
                ProfileId = profileId,
                ProfileVersion = profileVersion
            };
            db.UpgradeAttempts.Add(row);
        }

        row.SeriesId = seriesId;
        row.Reason = reason;
        row.Probed = probed;
        row.CandidatePageCount = pageCount;
        row.CandidateWidth = width;
        row.CandidateScore = score;
        row.CreatedAtUtc = DateTime.UtcNow;
        return row;

        bool Match(UpgradeAttempt a) =>
            a.ChapterId == chapterId && a.SourceMappingId == mappingId && a.SourceChapterId == sourceChapterId &&
            a.ProfileId == profileId && a.ProfileVersion == profileVersion;
    }
}

/// <summary>
/// Finds better copies of chapters the library already has a file for and queues them as
/// <see cref="DownloadOrigin.Upgrade"/> downloads. Only chapters with a file are ever looked at, and
/// <see cref="Chapter.Wanted"/> is neither read nor written: getting missing chapters is somebody
/// else's job, so even a scoring bug here cannot turn into a download of something new.
/// </summary>
public class UpgradeScanService(
    MakiDbContext db,
    UpgradeEvaluationService evaluation,
    SourceRegistry registry,
    SourceAvailability availability,
    SourceProbeService probes,
    DownloadQueueService queue,
    DownloadBatchNotifier batches,
    IAppSettings settings,
    TimeProvider time,
    ILogger<UpgradeScanService> logger)
{
    public const int SampleCount = 6;

    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static bool IsRunning => Gate.CurrentCount == 0;

    /// <summary>
    /// The pass over every series. Does nothing while <c>upgrades.enabled</c> is off unless
    /// <paramref name="ignoreGlobalSwitch"/> says an admin asked for it. A completed pass writes
    /// <c>upgrades.lastScanDate</c>.
    /// </summary>
    /// <exception cref="UpgradeScanBusyException">Another scan is running.</exception>
    public Task<UpgradeScanResult> ScanAllAsync(CancellationToken ct, bool ignoreGlobalSwitch = false) =>
        RunAsync(null, ignoreGlobalSwitch, ct);

    /// <summary>One series, whatever the global switch says, so an admin can try a profile out.</summary>
    /// <exception cref="UpgradeScanBusyException">Another scan is running.</exception>
    public Task<UpgradeScanResult> ScanSeriesAsync(int seriesId, CancellationToken ct) => RunAsync(seriesId, true, ct);

    /// <summary>
    /// One chapter, for "Upgrade now": ignores the global switch, the quiet period and the daily cap,
    /// and reports what happened to every candidate. An empty result for a chapter without a file.
    /// </summary>
    /// <exception cref="UpgradeScanBusyException">Another scan is running.</exception>
    /// <param name="userId">Who asked; recorded on the queued row so its completion stays out of the inbox.</param>
    public async Task<UpgradeScanResult> ScanChapterAsync(int chapterId, int? userId, CancellationToken ct)
    {
        var seriesId = await db.Chapters.AsNoTracking()
            .Where(c => c.Id == chapterId && c.ChapterFileId != null)
            .Select(c => (int?)c.SeriesId)
            .FirstOrDefaultAsync(ct);
        return seriesId is { } id ? await RunAsync(id, true, ct, chapterId, userId) : new Run(0, chapterId).Result();
    }

    private async Task<UpgradeScanResult> RunAsync(int? onlySeries, bool ignoreGlobalSwitch, CancellationToken ct,
        int? onlyChapter = null, int? userId = null)
    {
        if (!await Gate.WaitAsync(0, ct))
        {
            throw new UpgradeScanBusyException();
        }

        try
        {
            var options = await UpgradeOptions.LoadAsync(settings, ct);
            var run = new Run(options.MaxProbesPerRun, onlyChapter) { Targeted = onlySeries is not null, UserId = userId };
            if (!ignoreGlobalSwitch && !options.Enabled)
            {
                return run.Result();
            }

            var today = time.GetUtcNow().UtcDateTime.Date;
            run.EnqueuedToday = await db.DownloadQueue.IgnoreQueryFilters()
                .CountAsync(q => q.Origin == DownloadOrigin.Upgrade && q.QueuedAt >= today, ct);
            var disabled = (await availability.DisabledAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var seriesIds = onlySeries is { } id
                ? [id]
                : await db.Series.OrderBy(s => s.Id).Select(s => s.Id).ToListAsync(ct);

            foreach (var seriesId in seriesIds)
            {
                ct.ThrowIfCancellationRequested();
                await ScanSeriesCoreAsync(seriesId, options, disabled, run, ct);

                // One context serves the whole library pass. Without this every series' memo rows
                // stay tracked, and each later SaveChanges runs DetectChanges over all of them. The
                // memo stays tracked within a series because UpgradeAttempts.UpsertAsync looks in
                // Local before it queries.
                if (onlySeries is null)
                {
                    db.ChangeTracker.Clear();
                }
            }

            if (onlySeries is null)
            {
                await settings.SetAsync(SettingKeys.UpgradesLastScanDate,
                    UpgradeOptions.MarkerDate(UpgradeOptions.LocalNow(time)), ct);
            }

            var result = run.Result();
            logger.LogInformation(
                "Upgrade scan: {Series} series, {Chapters} chapters, {Probed} probed, {Enqueued} queued",
                result.SeriesScanned, result.ChaptersChecked, result.CandidatesProbed, result.Enqueued);
            return result;
        }
        finally
        {
            Gate.Release();
        }
    }

    private sealed class Run(int probeBudget, int? onlyChapter)
    {
        public int? OnlyChapter { get; } = onlyChapter;

        /// <summary>A series or chapter someone asked for, so series-level skips are worth reporting.</summary>
        public bool Targeted { get; init; }

        /// <summary>A whole series scanned because someone asked: waits for nothing and re-checks every candidate.</summary>
        public bool ByHand => Targeted && OnlyChapter is null;

        public int? UserId { get; init; }

        public List<UpgradeCandidateOutcome>? Outcomes { get; } = onlyChapter is null ? null : [];
        public int? QueuedFromMappingId { get; set; }
        public int ProbesLeft { get; set; } = probeBudget;
        public int SeriesScanned { get; set; }
        public int ChaptersChecked { get; set; }
        public int CandidatesProbed { get; set; }
        public int Enqueued { get; set; }
        public int EnqueuedToday { get; set; }
        public Dictionary<string, int> Skipped { get; } = new(StringComparer.Ordinal);

        public void Skip(string reason) => Skipped[reason] = Skipped.GetValueOrDefault(reason) + 1;

        public void Outcome(SourceMapping mapping, string sourceChapterId, string reason, bool probed, int? pageCount,
            int? width, int? score) =>
            Outcomes?.Add(new UpgradeCandidateOutcome(mapping.Id, mapping.SourceName, sourceChapterId, reason, probed,
                pageCount, width, score));

        public UpgradeScanResult Result() =>
            new(SeriesScanned, ChaptersChecked, CandidatesProbed, Enqueued, new Dictionary<string, int>(Skipped),
                Outcomes is null ? [] : [.. Outcomes], QueuedFromMappingId);
    }

    private sealed record Survivor(
        Chapter Chapter, ChapterFile File, QualityScore Current, SourceMapping Mapping, ISource Source,
        ChapterSourceLink Link, string? Group, QualityScore Optimistic);

    private sealed record Winner(Survivor Survivor, QualityScore Score, ProbeResult Probe, long? SizeBytes);

    private async Task ScanSeriesCoreAsync(
        int seriesId, UpgradeOptions options, HashSet<string> disabled, Run run, CancellationToken ct)
    {
        var series = await db.Series.AsNoTracking()
            .Where(s => s.Id == seriesId)
            .Select(s => new { s.Id, s.Title, s.Incognito })
            .FirstOrDefaultAsync(ct);
        if (series is null)
        {
            return;
        }

        var evaluator = await evaluation.ForSeriesAsync(seriesId, ct);
        var skip = evaluator is null ? "no_profile"
            : !evaluator.Profile.UpgradesEnabled ? "upgrades_disabled"
            : series.Incognito != IncognitoMode.Off && !options.ScanIncognito ? "incognito"
            : null;
        if (skip is not null)
        {
            if (run.Targeted)
            {
                run.Skip(skip);
            }

            return;
        }

        run.SeriesScanned++;
        var probedBefore = run.CandidatesProbed;
        var enqueuedBefore = run.Enqueued;
        var checkedBefore = run.ChaptersChecked;
        var skippedBefore = new Dictionary<string, int>(run.Skipped);
        var profile = evaluator!.Profile;
        var now = time.GetUtcNow().UtcDateTime;

        var chapters = await db.Chapters.AsNoTracking()
            .Where(c => c.SeriesId == seriesId && c.ChapterFileId != null &&
                        (run.OnlyChapter == null || c.Id == run.OnlyChapter))
            .Include(c => c.ChapterFile)
            .Include(c => c.SourceLinks).ThenInclude(l => l.SourceMapping)
            .ToListAsync(ct);
        var fileUse = (await db.Chapters.AsNoTracking()
                .Where(c => c.SeriesId == seriesId && c.ChapterFileId != null)
                .Select(c => c.ChapterFileId!.Value)
                .ToListAsync(ct))
            .GroupBy(id => id)
            .ToDictionary(g => g.Key, g => g.Count());
        var active = (await db.DownloadQueue
                .Where(q => q.SeriesId == seriesId && q.ChapterId != null &&
                            q.Status != QueueStatus.Completed && q.Status != QueueStatus.Failed &&
                            q.Status != QueueStatus.Cancelled)
                .Select(q => q.ChapterId!.Value)
                .ToListAsync(ct))
            .ToHashSet();
        // An `enqueued` memo only stands while that download could still happen. One that failed or
        // was cancelled (or was cleared from history) leaves the candidate open again.
        var lastUpgrade = (await db.DownloadQueue
                .Where(q => q.SeriesId == seriesId && q.ChapterId != null && q.Origin == DownloadOrigin.Upgrade)
                .Select(q => new { ChapterId = q.ChapterId!.Value, q.Id, q.Status })
                .ToListAsync(ct))
            .GroupBy(q => q.ChapterId)
            .ToDictionary(g => g.Key, g => (QueueStatus?)g.MaxBy(q => q.Id)!.Status);
        var memo = (await db.UpgradeAttempts
                .Where(a => a.SeriesId == seriesId && a.ProfileId == profile.Id && a.ProfileVersion == profile.Version)
                .ToListAsync(ct))
            .ToDictionary(a => (a.ChapterId, a.SourceMappingId, a.SourceChapterId));
        var estimates = await SourceQualitySamples.EstimatesAsync(db, seriesId, ct);

        var survivors = new List<Survivor>();
        foreach (var chapter in chapters)
        {
            run.ChaptersChecked++;
            var file = chapter.ChapterFile!;
            if (file.MeasuredAtUtc is null)
            {
                run.Skip("unmeasured");
                continue;
            }

            if (file.Trusted)
            {
                run.Skip("trusted");
                continue;
            }

            if (!UpgradeTrash.IsReplaceable(file.RelativePath))
            {
                run.Skip(UpgradeReasons.UnsupportedFile);
                continue;
            }

            // A volume or compilation backing several chapters: replacing it with one chapter's copy
            // would take the others' pages with it.
            if (fileUse[file.Id] > 1)
            {
                run.Skip("shared_file");
                continue;
            }

            var current = evaluator.Evaluate(file, chapter.Language)!.Value;
            if (current.CutoffMet)
            {
                run.Skip("cutoff_met");
                continue;
            }

            var settled = file.ReplacedAtUtc is { } replaced && replaced > file.DateAdded ? replaced : file.DateAdded;
            // Someone pressing "Scan for upgrades" wants it tried now, so only the daily scan waits.
            if (run.OnlyChapter is null && !run.ByHand && settled.AddDays(options.QuietPeriodDays) > now)
            {
                run.Skip(UpgradeReasons.QuietPeriod);
                continue;
            }

            if (active.Contains(chapter.Id))
            {
                run.Skip("queued");
                continue;
            }

            var fileName = Path.GetFileName(file.RelativePath);
            var anyOtherSource = false;
            foreach (var link in chapter.SourceLinks)
            {
                if (link.SourceMapping is not { Enabled: true } mapping || disabled.Contains(mapping.SourceName) ||
                    registry.Find(mapping.SourceName) is not { } source)
                {
                    continue;
                }

                // The copy on disk came from here. Only a different chapter id on the same source (a
                // group's re-upload) is a new candidate; with no recorded id there is no telling.
                if (string.Equals(mapping.SourceName, file.SourceName, StringComparison.OrdinalIgnoreCase) &&
                    (file.SourceChapterId is null || file.SourceChapterId == link.SourceChapterId))
                {
                    continue;
                }

                anyOtherSource = true;

                // A scan asked for by hand looks at every candidate again; only a download it already
                // queued, and that could still happen, keeps it from being queued twice.
                if (memo.TryGetValue((chapter.Id, mapping.Id, link.SourceChapterId), out var seen) &&
                    (seen.Reason == UpgradeReasons.Enqueued
                        ? lastUpgrade.GetValueOrDefault(chapter.Id) is not (null or QueueStatus.Failed or QueueStatus.Cancelled)
                        : !run.ByHand &&
                          !(seen.Reason is UpgradeReasons.ProbeFailed or UpgradeReasons.SourceCooldown &&
                            seen.CreatedAtUtc < now.AddDays(-1)) &&
                          !(seen.Reason == UpgradeReasons.EstimateNotHigher &&
                            seen.CreatedAtUtc < now - UpgradeReasons.EstimateMemoLifetime)))
                {
                    run.Skip("memoised");
                    run.Outcome(mapping, link.SourceChapterId, seen.Reason, seen.Probed, seen.CandidatePageCount,
                        seen.CandidateWidth, seen.CandidateScore);
                    continue;
                }

                var group = link.Group ?? ChapterFileQualityService.SiteGroup(source);
                var listing = evaluator.CandidateFor(mapping.SourceName, group, fileName, null, null, null, null,
                    chapter.Language);
                if (!QualityScorer.Allows(profile, listing.Tier))
                {
                    await RecordAsync(chapter, mapping, link, profile, UpgradeReasons.TierNotAllowed, false, null, null, null, run, ct);
                    continue;
                }

                if (!evaluator.CouldUpgrade(current.Score, file.PageCount, file.Trusted, listing))
                {
                    await RecordAsync(chapter, mapping, link, profile, UpgradeReasons.ScoreNotHigher, false, null, null, null, run, ct);
                    continue;
                }

                // Only once the listing alone could still win, so a loser for good stays score_not_higher.
                if (estimates.GetValueOrDefault(mapping.Id) is { } estimate && estimate.IsReliable(now))
                {
                    listing = estimate.Apply(listing);
                    if (!evaluator.CouldUpgrade(current.Score, file.PageCount, file.Trusted, listing))
                    {
                        await RecordAsync(chapter, mapping, link, profile, UpgradeReasons.EstimateNotHigher, false, null,
                            estimate.MedianWidth, evaluator.Score(listing).Score, run, ct);
                        continue;
                    }
                }

                survivors.Add(new Survivor(chapter, file, current.Score, mapping, source, link, group,
                    evaluator.OptimisticScore(listing)));
            }

            if (!anyOtherSource)
            {
                run.Skip(UpgradeReasons.NoOtherSource);
            }
        }

        await db.SaveChangesAsync(ct);

        var winners = new Dictionary<int, Winner>();
        var outranked = new List<Winner>();
        foreach (var s in survivors
                     .OrderByDescending(s => QualityScorer.Rank(profile, s.Optimistic.Tier))
                     .ThenByDescending(s => s.Optimistic.Score))
        {
            if (run.ProbesLeft <= 0)
            {
                run.Skip("probe_budget");
                run.Outcome(s.Mapping, s.Link.SourceChapterId, "probe_budget", false, null, null, null);
                continue;
            }

            var sourceName = s.Mapping.SourceName;
            if (queue.CooldownRemaining(sourceName) > TimeSpan.Zero)
            {
                await RecordAsync(s.Chapter, s.Mapping, s.Link, profile, UpgradeReasons.SourceCooldown, false, null, null, null, run, ct);
                continue;
            }

            run.ProbesLeft--;
            run.CandidatesProbed++;
            var chapter = s.Chapter;
            var probe = await probes.ProbeAsync(s.Source, sourceName, new SourceChapter(
                sourceName, s.Mapping.SourceSeriesId, s.Link.SourceChapterId, chapter.NumberRaw, chapter.Number,
                chapter.Volume, chapter.Title, chapter.Language, chapter.ReleaseDate), SampleCount, ct);
            if (probe is null)
            {
                var reason = queue.CooldownRemaining(sourceName) > TimeSpan.Zero
                    ? UpgradeReasons.SourceCooldown
                    : UpgradeReasons.ProbeFailed;
                await RecordAsync(chapter, s.Mapping, s.Link, profile, reason, true, null, null, null, run, ct);
                continue;
            }

            long? size = probe.SampledPages > 0 ? probe.SampleBytes / probe.SampledPages * probe.PageCount : null;
            await SourceQualitySamples.RecordAsync(db, s.Mapping, chapter.Id, SourceQualityOrigin.Probe, probe.PageCount,
                probe.MedianWidth, probe.MedianHeight, size, probe.ImageFormat, now, ct);
            var score = evaluator.Score(evaluator.CandidateFor(sourceName, s.Group, Path.GetFileName(s.File.RelativePath),
                probe.PageCount, probe.MedianWidth, probe.ImageFormat, size, chapter.Language, probe.MedianHeight));
            if (!QualityScorer.IsUpgrade(profile, s.Current, s.File.PageCount, s.File.Trusted, score, probe.MedianWidth,
                    probe.PageCount))
            {
                var reason = UpgradeReasons.Explain(profile, s.File.PageCount, score, probe.MedianWidth, probe.PageCount);
                await RecordAsync(chapter, s.Mapping, s.Link, profile, reason, true, probe.PageCount, probe.MedianWidth,
                    score.Score, run, ct);
                continue;
            }

            var winner = new Winner(s, score, probe, size);
            if (!winners.TryGetValue(chapter.Id, out var best))
            {
                winners[chapter.Id] = winner;
            }
            else if (Beats(profile, winner, best))
            {
                outranked.Add(best);
                winners[chapter.Id] = winner;
            }
            else
            {
                outranked.Add(winner);
            }
        }

        await db.SaveChangesAsync(ct);

        // Better than the file but beaten by another candidate for the same chapter. Not memoised, so a
        // later pass can still take it if the winner falls through.
        foreach (var w in outranked)
        {
            run.Outcome(w.Survivor.Mapping, w.Survivor.Link.SourceChapterId, UpgradeReasons.ScoreNotHigher, true,
                w.Probe.PageCount, w.Probe.MedianWidth, w.Score.Score);
        }

        var queued = new List<int>();
        foreach (var w in winners.Values)
        {
            if (run.OnlyChapter is null && options.MaxPerDay > 0 && run.EnqueuedToday >= options.MaxPerDay)
            {
                run.Skip("daily_cap");
                continue;
            }

            var s = w.Survivor;
            var attempt = await UpgradeAttempts.UpsertAsync(db, s.Chapter.Id, seriesId, s.Mapping.Id,
                s.Link.SourceChapterId, profile.Id, profile.Version, UpgradeReasons.Enqueued, true, w.Probe.PageCount,
                w.Probe.MedianWidth, w.Score.Score, ct);
            await db.SaveChangesAsync(ct);

            var info = new UpgradeInfo
            {
                ChapterFileId = s.File.Id,
                AttemptId = attempt.Id,
                ProfileId = profile.Id,
                ProfileVersion = profile.Version,
                Before = UpgradeEvaluator.Snapshot(s.File, s.Current.Score),
                Predicted = new QualitySnapshot
                {
                    Tier = QualitySnapshot.TierName(w.Score.Tier),
                    SourceName = s.Mapping.SourceName,
                    SourceChapterId = s.Link.SourceChapterId,
                    Group = s.Group,
                    PageCount = w.Probe.PageCount,
                    MedianWidth = w.Probe.MedianWidth,
                    MedianHeight = w.Probe.MedianHeight,
                    ImageFormat = w.Probe.ImageFormat,
                    SizeBytes = w.SizeBytes,
                    Score = w.Score.Score
                }
            };

            var item = await queue.EnqueueUpgradeAsync(s.Chapter.Id, s.Mapping.Id, s.Link.SourceChapterId, info, run.UserId, ct);
            if (item is null)
            {
                db.UpgradeAttempts.Remove(attempt);
                await db.SaveChangesAsync(ct);
                run.Skip("queued");
                run.Outcome(s.Mapping, s.Link.SourceChapterId, "queued", true, w.Probe.PageCount, w.Probe.MedianWidth,
                    w.Score.Score);
                continue;
            }

            run.Enqueued++;
            run.EnqueuedToday++;
            queued.Add(item.Id);
            run.Outcome(s.Mapping, s.Link.SourceChapterId, UpgradeReasons.Enqueued, true, w.Probe.PageCount,
                w.Probe.MedianWidth, w.Score.Score);
            if (run.OnlyChapter is not null)
            {
                run.QueuedFromMappingId = s.Mapping.Id;
            }
        }

        if (queued.Count > 0)
        {
            await batches.QueuedAsync(series.Id, series.Title, queued, DownloadOrigin.Upgrade, announce: false);
        }

        var probed = run.CandidatesProbed - probedBefore;
        var enqueued = run.Enqueued - enqueuedBefore;
        var skips = run.Skipped
            .Select(kv => (kv.Key, Count: kv.Value - skippedBefore.GetValueOrDefault(kv.Key)))
            .Where(x => x.Count > 0)
            .ToDictionary(x => x.Key, x => x.Count);
        var skipsJson = JsonSerializer.Serialize(skips);
        var checkedCount = run.ChaptersChecked - checkedBefore;
        await db.Series.IgnoreQueryFilters()
            .Where(x => x.Id == seriesId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.LastUpgradeScanUtc, now)
                .SetProperty(x => x.LastUpgradeScanProbed, probed)
                .SetProperty(x => x.LastUpgradeScanQueued, enqueued)
                .SetProperty(x => x.LastUpgradeScanChecked, checkedCount)
                .SetProperty(x => x.LastUpgradeScanSkipsJson, skipsJson), ct);
    }

    private static bool Beats(UpgradeProfile profile, Winner a, Winner b)
    {
        var rank = QualityScorer.Rank(profile, a.Score.Tier).CompareTo(QualityScorer.Rank(profile, b.Score.Tier));
        if (rank != 0) return rank > 0;
        if (a.Score.Score != b.Score.Score) return a.Score.Score > b.Score.Score;
        return (a.Probe.MedianWidth ?? 0) > (b.Probe.MedianWidth ?? 0);
    }

    private async Task RecordAsync(Chapter chapter, SourceMapping mapping, ChapterSourceLink link, UpgradeProfile profile,
        string reason, bool probed, int? pageCount, int? width, int? score, Run run, CancellationToken ct)
    {
        await UpgradeAttempts.UpsertAsync(db, chapter.Id, chapter.SeriesId, mapping.Id, link.SourceChapterId, profile.Id,
            profile.Version, reason, probed, pageCount, width, score, ct);
        run.Skip(reason);
        run.Outcome(mapping, link.SourceChapterId, reason, probed, pageCount, width, score);
    }
}
