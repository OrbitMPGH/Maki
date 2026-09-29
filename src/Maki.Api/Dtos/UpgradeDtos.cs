using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Parsing;
using Maki.Core.Quality;

namespace Maki.Api.Dtos;

/// <param name="Tier">Lowercase <see cref="QualityTier"/> name.</param>
public record ProfileTierDto(string Tier, bool Allowed);

public record FormatScoreDto(int FormatId, int Score);

/// <param name="Tiers">Highest priority first.</param>
/// <param name="SeriesCount">Series pinned to this profile directly; ones that only inherit it as the default are not counted.</param>
public record UpgradeProfileDto(
    int Id,
    string Name,
    string? Description,
    IReadOnlyList<ProfileTierDto> Tiers,
    string Cutoff,
    bool UpgradesEnabled,
    int MinScoreDelta,
    int UpgradeUntilScore,
    IReadOnlyList<FormatScoreDto> FormatScores,
    int ResolutionWeight,
    int CompressionWeight,
    int PageTolerancePercent,
    bool AllowReplacingUnknown,
    int Version,
    int SeriesCount)
{
    public static UpgradeProfileDto From(UpgradeProfile p, int seriesCount) => new(
        p.Id,
        p.Name,
        p.Description,
        [.. p.Tiers.Select(t => new ProfileTierDto(QualityNames.Tier(t.Tier), t.Allowed))],
        QualityNames.Tier(p.Cutoff),
        p.UpgradesEnabled,
        p.MinScoreDelta,
        p.UpgradeUntilScore,
        [.. p.FormatScores.Select(s => new FormatScoreDto(s.FormatId, s.Score))],
        p.ResolutionWeight,
        p.CompressionWeight,
        p.PageTolerancePercent,
        p.AllowReplacingUnknown,
        p.Version,
        seriesCount);
}

public record UpgradeProfileWriteDto(
    string? Name,
    List<ProfileTierDto>? Tiers,
    string? Cutoff,
    bool UpgradesEnabled,
    int MinScoreDelta,
    int UpgradeUntilScore,
    List<FormatScoreDto>? FormatScores,
    int PageTolerancePercent,
    bool AllowReplacingUnknown,
    int ResolutionWeight = 0,
    int CompressionWeight = 0,
    string? Description = null);

/// <param name="Type">camelCase <see cref="FormatConditionType"/> name, e.g. <c>minWidth</c>.</param>
public record FormatConditionDto(string Type, string Value, bool Required, bool Negate);

/// <param name="ProfileCount">Profiles that give this format a score.</param>
public record QualityFormatDto(
    int Id, string Name, IReadOnlyList<FormatConditionDto> Conditions, int Version, int ProfileCount)
{
    public static QualityFormatDto From(QualityFormat f, int profileCount) => new(
        f.Id,
        f.Name,
        [.. f.Conditions.Select(c => new FormatConditionDto(QualityNames.ConditionType(c.Type), c.Value, c.Required, c.Negate))],
        f.Version,
        profileCount);
}

public record QualityFormatWriteDto(string? Name, List<FormatConditionDto>? Conditions);

/// <param name="ChapterNumber">Null for a chapter with no number, such as an unnumbered one-shot.</param>
public record CutoffUnmetRowDto(
    int SeriesId,
    string SeriesTitle,
    int ChapterId,
    decimal? ChapterNumber,
    string? ChapterTitle,
    int FileId,
    string FileName,
    ChapterFileQualityDto Quality,
    int ProfileId,
    string ProfileName,
    string Cutoff);

public record CutoffUnmetPageDto(IReadOnlyList<CutoffUnmetRowDto> Rows, int Total, int Page, int PageSize);

/// <param name="ProfilesConfigured">
/// True when at least one series the caller can see resolves to a profile, its own or the default.
/// </param>
/// <param name="LastScanDate">Local date (yyyy-MM-dd) of the last daily scan, or null.</param>
public record UpgradeSummaryDto(
    bool ProfilesConfigured,
    long TrashBytes = 0,
    int TrashFiles = 0,
    string? LastScanDate = null,
    bool ScanRunning = false,
    int PendingProposals = 0,
    string? LastVolumeSearchDate = null);

/// <param name="Tier">Lowercase <see cref="QualityTier"/> name.</param>
public record QualitySnapshotDto(
    string Tier,
    string? SourceName,
    string? Group,
    int? PageCount,
    int? MedianWidth,
    int? MedianHeight,
    string? ImageFormat,
    long? SizeBytes,
    int Score)
{
    public static QualitySnapshotDto From(QualitySnapshot s) => new(
        QualityNames.TryParseTier(s.Tier, out var tier) ? QualityNames.Tier(tier) : "unknown",
        s.SourceName, s.Group, s.PageCount, s.MedianWidth, s.MedianHeight, s.ImageFormat, s.SizeBytes, s.Score);
}

/// <summary>How a series orders its sources for downloads, and each source's measured record.</summary>
/// <param name="SeriesMode">"manual", "quality", or null to follow <paramref name="DefaultMode"/>.</param>
/// <param name="Order">Mapping ids in the order a download tries them; disabled mappings last.</param>
/// <param name="Scout">The latest source measurement run for the series since startup, or null.</param>
/// <param name="QualityOrder">Enabled mapping ids as best quality first would order them, whatever the mode.</param>
/// <param name="Tiers">Per mapping id, the tier its copies count as. Best quality first compares it before the score.</param>
public record SourceOrderDto(
    string? SeriesMode, string DefaultMode, string Mode, IReadOnlyList<int> Order, IReadOnlyList<SourceQualityDto> Sources,
    ScoutSnapshot? Scout, IReadOnlyList<int> QualityOrder, IReadOnlyDictionary<int, string> Tiers);

/// <summary>One source mapping's measured track record for a series.</summary>
/// <param name="BitsPerPixel">JPG-equivalent median, see <see cref="MeasuredQuality"/>.</param>
/// <param name="Reliable">Enough recent samples that the upgrade scan trusts them instead of probing.</param>
/// <param name="ResolutionPoints">Under the series' upgrade profile; null when it has none.</param>
public record SourceQualityDto(
    int MappingId,
    int Samples,
    int MedianWidth,
    double BitsPerPixel,
    string? ImageFormat,
    DateTime LatestUtc,
    bool Reliable,
    int? ResolutionPoints,
    int? CompressionPoints)
{
    public static SourceQualityDto From(int mappingId, SourceQualityEstimate estimate, UpgradeProfile? profile, DateTime nowUtc)
    {
        var candidate = estimate.Apply(
            new QualityCandidate(QualityTier.Unknown, null, null, null, null, null, null, null, null, null));
        var points = profile is null ? ((int, int)?)null : MeasuredQuality.Points(profile, candidate);
        return new SourceQualityDto(mappingId, estimate.Samples, estimate.MedianWidth,
            Math.Round(MeasuredQuality.BitsPerPixel(candidate) ?? 0, 2), estimate.ImageFormat, estimate.LatestUtc,
            estimate.IsReliable(nowUtc), points?.Item1, points?.Item2);
    }
}

/// <summary>Where an applied upgrade's history row stands, for the queue's Revert button.</summary>
public record UpgradeHistoryState(bool Reverted, bool TrashAvailable);

/// <param name="Outcome">pending, applied or rejected; a torrent row may also read parked.</param>
/// <param name="Reason">A reason code when rejected, worded by the client.</param>
/// <param name="Before">Null for a torrent row, which replaces several files at once.</param>
/// <param name="Reverted">The applied upgrade has since been reverted.</param>
/// <param name="TrashAvailable">The replaced copy is still in the trash, so a revert can happen.</param>
/// <param name="Force">A user picked this copy to replace the file, rather than the upgrader.</param>
/// <param name="HistoryGroupId">A torrent row's history group once applied.</param>
/// <param name="ReplacedFiles">How many library files a torrent row replaces; 0 for a scraper row.</param>
public record UpgradeQueueInfoDto(
    string Outcome,
    string? Reason,
    QualitySnapshotDto? Before,
    QualitySnapshotDto? Predicted,
    QualitySnapshotDto? After,
    int? HistoryId,
    bool Reverted,
    bool TrashAvailable,
    bool Force,
    string? HistoryGroupId = null,
    int ReplacedFiles = 0)
{
    public static UpgradeQueueInfoDto? From(string? json, UpgradeHistoryState? history = null)
    {
        if (TorrentUpgradeInfo.Parse(json) is { } torrent)
        {
            return new UpgradeQueueInfoDto(
                torrent.Outcome,
                null,
                null,
                null,
                null,
                null,
                history?.Reverted ?? false,
                history?.TrashAvailable ?? false,
                false,
                torrent.HistoryGroupId?.ToString(),
                torrent.ReplacedFileIds.Count);
        }

        return UpgradeInfo.Parse(json) is { } info
            ? new UpgradeQueueInfoDto(
                info.Outcome,
                info.Reason,
                QualitySnapshotDto.From(info.Before),
                QualitySnapshotDto.From(info.Predicted),
                info.After is null ? null : QualitySnapshotDto.From(info.After),
                info.HistoryId,
                history?.Reverted ?? false,
                history?.TrashAvailable ?? false,
                info.Force)
            : null;
    }
}

/// <param name="FileName">The file as it is now; for a grouped row, the volume file that replaced it.</param>
/// <param name="TrashAvailable">The replaced copy is still on disk, so the upgrade can be reverted.</param>
/// <param name="GroupId">Shared by the rows of one torrent replacement, which revert together.</param>
/// <param name="GroupSize">Rows in that group; 1 for an ungrouped row.</param>
public record UpgradeHistoryRowDto(
    int Id,
    int SeriesId,
    string SeriesTitle,
    int ChapterId,
    decimal? ChapterNumber,
    string? ChapterTitle,
    int FileId,
    string FileName,
    QualitySnapshotDto Before,
    QualitySnapshotDto After,
    string ProfileName,
    long TrashBytes,
    bool TrashAvailable,
    DateTime CreatedAt,
    DateTime? RevertedAt,
    string? GroupId = null,
    int GroupSize = 1);

public record UpgradeHistoryPageDto(IReadOnlyList<UpgradeHistoryRowDto> Rows, int Total, int Page, int PageSize);

public record UpgradeCandidateOutcomeDto(
    int MappingId, string SourceName, string SourceChapterId, string Reason, bool Probed, int? PageCount,
    int? MedianWidth, int? Score);

/// <param name="Skipped">Chapters and candidates passed over, keyed by reason code.</param>
/// <param name="Candidates">Every candidate's outcome; empty for series and library scans.</param>
public record UpgradeScanResultDto(
    int SeriesScanned, int ChaptersChecked, int CandidatesProbed, int Enqueued, IReadOnlyDictionary<string, int> Skipped,
    IReadOnlyList<UpgradeCandidateOutcomeDto> Candidates, int? QueuedFromMappingId)
{
    public static UpgradeScanResultDto From(Services.UpgradeScanResult r) => new(
        r.SeriesScanned, r.ChaptersChecked, r.CandidatesProbed, r.Enqueued, r.Skipped,
        [.. r.Candidates.Select(c => new UpgradeCandidateOutcomeDto(c.MappingId, c.SourceName, c.SourceChapterId,
            c.Reason, c.Probed, c.PageCount, c.MedianWidth, c.Score))],
        r.QueuedFromMappingId);
}

/// <param name="SeriesId">Scan one series. Leave both ids out to scan the library.</param>
/// <param name="ChapterId">Scan one chapter and report every candidate.</param>
public record UpgradeScanRequest(int? SeriesId, int? ChapterId = null);

public record NumberRangeDto(decimal Start, decimal End);

/// <param name="WholeSeries">A digital pack naming no volumes or chapters, taken to cover the whole series.</param>
public record ReleaseSpanDto(decimal? VolumeStart, decimal? VolumeEnd, IReadOnlyList<NumberRangeDto> Chapters, bool WholeSeries)
{
    public static ReleaseSpanDto From(ReleaseSpan span, bool wholeSeries = false) => new(
        span.Volumes?.Start, span.Volumes?.End,
        [.. span.ChapterSegments.Select(s => new NumberRangeDto(s.Start, s.End))], wholeSeries);
}

/// <param name="Tier">Lowercase <see cref="QualityTier"/> name.</param>
/// <param name="Status">pending, accepted, dismissed or expired.</param>
public record TorrentProposalDto(
    int Id, int SeriesId, string SeriesTitle, string Title, string Indexer, long SizeBytes, ReleaseSpanDto Span,
    IReadOnlyList<string> Reasons, int UpgradeCount, int AlreadyMetCount, int SkippedCount, int MissingCount,
    int UnknownCount, int Score, string Tier, string Status, DateTime CreatedAtUtc, DateTime? ResolvedAtUtc,
    int? QueueItemId);

/// <param name="Verdict">ignore, proposal or autoGrab.</param>
/// <param name="Tier">Lowercase <see cref="QualityTier"/> name.</param>
public record ReleaseParsedDto(
    ReleaseSpanDto Span, string Tier, int Score, string Verdict, IReadOnlyList<string> Reasons, int UpgradeCount,
    int AlreadyMetCount, int MissingCount, bool TitleMatched);

/// <param name="Reason">A <c>not_eligible_*</c> code, <c>search_failed</c> or <c>grab_failed</c>; null when it searched.</param>
public record SeriesVolumeSearchResultDto(bool Searched, int ResultCount, int? Grabbed, int? ProposalId, string? Reason);

/// <param name="SeriesId">Search one series now. Leave it out to run the volume search job.</param>
public record VolumeSearchRequest(int? SeriesId);

public record SetTrustedRequest(bool Trusted);

public record TrustedDto(bool Trusted);

/// <summary>Wire spellings for the quality enums: tiers lowercase, condition types camelCase.</summary>
public static class QualityNames
{
    public static string Tier(QualityTier tier) => tier.ToString().ToLowerInvariant();

    public static bool TryParseTier(string? value, out QualityTier tier) =>
        Enum.TryParse(value, ignoreCase: true, out tier) && Enum.IsDefined(tier) && !int.TryParse(value, out _);

    public static string ConditionType(FormatConditionType type)
    {
        var name = type.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    public static bool TryParseConditionType(string? value, out FormatConditionType type) =>
        Enum.TryParse(value, ignoreCase: true, out type) && Enum.IsDefined(type) && !int.TryParse(value, out _);
}
