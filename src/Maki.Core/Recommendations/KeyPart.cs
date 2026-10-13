namespace Maki.Core.Recommendations;

/// <summary>
/// Cache-key fragments for user-supplied names. Length-prefixed so a name containing the key's own
/// delimiters cannot render the same text as two different names.
/// </summary>
public static class KeyPart
{
    public static string Of(string? value) => $"{value?.Length ?? 0}:{value}";

    public static string List(IEnumerable<string>? values) =>
        string.Join('.', (values ?? []).Select(Of));
}
