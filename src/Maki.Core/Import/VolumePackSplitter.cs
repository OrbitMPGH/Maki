using Maki.Core.Parsing;

namespace Maki.Core.Import;

/// <summary>
/// Splits a multi-volume pack ("Title vol 01-08.cbz" holding one folder per volume) into one
/// <see cref="ComicSource"/> per volume, so each volume is imported as a file of its own. A pack
/// left whole links to a single volume and reads as an omnibus labelled with its first volume.
/// </summary>
public static class VolumePackSplitter
{
    /// <summary>
    /// The pack's volumes, in volume order, or just <paramref name="source"/> when its pages are not
    /// grouped into two or more folders that each name one volume. The volume folder is the first
    /// path segment of a page that reads as a single volume, so a wrapper folder named for the whole
    /// range ("Title vol 01-08") is passed over. A page outside every volume folder means the pack
    /// is not cleanly divided, and it is left whole rather than losing that page.
    /// </summary>
    public static IReadOnlyList<ComicSource> Split(ComicSource source)
    {
        if (source.Pages.Count == 0 || source.Entry is not null || source.Slice is not null)
        {
            return [source];
        }

        var groups = new Dictionary<string, (int Volume, long Pages)>(StringComparer.Ordinal);
        foreach (var page in source.Pages)
        {
            if (VolumeFolderOf(page) is not { } folder)
            {
                return [source];
            }

            groups[folder.Prefix] = groups.TryGetValue(folder.Prefix, out var seen)
                ? (seen.Volume, seen.Pages + 1)
                : (folder.Volume, 1);
        }

        if (groups.Count < 2 || groups.Values.Select(g => g.Volume).Distinct().Count() != groups.Count)
        {
            return [source];
        }

        return groups
            .OrderBy(g => g.Value.Volume)
            .Select(g => source with
            {
                Name = Path.GetFileName(g.Key.TrimEnd('/')) + ".cbz",
                Kind = ComicSourceKind.Cbz,
                Size = source.Size * g.Value.Pages / source.Pages.Count,
                Pages = source.Pages.Where(p => p.StartsWith(g.Key, StringComparison.Ordinal)).ToList(),
                Slice = g.Key
            })
            .ToList();
    }

    private static (string Prefix, int Volume)? VolumeFolderOf(string page)
    {
        var segments = page.Replace((char)92, '/').Split('/');
        var prefix = "";
        for (var i = 0; i < segments.Length - 1; i++)
        {
            prefix += segments[i] + "/";
            var parsed = ReleaseNameParser.ParseFileName(segments[i]);
            if (parsed is { IsVolume: true, VolumeEnd: null, Volume: { } volume })
            {
                return (prefix, volume);
            }
        }

        return null;
    }
}
