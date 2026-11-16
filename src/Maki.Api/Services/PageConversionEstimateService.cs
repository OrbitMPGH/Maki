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
    /// <param name="Total">Every bucket the pass converts, added up.</param>
    public sealed record EstimateResult(
        IReadOnlyList<EstimateBucket> Rules, EstimateBucket Default, EstimateBucket Overrides, PassedOver PassedOver,
        IReadOnlyList<ConvertedEarlier> ConvertedEarlier, int ConvertedWithOriginalInTrash, double MsPerMegapixel,
        EstimateTotal Total);

    /// <summary>
    /// What the pass would do to one bucket. <c>Expected</c> is the bucket's bytes times the sampled files'
    /// size-weighted mean ratio; the low and high ends are a 90% range for that total, from the sampling error
    /// of the mean. They are never the spread of single files, which is far wider and says nothing about a
    /// total over thousands of them. Seconds work the same way from the sampled pages' encode rate.
    /// </summary>
    /// <param name="HardlinkedBytes">Extrapolated from the sampled files; never counted as saving.</param>
    public sealed record EstimateBucket(
        bool Skip, int Files, long Bytes, int SampledFiles, long ExpectedLow, long ExpectedHigh, long HardlinkedBytes,
        double SecondsLow, double SecondsHigh, long Expected, double Seconds);

    /// <summary>The converting buckets added up; the ranges combine the buckets' sampling errors, which are independent.</summary>
    public sealed record EstimateTotal(
        int Files, long Bytes, bool Sampled, long Expected, long ExpectedLow, long ExpectedHigh, double Seconds,
        double SecondsLow, double SecondsHigh);

    /// <summary>z for a two-sided 90% range.</summary>
    private const double Z90 = 1.645;

    /// <summary>The narrowest size range, as a share of the expected size: only three pages of each file are encoded.</summary>
    internal const double MinSizeSpread = 0.03;

    /// <summary>The narrowest time range, as a share of the expected time: how busy the server is moves it more than sampling.</summary>
    internal const double MinTimeSpread = 0.15;

    /// <summary>A sampled estimate and the half width of its 90% range.</summary>
    private readonly record struct Figure(double Value, double Half)
    {
        public static readonly Figure None = new(0, 0);
    }

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

    /// <param name="Ms">The page's share of its file's wall clock, times the pages that ran together.</param>
    /// <param name="InFlight">How many pages like it the pass encodes at once.</param>
    private sealed record PageTime(double Ms, double Megapixels, int InFlight);

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

    /// <summary>
    /// A stable hash of what decides the estimate: the default settings (formats, stops, PNG choice, split
    /// height) and each rule but its name.
    /// </summary>
    public static string Hash(PageConversionPlan plan)
    {
        // The format is named on its own too: at Lossless the default's name leaves it out, yet series
        // set to a fixed stop still convert in it.
        var parts = new List<string>
        {
            PageConversionSettings.FormatName(plan.Format), plan.DefaultSettings.Name(), plan.PngLossless ? "png-lossless" : "png"
        };
        parts.AddRange(plan.Rules.OrderBy(r => r.Order).Select(r =>
            $"{r.Conditions.ToJson()}|{(r.Skip ? "skip" : plan.Settings(r).Name())}"));
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
        var buckets = new Dictionary<Group, (EstimateBucket Bucket, int HardlinkedFiles, Figure Size, Figure Time)>();
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
            totalMegapixels > 0 ? totalMs / totalMegapixels : 0,
            Total([.. buckets.Values.Where(b => !b.Bucket.Skip && b.Bucket.Files > 0)]));
    }

    private static EstimateTotal Total(IReadOnlyList<(EstimateBucket Bucket, int HardlinkedFiles, Figure Size, Figure Time)> buckets)
    {
        var bytes = buckets.Sum(b => b.Bucket.Bytes);
        var expected = buckets.Sum(b => b.Size.Value);
        var sizeHalf = Math.Sqrt(buckets.Sum(b => b.Size.Half * b.Size.Half));
        var seconds = buckets.Sum(b => b.Time.Value);
        var timeHalf = Math.Sqrt(buckets.Sum(b => b.Time.Half * b.Time.Half));
        return new EstimateTotal(
            buckets.Sum(b => b.Bucket.Files), bytes, buckets.Any(b => b.Bucket.SampledFiles > 0),
            (long)Math.Round(expected),
            (long)Math.Round(Math.Max(0, expected - sizeHalf)),
            (long)Math.Round(Math.Min(bytes, expected + sizeHalf)),
            seconds, Math.Max(0, seconds - timeHalf), seconds + timeHalf);
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

        // The pages encode together through the conversion gate, as the pass would run them, and the file's
        // wall clock from the first encode starting to the last one ending is shared out by megapixels. Times
        // the pages that actually ran together, it is lane time.
        var running = 0;
        var together = 0;
        var first = long.MaxValue;
        var last = 0L;
        var sizes = await Task.WhenAll(pages.Select(p => Task.Run(() => ImageWorkGate.RunConversionAsync(() =>
        {
            InterlockedMin(ref first, Stopwatch.GetTimestamp());
            InterlockedMax(ref together, Interlocked.Increment(ref running));
            try
            {
                return Encode(p.Bytes, p.Extension, pick.Settings);
            }
            finally
            {
                Interlocked.Decrement(ref running);
                InterlockedMax(ref last, Stopwatch.GetTimestamp());
            }
        }, PageConversion.WeightOf(p.Bytes, p.Extension, pick.Settings), ct), ct)));
        var wall = pages.Count == 0 ? 0 : Stopwatch.GetElapsedTime(first, last).TotalMilliseconds;

        var original = pages.Sum(p => (long)p.Bytes.Length);
        var kept = sizes.Sum();
        var megapixels = pages
            .Select(p => ImageCodec.TryIdentify(p.Bytes) is { Width: > 0, Height: > 0 } header
                ? (double)header.Width * header.Height / 1_000_000
                : 0)
            .ToList();
        var sampled = megapixels.Sum();
        // Fewer pages ran together than were sampled when the gate held them back, and it holds the pass back
        // the same way; when all of them did, the pass runs as many as its lanes allow.
        var lanes = ImageWorkGate.ConversionsInFlight(pages.Any(p => PageConversion.WeightOf(p.Bytes, p.Extension, pick.Settings).Avif));
        var inFlight = together < pages.Count ? Math.Max(1, together) : Math.Max(together, lanes);
        var times = megapixels
            .Where(mp => mp > 0)
            .Select(mp => new PageTime(wall * Math.Max(1, together) * mp / sampled, mp, inFlight))
            .ToList();

        var ratio = original == 0 ? 1 : (double)kept / original;
        if (ratio > 1 - PageConversion.MinFileSaving)
        {
            ratio = 1;
        }

        return new FileSample(file.Size, false, ratio, times, original);
    }

    private static long Encode(byte[] bytes, string extension, PageConversionSettings settings)
    {
        try
        {
            var page = PageConversion.Convert(bytes, extension, settings);
            return page.Converted ? page.Bytes.Length : bytes.Length;
        }
        catch (VipsException)
        {
            return bytes.Length;
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }

    private static void InterlockedMax(ref long target, long value)
    {
        long seen;
        while (value > (seen = Interlocked.Read(ref target)) && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }

    private static void InterlockedMin(ref long target, long value)
    {
        long seen;
        while (value < (seen = Interlocked.Read(ref target)) && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }

    private static (EstimateBucket Bucket, int HardlinkedFiles, Figure Size, Figure Time) Bucket(Group group, List<PageTime> allPages)
    {
        var files = group.Files.Count;
        var bytes = group.Bytes;
        var samples = group.Samples;
        if (group.Skip || samples.Count == 0)
        {
            var time = group.Skip ? Figure.None : Seconds(group, 0, allPages);
            var whole = new Figure(bytes, 0);
            return (Make(group.Skip, 0, whole, 0, time), 0, group.Skip ? Figure.None : whole, time);
        }

        var sampledBytes = samples.Sum(s => s.Size);
        var hardShare = sampledBytes == 0 ? 0 : (double)samples.Where(s => s.Hardlinked).Sum(s => s.Size) / sampledBytes;
        var hardFiles = (int)Math.Round(files * (double)samples.Count(s => s.Hardlinked) / samples.Count);
        var pages = samples.SelectMany(s => s.Pages).ToList();

        var ratio = MeanOf([.. samples.Select(s => (s.Ratio, (double)Math.Max(1, s.Size)))], files);
        var expected = bytes * ratio.Value;
        var size = new Figure(expected, Math.Max(Z90 * ratio.Half * bytes, MinSizeSpread * expected));
        var seconds = Seconds(group, hardShare, pages.Count > 0 ? pages : allPages);
        return (Make(false, samples.Count, size, (long)Math.Round(bytes * hardShare), seconds), hardFiles, size, seconds);

        EstimateBucket Make(bool skip, int sampled, Figure size, long hardlinked, Figure time) => new(
            skip, files, bytes, sampled,
            (long)Math.Round(Math.Max(0, size.Value - size.Half)),
            (long)Math.Round(Math.Min(bytes, size.Value + size.Half)),
            hardlinked,
            Math.Max(0, time.Value - time.Half), time.Value + time.Half,
            (long)Math.Round(size.Value), time.Value);
    }

    /// <summary>
    /// The weighted mean of the samples and its standard error (as <see cref="Figure.Half"/>, not yet scaled
    /// by z), with the finite population correction when <paramref name="population"/> items exist in all:
    /// a bucket whose every file was sampled has no sampling error left.
    /// </summary>
    private static Figure MeanOf(IReadOnlyList<(double Value, double Weight)> samples, int population)
    {
        var weight = samples.Sum(s => s.Weight);
        if (weight <= 0)
        {
            return Figure.None;
        }

        var mean = samples.Sum(s => s.Value * s.Weight) / weight;
        var n = samples.Count;
        if (n < 2)
        {
            return new Figure(mean, 0);
        }

        var se = Math.Sqrt(n / (n - 1.0) * samples.Sum(s => s.Weight * s.Weight * (s.Value - mean) * (s.Value - mean))) / weight;
        var correction = population > 0 ? Math.Sqrt(Math.Max(0, 1 - (double)n / population)) : 1;
        return new Figure(mean, se * correction);
    }

    /// <summary>
    /// Measured files count their pages times the median page; unmeasured ones are extrapolated by
    /// bytes. Hardlinked files are never encoded, so their share is left out. Each sample is lane time,
    /// and the pass encodes <see cref="PageTime.InFlight"/> pages like it at once. The rate is the sampled
    /// pages' megapixel-weighted mean, with its sampling error.
    /// </summary>
    private static Figure Seconds(Group group, double hardShare, List<PageTime> pages)
    {
        if (pages.Count == 0 || group.Files.Count == 0)
        {
            return Figure.None;
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

        var rate = MeanOf([.. pages.Where(x => x.Megapixels > 0).Select(x => (x.Ms / x.Megapixels / x.InFlight, x.Megapixels))], 0);
        var scale = megapixels * (1 - hardShare) / 1000;
        var seconds = scale * rate.Value;
        return new Figure(seconds, Math.Max(Z90 * rate.Half * scale, MinTimeSpread * seconds));
    }
}
