using Maki.Core.Entities;
using Maki.Core.Images;
using Maki.Core.Paths;
using Maki.Core.Quality;
using Maki.Core.Sources;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Builds the <see cref="PlanCandidate"/> for a file (library pass, download path, wizard) and keeps
/// the files the pass already checked in line with the plan when it changes.
/// </summary>
public class PageConversionPlanService(MakiDbContext db)
{
    public sealed record SeriesFacts(PageConversionOverride Override, string? Type, IReadOnlyList<string> Tags);

    public sealed record FileFacts(
        int Id, int SeriesId, QualityTier Tier, string SourceName, string? ImageFormat, long Size,
        int? PageCount, int? MedianWidth, int? MedianHeight);

    public sealed record ScopeBucket(int Files, long Bytes);

    /// <param name="Rules">One bucket per rule in order: files whose first matching rule it is.</param>
    /// <param name="Overrides">Files the series override decided.</param>
    /// <param name="ConvertedEarlier">Files already converted, in no other bucket.</param>
    public sealed record Scope(
        IReadOnlyList<ScopeBucket> Rules, ScopeBucket Default, ScopeBucket Overrides, ScopeBucket AlreadyCompact,
        ScopeBucket ConvertedEarlier);

    private static readonly SeriesFacts NoSeries = new(PageConversionOverride.Default, null, []);

    /// <summary>Type, override and user tags of each series, or of every series when <paramref name="seriesIds"/> is null.</summary>
    public static async Task<Dictionary<int, SeriesFacts>> SeriesFactsAsync(
        MakiDbContext db, IReadOnlyCollection<int>? seriesIds, CancellationToken ct)
    {
        var series = db.Series.IgnoreQueryFilters().AsNoTracking();
        var tagged = from link in db.SeriesTags.IgnoreQueryFilters()
            join tag in db.Tags.IgnoreQueryFilters() on link.TagId equals tag.Id
            select new { link.SeriesId, tag.Label };
        if (seriesIds is not null)
        {
            series = series.Where(s => seriesIds.Contains(s.Id));
            tagged = tagged.Where(t => seriesIds.Contains(t.SeriesId));
        }

        var tags = (await tagged.ToListAsync(ct)).ToLookup(t => t.SeriesId, t => t.Label);
        var rows = await series.Select(s => new { s.Id, s.PageConversion, s.Type }).ToListAsync(ct);
        return rows.ToDictionary(s => s.Id, s => new SeriesFacts(s.PageConversion, s.Type, tags[s.Id].ToList()));
    }

    public static async Task<SeriesFacts> SeriesFactsAsync(MakiDbContext db, int seriesId, CancellationToken ct) =>
        (await SeriesFactsAsync(db, [seriesId], ct)).GetValueOrDefault(seriesId, NoSeries);

    public static PlanCandidate Candidate(FileFacts file, SeriesFacts series) => new(
        series.Override, series.Type, series.Tags, file.Tier, file.SourceName, file.ImageFormat, file.Size,
        file.PageCount, file.MedianWidth, file.MedianHeight);

    public static PlanCandidate Candidate(ChapterFile file, SeriesFacts series) => Candidate(
        new FileFacts(file.Id, file.SeriesId, file.Tier, file.SourceName, file.ImageFormat, file.Size, file.PageCount,
            file.MedianWidth, file.MedianHeight), series);

    /// <summary>
    /// A download has no row yet: the tier comes from the source's kind, the way quality stamping
    /// derives it, and the format and size from the archive as it was packaged.
    /// </summary>
    public static async Task<PlanCandidate> ForDownloadAsync(MakiDbContext db, int seriesId, SourceKind kind,
        string sourceName, ChapterFileMeasurement measurement, long size, CancellationToken ct)
    {
        var series = await SeriesFactsAsync(db, seriesId, ct);
        var tier = QualityTierResolver.Resolve(kind, null, "", isVolume: false);
        return new PlanCandidate(series.Override, series.Type, series.Tags, tier, sourceName, measurement.ImageFormat, size,
            measurement.PageCount, measurement.MedianWidth, measurement.MedianHeight);
    }

    /// <summary>
    /// Puts back in line every checked, unconverted file the plan now answers differently for, and
    /// every one checked before answers were recorded. Converted files are never touched, nor files
    /// stamped <see cref="PlanAction.HardSkipName"/>, which no plan converts, or
    /// <see cref="PlanAction.UndoneName"/>, which an admin put back by hand; with conversion off
    /// nothing is. Returns how many files were put back.
    /// </summary>
    public Task<int> RecheckAsync(PageConversionPlan? plan, int? seriesId, CancellationToken ct) =>
        RecheckAsync(plan, seriesId is { } only ? db.ChapterFiles.Where(f => f.SeriesId == only) : null, ct);

    /// <summary>The same for one file, which a pass decided by its old plan after a save had rechecked the rest.</summary>
    public Task<int> RecheckFileAsync(PageConversionPlan? plan, int fileId, CancellationToken ct) =>
        RecheckAsync(plan, db.ChapterFiles.Where(f => f.Id == fileId), ct);

    private async Task<int> RecheckAsync(PageConversionPlan? plan, IQueryable<ChapterFile>? scoped, CancellationToken ct)
    {
        if (plan is null)
        {
            return 0;
        }

        var query = (scoped ?? db.ChapterFiles).IgnoreQueryFilters().AsNoTracking()
            .Where(f => f.ConversionCheckedAtUtc != null && f.OriginalImageFormat == null &&
                        f.CheckedWith != PlanAction.HardSkipName && f.CheckedWith != PlanAction.UndoneName);
        var rows = await query
            .Select(f => new
            {
                f.Id, f.SeriesId, f.Tier, f.SourceName, f.ImageFormat, f.Size, f.PageCount, f.MedianWidth,
                f.MedianHeight, f.CheckedWith
            })
            .ToListAsync(ct);
        if (rows.Count == 0)
        {
            return 0;
        }

        var series = await SeriesFactsAsync(db, scoped is null ? null : rows.Select(r => r.SeriesId).Distinct().ToList(), ct);
        var stale = rows
            .Where(r => r.CheckedWith is null || plan.Resolve(Candidate(
                new FileFacts(r.Id, r.SeriesId, r.Tier, r.SourceName, r.ImageFormat, r.Size, r.PageCount, r.MedianWidth,
                    r.MedianHeight),
                series.GetValueOrDefault(r.SeriesId, NoSeries))).Name != r.CheckedWith)
            .Select(r => r.Id)
            .ToList();
        foreach (var chunk in stale.Chunk(500))
        {
            await db.ChapterFiles.IgnoreQueryFilters()
                .Where(f => chunk.Contains(f.Id) && f.ConversionCheckedAtUtc != null && f.OriginalImageFormat == null &&
                            f.CheckedWith != PlanAction.UndoneName)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(f => f.ConversionCheckedAtUtc, (DateTime?)null)
                    .SetProperty(f => f.CheckedWith, (string?)null), ct);
        }

        return stale.Count;
    }

    /// <summary>A library archive as the scope and the estimate read it.</summary>
    /// <param name="PendingImport">The series still waits on an import link, so the pass leaves it alone.</param>
    /// <param name="ConversionCheckedAtUtc">Set once the pass has decided the file; it does not revisit it.</param>
    public sealed record ScopeFile(
        int Id, int SeriesId, string RelativePath, string Root, QualityTier Tier, string SourceName, string? ImageFormat,
        long Size, int? PageCount, int? MedianWidth, int? MedianHeight, string? OriginalImageFormat, string? CheckedWith,
        long ConversionSavedBytes, bool PendingImport, DateTime? ConversionCheckedAtUtc)
    {
        public FileFacts Facts => new(Id, SeriesId, Tier, SourceName, ImageFormat, Size, PageCount, MedianWidth, MedianHeight);
    }

    public enum Place
    {
        ConvertedEarlier,
        Override,
        AlreadyCompact,
        Rule,
        Default
    }

    /// <param name="Rule">Index into the plan's rules in order when <paramref name="Place"/> is <see cref="Place.Rule"/>, else -1.</param>
    public sealed record Placement(Place Place, int Rule, PlanAction? Action);

    /// <summary>Every cbz or zip on a root folder it resolves under, in one query.</summary>
    public async Task<List<ScopeFile>> ScopeFilesAsync(CancellationToken ct)
    {
        var rows = await db.ChapterFiles.IgnoreQueryFilters().AsNoTracking()
            .Where(f => f.RelativePath.ToLower().EndsWith(".cbz") || f.RelativePath.ToLower().EndsWith(".zip"))
            .Select(f => new
            {
                f.Id, f.SeriesId, f.RelativePath, f.Tier, f.SourceName, f.ImageFormat, f.Size, f.PageCount,
                f.MedianWidth, f.MedianHeight, f.OriginalImageFormat, f.CheckedWith, f.ConversionSavedBytes,
                f.ConversionCheckedAtUtc,
                PendingImport = db.Series.IgnoreQueryFilters().Any(s =>
                    s.Id == f.SeriesId && s.PendingImportLink != PendingImportLink.None),
                Root = db.Series.IgnoreQueryFilters().Where(s => s.Id == f.SeriesId).Select(s => s.RootFolder!.Path).FirstOrDefault()
            })
            .ToListAsync(ct);
        return rows
            .Where(r => r.Root is not null && LibraryPaths.Resolve(r.Root, r.RelativePath) is not null)
            .Select(r => new ScopeFile(r.Id, r.SeriesId, r.RelativePath, r.Root!, r.Tier, r.SourceName, r.ImageFormat, r.Size,
                r.PageCount, r.MedianWidth, r.MedianHeight, r.OriginalImageFormat, r.CheckedWith, r.ConversionSavedBytes,
                r.PendingImport, r.ConversionCheckedAtUtc))
            .ToList();
    }

    /// <summary>Which bucket of the wizard a file lands in under <paramref name="plan"/>; <paramref name="rules"/> is the plan's rules in order.</summary>
    public static Placement Classify(PageConversionPlan plan, List<PlanRule> rules, ScopeFile file, SeriesFacts? series)
    {
        if (file.OriginalImageFormat is not null)
        {
            return new Placement(Place.ConvertedEarlier, -1, null);
        }

        var candidate = Candidate(file.Facts, series ?? NoSeries);
        var action = plan.Resolve(candidate);
        return action switch
        {
            PlanAction.Skip { Reason: "override" } or PlanAction.Convert { Source: "override" } =>
                new Placement(Place.Override, -1, action),
            PlanAction.Skip { Reason: "alreadycompact" } => new Placement(Place.AlreadyCompact, -1, action),
            PlanAction.Skip { Reason: "rule" } or PlanAction.Convert { Source: "rule" } =>
                new Placement(Place.Rule, rules.IndexOf(plan.MatchingRule(candidate)!), action),
            _ => new Placement(Place.Default, -1, action)
        };
    }

    /// <summary>Where the library's archives would land under <paramref name="plan"/>, counted in memory over one query.</summary>
    public async Task<Scope> ScopeAsync(PageConversionPlan plan, CancellationToken ct)
    {
        var rows = await ScopeFilesAsync(ct);
        var series = await SeriesFactsAsync(db, null, ct);
        var rules = plan.Rules.OrderBy(r => r.Order).ToList();
        var perRule = new (int Files, long Bytes)[rules.Count];
        (int Files, long Bytes) fallthrough = default, overrides = default, compact = default, converted = default;

        foreach (var row in rows.Where(r => !r.PendingImport))
        {
            var placement = Classify(plan, rules, row, series.GetValueOrDefault(row.SeriesId));
            switch (placement.Place)
            {
                case Place.ConvertedEarlier:
                    Add(ref converted, row.Size);
                    break;
                case Place.Override:
                    Add(ref overrides, row.Size);
                    break;
                case Place.AlreadyCompact:
                    Add(ref compact, row.Size);
                    break;
                case Place.Rule:
                    Add(ref perRule[placement.Rule], row.Size);
                    break;
                default:
                    Add(ref fallthrough, row.Size);
                    break;
            }
        }

        return new Scope([.. perRule.Select(Bucket)], Bucket(fallthrough), Bucket(overrides), Bucket(compact), Bucket(converted));

        static void Add(ref (int Files, long Bytes) bucket, long bytes) => bucket = (bucket.Files + 1, bucket.Bytes + bytes);
        static ScopeBucket Bucket((int Files, long Bytes) bucket) => new(bucket.Files, bucket.Bytes);
    }
}
