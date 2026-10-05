using System.Globalization;
using System.Text.Json;
using Maki.Core.Configuration;
using Maki.Core.Download;
using Maki.Core.Entities;
using Maki.Core.Inbox;
using Maki.Core.Naming;
using Maki.Core.Parsing;
using Maki.Core.Paths;
using Maki.Core.Quality;
using Maki.Core.Reading;
using Maki.Core.Scrobbling;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <param name="Parsed">The title as the verdict read it: a trailing chapter number is already in the span.</param>
/// <param name="WholeSeries">A digital pack naming no span, judged against every chapter of the series.</param>
/// <param name="Language">The one chapter language the verdict judged.</param>
public sealed record TorrentCandidateView(ReleaseDto Release, ParsedReleaseTitle Parsed, bool TitleMatched,
    QualityTier Tier, int Score, SpanVerdict Verdict, bool WholeSeries = false, string? Language = null);

/// <summary>The shape of <see cref="TorrentProposal.SpanJson"/>.</summary>
public sealed record StoredReleaseSpan(NumberRange? Volumes, IReadOnlyList<NumberRange>? ChapterSegments, bool WholeSeries = false)
{
    public ReleaseSpan ToSpan() => new(Volumes, ChapterSegments ?? []);
}

/// <param name="Grabbed">The queue item an auto-grab created.</param>
/// <param name="ProposalId">The pending proposal the search left.</param>
/// <param name="Reason">A <see cref="VolumeSearchReasons"/> code, or null when the search ran.</param>
public sealed record SeriesVolumeSearchResult(bool Searched, int ResultCount, int? Grabbed, int? ProposalId, string? Reason);

public static class VolumeSearchReasons
{
    public const string Disabled = "not_eligible_disabled";
    public const string NoProfile = "not_eligible_no_profile";
    public const string ProfileDisabled = "not_eligible_profile_disabled";
    public const string Cutoff = "not_eligible_cutoff";
    public const string NothingBelowCutoff = "not_eligible_nothing_below_cutoff";
    public const string PendingProposal = "not_eligible_pending_proposal";
    public const string Incognito = "not_eligible_incognito";
    public const string NoProwlarr = "not_eligible_no_prowlarr";
    public const string Recent = "not_eligible_recent";
    public const string NotFound = "not_eligible_not_found";
    public const string SearchFailed = "search_failed";
    public const string GrabFailed = "grab_failed";
}

/// <summary>The instance settings a volume search reads, loaded once per job run rather than per series.</summary>
public sealed record VolumeSearchSettings(UpgradeOptions Options, bool ProwlarrConfigured);

public enum ProposalActionError
{
    None,
    NotFound,
    Resolved
}

/// <summary>The per-chapter rules a torrent release is judged by, shared by the search and the import plan.</summary>
public static class TorrentUpgradeRules
{
    public static QualityCandidate Candidate(string title, string indexer, string? group, bool isVolume) => new(
        QualityTierResolver.Resolve(null, title, title, isVolume),
        $"torrent:{indexer}", null, group, title, null, null, null, null, null);

    /// <summary>
    /// A chapter with a file is an upgrade when the profile would take the release over it. Nothing is
    /// measured before the download, so the file's measured points are left out of the comparison
    /// rather than counted against a release that has none; the post-download guard checks the pages.
    /// A chapter without a file is only ever classified, never wanted by this.
    /// </summary>
    public static ChapterSpanState StateOf(UpgradeEvaluator evaluator, Chapter chapter, ChapterFile? file, QualityScore candidate)
    {
        if (file is null)
        {
            return chapter.Wanted ? ChapterSpanState.Missing : ChapterSpanState.Skipped;
        }

        if (file.Trusted || evaluator.Evaluate(file, chapter.Language) is not { } current)
        {
            return ChapterSpanState.AlreadyMet;
        }

        return QualityScorer.IsUnmeasuredUpgrade(evaluator.Profile, current.Score, file.Trusted, candidate)
            ? ChapterSpanState.Upgrade
            : ChapterSpanState.AlreadyMet;
    }

    /// <summary>
    /// The rows of one language: English when the series has any, otherwise the language with the most
    /// files, then the most rows, then the first code in ordinal order. Null language for no rows.
    /// </summary>
    public static (string? Language, List<Chapter> Chapters) PrimaryLanguage(List<Chapter> chapters)
    {
        var language = chapters.Any(c => ChapterFileLanguage.Of(c) == FileNameBuilder.DefaultLanguage)
            ? FileNameBuilder.DefaultLanguage
            : chapters
                .GroupBy(ChapterFileLanguage.Of, StringComparer.Ordinal)
                .OrderByDescending(g => g.Where(c => c.ChapterFileId != null).Select(c => c.ChapterFileId).Distinct().Count())
                .ThenByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.Key)
                .FirstOrDefault();
        return (language, [.. chapters.Where(c => ChapterFileLanguage.Of(c) == language)]);
    }
}

/// <summary>
/// Finds torrent volume releases that would replace a series' below-cutoff files, and grabs them or
/// proposes them. Chapters without a file are only ever counted: a volume that would add chapters the
/// user does not have becomes a proposal, and <see cref="Chapter.Wanted"/> is never written.
/// </summary>
public class TorrentUpgradeService(
    MakiDbContext db,
    UpgradeEvaluationService evaluation,
    ReleaseService releases,
    InboxService inbox,
    IAppSettings settings,
    TimeProvider time,
    ILogger<TorrentUpgradeService> logger)
{
    public static readonly TimeSpan SearchInterval = TimeSpan.FromDays(7);
    private const int MaxPlaceholders = 2000;

    public async Task<IReadOnlyList<TorrentCandidateView>> EvaluateAsync(
        int seriesId, IReadOnlyList<ReleaseDto> found, CancellationToken ct)
    {
        var series = await db.Series.AsNoTracking().Include(s => s.RootFolder).FirstOrDefaultAsync(s => s.Id == seriesId, ct);
        if (series is null || found.Count == 0)
        {
            return [];
        }

        var evaluator = await evaluation.ForSeriesAsync(seriesId, ct);
        var options = await UpgradeOptions.LoadAsync(settings, ct);
        var library = evaluator is null ? null : await LibraryAsync(series, ct);

        var views = new List<TorrentCandidateView>();
        foreach (var release in found)
        {
            var (parsed, trailing) = WithTrailingChapter(ReleaseTitleParser.Parse(release.Title), series);
            var matched = ReleaseTitleMatcher.Matches(parsed, series.Title, series.OriginalTitle, series.AltTitles);
            var wholeSeries = parsed.Span.IsEmpty && parsed.IsDigital && matched;
            var candidate = TorrentUpgradeRules.Candidate(release.Title, release.Indexer, parsed.Group,
                parsed.Span.Volumes is not null || wholeSeries);
            if (evaluator is null || library is null)
            {
                views.Add(new TorrentCandidateView(release, parsed, matched, candidate.Tier, 0,
                    new SpanVerdict(SpanOutcome.Ignore, [SpanVerdictReasons.NoProfile], 0, 0, 0, 0, 0, []), wholeSeries));
                continue;
            }

            var score = evaluator.Score(candidate);
            var (chapters, mappingKnown) = wholeSeries
                ? (library.All(evaluator, score), true)
                : library.Covered(parsed.Span, evaluator, score);
            var verdict = SpanVerdicts.Evaluate(new SpanVerdictInput(parsed.Span, matched, mappingKnown, chapters,
                release.Size, options.TorrentAutoGrabMaxBytes, options.VolumeMissingTolerance, wholeSeries));
            if (trailing)
            {
                verdict = verdict.AtMostProposal(SpanVerdictReasons.TrailingNumber);
            }

            views.Add(new TorrentCandidateView(release, parsed, matched, candidate.Tier, score.Score, verdict, wholeSeries,
                library.Language));
        }

        return views;
    }

    /// <summary>
    /// A number ending the release title is a chapter unless the series' own title ends in it
    /// ("Mob Psycho 100"). As a chapter, the title without it joins the candidates the matcher sees.
    /// Such a guess is never enough to grab on its own, so the caller caps the verdict at a proposal.
    /// </summary>
    private static (ParsedReleaseTitle Parsed, bool Trailing) WithTrailingChapter(ParsedReleaseTitle parsed, Series series)
    {
        if (parsed.TrailingNumber is not { } number || parsed.TitleWithoutTrailingNumber is not { } stripped)
        {
            return (parsed, false);
        }

        IEnumerable<string?> titles = [series.Title, series.OriginalTitle, .. series.AltTitles.Select(a => a.Title)];
        if (titles.Any(t => EndsWithNumber(t, number)))
        {
            return (parsed, false);
        }

        return (parsed with
        {
            TitleCandidates = [.. parsed.TitleCandidates, stripped],
            Span = parsed.Span with { ChapterSegments = [.. parsed.Span.ChapterSegments, new NumberRange(number, number)] }
        }, true);
    }

    private static bool EndsWithNumber(string? title, decimal number)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var last = ScrobbleMatching.NormalizeTitle(title).Split(' ')[^1];
        return decimal.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value == number;
    }

    /// <summary>
    /// Leaves a pending proposal for the release, or refreshes the one already pending. Null when the
    /// user dismissed this release before or already grabbed it.
    /// </summary>
    public async Task<TorrentProposal?> ProposeAsync(int seriesId, TorrentCandidateView best, CancellationToken ct)
    {
        var release = best.Release with { Parsed = null };
        var row = await db.TorrentProposals.FirstOrDefaultAsync(p => p.SeriesId == seriesId && p.ReleaseGuid == release.Guid, ct);
        if (row is { Status: TorrentProposalStatus.Dismissed or TorrentProposalStatus.Accepted })
        {
            return null;
        }

        var now = time.GetUtcNow().UtcDateTime;
        var fresh = row is null || row.Status != TorrentProposalStatus.Pending;
        if (row is null)
        {
            row = new TorrentProposal { SeriesId = seriesId, ReleaseGuid = release.Guid };
            db.TorrentProposals.Add(row);
        }

        var verdict = best.Verdict;
        row.ReleaseInfoJson = JsonSerializer.Serialize(release, QualitySnapshot.Json);
        row.Title = release.Title;
        row.Indexer = release.Indexer;
        row.SizeBytes = release.Size;
        row.SpanJson = JsonSerializer.Serialize(new StoredReleaseSpan(best.Parsed.Span.Volumes,
            best.Parsed.Span.ChapterSegments, best.WholeSeries), QualitySnapshot.Json);
        row.ReasonsJson = JsonSerializer.Serialize(verdict.Reasons);
        row.UpgradeCount = verdict.UpgradeCount;
        row.AlreadyMetCount = verdict.AlreadyMetCount;
        row.SkippedCount = verdict.SkippedCount;
        row.MissingCount = verdict.MissingCount;
        row.UnknownCount = verdict.UnknownCount;
        row.Score = best.Score;
        row.Status = TorrentProposalStatus.Pending;
        row.QueueItemId = null;
        row.ResolvedByUserId = null;
        row.ResolvedAtUtc = null;
        if (fresh)
        {
            row.CreatedAtUtc = now;
        }

        await db.SaveChangesAsync(ct);
        if (fresh)
        {
            inbox.RaiseForSeries(InboxEventType.TorrentProposalPending, new InboxMessage(
                Key: "inbox.upgrade.torrentProposal",
                Params: InboxMessage.Args(new
                {
                    replaced = verdict.ReplacedFileIds.Count,
                    missing = verdict.MissingCount,
                    sizeBytes = release.Size
                }),
                SeriesId: seriesId,
                Url: $"/series/{seriesId}?tab=chapters"), seriesId);
        }

        return row;
    }

    /// <summary>
    /// One Prowlarr search for the series, then the best auto-grab is queued or the best proposal left.
    /// The manual trigger ignores the weekly interval and passes <paramref name="ignoreGlobalSwitch"/>
    /// so an admin can try a series with the instance switches off; the job passes neither.
    /// </summary>
    /// <param name="run">The job's settings, read once per run; null reads them here.</param>
    public async Task<SeriesVolumeSearchResult> SearchSeriesAsync(
        int seriesId, CancellationToken ct, bool respectInterval = false, bool ignoreGlobalSwitch = false,
        VolumeSearchSettings? run = null)
    {
        var series = await db.Series.FirstOrDefaultAsync(s => s.Id == seriesId, ct);
        if (series is null)
        {
            return NotEligible(VolumeSearchReasons.NotFound);
        }

        run ??= await LoadSettingsAsync(ct);
        var options = run.Options;
        var evaluator = await evaluation.ForSeriesAsync(seriesId, ct);
        var eligible = await EligibilityAsync(series, run, evaluator, respectInterval, ignoreGlobalSwitch, ct);
        if (eligible is not null)
        {
            // The weekly job would otherwise walk every such series again each day. A change that makes
            // one eligible waits out the interval, the same as a series that was searched.
            if (respectInterval && eligible is VolumeSearchReasons.Incognito or VolumeSearchReasons.NoProfile
                    or VolumeSearchReasons.ProfileDisabled or VolumeSearchReasons.Cutoff
                    or VolumeSearchReasons.NothingBelowCutoff)
            {
                await StampSearchedAsync(seriesId);
            }

            return NotEligible(eligible);
        }

        // A manual search with the instance switches off may look and propose, never grab on its own.
        var switchesOff = !options.Enabled || !options.VolumeSearch;

        try
        {
            ReleaseSearchResult result;
            try
            {
                result = await releases.SearchAsync(seriesId, null, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException
                                           && !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Volume search for '{Title}' failed", series.Title);
                return new SeriesVolumeSearchResult(true, 0, null, null, VolumeSearchReasons.SearchFailed);
            }

            var blocked = await BlockedGuidsAsync(seriesId, ct);
            var views = (await EvaluateAsync(seriesId, result.Releases, ct))
                .Where(v => !blocked.Contains(v.Release.Guid))
                .Select(v => switchesOff ? v with { Verdict = v.Verdict.AtMostProposal(SpanVerdictReasons.UpgradesDisabled) } : v)
                .OrderByDescending(v => v.Score)
                .ThenByDescending(v => v.Release.Seeders ?? 0)
                .ToList();

            if (views.FirstOrDefault(v => v.Verdict.Outcome == SpanOutcome.AutoGrab) is { } grab)
            {
                try
                {
                    var item = await releases.GrabAsync(seriesId, grab.Release with { Parsed = null }, DownloadOrigin.Upgrade,
                        null, InfoFor(evaluator!, grab, null).Serialize(), ct);
                    logger.LogInformation("Volume search grabbed '{Release}' for '{Title}'", grab.Release.Title, series.Title);
                    return new SeriesVolumeSearchResult(true, result.Releases.Count, item.Id, null, null);
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException
                                               && !ct.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Could not grab '{Release}' for '{Title}'", grab.Release.Title, series.Title);
                    return new SeriesVolumeSearchResult(true, result.Releases.Count, null, null, VolumeSearchReasons.GrabFailed);
                }
            }

            foreach (var proposal in views.Where(v => v.Verdict.Outcome == SpanOutcome.Proposal))
            {
                if (await ProposeAsync(seriesId, proposal, ct) is { } row)
                {
                    return new SeriesVolumeSearchResult(true, result.Releases.Count, null, row.Id, null);
                }
            }

            return new SeriesVolumeSearchResult(true, result.Releases.Count, null, null, null);
        }
        finally
        {
            await StampSearchedAsync(seriesId);
        }
    }

    public async Task<VolumeSearchSettings> LoadSettingsAsync(CancellationToken ct) => new(
        await UpgradeOptions.LoadAsync(settings, ct),
        !string.IsNullOrWhiteSpace(await settings.GetAsync(SettingKeys.ProwlarrUrl, ct)) &&
        !string.IsNullOrWhiteSpace(await settings.GetAsync(SettingKeys.ProwlarrApiKey, ct)));

    private Task StampSearchedAsync(int seriesId) =>
        db.Series.Where(s => s.Id == seriesId)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.LastVolumeSearchUtc, time.GetUtcNow().UtcDateTime),
                CancellationToken.None);

    /// <summary>Queues the proposal's release with the verdict as it reads against the library now.</summary>
    public async Task<(int? QueueItemId, ProposalActionError Error)> GrabProposalAsync(int proposalId, int? userId, CancellationToken ct)
    {
        var row = await db.TorrentProposals.FirstOrDefaultAsync(p => p.Id == proposalId, ct);
        if (row is null)
        {
            return (null, ProposalActionError.NotFound);
        }

        if (row.Status != TorrentProposalStatus.Pending)
        {
            return (null, ProposalActionError.Resolved);
        }

        var release = JsonSerializer.Deserialize<ReleaseDto>(row.ReleaseInfoJson, QualitySnapshot.Json)!;
        var evaluator = await evaluation.ForSeriesAsync(row.SeriesId, ct);
        var view = (await EvaluateAsync(row.SeriesId, [release], ct)).FirstOrDefault();
        var info = evaluator is not null && view is not null
            ? InfoFor(evaluator, view, row.Id)
            : new TorrentUpgradeInfo { ProposalId = row.Id, Reasons = [SpanVerdictReasons.NoProfile] };

        var item = await releases.GrabAsync(row.SeriesId, release, DownloadOrigin.Upgrade, userId, info.Serialize(), ct);
        row.Status = TorrentProposalStatus.Accepted;
        row.QueueItemId = item.Id;
        row.ResolvedByUserId = userId;
        row.ResolvedAtUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return (item.Id, ProposalActionError.None);
    }

    public async Task<ProposalActionError> DismissAsync(int proposalId, int? userId, CancellationToken ct)
    {
        var row = await db.TorrentProposals.FirstOrDefaultAsync(p => p.Id == proposalId, ct);
        if (row is null)
        {
            return ProposalActionError.NotFound;
        }

        if (row.Status != TorrentProposalStatus.Pending)
        {
            return ProposalActionError.Resolved;
        }

        row.Status = TorrentProposalStatus.Dismissed;
        row.ResolvedByUserId = userId;
        row.ResolvedAtUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return ProposalActionError.None;
    }

    /// <summary>Pending proposals older than the expiry go to Expired, which is no memo: the release may be proposed again.</summary>
    public async Task<int> ExpireAsync(int expiryDays, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var cutoff = now.AddDays(-expiryDays);
        return await db.TorrentProposals.IgnoreQueryFilters()
            .Where(p => p.Status == TorrentProposalStatus.Pending && p.CreatedAtUtc < cutoff)
            .ExecuteUpdateAsync(u => u
                .SetProperty(p => p.Status, TorrentProposalStatus.Expired)
                .SetProperty(p => p.ResolvedAtUtc, now), ct);
    }

    /// <summary>
    /// Series the job may search, least recently searched first: outside the weekly interval and
    /// without a pending proposal. The remaining rules are checked per series by the search itself.
    /// </summary>
    public async Task<List<int>> CandidateSeriesAsync(CancellationToken ct)
    {
        var cutoff = time.GetUtcNow().UtcDateTime - SearchInterval;
        var rows = await db.Series.IgnoreQueryFilters()
            .Where(s => s.LastVolumeSearchUtc == null || s.LastVolumeSearchUtc < cutoff)
            .Where(s => !db.TorrentProposals.IgnoreQueryFilters()
                .Any(p => p.SeriesId == s.Id && p.Status == TorrentProposalStatus.Pending))
            .Select(s => new { s.Id, s.LastVolumeSearchUtc })
            .ToListAsync(ct);
        return [.. rows.OrderBy(r => r.LastVolumeSearchUtc is not null).ThenBy(r => r.LastVolumeSearchUtc).ThenBy(r => r.Id).Select(r => r.Id)];
    }

    private static SeriesVolumeSearchResult NotEligible(string reason) => new(false, 0, null, null, reason);

    private static TorrentUpgradeInfo InfoFor(UpgradeEvaluator evaluator, TorrentCandidateView view, int? proposalId) => new()
    {
        ProposalId = proposalId,
        ProfileId = evaluator.Profile.Id,
        ProfileVersion = evaluator.Profile.Version,
        ReplacedFileIds = [.. view.Verdict.ReplacedFileIds],
        Reasons = [.. view.Verdict.Reasons],
        Language = view.Language
    };

    private async Task<string?> EligibilityAsync(
        Series series, VolumeSearchSettings run, UpgradeEvaluator? evaluator, bool respectInterval,
        bool ignoreGlobalSwitch, CancellationToken ct)
    {
        var options = run.Options;
        if (!ignoreGlobalSwitch && (!options.Enabled || !options.VolumeSearch))
        {
            return VolumeSearchReasons.Disabled;
        }

        if (!run.ProwlarrConfigured)
        {
            return VolumeSearchReasons.NoProwlarr;
        }

        if (series.Incognito != IncognitoMode.Off && !options.ScanIncognito)
        {
            return VolumeSearchReasons.Incognito;
        }

        if (evaluator is null)
        {
            return VolumeSearchReasons.NoProfile;
        }

        if (!evaluator.Profile.UpgradesEnabled)
        {
            return VolumeSearchReasons.ProfileDisabled;
        }

        // A cutoff on the tier alone that stops below Volume never asks for one. With a score to reach
        // as well, a file at any tier can still be short of the cutoff, so the files decide below.
        var profile = evaluator.Profile;
        if (!QualityScorer.Allows(profile, QualityTier.Volume) ||
            profile.UpgradeUntilScore == 0 &&
            QualityScorer.Rank(profile, profile.Cutoff) < QualityScorer.Rank(profile, QualityTier.Volume))
        {
            return VolumeSearchReasons.Cutoff;
        }

        if (await db.TorrentProposals.AnyAsync(p => p.SeriesId == series.Id && p.Status == TorrentProposalStatus.Pending, ct))
        {
            return VolumeSearchReasons.PendingProposal;
        }

        if (respectInterval && series.LastVolumeSearchUtc is { } last && last > time.GetUtcNow().UtcDateTime - SearchInterval)
        {
            return VolumeSearchReasons.Recent;
        }

        // The best any volume release could score, put through the same rule the verdict uses.
        var volume = evaluator.OptimisticScore(new QualityCandidate(QualityTier.Volume, null, null, null, null,
            null, null, null, null, null));
        var chapters = await db.Chapters.AsNoTracking()
            .Where(c => c.SeriesId == series.Id && c.ChapterFileId != null && !c.ChapterFile!.Trusted)
            .Include(c => c.ChapterFile)
            .ToListAsync(ct);
        return chapters.Any(c => TorrentUpgradeRules.StateOf(evaluator, c, c.ChapterFile, volume) == ChapterSpanState.Upgrade)
            ? null
            : VolumeSearchReasons.NothingBelowCutoff;
    }

    /// <summary>
    /// Releases the user dismissed, reverted or turned down at import, and ones already downloading for
    /// the series. A turned-down import is written down as a dismissed proposal here, so clearing the
    /// queue history does not let the search grab it again.
    /// </summary>
    private async Task<HashSet<string>> BlockedGuidsAsync(int seriesId, CancellationToken ct)
    {
        var dismissed = await db.TorrentProposals
            .Where(p => p.SeriesId == seriesId && (p.Status == TorrentProposalStatus.Dismissed || p.Status == TorrentProposalStatus.Accepted))
            .Select(p => p.ReleaseGuid)
            .ToListAsync(ct);
        var torrents = await db.DownloadQueue
            .Where(q => q.SeriesId == seriesId && q.Protocol == AcquisitionProtocol.Torrent && q.ReleaseInfoJson != null &&
                        (q.Status != QueueStatus.Completed && q.Status != QueueStatus.Failed && q.Status != QueueStatus.Cancelled ||
                         q.Status == QueueStatus.Cancelled && q.ErrorKey == RejectedImportKey))
            .Select(q => new { q.Status, Json = q.ReleaseInfoJson! })
            .ToListAsync(ct);
        var blocked = dismissed.ToHashSet(StringComparer.Ordinal);
        var recorded = dismissed.ToHashSet(StringComparer.Ordinal);
        var declined = false;
        foreach (var row in torrents)
        {
            if (ParseRelease(row.Json) is not { } info)
            {
                continue;
            }

            if (row.Status == QueueStatus.Cancelled && recorded.Add(info.Guid))
            {
                await DeclineAsync(db, seriesId, row.Json, null, time.GetUtcNow().UtcDateTime, ct);
                declined = true;
            }

            blocked.Add(info.Guid);
        }

        if (declined)
        {
            await db.SaveChangesAsync(ct);
        }

        return blocked;
    }

    /// <summary>The error a parked import the user rejected is cancelled with.</summary>
    private const string RejectedImportKey = "error.download.importRejected";

    /// <summary>
    /// Records the release on a torrent queue row as dismissed for the series, unsaved, so the volume
    /// search never grabs or proposes it again. For a release the user reverted or turned down: the
    /// torrent counterpart of the scraper's <c>reverted_by_user</c> memo.
    /// </summary>
    public static async Task DeclineAsync(MakiDbContext db, int seriesId, string? releaseInfoJson, int? userId,
        DateTime nowUtc, CancellationToken ct)
    {
        if (ParseRelease(releaseInfoJson) is not { Guid.Length: > 0 } release)
        {
            return;
        }

        var row = db.TorrentProposals.Local.FirstOrDefault(p => p.SeriesId == seriesId && p.ReleaseGuid == release.Guid)
                  ?? await db.TorrentProposals.IgnoreQueryFilters()
                      .FirstOrDefaultAsync(p => p.SeriesId == seriesId && p.ReleaseGuid == release.Guid, ct);
        if (row is { Status: TorrentProposalStatus.Dismissed or TorrentProposalStatus.Accepted })
        {
            return;
        }

        if (row is null)
        {
            row = new TorrentProposal
            {
                SeriesId = seriesId,
                ReleaseGuid = release.Guid,
                ReleaseInfoJson = releaseInfoJson!,
                Title = release.Title,
                Indexer = release.Indexer,
                CreatedAtUtc = nowUtc
            };
            db.TorrentProposals.Add(row);
        }

        row.Status = TorrentProposalStatus.Dismissed;
        row.ResolvedByUserId = userId;
        row.ResolvedAtUtc = nowUtc;
    }

    private static ReleaseInfo? ParseRelease(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ReleaseInfo>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<LibraryState> LibraryAsync(Series series, CancellationToken ct)
    {
        var (language, chapters) = TorrentUpgradeRules.PrimaryLanguage(await db.Chapters.AsNoTracking()
            .Where(c => c.SeriesId == series.Id)
            .Include(c => c.ChapterFile)
            .ToListAsync(ct));

        // A volume file already in the library says which volume its chapters belong to, by its name
        // and by the chapter markers in its page names, even where the chapter rows carry no volume.
        var inferred = new Dictionary<int, int>();
        var rootPath = series.RootFolder?.Path;
        foreach (var file in chapters.Where(c => c.ChapterFile is not null).Select(c => c.ChapterFile!).DistinctBy(f => f.Id))
        {
            var parsed = ReleaseNameParser.ParseFileName(file.RelativePath);
            if (!parsed.IsVolume || parsed.VolumeEnd is { } end && end != parsed.Volume)
            {
                continue;
            }

            var covered = chapters.Where(c => c.ChapterFileId == file.Id).ToList();
            if (rootPath is not null && LibraryPaths.Resolve(rootPath, file.RelativePath) is { } path && File.Exists(path)
                && UpgradeTrash.IsReplaceable(path))
            {
                try
                {
                    covered.AddRange(TorrentImportService.ChaptersCoveredBy(chapters, parsed, CbzReader.PageNames(path)));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    logger.LogDebug(ex, "Could not read the page names of {Path}", path);
                }
            }

            foreach (var chapter in covered.Where(c => c.Volume is null))
            {
                inferred.TryAdd(chapter.Id, parsed.Volume!.Value);
            }
        }

        return new LibraryState(language, chapters, inferred);
    }

    private sealed class LibraryState(string? language, List<Chapter> chapters, Dictionary<int, int> inferred)
    {
        public string? Language => language;

        private int? VolumeOf(Chapter chapter) => chapter.Volume ?? (inferred.TryGetValue(chapter.Id, out var v) ? v : null);

        public List<SpanChapter> All(UpgradeEvaluator evaluator, QualityScore score) =>
        [
            .. chapters.Select(c => new SpanChapter(c.Number ?? 0, VolumeOf(c),
                TorrentUpgradeRules.StateOf(evaluator, c, c.ChapterFile, score), c.ChapterFileId))
        ];

        public (List<SpanChapter> Chapters, bool MappingKnown) Covered(ReleaseSpan span, UpgradeEvaluator evaluator, QualityScore score)
        {
            var result = new List<SpanChapter>();
            foreach (var chapter in chapters)
            {
                var volume = VolumeOf(chapter);
                var inVolumes = span.Volumes is { } vr && volume is { } v && v >= vr.Start && v <= vr.End;
                var inSegments = chapter.Number is { } n && span.ChapterSegments.Any(s => n >= s.Start && n <= s.End);
                if (!inVolumes && !inSegments)
                {
                    continue;
                }

                result.Add(new SpanChapter(chapter.Number ?? 0, volume,
                    TorrentUpgradeRules.StateOf(evaluator, chapter, chapter.ChapterFile, score), chapter.ChapterFileId));
            }

            var numbers = chapters.Where(c => c.Number is not null).Select(c => c.Number!.Value).ToHashSet();
            foreach (var segment in span.ChapterSegments)
            {
                var start = decimal.Ceiling(segment.Start);
                var end = Math.Min(decimal.Floor(segment.End), start + MaxPlaceholders);
                for (var n = start; n <= end; n++)
                {
                    if (!numbers.Contains(n))
                    {
                        result.Add(new SpanChapter(n, null, ChapterSpanState.Unknown, null));
                    }
                }
            }

            var known = true;
            if (span.Volumes is { } volumes)
            {
                var first = (int)decimal.Ceiling(volumes.Start);
                var last = (int)Math.Min(decimal.Floor(volumes.End), first + MaxPlaceholders);
                var mapped = chapters.Select(VolumeOf).OfType<int>().ToHashSet();
                for (var v = first; v <= last && known; v++)
                {
                    known = mapped.Contains(v);
                }
            }

            return (result, known);
        }
    }
}
