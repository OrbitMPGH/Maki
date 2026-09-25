namespace Maki.Core.Paths;

/// <summary>
/// Turns a <c>ChapterFile.RelativePath</c> into an absolute path under its root folder.
/// <para>
/// Note the relative path is relative to the ROOT FOLDER, not to the series folder — it
/// already begins with the series' folder name.
/// </para>
/// </summary>
public static class LibraryPaths
{
    /// <summary>
    /// Resolves and canonicalizes a library-relative path, returning null when it would escape
    /// the root folder. Callers taking a path from a request must use this rather than a bare
    /// <see cref="Path.Combine(string, string)"/>: <c>Combine</c> happily accepts <c>..\..</c>
    /// segments, and an absolute second argument silently discards the root entirely.
    /// </summary>
    public static string? Resolve(string rootPath, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
            var full = Path.GetFullPath(Path.Combine(root, relativePath));
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            return full.StartsWith(root + Path.DirectorySeparatorChar, comparison) ? full : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>Folder names compare the way the host's filesystem does.</summary>
    public static StringComparer FolderComparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// The root-level folder a stored relative path sits in, or null for a file directly in the
    /// root (a manual link can point there).
    /// </summary>
    public static string? TopFolder(string relativePath)
    {
        var separator = relativePath.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        return separator > 0 ? relativePath[..separator] : null;
    }
}
