using System.IO.Compression;
using System.Text.Json;

namespace Maki.Core.Import;

/// <summary>Where a folder's series name came from.</summary>
public static class FolderHintOrigin
{
    public const string ComicInfo = "comicInfo";
    public const string SeriesJson = "seriesJson";
}

/// <param name="SeriesName">The series the files say they belong to, or null.</param>
/// <param name="NameOrigin">One of <see cref="FolderHintOrigin"/> when <paramref name="SeriesName"/> is set.</param>
/// <param name="WebLinks">ComicInfo <c>Web</c> links, split.</param>
public sealed record FolderHints(string? SeriesName, string? NameOrigin, IReadOnlyList<string> WebLinks)
{
    public static readonly FolderHints None = new(null, null, []);
}

/// <summary>
/// What an import folder says about itself: the ComicInfo.xml of its first ready CBZ (one entry
/// read, nothing extracted) and a Mylar-style <c>series.json</c>. Komga writes no ids into files
/// and Mylar's <c>comicid</c> is a ComicVine id, so only the series name and the web links count.
/// </summary>
public static class FolderMetadataHints
{
    private const long MaxEntryBytes = 1024 * 1024;

    public static FolderHints Read(string dir, IReadOnlyList<ComicSource> comics)
    {
        var (comicInfoName, links) = ReadComicInfo(comics);
        if (comicInfoName is not null)
        {
            return new FolderHints(comicInfoName, FolderHintOrigin.ComicInfo, links);
        }

        var jsonName = ReadSeriesJson(dir);
        return jsonName is not null
            ? new FolderHints(jsonName, FolderHintOrigin.SeriesJson, links)
            : new FolderHints(null, null, links);
    }

    private static (string? Series, IReadOnlyList<string> Links) ReadComicInfo(IReadOnlyList<ComicSource> comics)
    {
        var first = comics
            .Where(c => c.Kind is ComicSourceKind.Cbz or ComicSourceKind.Zip && c.Entry is null && c.Slice is null)
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        if (first is null)
        {
            return (null, []);
        }

        try
        {
            using var zip = ZipFile.OpenRead(first.Path);
            var entry = zip.Entries
                .Where(e => string.Equals(e.Name, "ComicInfo.xml", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.FullName.Length)
                .FirstOrDefault();
            if (entry is null || entry.Length > MaxEntryBytes)
            {
                return (null, []);
            }

            using var reader = new StreamReader(entry.Open());
            var info = ComicInfo.ComicInfoBuilder.Deserialize(new StringReader(reader.ReadToEnd()));
            return (Clean(info?.Series), SplitLinks(info?.Web));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return (null, []);
        }
    }

    internal static IReadOnlyList<string> SplitLinks(string? web) =>
        string.IsNullOrWhiteSpace(web)
            ? []
            : web.Split([',', ' ', '\t', '\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? ReadSeriesJson(string dir)
    {
        var path = Path.Combine(dir, "series.json");
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > MaxEntryBytes)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var metadata = root.TryGetProperty("metadata", out var m) && m.ValueKind == JsonValueKind.Object ? m : root;
            return metadata.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                ? Clean(name.GetString())
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > 300 ? null : trimmed;
    }
}
