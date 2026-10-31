using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Maki.Core.Configuration;
using Maki.Core.Images;
using Maki.Core.Paths;
using Maki.Data;
using Microsoft.EntityFrameworkCore;
using NetVips;

namespace Maki.Api.Services;

/// <summary>
/// The wizard's review step: what a plan would do to the library, measured by encoding a sample of
/// each bucket's files on this machine. One estimate at a time runs in the background; starting
/// another cancels it.
/// </summary>
public class PageConversionEstimateService(
    IServiceScopeFactory scopes, IAppSettings appSettings, IHostApplicationLifetime lifetime,
    ILogger<PageConversionEstimateService> logger)
{
    public const int FilesPerBucket = 12;
    public const int PagesPerFile = 3;

    /// <param name="State">"idle", "running", "done", "failed" or "cancelled".</param>
    /// <param name="Done">Sampled files encoded so far.</param>
    /// <param name="Total">Files the run samples.</param>
    /// <param name="Error">What went wrong, untranslated, for a failed run.</param>
    /// <param name="PlanHash">Equal for two requests with the same plan.</param>
    public sealed record EstimateDto(
        string State, int Done, int Total, EstimateResult? Result, string? Error, DateTime? StartedAtUtc,
        DateTime? FinishedAtUtc, string? PlanHash);

    /// <param name="Rules">One per rule, in order.</param>
    /// <param name="Overrides">Series with a fixed stop of their own; Never series are passed over.</param>
    public sealed record EstimateResult(
        IReadOnlyList<EstimateBucket> Rules, EstimateBucket Default, EstimateBucket Overrides, PassedOver PassedOver,
        IReadOnlyList<ConvertedEarlier> ConvertedEarlier, int ConvertedWithOriginalInTrash, double MsPerMegapixel);

    /// <param name="ExpectedLow">Bytes after the pass, 25th percentile.</param>
    /// <param name="ExpectedHigh">Bytes after the pass, 75th percentile.</param>
    /// <param name="HardlinkedBytes">Extrapolated from the sampled files; never counted as saving.</param>
    public sealed record EstimateBucket(
        bool Skip, int Files, long Bytes, int SampledFiles, long ExpectedLow, long ExpectedHigh, long HardlinkedBytes,
        double SecondsLow, double SecondsHigh);

    /// <param name="Undone">Files an undo put back, which the pass leaves alone.</param>
    /// <param name="AlreadyChecked">
    /// Files the pass already decided and left as they were under the same answer, or could never convert
    /// (hardlinked when it looked, a PDF, a path that does not resolve); it does not revisit them.
    /// </param>
    public sealed record PassedOver(
        int HardlinkedFiles, long HardlinkedBytes, int CleanupDue, int AlreadyCompact, int DownloadInFlight,
        int PendingImport, int SkippedByRule, long SkippedByRuleBytes, int NeverSeries, int NeverFiles, long NeverBytes,
        int Undone, int AlreadyChecked, long AlreadyCheckedBytes);

    /// <param name="CheckedWith">The setting the files were converted at, or "unknown".</param>
    public sealed record ConvertedEarlier(string CheckedWith, int Files, long SavedBytes);

    private sealed record Pick(PageConversionPlanService.ScopeFile File, PageConversionSettings Settings);

    private sealed record PageTime(double Ms, double Megapixels);

    private sealed record FileSample(long Size, bool Hardlinked, double Ratio, IReadOnlyList<PageTime> Pages, long PageBytes);

    private sealed class Group
    {
        public bool Skip { get; init; }
        public List<Pick> Files { get; } = [];
        public List<Pick> Sampled { get; } = [];
        public List<FileSample> Samples { get; } = [];
        public long Bytes => Files.Sum(f => f.File.Size);
    }

    private readonly Lock _sync = new();
    private EstimateDto _state = new("idle", 0, 0, null, null, null, null, null);
    private CancellationTokenSource? _cts;
    private int _generation;

    /// <summary>Awaited before each sampled file; tests hold a run here.</summary>
    internal Func<CancellationToken, Task> BeforeFile { get; init; } = _ => Task.CompletedTask;

    /// <summary>The run started last.</summary>
    internal Task Running { get; private set; } = Task.CompletedTask;

    public EstimateDto Current
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    /// <summary>
    /// Starts an estimate of <paramref name="plan"/>, cancelling one still running. The same plan
    /// asked for again while it runs keeps the run going.
    /// </summary>
    public EstimateDto Start(PageConversionPlan plan)
    {
        var hash = Hash(plan);
        lock (_sync)
        {
            if (_state.State == "running" && _state.PlanHash == hash)
            {
                return _state;
            }

            _cts?.Cancel();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
            _cts = cts;
            var generation = ++_generation;
            _state = new EstimateDto("running", 0, 0, null, null, DateTime.UtcNow, null, hash);
            Running = Task.Run(() => RunAsync(generation, plan, cts));
            return _state;
        }
    }

    /// <summary>A stable hash of what decides the estimate: format, default stop, PNG choice and each rule but its name.</summary>
    public static string Hash(PageConversionPlan plan)
    {
        var parts = new List<string>
        {
            PageConversionSettings.FormatName(plan.Format), PageConversionSettings.StopName(plan.DefaultStop),
            plan.PngLossless ? "png-lossless" : "png"
        };
        parts.AddRange(plan.Rules.OrderBy(r => r.Order).Select(r =>
            $"{r.Conditions.ToJson()}|{(r.Skip || r.Stop is not { } stop ? "skip" : PageConversionSettings.StopName(stop))}"));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', parts)));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    private async Task RunAsync(int generation, PageConversionPlan plan, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            var result = await EstimateAsync(plan, (done, total) => Update(generation, s => s with { Done = done, Total = total }), ct);
            Update(generation, s => s with { State = "done", Result = result, FinishedAtUtc = DateTime.UtcNow });
        }
        // On shutdown the scope may be disposed under the run before it sees the token.
        catch (Exception) when (ct.IsCancellationRequested)
        {
            Update(generation, s => s with { State = "cancelled", FinishedAtUtc = DateTime.UtcNow });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The page conversion estimate failed");
            Update(generation, s => s with { State = "failed", Error = ex.Message, FinishedAtUtc = DateTime.UtcNow });
        }
        finally
        {
            lock (_sync)
            {
                if (_cts == cts)
                {
                    _cts = null;
                }
            }

            cts.Dispose();
        }
    }

    private void Update(int generation, Func<EstimateDto, EstimateDto> change)
    {
        lock (_sync)
        {
            if (generation == _generation)
            {
                _state = change(_state);
            }
        }
    }

    private async Task<EstimateResult> EstimateAsync(PageConversionPlan plan, Action<int, int> progress, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        var files = await new PageConversionPlanService(db).ScopeFilesAsync(ct);
        var series = await PageConversionPlanService.SeriesFactsAsync(db, null, ct);
        var busy = (await SeriesLocks.InFlight(db.DownloadQueue.IgnoreQueryFilters())
            .Select(q => q.SeriesId)
            .Distinct()
            .ToListAsync(ct)).ToHashSet();
        var rules = plan.Rules.OrderBy(r => r.Order).ToList();
        var groups = rules.Select(r => new Group { Skip = r.Skip }).ToList();
        var fallthrough = new Group();
        var overrides = new Group();
        var candidates = new List<(Group Target, Pick Pick)>();
        var converted = new List<PageConversionPlanService.ScopeFile>();
        int compact = 0, inFlight = 0, pendingImport = 0, skippedByRule = 0, neverFiles = 0, undone = 0, dueFiles = 0,
            alreadyChecked = 0;
        long skippedByRuleBytes = 0, neverBytes = 0, alreadyCheckedBytes = 0;

        foreach (var file in files)
        {
            if (file.OriginalImageFormat is not null)
            {
                converted.Add(file);
                continue;
            }

            if (file.PendingImport)
            {
                pendingImport++;
                continue;
            }

            if (file.CheckedWith == PlanAction.UndoneName)
            {
                undone++;
                continue;
            }

            if (busy.Contains(file.SeriesId))
            {
                inFlight++;
                continue;
            }

            var placement = PageConversionPlanService.Classify(plan, rules, file, series.GetValueOrDefault(file.SeriesId));
            var settings = (placement.Action as PlanAction.Convert)?.Settings;
            Group target;
            switch (placement.Place)
            {
                case PageConversionPlanService.Place.AlreadyCompact:
                    compact++;
                    continue;
                case PageConversionPlanService.Place.Override when settings is null:
                    neverFiles++;
                    neverBytes += file.Size;
                    continue;
                case PageConversionPlanService.Place.Override:
                    target = overrides;
                    break;
                case PageConversionPlanService.Place.Rule when settings is null:
                    skippedByRule++;
                    skippedByRuleBytes += file.Size;
                    groups[placement.Rule].Files.Add(new Pick(file, plan.Settings(plan.DefaultStop)));
                    continue;
                case PageConversionPlanService.Place.Rule:
                    target = groups[placement.Rule];
                    break;
                default:
                    target = fallthrough;
                    break;
            }

            if (file.ConversionCheckedAtUtc is not null &&
                (file.CheckedWith == PlanAction.HardSkipName || file.CheckedWith == placement.Action!.Name))
            {
                alreadyChecked++;
                alreadyCheckedBytes += file.Size;
                continue;
            }

            candidates.Add((target, new Pick(file, settings!)));
        }

        progress(0, candidates.GroupBy(c => c.Target).Sum(g => Math.Min(FilesPerBucket, g.Count())));
        var cleanupDue = new Dictionary<int, HashSet<int>>();
        foreach (var seriesId in candidates.Select(c => c.Pick.File.SeriesId).Distinct())
        {
            cleanupDue[seriesId] = await PageConversionService.CleanupDueFilesAsync(db, appSettings, seriesId, ct);
        }

        foreach (var (target, pick) in candidates)
        {
            if (cleanupDue[pick.File.SeriesId].Contains(pick.File.Id))
            {
                dueFiles++;
            }
            else
            {
                target.Files.Add(pick);
            }
        }

        var converting = groups.Where(g => !g.Skip).Append(fallthrough).Append(overrides).ToList();
        foreach (var group in converting)
        {
            group.Sampled.AddRange(Spread(group.Files));
        }

        var total = converting.Sum(g => g.Sampled.Count);
        var done = 0;
        progress(0, total);
        foreach (var group in converting)
        {
            foreach (var pick in group.Sampled)
            {
                await BeforeFile(ct);
                if (await SampleAsync(pick, ct) is { } sample)
                {
                    group.Samples.Add(sample);
                }

                progress(++done, total);
            }
        }

        var allPages = converting.SelectMany(g => g.Samples).SelectMany(s => s.Pages).ToList();
        var buckets = new Dictionary<Group, (EstimateBucket Bucket, int HardlinkedFiles)>();
        foreach (var group in groups.Append(fallthrough).Append(overrides))
        {
            buckets[group] = Bucket(group, allPages);
        }

        var trashed = await PageConversionUndoService.Restorable(db, null).CountAsync(ct);
        var totalMs = allPages.Sum(p => p.Ms);
        var totalMegapixels = allPages.Sum(p => p.Megapixels);
        return new EstimateResult(
            [.. groups.Select(g => buckets[g].Bucket)],
            buckets[fallthrough].Bucket,
            buckets[overrides].Bucket,
            new PassedOver(
                buckets.Values.Sum(b => b.HardlinkedFiles), buckets.Values.Sum(b => b.Bucket.HardlinkedBytes), dueFiles,
                compact, inFlight, pendingImport, skippedByRule, skippedByRuleBytes,
                series.Values.Count(s => s.Override == Maki.Core.Entities.PageConversionOverride.Never), neverFiles, neverBytes,
                undone, alreadyChecked, alreadyCheckedBytes),
            [.. converted
                .GroupBy(f => f.CheckedWith ?? "unknown")
                .Select(g => new ConvertedEarlier(g.Key, g.Count(), g.Sum(f => f.ConversionSavedBytes)))
                .OrderByDescending(c => c.Files)
                .ThenBy(c => c.CheckedWith, StringComparer.Ordinal)],
            trashed,
            totalMegapixels > 0 ? totalMs / totalMegapixels : 0);
    }

    /// <summary>Up to <see cref="FilesPerBucket"/> files evenly spaced by raw bits per pixel, unmeasured ones last.</summary>
    private static List<Pick> Spread(List<Pick> files)
    {
        var sorted = files
            .OrderBy(f => PageConversionPlanService.Candidate(f.File.Facts, NoSeries).BitsPerPixel is null ? 1 : 0)
            .ThenBy(f => PageConversionPlanService.Candidate(f.File.Facts, NoSeries).BitsPerPixel)
            .ThenBy(f => f.File.Id)
            .ToList();
        if (sorted.Count <= FilesPerBucket)
        {
            return sorted;
        }

        return Enumerable.Range(0, FilesPerBucket)
            .Select(i => sorted[(int)Math.Round(i * (sorted.Count - 1) / (double)(FilesPerBucket - 1))])
            .ToList();
    }

    private static readonly PageConversionPlanService.SeriesFacts NoSeries =
        new(Maki.Core.Entities.PageConversionOverride.Default, null, []);

    /// <summary>
    /// Encodes up to three middle pages the way the pass would, each kept when it does not clear the
    /// page floor, and returns the file's ratio after the file floor. A hardlinked file is never
    /// converted, so it counts as kept whole and is not encoded. Null when the file cannot be read.
    /// </summary>
    private async Task<FileSample?> SampleAsync(Pick pick, CancellationToken ct)
    {
        var file = pick.File;
        if (LibraryPaths.Resolve(file.Root, file.RelativePath) is not { } path || !File.Exists(path))
        {
            return null;
        }

        if (HardLinks.IsShared(path))
        {
            return new FileSample(file.Size, true, 1, [], 0);
        }

        List<(byte[] Bytes, string Extension)> pages;
        try
        {
            using var zip = ZipFile.OpenRead(path);
            pages = ArchivePageConverter.MiddlePages(zip, PagesPerFile, ct);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException or
                                       UnauthorizedAccessException or NotSupportedException)
        {
            logger.LogDebug(ex, "Could not read {Path} for the conversion estimate", path);
            return null;
        }

        long original = 0, kept = 0;
        var times = new List<PageTime>();
        foreach (var (bytes, extension) in pages)
        {
            var (size, ms) = await ImageWorkGate.RunAsync(() => Task.FromResult(Encode(bytes, extension, pick.Settings)), ct);
            original += bytes.Length;
            kept += size;
            if (ImageCodec.TryIdentify(bytes) is { Width: > 0, Height: > 0 } header)
            {
                times.Add(new PageTime(ms, (double)header.Width * header.Height / 1_000_000));
            }
        }

        var ratio = original == 0 ? 1 : (double)kept / original;
        if (ratio > 1 - PageConversion.MinFileSaving)
        {
            ratio = 1;
        }

        return new FileSample(file.Size, false, ratio, times, original);
    }

    private static (long Size, double Ms) Encode(byte[] bytes, string extension, PageConversionSettings settings)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var page = PageConversion.Convert(bytes, extension, settings);
            return (page.Converted ? page.Bytes.Length : bytes.Length, watch.Elapsed.TotalMilliseconds);
        }
        catch (VipsException)
        {
            return (bytes.Length, watch.Elapsed.TotalMilliseconds);
        }
    }

    private static (EstimateBucket Bucket, int HardlinkedFiles) Bucket(Group group, List<PageTime> allPages)
    {
        var files = group.Files.Count;
        var bytes = group.Bytes;
        var samples = group.Samples;
        if (group.Skip || samples.Count == 0)
        {
            return (new EstimateBucket(group.Skip, files, bytes, 0, bytes, bytes, 0,
                group.Skip ? 0 : Seconds(group, 0, allPages, 0.25), group.Skip ? 0 : Seconds(group, 0, allPages, 0.75)), 0);
        }

        var sampledBytes = samples.Sum(s => s.Size);
        var hardShare = sampledBytes == 0 ? 0 : (double)samples.Where(s => s.Hardlinked).Sum(s => s.Size) / sampledBytes;
        var hardFiles = (int)Math.Round(files * (double)samples.Count(s => s.Hardlinked) / samples.Count);
        var pages = samples.SelectMany(s => s.Pages).ToList();
        return (new EstimateBucket(false, files, bytes, samples.Count,
            (long)Math.Round(bytes * WeightedPercentile(samples, 0.25)),
            (long)Math.Round(bytes * WeightedPercentile(samples, 0.75)),
            (long)Math.Round(bytes * hardShare),
            Seconds(group, hardShare, pages.Count > 0 ? pages : allPages, 0.25),
            Seconds(group, hardShare, pages.Count > 0 ? pages : allPages, 0.75)), hardFiles);
    }

    /// <summary>The ratio at <paramref name="p"/>, each file weighted by its size.</summary>
    private static double WeightedPercentile(List<FileSample> samples, double p)
    {
        var sorted = samples.OrderBy(s => s.Ratio).ToList();
        var total = sorted.Sum(s => (double)Math.Max(1, s.Size));
        var running = 0d;
        foreach (var sample in sorted)
        {
            running += Math.Max(1, sample.Size);
            if (running >= p * total)
            {
                return sample.Ratio;
            }
        }

        return sorted[^1].Ratio;
    }

    /// <summary>
    /// Measured files count their pages times the median page; unmeasured ones are extrapolated by
    /// bytes. Hardlinked files are never encoded, so their share is left out. Samples are timed one
    /// page at a time, and the pass encodes as many pages at once as the conversion limit allows.
    /// </summary>
    private static double Seconds(Group group, double hardShare, List<PageTime> pages, double p)
    {
        if (pages.Count == 0 || group.Files.Count == 0)
        {
            return 0;
        }

        var measured = group.Files.Select(f => f.File)
            .Where(f => f.PageCount is > 0 && f.MedianWidth is > 0 && f.MedianHeight is > 0)
            .ToList();
        var megapixels = measured.Sum(f => (double)f.PageCount!.Value * f.MedianWidth!.Value * f.MedianHeight!.Value) / 1_000_000;
        var measuredBytes = measured.Sum(f => f.Size);
        var unmeasuredBytes = group.Bytes - measuredBytes;
        if (unmeasuredBytes > 0)
        {
            var sampledPageBytes = group.Samples.Sum(s => s.PageBytes);
            var perByte = measuredBytes > 0
                ? megapixels / measuredBytes
                : sampledPageBytes > 0 ? group.Samples.SelectMany(s => s.Pages).Sum(x => x.Megapixels) / sampledPageBytes : 0;
            megapixels += unmeasuredBytes * perByte;
        }

        var rates = pages.Where(x => x.Megapixels > 0).Select(x => x.Ms / x.Megapixels).Order().ToList();
        if (rates.Count == 0)
        {
            return 0;
        }

        var rate = rates[(int)Math.Clamp(Math.Round(p * (rates.Count - 1)), 0, rates.Count - 1)];
        return megapixels * (1 - hardShare) * rate / 1000 / ImageWorkGate.ConversionLimit;
    }
}
