using System.Text.Json;

namespace Maki.Core.Configuration;

/// <summary>
/// Which supplementary rails the series page shows. Both are extras around the chapter list rather
/// than part of it, and both cost a catalogue query, so somebody who never uses them should be able
/// to turn them off.
/// <para>
/// Same discipline as <see cref="HomeLayoutSpec"/>: serialize only through <see cref="Json"/>, and
/// never rename a property. A name mismatch does not throw, it silently yields the parameter default,
/// so a renamed field degrades into "this person's preference was forgotten" rather than an error.
/// </para>
/// <para>
/// No <c>Merge</c> counterpart, because there is no key list and no ordering to reconcile — a rail
/// added later is a new property that reads as its default for everybody who stored the old shape.
/// </para>
/// </summary>
/// <param name="Related">MangaBaka's declared relations: sequels, prequels, spin-offs, side stories.</param>
/// <param name="Similar">The semantic "More like this" rail, seeded by this series alone.</param>
public record SeriesSectionsSpec(bool Related = true, bool Similar = true)
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Both rails on, which is what an unset setting means.</summary>
    public static SeriesSectionsSpec Default => new();

    /// <summary>Reads a stored blob, falling back to the default for null/blank/bad JSON.</summary>
    public static SeriesSectionsSpec Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Default;
        }

        try
        {
            return JsonSerializer.Deserialize<SeriesSectionsSpec>(json, Json) ?? Default;
        }
        catch (JsonException)
        {
            return Default;
        }
    }

    public static string Serialize(SeriesSectionsSpec spec) => JsonSerializer.Serialize(spec, Json);
}
