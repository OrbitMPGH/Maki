namespace Maki.Core.Recommendations;

/// <summary>
/// One creator or publisher named by a catalogue filter or the follow list. <paramref name="Role"/>
/// is a wire label (<c>author</c>, <c>artist</c>, <c>studio</c>) or null for any role. Stored by
/// name, never by an index id: the credit index renumbers its names on every rebuild.
/// </summary>
public record CatalogueCredit(string Name, string? Role = null);

public static class CatalogueCredits
{
    public const string Author = "author";
    public const string Artist = "artist";
    public const string Studio = "studio";

    private const int MaxCredits = 200;
    private const int MaxNameLength = 200;

    /// <summary>
    /// Trimmed, role labels canonicalized (<c>publisher</c> reads as <c>studio</c>, anything
    /// unknown as any role), duplicates dropped, capped. Null when nothing is left.
    /// </summary>
    public static IReadOnlyList<CatalogueCredit>? Normalize(IReadOnlyList<CatalogueCredit>? credits)
    {
        if (credits is null)
        {
            return null;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<CatalogueCredit>();
        foreach (var credit in credits)
        {
            var name = credit?.Name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength)
            {
                continue;
            }

            var role = NormalizeRole(credit!.Role);
            if (seen.Add($"{role}/{name}"))
            {
                kept.Add(new CatalogueCredit(name, role));
            }

            if (kept.Count == MaxCredits)
            {
                break;
            }
        }

        return kept.Count == 0 ? null : kept;
    }

    public static string? NormalizeRole(string? role) =>
        role?.Trim().ToLowerInvariant() switch
        {
            Author => Author,
            Artist => Artist,
            Studio or "publisher" => Studio,
            _ => null,
        };

    /// <summary>A stable cache-key fragment.</summary>
    public static string Key(IReadOnlyList<CatalogueCredit>? credits) =>
        credits is null
            ? string.Empty
            : string.Join(',', credits.Select(c => $"{c.Role}/{c.Name.ToLowerInvariant()}"));
}
