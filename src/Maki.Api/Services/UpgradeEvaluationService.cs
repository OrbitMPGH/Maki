using System.Globalization;
using Maki.Api.Dtos;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Quality;
using Maki.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Maki.Api.Services;

/// <summary>Scores chapter files under one resolved <see cref="UpgradeProfile"/>.</summary>
public sealed class UpgradeEvaluator
{
    private readonly ChapterFileQualityService _quality;
    private readonly IReadOnlyList<QualityFormat> _formats;
    private readonly RegexCache _regexes;

    /// <param name="formats">Any formats; only the ones the profile scores are kept, since the rest cannot change a score.</param>
    /// <param name="regexes">Share one across every evaluator of a pass; null starts a fresh one.</param>
    public UpgradeEvaluator(
        UpgradeProfile profile, IEnumerable<QualityFormat> formats, ChapterFileQualityService quality,
        RegexCache? regexes = null)
    {
        Profile = profile;
        _quality = quality;
        var scored = profile.FormatScores.Select(s => s.FormatId).ToHashSet();
        _formats = [.. formats.Where(f => scored.Contains(f.Id))];
        _regexes = regexes ?? new RegexCache();
    }

    public UpgradeProfile Profile { get; }

    /// <summary>Null for a file that has never been measured, since its score would be a guess.</summary>
    public (QualityScore Score, bool CutoffMet)? Evaluate(ChapterFile file, string? language)
    {
        if (file.MeasuredAtUtc is null)
        {
            return null;
        }

        var score = QualityScorer.Score(Profile, _formats, Candidate(file, language), _regexes);
        return (score, QualityScorer.CutoffMet(Profile, score.Tier, score.Score));
    }

    public ChapterFileQualityDto Quality(ChapterFile file, string? language) =>
        Evaluate(file, language) is { } result
            ? ChapterFileQualityDto.From(file, result.Score.Score, result.CutoffMet)
            : ChapterFileQualityDto.From(file);

    public QualityScore Score(QualityCandidate candidate) =>
        QualityScorer.Score(Profile, _formats, candidate, _regexes);

    public QualityScore OptimisticScore(QualityCandidate listing) =>
        QualityScorer.OptimisticScore(Profile, _formats, listing, _regexes);

    public bool CouldUpgrade(QualityScore current, int? currentPageCount, bool trusted, QualityCandidate listing) =>
        QualityScorer.CouldUpgrade(Profile, _formats, current, currentPageCount, trusted, listing, _regexes);

    public QualityCandidate CandidateFor(ChapterFile file, string? language) => Candidate(file, language);

    /// <summary>
    /// A copy that is not a library file yet: listing data, a probe or a freshly packaged archive.
    /// Unknown measurements stay null. <paramref name="fileName"/> is the name it would be stored under,
    /// which is the current file's name, so it only feeds the tier and never stands in for a release name.
    /// </summary>
    public QualityCandidate CandidateFor(
        string sourceName, string? group, string fileName, int? pageCount, int? medianWidth, string? imageFormat,
        long? sizeBytes, string? language, int? medianHeight = null)
    {
        var kind = _quality.KindOf(sourceName);
        return new QualityCandidate(
            QualityTierResolver.Resolve(kind, null, fileName, isVolume: false),
            sourceName, kind, group, null, pageCount, medianWidth, imageFormat, sizeBytes, language, medianHeight);
    }

    public static QualitySnapshot Snapshot(ChapterFile file, int score) => new()
    {
        Tier = QualitySnapshot.TierName(file.Tier),
        SourceName = file.SourceName,
        SourceChapterId = file.SourceChapterId,
        Group = file.Group,
        PageCount = file.PageCount,
        MedianWidth = file.MedianWidth,
        MedianHeight = file.MedianHeight,
        ImageFormat = file.ImageFormat,
        SizeBytes = file.Size,
        Score = score,
        ReleaseName = file.ReleaseName,
        ReleaseHash = file.ReleaseHash
    };

    private QualityCandidate Candidate(ChapterFile file, string? language) => new(
        file.Tier,
        file.SourceName,
        _quality.KindOf(file.SourceName),
        file.Group,
        file.ReleaseName ?? Path.GetFileName(file.RelativePath),
        file.PageCount,
        file.MedianWidth,
        file.ImageFormat,
        file.Size,
        language,
        file.MedianHeight);
}

/// <summary>
/// Resolves which upgrade profile applies to a series (its own pin, else the instance default) and
/// scores its files. Read only: nothing here writes to the database or touches a file.
/// </summary>
public class UpgradeEvaluationService(
    MakiDbContext db, ChapterFileQualityService quality, IMemoryCache? cache = null)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    /// <summary>
    /// How long an evaluated cutoff-unmet list is reused across page requests. Profile and format
    /// edits, measurements, pins, trust changes and visibility are all in the key; a rename waits
    /// this out.
    /// </summary>
    private static readonly TimeSpan CutoffCacheFor = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The evaluator for one series, or null when it resolves to no profile. One query reads the pin,
    /// the default id and the formats together; a second runs only when the series falls back to the
    /// default.
    /// </summary>
    public async Task<UpgradeEvaluator?> ForSeriesAsync(int seriesId, CancellationToken ct)
    {
        var row = await db.Series.AsNoTracking()
            .Where(s => s.Id == seriesId)
            .Select(s => new
            {
                s.UpgradeProfile,
                Default = db.AppConfig
                    .Where(c => c.Key == SettingKeys.UpgradesDefaultProfileId)
                    .Select(c => c.Value)
                    .FirstOrDefault(),
                Formats = db.QualityFormats.ToList()
            })
            .FirstOrDefaultAsync(ct);
        if (row is null)
        {
            return null;
        }

        var profile = row.UpgradeProfile;
        if (profile is null && ParseId(row.Default) is { } defaultId)
        {
            profile = await db.UpgradeProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == defaultId, ct);
        }

        if (profile is null)
        {
            return null;
        }

        return new UpgradeEvaluator(profile, row.Formats, quality);
    }

    public async Task<CutoffUnmetPageDto> CutoffUnmetAsync(int? seriesId, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var rows = await EvaluateAsync(seriesId, ct);
        var ordered = rows
            .OrderBy(r => r.SortTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.SeriesTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.ChapterNumber is null)
            .ThenBy(r => r.ChapterNumber)
            .ThenBy(r => r.Dto.FileId)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => r.Dto)
            .ToList();
        return new CutoffUnmetPageDto(ordered, rows.Count, page, pageSize);
    }

    /// <summary>Only whether profiles apply anywhere; the unmet count is the cutoff-unmet page's total.</summary>
    public async Task<UpgradeSummaryDto> SummaryAsync(CancellationToken ct) =>
        new((await ResolutionAsync(ct)).Configured);

    private sealed record Unmet(string SortTitle, string SeriesTitle, decimal? ChapterNumber, CutoffUnmetRowDto Dto);

    private async Task<(Dictionary<int, UpgradeProfile> Profiles, int? DefaultId, bool Configured)> ResolutionAsync(
        CancellationToken ct)
    {
        var profiles = await db.UpgradeProfiles.AsNoTracking().ToDictionaryAsync(p => p.Id, ct);
        if (profiles.Count == 0)
        {
            return (profiles, null, false);
        }

        var defaultId = ParseId(await db.AppConfig
            .Where(c => c.Key == SettingKeys.UpgradesDefaultProfileId)
            .Select(c => c.Value)
            .FirstOrDefaultAsync(ct));
        if (defaultId is { } id && !profiles.ContainsKey(id))
        {
            defaultId = null;
        }

        var configured = defaultId is not null
            ? await db.Series.AnyAsync(ct)
            : await db.Series.AnyAsync(s => s.UpgradeProfileId != null, ct);
        return (profiles, defaultId, configured);
    }

    /// <summary>
    /// One row per measured file whose resolved profile says its cutoff is unmet, labelled with the
    /// lowest-numbered chapter it backs. Visibility comes from the query filters on Chapter, Series and
    /// ChapterFile, so a caller never sees a file in a root folder they have no grant for.
    /// </summary>
    private async Task<List<Unmet>> EvaluateAsync(int? seriesId, CancellationToken ct)
    {
        var (profiles, defaultId, configured) = await ResolutionAsync(ct);
        if (!configured)
        {
            return [];
        }

        var formats = await db.QualityFormats.AsNoTracking().ToListAsync(ct);

        var query = db.Chapters.AsNoTracking()
            .Where(c => c.ChapterFileId != null && c.ChapterFile!.MeasuredAtUtc != null && !c.ChapterFile.Trusted);
        if (seriesId is { } onlySeries)
        {
            query = query.Where(c => c.SeriesId == onlySeries);
        }

        if (defaultId is null)
        {
            query = query.Where(c => c.Series!.UpgradeProfileId != null);
        }

        if (cache is null)
        {
            return await EvaluateRowsAsync(query, profiles, defaultId, formats, ct);
        }

        // An aggregate over the same rows, run under the same filters: far cheaper than loading and
        // scoring them, and it moves when a file is measured, trusted, removed, re-pinned or hidden.
        var stamp = await query
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Count = g.Count(),
                Measured = g.Max(c => c.ChapterFile!.MeasuredAtUtc),
                Pins = g.Sum(c => (long)(c.Series!.UpgradeProfileId ?? 0)),
            })
            .FirstOrDefaultAsync(ct);
        var key = (Kind: "cutoff-unmet", db.Scope.UserId, db.Scope.Unrestricted, db.Scope.AllRootFolders, seriesId,
            defaultId,
            Profiles: string.Join(',', profiles.Values.OrderBy(p => p.Id).Select(p => $"{p.Id}:{p.Version}")),
            Formats: string.Join(',', formats.OrderBy(f => f.Id).Select(f => $"{f.Id}:{f.Version}")),
            stamp?.Count, stamp?.Measured, stamp?.Pins);
        if (cache.TryGetValue(key, out List<Unmet>? hit) && hit is not null)
        {
            return hit;
        }

        var rows = await EvaluateRowsAsync(query, profiles, defaultId, formats, ct);
        cache.Set(key, rows, CutoffCacheFor);
        return rows;
    }

    private async Task<List<Unmet>> EvaluateRowsAsync(IQueryable<Chapter> query,
        Dictionary<int, UpgradeProfile> profiles, int? defaultId, List<QualityFormat> formats, CancellationToken ct)
    {
        var regexes = new RegexCache();
        var evaluators = profiles.Values.ToDictionary(p => p.Id, p => new UpgradeEvaluator(p, formats, quality, regexes));

        var chapters = await query
            .Select(c => new
            {
                ChapterId = c.Id,
                c.Number,
                c.Title,
                c.Language,
                c.SeriesId,
                SeriesTitle = c.Series!.Title,
                c.Series.SortTitle,
                c.Series.UpgradeProfileId,
                File = new ChapterFile
                {
                    Id = c.ChapterFile!.Id,
                    SeriesId = c.ChapterFile.SeriesId,
                    RelativePath = c.ChapterFile.RelativePath,
                    Size = c.ChapterFile.Size,
                    SourceName = c.ChapterFile.SourceName,
                    ReleaseName = c.ChapterFile.ReleaseName,
                    Tier = c.ChapterFile.Tier,
                    Group = c.ChapterFile.Group,
                    PageCount = c.ChapterFile.PageCount,
                    MedianWidth = c.ChapterFile.MedianWidth,
                    MedianHeight = c.ChapterFile.MedianHeight,
                    ImageFormat = c.ChapterFile.ImageFormat,
                    MeasuredAtUtc = c.ChapterFile.MeasuredAtUtc,
                    Trusted = c.ChapterFile.Trusted
                }
            })
            .ToListAsync(ct);

        var rows = new List<Unmet>();
        foreach (var group in chapters.GroupBy(c => c.File.Id))
        {
            var first = group.OrderBy(c => c.Number is null).ThenBy(c => c.Number).ThenBy(c => c.ChapterId).First();
            var profileId = first.UpgradeProfileId is { } own && evaluators.ContainsKey(own) ? own : defaultId;
            if (profileId is null)
            {
                continue;
            }

            var evaluator = evaluators[profileId.Value];
            if (evaluator.Evaluate(first.File, first.Language) is not { CutoffMet: false } result)
            {
                continue;
            }

            rows.Add(new Unmet(first.SortTitle, first.SeriesTitle, first.Number, new CutoffUnmetRowDto(
                first.SeriesId,
                first.SeriesTitle,
                first.ChapterId,
                first.Number,
                first.Title,
                first.File.Id,
                Path.GetFileName(first.File.RelativePath),
                ChapterFileQualityDto.From(first.File, result.Score.Score, result.CutoffMet),
                evaluator.Profile.Id,
                evaluator.Profile.Name,
                QualityNames.Tier(evaluator.Profile.Cutoff))));
        }

        return rows;
    }

    public static int? ParseId(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
}
