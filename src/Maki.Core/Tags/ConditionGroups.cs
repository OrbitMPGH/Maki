using System.Text.Json;

namespace Maki.Core.Tags;

/// <summary>
/// Rule conditions as alternatives: a rule matches when any group matches, and a group matches
/// when every condition in it holds. Shared by auto-tag rules and root folder rules, which store it
/// in a <c>ConditionsJson</c> column.
/// <para>
/// One group is stored as the flat condition list auto-tag rules always used, so a rule saved before
/// groups existed reads back unchanged and a one-group rule writes exactly the same text. Two or more
/// are a list of lists. Field names and values inside persist: append only.
/// </para>
/// </summary>
public static class ConditionGroups
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The most alternatives one rule may hold.</summary>
    public const int MaxGroups = 10;

    /// <summary>The stored groups; an unreadable value reads as none, which matches nothing.</summary>
    public static IReadOnlyList<IReadOnlyList<AutoTagCondition>> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            if (root.GetArrayLength() > 0 && root[0].ValueKind == JsonValueKind.Array)
            {
                return root.EnumerateArray()
                    .Select(group => (IReadOnlyList<AutoTagCondition>)Conditions(group))
                    .Where(group => group.Count > 0)
                    .ToList();
            }

            var single = Conditions(root);
            return single.Count == 0 ? [] : [single];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static List<AutoTagCondition> Conditions(JsonElement array) =>
        array.ValueKind != JsonValueKind.Array
            ? []
            : array.Deserialize<List<AutoTagCondition>>(Json)?
                .Where(c => c is { Field: not null, Value: not null })
                .ToList() ?? [];

    public static string Serialize(IReadOnlyList<IReadOnlyList<AutoTagCondition>> groups) =>
        groups.Count == 1
            ? AutoTagRules.Serialize(groups[0])
            : JsonSerializer.Serialize(groups.Select(g => g.ToList()).ToList(), Json);

    /// <summary>
    /// Trims and validates submitted groups. Null when there is no condition at all (the rule would
    /// match every series), a group is empty, there are too many, or a field or value is outside
    /// <paramref name="fields"/> and the closed value sets. Duplicate groups are dropped.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<AutoTagCondition>>? Normalize(
        IEnumerable<IEnumerable<AutoTagCondition>?>? groups, IReadOnlyCollection<string> fields)
    {
        var result = new List<IReadOnlyList<AutoTagCondition>>();
        foreach (var group in groups ?? [])
        {
            var conditions = new List<AutoTagCondition>();
            foreach (var condition in group ?? [])
            {
                if (AutoTagRules.NormalizeOne(condition, fields) is not { } normalized)
                {
                    return null;
                }

                if (!conditions.Contains(normalized))
                {
                    conditions.Add(normalized);
                }
            }

            if (conditions.Count == 0)
            {
                return null;
            }

            if (!result.Any(existing => existing.Count == conditions.Count && existing.All(conditions.Contains)))
            {
                result.Add(conditions);
            }
        }

        var total = result.Sum(g => g.Count);
        return result.Count == 0 || result.Count > MaxGroups || total > AutoTagRules.MaxConditions ? null : result;
    }

    /// <summary>Any group whose every condition holds. No groups matches nothing.</summary>
    public static bool Matches(IReadOnlyList<IReadOnlyList<AutoTagCondition>> groups, AutoTagSubject subject) =>
        groups.Any(group => AutoTagRules.Matches(group, subject));
}
