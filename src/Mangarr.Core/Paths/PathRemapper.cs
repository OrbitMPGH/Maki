namespace Mangarr.Core.Paths;

/// <summary>
/// Rewrites a path from one root prefix to another — for when two processes see the
/// same files under different mounts (Kavita or qBittorrent in Docker vs Mangarr on
/// the host), e.g. <c>C:\Manga\Series</c> → <c>/manga/Series</c>, or a qBittorrent
/// <c>/downloads/x.cbz</c> → <c>Z:\downloads\x.cbz</c>. The prefix match is case- and
/// separator-insensitive and must land on a path boundary; the target's separator
/// style is applied to the tail. A blank mapping returns the path unchanged.
/// </summary>
public static class PathRemapper
{
    public static string Map(string path, string? mapFrom, string? mapTo)
    {
        if (string.IsNullOrWhiteSpace(mapFrom) || string.IsNullOrWhiteSpace(mapTo))
        {
            return path;
        }

        var from = mapFrom.TrimEnd('/', '\\');
        // Separator-insensitive prefix match — users mix / and \ freely on Windows.
        var normPath = path.Replace('\\', '/');
        var normFrom = from.Replace('\\', '/');
        if (!normPath.StartsWith(normFrom, StringComparison.OrdinalIgnoreCase) ||
            (path.Length > from.Length && normPath[from.Length] != '/'))
        {
            return path; // prefix must end on a path boundary: C:\Manga must not match C:\MangaExtra
        }

        var separator = mapTo.Contains('/') ? '/' : '\\';
        var tail = path[from.Length..].TrimStart('/', '\\')
            .Replace(separator == '/' ? '\\' : '/', separator);
        var to = mapTo.TrimEnd('/', '\\');
        return tail.Length == 0 ? to : $"{to}{separator}{tail}";
    }
}
