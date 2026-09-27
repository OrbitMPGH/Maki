using Maki.Core.Entities;
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
    IReadOnlyList<ProfileTierDto> Tiers,
    string Cutoff,
    bool UpgradesEnabled,
    int MinScoreDelta,
    int UpgradeUntilScore,
    IReadOnlyList<FormatScoreDto> FormatScores,
    int PageTolerancePercent,
    bool AllowReplacingUnknown,
    int Version,
    int SeriesCount)
{
    public static UpgradeProfileDto From(UpgradeProfile p, int seriesCount) => new(
        p.Id,
        p.Name,
        [.. p.Tiers.Select(t => new ProfileTierDto(QualityNames.Tier(t.Tier), t.Allowed))],
        QualityNames.Tier(p.Cutoff),
        p.UpgradesEnabled,
        p.MinScoreDelta,
        p.UpgradeUntilScore,
        [.. p.FormatScores.Select(s => new FormatScoreDto(s.FormatId, s.Score))],
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
    bool AllowReplacingUnknown);

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
    int CutoffUnmet,
    bool ProfilesConfigured,
    long TrashBytes = 0,
    int TrashFiles = 0,
    string? LastScanDate = null,
    bool ScanRunning = false);

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

/// <summary>Where an applied upgrade's history row stands, for the queue's Revert button.</summary>
public record UpgradeHistoryState(bool Reverted, bool TrashAvailable);

/// <param name="Outcome">pending, applied or rejected.</param>
/// <param name="Reason">A reason code when rejected, worded by the client.</param>
/// <param name="Reverted">The applied upgrade has since been reverted.</param>
/// <param name="TrashAvailable">The replaced copy is still in the trash, so a revert can happen.</param>
public record UpgradeQueueInfoDto(
    string Outcome,
    string? Reason,
    QualitySnapshotDto Before,
    QualitySnapshotDto Predicted,
    QualitySnapshotDto? After,
    int? HistoryId,
    bool Reverted,
    bool TrashAvailable)
{
    public static UpgradeQueueInfoDto? From(string? json, UpgradeHistoryState? history = null) =>
        UpgradeInfo.Parse(json) is { } info
            ? new UpgradeQueueInfoDto(
                info.Outcome,
                info.Reason,
                QualitySnapshotDto.From(info.Before),
                QualitySnapshotDto.From(info.Predicted),
                info.After is null ? null : QualitySnapshotDto.From(info.After),
                info.HistoryId,
                history?.Reverted ?? false,
                history?.TrashAvailable ?? false)
            : null;
}

/// <param name="TrashAvailable">The replaced copy is still on disk, so the upgrade can be reverted.</param>
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
    DateTime? RevertedAt);

public record UpgradeHistoryPageDto(IReadOnlyList<UpgradeHistoryRowDto> Rows, int Total, int Page, int PageSize);

/// <param name="Skipped">Chapters and candidates passed over, keyed by reason code.</param>
public record UpgradeScanResultDto(
    int SeriesScanned, int ChaptersChecked, int CandidatesProbed, int Enqueued, IReadOnlyDictionary<string, int> Skipped);

public record UpgradeScanRequest(int? SeriesId);

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
