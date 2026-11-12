namespace Maki.Core.Entities;

/// <summary>
/// Puts <see cref="TagId"/> on a series when it is added and matches every condition in
/// <see cref="ConditionsJson"/> (a <c>Maki.Core.Tags.AutoTagCondition</c> list). Rules are independent
/// of each other. Existing series are only tagged by an explicit "apply to existing" action.
/// </summary>
public class AutoTagRule
{
    public int Id { get; set; }
    public int TagId { get; set; }
    public Tag? Tag { get; set; }
    public bool Enabled { get; set; } = true;
    public string ConditionsJson { get; set; } = "[]";
    public DateTime CreatedAt { get; set; }
}
