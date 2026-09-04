using System.Text.Json.Serialization;

namespace Maki.Core.Metadata;

/// <summary>
/// One provider-owned tag with the facets the provider files it under.
/// <para>
/// Only <see cref="Name"/> is guaranteed. A provider that reports bare strings, and every tag
/// stored before this record existed, leaves the rest null, so every consumer has to read them as
/// "unknown" rather than "none".
/// </para>
/// <para>
/// The JSON names are one letter because this is the on-disk shape of <c>Series.Tags</c> and a
/// popular series carries well over a hundred of these. Spelling the property names out multiplied
/// the column by roughly six for no reader's benefit; the API has its own DTO with real names.
/// </para>
/// </summary>
public record MetadataTag(
    [property: JsonPropertyName("n")] string Name,
    /// <summary>Importance bucket: core, defining, recurrent or incidental. See <see cref="Rank"/>.</summary>
    [property: JsonPropertyName("w")] string? Weight = null,
    /// <summary>
    /// Where the tag sits in the provider's taxonomy, ending with the tag itself, e.g.
    /// "Character Types > Female Lead > Popular Female Lead". Null when the provider has no
    /// taxonomy or the tag is not in it.
    /// </summary>
    [property: JsonPropertyName("p")] string? Path = null)
{
    /// <summary>
    /// Where a tag's bucket sorts, lowest first: a "core" tag is what the series is about, an
    /// "incidental" one is something that happens in it once. Anything unrecognised, including a
    /// tag whose weight was never captured, sorts last with the incidentals.
    /// <para>
    /// This is the one definition of the vocabulary. <c>TasteProfileService.BucketWeight</c> scores
    /// tags off the same ranking, so a bucket cannot come to mean one thing in a taste profile and
    /// another on the series page.
    /// </para>
    /// </summary>
    public static int Rank(string? weight) => weight?.ToLowerInvariant() switch
    {
        "core" => 0,
        "defining" => 1,
        "recurrent" => 2,
        _ => 3,
    };

    /// <summary>The buckets in display order. Anything outside this list ranks with the last.</summary>
    public static readonly IReadOnlyList<string> Buckets = ["core", "defining", "recurrent", "incidental"];

    /// <summary>
    /// The taxonomy branch this tag hangs off: <see cref="Path"/> without its final segment, which
    /// is the tag's own name. Null when there is no path, or when the path is only the name.
    /// </summary>
    public string? Category
    {
        get
        {
            if (Path is null) { return null; }
            var cut = Path.LastIndexOf(" > ", StringComparison.Ordinal);
            return cut <= 0 ? null : Path[..cut];
        }
    }
}
