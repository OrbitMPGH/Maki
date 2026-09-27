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

            // TrimEndingDirectorySeparator is a no-op on a drive root ("C:\", "/"), so root already
            // ends with the separator there; adding another would make the containment check fail.
            var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, comparison) ? full : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// A comparison key for a stored relative path. Rows written under Docker hold <c>/</c> while
    /// a Windows scan builds <c>\</c>, and a library moved between the two must still match its
    /// own files rather than showing every one twice.
    /// </summary>
    public static string ComparisonKey(string relativePath) =>
        relativePath.Replace('\\', '/').TrimStart('/');

    /// <summary>Folder names compare the way the host's filesystem does.</summary>
    public static StringComparer FolderComparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// The root-level folder a stored relative path sits in, or null for a file directly in the
    /// root (a manual link can point there) or for a "." or ".." segment, which is never a real
    /// folder name and, treated as one, would have a caller enumerate the whole root or its parent.
    /// Both separators count on every host, like <see cref="ComparisonKey"/>, so a row written on
    /// Windows still names its folder under Docker.
    /// </summary>
    public static string? TopFolder(string relativePath)
    {
        var separator = relativePath.IndexOfAny(['/', '\\']);
        if (separator <= 0)
        {
            return null;
        }

        var segment = relativePath[..separator];
        return segment is "." or ".." ? null : segment;
    }
}
