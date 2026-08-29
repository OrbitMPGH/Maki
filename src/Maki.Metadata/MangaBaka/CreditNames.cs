namespace Maki.Metadata.MangaBaka;

/// <summary>
/// Values the dump files under <c>authors</c> or <c>artists</c> that are not people.
///
/// <para>
/// <c>"Anthology"</c> is the most common value in the whole column, on 1,108 works. A credit channel
/// that believes it is a person makes every anthology share a creator with every other anthology.
/// The rest are the placeholders that sit beside it.
/// </para>
///
/// <para>
/// One list, used by both the index build and the seed-profile query, because a name filtered on one
/// side and kept on the other cannot match anything and would quietly shrink the channel instead of
/// cleaning it.
/// </para>
/// </summary>
public static class CreditNames
{
    private static readonly HashSet<string> Sentinels = new(StringComparer.OrdinalIgnoreCase)
    {
        "Anthology", "Various", "Various Artists", "Unknown", "N/A", "-", "TBA",
    };

    public static bool IsPerson(string? name) =>
        !string.IsNullOrWhiteSpace(name) && !Sentinels.Contains(name.Trim());
}
