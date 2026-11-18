using Maki.Core.Download;
using Maki.Core.Http;
using Maki.Core.Images;
using Maki.Core.Sources;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

public sealed record PageHashRun(int Hashed, int Failed, int Skipped, int Waiting);

/// <summary>Hashes every page of one source chapter, or returns null when any page could not be had.</summary>
public interface IChapterPageHasher
{
    Task<PageEvidence?> HashAsync(ISource source, SourceChapter chapter, CancellationToken ct);
}

/// <summary>Downloads the chapter's pages into a temporary folder, hashes them and deletes them again.</summary>
public class ChapterPageHasher(PageDownloader pageDownloader, ILogger<ChapterPageHasher> logger) : IChapterPageHasher
{
    public async Task<PageEvidence?> HashAsync(ISource source, SourceChapter chapter, CancellationToken ct)
    {
        var pages = await source.GetPagesAsync(chapter, ct);
        if (pages.Pages.Count == 0)
        {
            return null;
        }

        var dir = Path.Combine(Path.GetTempPath(), "maki-page-hashes", Guid.NewGuid().ToString("N"));
        try
        {
            var files = await pageDownloader.DownloadAsync(pages, source.Name, dir, null, ct, source as IPageClientSource);
            return files.Count == pages.Pages.Count ? PageHashStore.Of(files, logger) : null;
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}

public static class PageHashStore
{
    /// <summary>
    /// Every page's hash in order, and the chapter's strip (<see cref="StripHash"/>); null when a page
    /// does not decode, since a partial set would score as a better match. A strip that cannot be made
    /// (a very long webtoon) is left out and the pages alone are kept.
    /// </summary>
    public static PageEvidence? Of(IReadOnlyList<string> files, ILogger logger)
    {
        var hashes = new List<ulong>(files.Count);
        foreach (var file in files)
        {
            try
            {
                hashes.Add(PerceptualHash.OfFile(file));
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not hash page {File}", Path.GetFileName(file));
                return null;
            }
        }

        ulong[]? strip;
        try
        {
            strip = StripHash.OfFiles(files);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not make the page strip of {Files} pages", files.Count);
            strip = null;
        }

        return hashes.Count == 0 ? null : new PageEvidence(hashes, strip);
    }

    /// <summary>
    /// Stores an upload's hashes. A strip that could not be made is stored as empty, so the check does
    /// not fetch the chapter again for it.
    /// </summary>
    public static Task StoreAsync(MakiDbContext db, int mappingId, string sourceChapterId, PageEvidence evidence, CancellationToken ct)
    {
        var pages = ChapterPageMatch.Format(evidence.Pages);
        var strip = evidence.Strip is { } windows ? ChapterPageMatch.FormatStrip([.. windows]) : string.Empty;
        return db.ChapterSourceLinks
            .Where(l => l.SourceMappingId == mappingId && l.SourceChapterId == sourceChapterId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.PageHashes, pages).SetProperty(l => l.StripHashes, strip), ct);
    }

    /// <summary>
    /// Whether a chapter about to be downloaded could take part in a fold: its number is a part, or
    /// another chapter of the series is a part of its floor. Only those are hashed at download.
    /// </summary>
    public static async Task<bool> WantedForAsync(MakiDbContext db, int seriesId, decimal? number, CancellationToken ct)
    {
        if (number is not { } n || n <= 0)
        {
            return false;
        }

        var floor = decimal.Floor(n);
        if (n != floor)
        {
            return true;
        }

        // Chapter.Number is REAL, which SQLite cannot compare as a decimal, so the floor test runs here.
        var numbers = await db.Chapters.AsNoTracking()
            .Where(c => c.SeriesId == seriesId && c.Number != null)
            .Select(c => c.Number!.Value)
            .ToListAsync(ct);
        return numbers.Any(x => x > floor && x < floor + 1);
    }
}

/// <summary>
/// Fetches the page hashes merged chapter folds wait on (<see cref="ChapterReconciler.CheckPages"/>) and
/// stores them on the link. Unlike a page count this downloads every page of the chapter, so a run takes
/// at most <see cref="ChaptersPerRun"/>, floors closest to settled first. Chapters downloaded into the
/// library are hashed as they arrive (<see cref="PageHashStore.WantedForAsync"/>), so most checks
/// only fetch the side that is not on disk. A failed hash waits a day, like a failed count.
/// </summary>
public class ChapterPageHashService(
    MakiDbContext db,
    ChapterFoldPreviewService folds,
    SourceRegistry sourceRegistry,
    DownloadQueueService queue,
    IChapterPageHasher hasher,
    PageCountFailures failures,
    TimeProvider time,
    ILogger<ChapterPageHashService> logger)
{
    public const int ChaptersPerRun = 40;

    public const int ChaptersPerSeries = 12;

    /// <summary>A source that needs a browser opens a page per chapter; its syncs and downloads queue behind it.</summary>
    public const int BrowserChaptersPerRun = 10;

    private static readonly TimeSpan RetryFailedAfter = TimeSpan.FromDays(1);

    /// <param name="seriesId">
    /// One series only, for the check right after it is matched (<see cref="ChapterFoldChecks"/>): the
    /// budget is then the whole limit, browser sources included, since that series' downloads wait on it.
    /// </param>
    public async Task<PageHashRun> RunAsync(int budget, CancellationToken ct, int? seriesId = null)
    {
        var perSeriesLimit = seriesId is null ? ChaptersPerSeries : budget;
        var browserLimit = seriesId is null ? BrowserChaptersPerRun : budget;
        var (floors, _) = await folds.DecideAsync(seriesId, ct);
        var waiting = floors
            .Where(f => f.MissingPages.Count > 0)
            .OrderBy(f => f.MissingPages.Count)
            .ThenBy(f => f.SeriesId)
            .ThenBy(f => f.Decision.Floor)
            .SelectMany(f => f.MissingPages)
            .DistinctBy(l => (l.MappingId, l.ChapterId))
            .ToList();

        var now = time.GetUtcNow();
        var blocked = new HashSet<string>(await queue.HeldSourcesAsync(ct), StringComparer.OrdinalIgnoreCase);
        var perSeries = new Dictionary<int, int>();
        var perBrowser = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int hashed = 0, failed = 0, skipped = 0, spent = 0;
        foreach (var link in waiting)
        {
            if (spent >= budget)
            {
                break;
            }

            if (perSeries.GetValueOrDefault(link.SeriesId) >= perSeriesLimit ||
                blocked.Contains(link.SourceName) ||
                (failures.RecentHashes.TryGetValue((link.MappingId, link.SourceChapterId), out var failedAt) && now - failedAt < RetryFailedAfter))
            {
                skipped++;
                continue;
            }

            if (sourceRegistry.Find(link.SourceName) is not { } source ||
                queue.CooldownRemaining(link.SourceName) > TimeSpan.Zero)
            {
                blocked.Add(link.SourceName);
                skipped++;
                continue;
            }

            if (source.Capabilities.HasFlag(SourceCapabilities.NeedsBrowser))
            {
                if (perBrowser.GetValueOrDefault(source.Name) >= browserLimit)
                {
                    blocked.Add(link.SourceName);
                    skipped++;
                    continue;
                }

                perBrowser[source.Name] = perBrowser.GetValueOrDefault(source.Name) + 1;
            }

            spent++;
            perSeries[link.SeriesId] = perSeries.GetValueOrDefault(link.SeriesId) + 1;
            try
            {
                if (await HashAllAsync(source, link, ct) is not { Pages.Count: > 0 } hashes)
                {
                    failures.RecentHashes[(link.MappingId, link.SourceChapterId)] = now;
                    failed++;
                    continue;
                }

                await PageHashStore.StoreAsync(db, link.MappingId, link.SourceChapterId, hashes, ct);
                hashed++;
            }
            catch (Exception ex) when (RateLimitDetector.IsRateLimit(ex, out var retryAfter))
            {
                var until = queue.EnterRateLimitCooldown(link.SourceName, retryAfter);
                logger.LogInformation("Page hashing rate-limited by {Source} until {Until:u}", link.SourceName, until);
                blocked.Add(link.SourceName);
                failed++;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogDebug(ex, "Could not hash the pages of {Source} chapter {Chapter}", link.SourceName, link.SourceChapterId);
                failures.RecentHashes[(link.MappingId, link.SourceChapterId)] = now;
                failed++;
            }
        }

        if (hashed + failed > 0)
        {
            logger.LogInformation(
                "Hashed pages for {Hashed} chapters ({Failed} failed, {Skipped} passed over, {Waiting} waiting in all)",
                hashed, failed, skipped, waiting.Count);
        }

        return new PageHashRun(hashed, failed, skipped, waiting.Count);
    }

    /// <summary>
    /// A folded chapter's link stands for every part, so each part is hashed and the evidence joined in
    /// reading order; its first part alone would overwrite the whole's hashes with part of them.
    /// </summary>
    private async Task<PageEvidence?> HashAllAsync(ISource source, FoldLink link, CancellationToken ct)
    {
        var parts = new List<PageEvidence>();
        foreach (var chapter in link.ToSourceChapters())
        {
            if (await hasher.HashAsync(source, chapter, ct) is not { Pages.Count: > 0 } evidence)
            {
                return null;
            }

            parts.Add(evidence);
        }

        return parts.Count == 1
            ? parts[0]
            : new PageEvidence(
                [.. parts.SelectMany(p => p.Pages)],
                parts.All(p => p.Strip is { Count: > 0 }) ? [.. parts.SelectMany(p => p.Strip!)] : null);
    }
}
