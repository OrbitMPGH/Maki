using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Quality;
using Maki.Core.Sources;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <param name="Skipped">How many chapters or candidates were passed over, keyed by reason code.</param>
public sealed record UpgradeScanResult(int SeriesScanned, int ChaptersChecked, int CandidatesProbed, int Enqueued,
    IReadOnlyDictionary<string, int> Skipped);

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

    private async Task<UpgradeScanResult> RunAsync(int? onlySeries, bool ignoreGlobalSwitch, CancellationToken ct)
    {
        if (!await Gate.WaitAsync(0, ct))
        {
            throw new UpgradeScanBusyException();
        }

        try
        {
            var options = await UpgradeOptions.LoadAsync(settings, ct);
            var run = new Run(options.MaxProbesPerRun);
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

    private sealed class Run(int probeBudget)
    {
        public int ProbesLeft { get; set; } = probeBudget;
        public int SeriesScanned { get; set; }
        public int ChaptersChecked { get; set; }
        public int CandidatesProbed { get; set; }
        public int Enqueued { get; set; }
        public int EnqueuedToday { get; set; }
        public Dictionary<string, int> Skipped { get; } = new(StringComparer.Ordinal);

        public void Skip(string reason) => Skipped[reason] = Skipped.GetValueOrDefault(reason) + 1;

        public UpgradeScanResult Result() =>
            new(SeriesScanned, ChaptersChecked, CandidatesProbed, Enqueued, new Dictionary<string, int>(Skipped));
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
        if (evaluator is null || !evaluator.Profile.UpgradesEnabled)
        {
            return;
        }

        if (series.Incognito != IncognitoMode.Off && !options.ScanIncognito)
        {
            return;
        }

        run.SeriesScanned++;
        var profile = evaluator.Profile;
        var now = time.GetUtcNow().UtcDateTime;

        var chapters = await db.Chapters.AsNoTracking()
            .Where(c => c.SeriesId == seriesId && c.ChapterFileId != null)
            .Include(c => c.ChapterFile)
            .Include(c => c.SourceLinks).ThenInclude(l => l.SourceMapping)
            .ToListAsync(ct);
        var fileUse = chapters.GroupBy(c => c.ChapterFileId!.Value).ToDictionary(g => g.Key, g => g.Count());
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
            if (settled.AddDays(options.QuietPeriodDays) > now)
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

                if (memo.TryGetValue((chapter.Id, mapping.Id, link.SourceChapterId), out var seen) &&
                    !(seen.Reason is UpgradeReasons.ProbeFailed or UpgradeReasons.SourceCooldown &&
                      seen.CreatedAtUtc < now.AddDays(-1)) &&
                    !(seen.Reason == UpgradeReasons.Enqueued &&
                      lastUpgrade.GetValueOrDefault(chapter.Id) is null or QueueStatus.Failed or QueueStatus.Cancelled))
                {
                    run.Skip("memoised");
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

                survivors.Add(new Survivor(chapter, file, current.Score, mapping, source, link, group,
                    evaluator.OptimisticScore(listing)));
            }
        }

        await db.SaveChangesAsync(ct);

        var winners = new Dictionary<int, Winner>();
        foreach (var s in survivors
                     .OrderByDescending(s => QualityScorer.Rank(profile, s.Optimistic.Tier))
                     .ThenByDescending(s => s.Optimistic.Score))
        {
            if (run.ProbesLeft <= 0)
            {
                run.Skip("probe_budget");
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
            var score = evaluator.Score(evaluator.CandidateFor(sourceName, s.Group, Path.GetFileName(s.File.RelativePath),
                probe.PageCount, probe.MedianWidth, probe.ImageFormat, size, chapter.Language));
            if (!QualityScorer.IsUpgrade(profile, s.Current, s.File.PageCount, s.File.Trusted, score, probe.MedianWidth,
                    probe.PageCount))
            {
                var reason = UpgradeReasons.Explain(profile, s.File.PageCount, score, probe.MedianWidth, probe.PageCount);
                await RecordAsync(chapter, s.Mapping, s.Link, profile, reason, true, probe.PageCount, probe.MedianWidth,
                    score.Score, run, ct);
                continue;
            }

            var winner = new Winner(s, score, probe, size);
            if (!winners.TryGetValue(chapter.Id, out var best) || Beats(profile, winner, best))
            {
                winners[chapter.Id] = winner;
            }
        }

        await db.SaveChangesAsync(ct);

        var queued = new List<int>();
        foreach (var w in winners.Values)
        {
            if (options.MaxPerDay > 0 && run.EnqueuedToday >= options.MaxPerDay)
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

            var item = await queue.EnqueueUpgradeAsync(s.Chapter.Id, s.Mapping.Id, s.Link.SourceChapterId, info, null, ct);
            if (item is null)
            {
                db.UpgradeAttempts.Remove(attempt);
                await db.SaveChangesAsync(ct);
                run.Skip("queued");
                continue;
            }

            run.Enqueued++;
            run.EnqueuedToday++;
            queued.Add(item.Id);
        }

        if (queued.Count > 0)
        {
            await batches.QueuedAsync(series.Id, series.Title, queued, DownloadOrigin.Upgrade, announce: false);
        }
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
    }
}
