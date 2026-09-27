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
public record UpgradeSummaryDto(int CutoffUnmet, bool ProfilesConfigured);

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
