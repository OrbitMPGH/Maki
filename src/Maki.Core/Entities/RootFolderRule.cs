namespace Maki.Core.Entities;

/// <summary>
/// Sends a series added without a chosen root folder to <see cref="RootFolderId"/> when it matches
/// <see cref="ConditionsJson"/> (<c>Maki.Core.Tags.ConditionGroups</c>). Rules are tried in
/// <see cref="Position"/> order and the first match wins; no match falls back to the first root
/// folder. Deleted with its root folder.
/// </summary>
public class RootFolderRule
{
    public int Id { get; set; }
    public int RootFolderId { get; set; }
    public RootFolder? RootFolder { get; set; }
    public int Position { get; set; }
    public bool Enabled { get; set; } = true;
    public string ConditionsJson { get; set; } = "[]";
    public DateTime CreatedAt { get; set; }
}
