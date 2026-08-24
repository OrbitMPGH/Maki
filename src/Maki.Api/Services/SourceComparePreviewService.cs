using System.Collections.Concurrent;
using System.Globalization;
using Maki.Api.Configuration;
using Maki.Core.Download;
using Maki.Core.Http;
using Maki.Core.Images;
using Maki.Core.Sources;
using SixLabors.ImageSharp;

namespace Maki.Api.Services;

/// <summary>One of a series' live source mappings, flattened so the background job needs no DbContext.</summary>
public record SourceCompareCandidate(int MappingId, string SourceName, string SourceSeriesId, string? LanguageFilter);

/// <summary>
/// One sampled page. <paramref name="Width"/>/<paramref name="Height"/> are null when the format
/// could not be read for measurement — AVIF, which browsers display happily and ImageSharp cannot
/// decode at all. The image is still shown; only its caption is thinner.
/// </summary>
public record ComparePage(string Url, int? Width, int? Height, long Bytes);

public record ComparePanel(
    int MappingId,
    string SourceName,
    string DisplayName,
    string Status,
    string? Error,
    string? ChapterLabel,
    /// <summary>
    /// This source's pages were matched against the others' by image content. False for a source
    /// carrying a different edition, whose column is shown for ranking but lines up with nothing.
    /// </summary>
    bool Aligned,
    /// <summary>
    /// One entry per grid row, null where this source has no page for that row. Row N is the same
    /// drawing in every aligned column.
    /// </summary>
    List<ComparePage?> Pages);

public record CompareSnapshot(
    int SeriesId,
    bool Running,
    bool MixedChapters,
    bool PagesAligned,
    decimal? ChapterNumber,
    List<decimal> CommonChapters,
    List<ComparePanel> Panels);

/// <summary>
/// Fetches a few pages of the same chapter from each of a series' sources so the user can judge
/// scan quality side by side and rank the sources themselves.
/// <para>
/// Pages are fetched <b>server-side into a cache directory</b> and served from there, rather than
/// proxied live. Page CDNs mostly live off the source's own domain and are not covered by
/// <see cref="ISource.CoverHosts"/>/<c>CoverHostPolicy</c>, and <see cref="PageRequest"/> carries
/// per-page headers plus scramble/XOR/pre-fetched-bytes handling that only
/// <see cref="PageDownloader"/> knows about. Going through the downloader gets all of that for
/// free and keeps the client from ever naming a URL, so this adds no SSRF surface.
/// </para>
/// <para>
/// Jobs run detached from the request: listing chapters and fetching pages across several
/// rate-limited sources takes tens of seconds, so the controller starts a job and the client polls
/// <see cref="Snapshot"/> — the same shape as <c>Series.SourceMatchPending</c>.
/// </para>
/// </summary>
public sealed class SourceComparePreviewService(
    SourceRegistry sourceRegistry,
    SourceChapterListCache chapterLists,
    PageDownloader pageDownloader,
    DownloadQueueService queue,
    AppPaths paths,
    ILogger<SourceComparePreviewService> logger)
{
    /// <summary>Sources fetched at once within a job. Different sources, so different rate limiters.</summary>
    private const int MaxParallelSources = 3;

    /// <summary>Comparisons running at once instance-wide. This is a user staring at a modal, not a batch job.</summary>
    private const int MaxConcurrentJobs = 2;

    /// <summary>
    /// Pages fetched beyond the sample size, so <see cref="PageAlignment"/> has something to work
    /// with. A source that opens with a credit page and a cover is already two pages out of step,
    /// and the offset search still needs overlapping pages left after shifting.
    /// </summary>
    private const int AlignmentLookahead = 3;

    /// <summary>A job that never returns must not pin its slot forever.</summary>
    private static readonly TimeSpan JobDeadline = TimeSpan.FromMinutes(3);

    /// <summary>
    /// How many chapter numbers the chapter picker offers. The lowest and highest few, not the
    /// whole catalogue: a 900-chapter series would otherwise put 900 numbers in every poll.
    /// </summary>
    private const int PickerEnds = 15;

    private readonly ConcurrentDictionary<int, Job> _jobs = new();

    /// <summary>
    /// Pages sampled per source. One is enough for a long strip — a single manhwa page is a whole
    /// screenful of art at full width. Page-based work needs a proper run of them: the front of a
    /// chapter is title pages and establishing spreads, and lettering and typesetting, which is
    /// where sources actually differ, only show up once the dialogue does.
    /// </summary>
    public static int SampleCountFor(string? seriesType)
    {
        var type = seriesType?.Trim().ToLowerInvariant() ?? string.Empty;
        var longStrip = type.Contains("manhwa") || type.Contains("manhua") ||
                        type.Contains("webtoon") || type.Contains("long strip");
        return longStrip ? 1 : 6;
    }

    /// <summary>
    /// Starts (or restarts) the comparison for a series. Returns as soon as the panels exist —
    /// they fill in independently, so one dead source never holds up the rest.
    /// </summary>
    /// <exception cref="InvalidOperationException">Too many comparisons already running.</exception>
    public CompareSnapshot Start(
        int seriesId, string? seriesType, IReadOnlyList<SourceCompareCandidate> candidates, decimal? chapterNumber)
    {
        // Finished jobs are kept so a modal left open still has something to draw, but only for as
        // long as anyone could still be looking at one.
        foreach (var (id, stale) in _jobs.Where(pair => pair.Value.Expired).ToList())
        {
            _jobs.TryRemove(id, out _);
            TryDelete(Path.Combine(paths.SourcePreviewDir, id.ToString()));
            stale.Cancel();
        }

        if (_jobs.TryRemove(seriesId, out var previous))
        {
            previous.Cancel();
        }
        else if (_jobs.Count(pair => !pair.Value.Finished) >= MaxConcurrentJobs)
        {
            throw new InvalidOperationException(
                "Another source comparison is already running. Wait for it to finish and try again.");
        }

        var seriesDir = Path.Combine(paths.SourcePreviewDir, seriesId.ToString());
        TryDelete(seriesDir);

        var job = new Job(seriesId, SampleCountFor(seriesType), chapterNumber)
        {
            Panels = [.. candidates.Select(c => new PanelState
            {
                MappingId = c.MappingId,
                SourceName = c.SourceName,
                DisplayName = sourceRegistry.Find(c.SourceName)?.DisplayName ?? c.SourceName,
                SourceSeriesId = c.SourceSeriesId,
                LanguageFilter = c.LanguageFilter
            })]
        };

        _jobs[seriesId] = job;
        job.Work = Task.Run(() => RunAsync(job));
        return Snapshot(job);
    }

    /// <summary>Current state of a series' comparison, or null when none has been started.</summary>
    public CompareSnapshot? Snapshot(int seriesId) =>
        _jobs.TryGetValue(seriesId, out var job) ? Snapshot(job) : null;

    /// <summary>
    /// The file holding one sampled page, or null. The source name is resolved through the registry
    /// by the caller before it reaches here, so no caller-supplied text becomes a path segment.
    /// </summary>
    public string? PageFile(int seriesId, string sourceName, int index)
    {
        var dir = Path.Combine(paths.SourcePreviewDir, seriesId.ToString(), sourceName);
        if (!Directory.Exists(dir))
        {
            return null;
        }

        return Directory.GetFiles(dir, $"{index:000}.*")
            .FirstOrDefault(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
    }

    private async Task RunAsync(Job job)
    {
        var ct = job.Cts.Token;
        var gate = new SemaphoreSlim(MaxParallelSources, MaxParallelSources);

        try
        {
            // Phase 1: every panel lists its source's chapters. Has to finish before any page is
            // fetched — the comparison is only worth anything if all sources show the same chapter.
            await Task.WhenAll(job.Panels.Select(panel => WithGate(gate, () => ListAsync(job, panel, ct), ct)));

            PickChapter(job);

            // Phase 2: fetch the sample pages.
            await Task.WhenAll(job.Panels.Select(panel => WithGate(gate, () => FetchAsync(job, panel, ct), ct)));

            // Phase 3: line the pages up. Only possible once every panel is in, since it compares
            // them against each other.
            AlignPages(job);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Source comparison for series {SeriesId} ended early", job.SeriesId);
        }
        finally
        {
            job.MarkFinished();
            gate.Dispose();
        }
    }

    private static async Task WithGate(SemaphoreSlim gate, Func<Task> work, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await work();
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task ListAsync(Job job, PanelState panel, CancellationToken ct)
    {
        try
        {
            // Waiting out a source's backoff can mean fifteen minutes of a spinner. Say so instead.
            if (queue.CooldownRemaining(panel.SourceName) > TimeSpan.Zero)
            {
                Fail(job, panel, "Rate limited right now, try again shortly");
                return;
            }

            var source = sourceRegistry.Find(panel.SourceName);
            if (source is null)
            {
                Fail(job, panel, "Source is no longer registered");
                return;
            }

            // Shares the single-flighted per-series listing with the download path, so reopening
            // the comparison inside the cache TTL costs no extra request.
            var chapters = await chapterLists.GetAsync(source, panel.SourceSeriesId, panel.LanguageFilter, ct);
            lock (job.Sync)
            {
                panel.Chapters = chapters;
            }

            if (chapters.Count == 0)
            {
                Fail(job, panel, "Source lists no chapters for this series");
            }
        }
        catch (Exception ex)
        {
            HandleFailure(job, panel, ex);
        }
    }

    /// <summary>
    /// Which chapter every source gets asked for, and which numbers the chapter picker offers.
    /// <para>
    /// <paramref name="requested"/> wins when given. Otherwise it is <b>the lowest chapter the most
    /// sources carry</b> — which on any normal series is chapter 1, the one everybody has and the
    /// only one nobody needs a spoiler warning for. Widest-agreement rather than plain lowest,
    /// because a source whose catalogue only goes back a few chapters (MangaDex drops old fan
    /// scans) would otherwise force every comparison onto a chapter only one source can show.
    /// </para>
    /// <para>
    /// The picker offers the <i>union</i> of what the sources list, not the shared subset, so
    /// chapter 1 is still selectable by hand on a series where the automatic pick had to go later.
    /// </para>
    /// </summary>
    public static (List<decimal> Picker, decimal? Target) PlanChapter(
        IReadOnlyList<IReadOnlyList<SourceChapter>> listings, decimal? requested)
    {
        var present = listings.Where(l => l.Count > 0).ToList();
        if (present.Count == 0)
        {
            return ([], requested);
        }

        // How many sources carry each number. Distinct per listing first, so a source that lists a
        // chapter twice doesn't count as two sources agreeing.
        var carriers = present
            .SelectMany(Numbers)
            .GroupBy(n => n)
            .ToDictionary(g => g.Key, g => g.Count());

        var union = carriers.Keys.OrderBy(n => n).ToList();

        // Only the ends: a 1400-chapter series would otherwise put 1400 numbers into every poll.
        var picker = union.Count <= PickerEnds * 2
            ? union
            : [.. union.Take(PickerEnds), .. union.TakeLast(PickerEnds)];

        var target = requested ?? union
            .OrderByDescending(n => carriers[n])
            .ThenBy(n => n)
            .First();

        return (picker, target);

        static IEnumerable<decimal> Numbers(IReadOnlyList<SourceChapter> listing) =>
            listing.Where(c => c.Number is not null).Select(c => c.Number!.Value).Distinct();
    }

    private static void PickChapter(Job job)
    {
        lock (job.Sync)
        {
            var listed = job.Panels.Where(p => p.Chapters is { Count: > 0 }).ToList();
            if (listed.Count == 0)
            {
                return;
            }

            var (picker, target) = PlanChapter([.. listed.Select(p => p.Chapters!)], job.RequestedChapter);
            job.CommonChapters = picker;
            job.ChapterNumber = target;

            foreach (var panel in listed)
            {
                panel.Target = target is { } number
                    ? panel.Chapters!.FirstOrDefault(c => c.Number == number)
                    : null;

                // A source that doesn't carry the target chapter falls back to its own first one,
                // rather than showing an empty column, and the snapshot says the comparison isn't
                // like-for-like. Not when the *user* named the chapter though: quietly answering a
                // different question than the one they asked is worse than an empty column.
                if (panel.Target is null && job.RequestedChapter is null)
                {
                    panel.Target = panel.Chapters!.Where(c => c.Number is not null).MinBy(c => c.Number);
                }

                if (panel.Target?.Number != target)
                {
                    job.MixedChapters = true;
                }
            }
        }
    }

    private async Task FetchAsync(Job job, PanelState panel, CancellationToken ct)
    {
        SourceChapter? target;
        lock (job.Sync)
        {
            if (panel.Status == PanelStatus.Failed)
            {
                return;
            }

            target = panel.Target;
            panel.Status = PanelStatus.Fetching;
        }

        if (target is null)
        {
            Fail(job, panel, "Chapter not listed by this source");
            return;
        }

        try
        {
            var source = sourceRegistry.GetRequired(panel.SourceName);

            // Resolved here rather than at listing time: several sources hand back short-lived URLs.
            var pages = await source.GetPagesAsync(target, ct);
            if (pages.Pages.Count == 0)
            {
                Fail(job, panel, "Source returned no pages");
                return;
            }

            // Truncating the page list is the whole trick — the downloader then fetches a sample
            // instead of a chapter, while still applying this source's headers, cooldown,
            // descrambling and XOR decryption.
            //
            // Deeper than the sample size on purpose: AlignPages has to see past whatever leading
            // credit or cover pages this source adds, and the offset search needs overlap left over
            // once it has shifted a sequence.
            var depth = Math.Min(job.SampleCount + AlignmentLookahead, pages.Pages.Count);
            var sample = new ChapterPages([.. pages.Pages.Take(depth)]);
            var dir = Path.Combine(paths.SourcePreviewDir, job.SeriesId.ToString(), panel.SourceName);
            var files = await pageDownloader.DownloadAsync(sample, panel.SourceName, dir, null, ct);

            var rendered = new List<ComparePage?>();
            var hashes = new List<ulong?>();
            for (var i = 0; i < files.Count; i++)
            {
                // Measuring and hashing are best-effort. ImageSharp cannot decode AVIF at all (see
                // ImageValidator, which trusts the container magic for exactly this reason) while
                // every browser renders it fine — so a format we can't read costs the page its
                // dimensions and its place in the alignment, never its place in the comparison.
                int? width = null;
                int? height = null;
                ulong? hash = null;
                try
                {
                    var info = await Image.IdentifyAsync(files[i], ct);
                    width = info.Width;
                    height = info.Height;
                    hash = await PerceptualHash.OfFileAsync(files[i], ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogDebug(ex, "Could not read {File} for comparison", files[i]);
                }

                rendered.Add(new ComparePage(
                    $"/api/v1/sourcemapping/compare/image/{job.SeriesId}/{panel.SourceName}/{i}?v={job.Token}",
                    width,
                    height,
                    new FileInfo(files[i]).Length));
                hashes.Add(hash);
            }

            lock (job.Sync)
            {
                // Everything fetched, shown unaligned for now: panels land one at a time and there
                // is nothing to align against until the last one is in. AlignPages trims this.
                panel.Pages = rendered;
                panel.Hashes = hashes;
                panel.Status = PanelStatus.Ready;
            }
        }
        catch (Exception ex)
        {
            HandleFailure(job, panel, ex);
        }
    }


    /// <summary>
    /// Trims every ready panel down to the same pages, in the same order, so that column 1 row 2 and
    /// column 2 row 2 are the same drawing. Sources disagree on how a chapter starts — a credit page
    /// here, a colour cover there — and comparing whatever landed at index 2 compares nothing.
    /// <para>
    /// Panels that failed sit this out; they have no pages either way. A single ready panel is left
    /// alone, since there is nothing to line it up against.
    /// </para>
    /// </summary>
    private static void AlignPages(Job job)
    {
        lock (job.Sync)
        {
            var ready = job.Panels.Where(p => p.Status == PanelStatus.Ready && p.Pages.Count > 0).ToList();
            if (ready.Count == 0)
            {
                return;
            }

            var result = PageAlignment.Align([.. ready.Select(p => (IReadOnlyList<ulong?>)p.Hashes)], job.SampleCount);
            if (result.Slots.Count == 0)
            {
                return;
            }

            for (var p = 0; p < ready.Count; p++)
            {
                var index = p;
                ready[index].Pages = [.. result.Slots.Select(slot => slot[index] is { } page ? ready[index].Pages[page] : null)];
                ready[index].Aligned = result.Aligned[index];
            }

            // Only worth telling the user about when two panels were actually matched to each other;
            // a lone source lines up with nothing and there is nothing to claim.
            job.PagesAligned = result.Aligned.Count(a => a) > 1;
        }
    }

    private void HandleFailure(Job job, PanelState panel, Exception ex)
    {
        if (ex is OperationCanceledException)
        {
            Fail(job, panel, "Timed out");
            return;
        }

        // Same shared cooldown the download queue uses, so a 429 raised by somebody comparing
        // sources also holds downloads off that site. Never the other way round: a preview
        // succeeding says nothing about whether downloads are healthy, so the backoff counter
        // is only ever raised here, never cleared.
        if (RateLimitDetector.IsRateLimit(ex, out var retryAfter))
        {
            var until = queue.EnterRateLimitCooldown(panel.SourceName, retryAfter);
            logger.LogInformation("Source comparison rate-limited by {Source} until {Until:u}", panel.SourceName, until);
            Fail(job, panel, "Rate limited, try again shortly");
            return;
        }

        logger.LogWarning(ex, "Source comparison failed on {Source} for series {SeriesId}",
            panel.SourceName, job.SeriesId);
        Fail(job, panel, ex.Message);
    }

    private static void Fail(Job job, PanelState panel, string error)
    {
        lock (job.Sync)
        {
            panel.Status = PanelStatus.Failed;
            panel.Error = error;
        }
    }

    private static CompareSnapshot Snapshot(Job job)
    {
        lock (job.Sync)
        {
            return new CompareSnapshot(
                job.SeriesId,
                !job.Finished,
                job.MixedChapters,
                job.PagesAligned,
                job.ChapterNumber,
                job.CommonChapters,
                [.. job.Panels.Select(p => new ComparePanel(
                    p.MappingId,
                    p.SourceName,
                    p.DisplayName,
                    p.Status,
                    p.Error,
                    // Invariant: a decimal chapter renders "6,5" on a comma-decimal server, which
                    // then sits next to the "6.5" the chapter picker shows.
                    p.Target?.Number?.ToString(CultureInfo.InvariantCulture) ?? p.Target?.NumberRaw,
                    p.Aligned,
                    p.Pages))]);
        }
    }

    private void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not clear source preview dir {Dir}", dir);
        }
    }

    private static class PanelStatus
    {
        public const string Listing = "listing";
        public const string Fetching = "fetching";
        public const string Ready = "ready";
        public const string Failed = "failed";
    }

    private sealed class PanelState
    {
        public required int MappingId { get; init; }
        public required string SourceName { get; init; }
        public required string DisplayName { get; init; }
        public required string SourceSeriesId { get; init; }
        public required string? LanguageFilter { get; init; }

        public string Status { get; set; } = PanelStatus.Listing;
        public string? Error { get; set; }
        public IReadOnlyList<SourceChapter>? Chapters { get; set; }
        public SourceChapter? Target { get; set; }
        public List<ComparePage?> Pages { get; set; } = [];

        /// <summary>
        /// Perceptual hash per fetched page, in served order, null for a page that could not be
        /// decoded. Input to <see cref="PageAlignment"/>.
        /// </summary>
        public List<ulong?> Hashes { get; set; } = [];

        public bool Aligned { get; set; }
    }

    private sealed class Job(int seriesId, int sampleCount, decimal? requestedChapter)
    {
        /// <summary>Guards every mutable field below — panels run in parallel and are read by pollers.</summary>
        public readonly object Sync = new();

        public readonly CancellationTokenSource Cts = new(JobDeadline);

        /// <summary>Cache-buster on page URLs: a re-run reuses the same paths with different images.</summary>
        public readonly string Token = Guid.NewGuid().ToString("N")[..8];

        public int SeriesId { get; } = seriesId;
        public int SampleCount { get; } = sampleCount;
        public decimal? RequestedChapter { get; } = requestedChapter;

        public List<PanelState> Panels { get; init; } = [];
        public List<decimal> CommonChapters { get; set; } = [];
        public decimal? ChapterNumber { get; set; }
        public bool MixedChapters { get; set; }

        /// <summary>Pages were matched across sources rather than shown at their raw indexes.</summary>
        public bool PagesAligned { get; set; }

        public Task? Work { get; set; }

        private long _finishedAtTicks;

        public bool Finished => Interlocked.Read(ref _finishedAtTicks) != 0;

        /// <summary>Nobody is coming back to a comparison they closed an hour ago.</summary>
        public bool Expired
        {
            get
            {
                var ticks = Interlocked.Read(ref _finishedAtTicks);
                return ticks != 0 && DateTime.UtcNow.Ticks - ticks > TimeSpan.FromHours(1).Ticks;
            }
        }

        public void MarkFinished() => Interlocked.Exchange(ref _finishedAtTicks, DateTime.UtcNow.Ticks);

        public void Cancel()
        {
            try
            {
                Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already finished.
            }
        }
    }
}
