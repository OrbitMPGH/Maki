using System.Text.Json;

namespace Maki.Core.Entities;

/// <summary>
/// One source's sub-chapters (10.1, 10.2, ...) folded into the whole chapter row 10, after the pages
/// showed a whole on the floor holds them (<c>ChapterReconciler.CheckPages</c>). Sync reads it to list
/// those uploads as the one chapter they are: while the source still lists every part, they stand for
/// chapter <see cref="Floor"/>; once one goes or changes, the fold is dropped and the parts come back
/// as rows of their own.
/// </summary>
public class ChapterFold
{
    public int Id { get; set; }
    public int SeriesId { get; set; }
    public int SourceMappingId { get; set; }
    public string Language { get; set; } = "en";
    public decimal Floor { get; set; }

    /// <summary>The parts in reading order (<see cref="ChapterFoldPart"/>), as JSON.</summary>
    public string PartsJson { get; set; } = "[]";

    public DateTime FoldedAt { get; set; }

    public IReadOnlyList<ChapterFoldPart> Parts => ChapterFoldPart.Parse(PartsJson);
}

/// <summary>One upload of a folded chapter, with its page count so positions can be carried across.</summary>
public sealed record ChapterFoldPart(string SourceChapterId, decimal Number, string? NumberRaw, int Pages)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Format(IEnumerable<ChapterFoldPart> parts) => JsonSerializer.Serialize(parts.ToList(), Options);

    public static IReadOnlyList<ChapterFoldPart> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<ChapterFoldPart>>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
