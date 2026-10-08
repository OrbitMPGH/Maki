using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Maki.Core.Storage;

/// <summary>
/// Free space for a library path, including UNC network shares.
/// <para>
/// <see cref="DriveInfo"/> only understands drive letters and mount points — constructing one
/// for a UNC root (<c>\\host\share\</c>) throws, so network root folders reported no free space
/// at all. Windows will answer for a UNC path via GetDiskFreeSpaceEx, which takes any directory
/// rather than a root, so that's the path used there.
/// </para>
/// </summary>
public static class DiskSpace
{
    /// <summary>
    /// Bytes available to the current user at <paramref name="path"/>, or null if the location
    /// can't report it (offline share, permission denied, unsupported filesystem).
    /// </summary>
    public static long? AvailableFor(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(path);

            if (OperatingSystem.IsWindows())
            {
                // Takes any directory, so it answers for UNC shares and for volumes mounted into a
                // folder, both of which the drive letter would get wrong.
                if (WindowsAvailableFor(full) is { } free) return free;
                if (IsUnc(full)) return null;
                var root = Path.GetPathRoot(full);
                return root is null ? null : new DriveInfo(root).AvailableFreeSpace;
            }

            // GetPathRoot is "/" for every path here, which in Docker is the container's overlay
            // filesystem rather than the bind-mounted library volume.
            var mount = LongestMount(full, DriveInfo.GetDrives().Select(d => d.Name), StringComparison.Ordinal);
            return mount is null ? null : new DriveInfo(mount).AvailableFreeSpace;
        }
        catch (Exception)
        {
            // Best-effort: a missing/offline/denied path just has no number to show.
            return null;
        }
    }

    /// <summary>
    /// The mount point holding <paramref name="fullPath"/>: the longest one that is the path itself or
    /// a whole-segment prefix of it, so "/manga2" is not read as living on "/manga".
    /// </summary>
    public static string? LongestMount(string fullPath, IEnumerable<string> mounts, StringComparison comparison) =>
        mounts
            .Where(mount => Contains(mount, fullPath, comparison))
            .OrderByDescending(mount => mount.TrimEnd(Separators).Length)
            .FirstOrDefault();

    private static bool Contains(string mount, string fullPath, StringComparison comparison)
    {
        var trimmed = mount.TrimEnd(Separators);
        if (trimmed.Length == 0) return fullPath.StartsWith(mount, comparison);
        if (!fullPath.StartsWith(trimmed, comparison)) return false;
        return fullPath.Length == trimmed.Length || Separators.Contains(fullPath[trimmed.Length]);
    }

    private static readonly char[] Separators = ['/', '\\'];

    private static bool IsUnc(string fullPath)
    {
        try
        {
            return new Uri(fullPath).IsUnc;
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static long? WindowsAvailableFor(string fullPath)
    {
        // The API wants a directory that exists; an offline share fails here rather than lying.
        return GetDiskFreeSpaceEx(fullPath, out var freeForUser, out _, out _) && freeForUser <= long.MaxValue
            ? (long)freeForUser
            : null;
    }

    /// <param name="freeBytesAvailable">
    /// Free bytes available to the *calling user* — differs from total free space when the share
    /// enforces per-user quotas, and matches what DriveInfo.AvailableFreeSpace reports locally.
    /// </param>
    // DllImport rather than LibraryImport: the source generator requires AllowUnsafeBlocks, which
    // isn't worth turning on across Maki.Core for a single call.
    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(
        string directoryName,
        out ulong freeBytesAvailable,
        out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);
}
