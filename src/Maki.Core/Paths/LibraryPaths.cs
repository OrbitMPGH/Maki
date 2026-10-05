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
    /// <para>
    /// A backslash counts as a separator on every host, like <see cref="ComparisonKey"/>: a row written
    /// on Windows holds <c>\</c>, which Linux would otherwise read as part of a single file name.
    /// </para>
    /// </summary>
    public static string? Resolve(string rootPath, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        if (Path.DirectorySeparatorChar != '\\')
        {
            relativePath = relativePath.Replace('\\', '/');
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
            // Windows collapses names like "..." or ". ." to the root itself.
            return full.StartsWith(prefix, comparison)
                && !string.Equals(Path.TrimEndingDirectorySeparator(full), root, comparison)
                ? full
                : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// <see cref="Resolve"/>, but also null when the path or any folder between it and the root is
    /// a symbolic link or junction. Use it wherever the result is deleted, moved or rewritten: the
    /// lexical check alone lets a link planted inside a series folder lead out of the library.
    /// </summary>
    public static string? ResolveNoLinks(string rootPath, string relativePath) =>
        Resolve(rootPath, relativePath) is { } path && !TraversesLink(rootPath, path) ? path : null;

    /// <summary>
    /// <see cref="Resolve"/>, but null when a folder between the path and the root is a link. The
    /// final entry may itself be a link: deleting a linked file removes only the link.
    /// </summary>
    public static string? ResolveForDelete(string rootPath, string relativePath) =>
        Resolve(rootPath, relativePath) is { } path
        && (Path.GetDirectoryName(path) is not { } parent || !TraversesLink(rootPath, parent))
            ? path
            : null;

    /// <summary>
    /// True when anything below <paramref name="directory"/> is a link. Linked folders are not
    /// entered, and <paramref name="directory"/> itself is not checked.
    /// </summary>
    public static bool ContainsLink(string directory)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.TryPop(out var current))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                if (IsLink(entry))
                {
                    return true;
                }

                if (Directory.Exists(entry))
                {
                    pending.Push(entry);
                }
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="fullPath"/> or an ancestor below <paramref name="rootPath"/> is a
    /// reparse point. The root itself is only checked with <paramref name="includeRoot"/>, since a
    /// library mounted through a link is ordinary. Entries that do not exist are skipped.
    /// </summary>
    public static bool TraversesLink(string rootPath, string fullPath, bool includeRoot = false)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        for (var current = Path.GetFullPath(fullPath); current != null; current = Path.GetDirectoryName(current))
        {
            var atRoot = string.Equals(Path.TrimEndingDirectorySeparator(current), root, comparison);
            if ((!atRoot || includeRoot) && IsLink(current))
            {
                return true;
            }

            if (atRoot)
            {
                break;
            }
        }

        return false;
    }

    /// <summary>
    /// Every file under <paramref name="directory"/>, recursively, never entering a linked folder
    /// and never returning a linked file. <paramref name="directory"/> itself is not checked.
    /// </summary>
    public static IEnumerable<string> EnumerateFilesNoLinks(string directory, Func<string, bool>? enterDirectory = null)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.TryPop(out var current))
        {
            foreach (var file in Directory.EnumerateFiles(current))
            {
                if (!IsLink(file))
                {
                    yield return file;
                }
            }

            foreach (var child in Directory.EnumerateDirectories(current))
            {
                if (!IsLink(child) && (enterDirectory?.Invoke(child) ?? true))
                {
                    pending.Push(child);
                }
            }
        }
    }

    /// <summary>Every folder under <paramref name="directory"/>, recursively, never a linked one or anything below it.</summary>
    public static IEnumerable<string> EnumerateDirectoriesNoLinks(string directory)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.TryPop(out var current))
        {
            foreach (var child in Directory.EnumerateDirectories(current))
            {
                if (!IsLink(child))
                {
                    yield return child;
                    pending.Push(child);
                }
            }
        }
    }

    /// <summary>True for an existing symbolic link, junction or other reparse point, and for anything whose attributes cannot be read.</summary>
    public static bool IsLink(string path)
    {
        try
        {
            return (File.Exists(path) || Directory.Exists(path))
                && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// A comparison key for a stored relative path. Rows written under Docker hold <c>/</c> while
    /// a Windows scan builds <c>\</c>, and a library moved between the two must still match its
    /// own files rather than showing every one twice.
    /// </summary>
    public static string ComparisonKey(string relativePath) =>
        relativePath.Replace('\\', '/').TrimStart('/');

    /// <summary>
    /// True when two paths name one directory on disk. A case-insensitive filesystem (Windows, or an
    /// SMB mount under Docker) answers to <c>chainsaw man</c> and <c>Chainsaw Man</c> alike, so
    /// comparing the strings, or asking whether the second exists, cannot tell a second folder from
    /// the first one under another spelling. The parent's listing can: a case-sensitive filesystem
    /// holding two such folders lists both names.
    /// </summary>
    public static bool IsSameDirectory(string a, string b)
    {
        var fullA = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a));
        var fullB = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b));
        if (string.Equals(fullA, fullB, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.Equals(fullA, fullB, StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(fullA) || !Directory.Exists(fullB))
        {
            return false;
        }

        var parent = Path.GetDirectoryName(fullB);
        if (parent is null)
        {
            return true;
        }

        var nameA = Path.GetFileName(fullA);
        var nameB = Path.GetFileName(fullB);
        if (string.Equals(nameA, nameB, StringComparison.Ordinal))
        {
            return IsSameDirectory(Path.GetDirectoryName(fullA)!, parent);
        }

        var listed = Directory.EnumerateDirectories(parent).Select(Path.GetFileName).ToList();
        return !(listed.Contains(nameA, StringComparer.Ordinal) && listed.Contains(nameB, StringComparer.Ordinal));
    }

    /// <summary>
    /// Folder names ignore case on every host. A case-insensitive share mounted under Docker holds
    /// <c>One Piece</c> and <c>one piece</c> as one folder, so an ordinal comparison there would let
    /// one series reach into another's folder, or count a folder it shares as its own.
    /// </summary>
    public static StringComparer FolderComparer { get; } = StringComparer.OrdinalIgnoreCase;

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
