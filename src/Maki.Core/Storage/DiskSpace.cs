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

            if (OperatingSystem.IsWindows() && IsUnc(full))
            {
                return UncAvailableFor(full);
            }

            var root = Path.GetPathRoot(full);
            return root is null ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception)
        {
            // Best-effort: a missing/offline/denied path just has no number to show.
            return null;
        }
    }

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
    private static long? UncAvailableFor(string fullPath)
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
