namespace Maki.Metadata.Embedding;

/// <summary>
/// Tag name to the ids of that tag and everything below it in MangaBaka's tag tree, for the
/// "include subtags" filter option. Built from <c>name_path</c>, which names the tag itself as its
/// last segment ("Locations &gt; School &gt; College"), so a tag's subtree is every tag whose path
/// starts with its own. The dump does carry some implied parents (a "High School" series is often
/// also tagged "School"), but only for some pairs: over half the "College" series carry no
/// "School", which is why this cannot lean on the data alone.
/// </summary>
public static class TagSubtrees
{
    private const string Separator = " > ";

    public static IReadOnlyDictionary<string, int[]> Build(IEnumerable<(int Id, string Name, string Path)> tags)
    {
        var list = tags.ToList();
        var byPrefix = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, _, path) in list)
        {
            if (path.Length == 0)
            {
                continue;
            }

            var segments = path.Split(Separator);
            for (var depth = 1; depth <= segments.Length; depth++)
            {
                var prefix = string.Join(Separator, segments, 0, depth);
                if (!byPrefix.TryGetValue(prefix, out var ids))
                {
                    byPrefix[prefix] = ids = [];
                }

                ids.Add(id);
            }
        }

        var result = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in list.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            var ids = new HashSet<int>();
            foreach (var (id, _, path) in group)
            {
                ids.Add(id);
                if (path.Length > 0 && byPrefix.TryGetValue(path, out var below))
                {
                    ids.UnionWith(below);
                }
            }

            var sorted = ids.ToArray();
            Array.Sort(sorted);
            result[group.Key] = sorted;
        }

        return result;
    }
}
