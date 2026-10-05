using Maki.Core.Quality;

namespace Maki.Core.Entities;

/// <summary>
/// A named set of conditions a chapter file either matches or not. Carries no score of its own:
/// each <see cref="UpgradeProfile"/> weighs it through <see cref="UpgradeProfile.FormatScores"/>.
/// </summary>
public class QualityFormat
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<FormatCondition> Conditions { get; set; } = [];
    public int Version { get; set; } = 1;
}

public record FormatCondition(FormatConditionType Type, string Value, bool Required, bool Negate);
