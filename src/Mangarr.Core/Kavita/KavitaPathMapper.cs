namespace Mangarr.Core.Kavita;

/// <summary>
/// Rewrites a Mangarr-side library path to how Kavita sees it — e.g.
/// <c>C:\Manga\Series</c> → <c>/manga/Series</c> when Kavita runs in Docker.
/// Separator style follows the mapped prefix.
/// </summary>
public static class KavitaPathMapper
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
