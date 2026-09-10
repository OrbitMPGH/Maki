using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Maki.Core.Storage;

/// <summary>How a file ended up at its destination.</summary>
public enum FilePlacement
{
    /// <summary>A second directory entry pointing at the source's data — no extra disk used.</summary>
    Hardlinked,

    /// <summary>A full byte copy, either by choice or because a hardlink wasn't possible.</summary>
    Copied
}

/// <summary>
/// Places a file at a destination as a hardlink when the filesystem allows it, falling back to a
/// copy. Used for importing completed torrents: a hardlink lets the same bytes stay seeded in the
/// download folder and live in the library at once, instead of storing them twice.
/// <para>
/// A hardlink only works within one filesystem — download folder and library on different volumes
/// (or a network share, or exFAT/FAT32) can't share an inode, and there's no portable way to ask
/// in advance that isn't also a lie under Docker bind mounts. So the link is simply attempted and
/// the copy is the fallback.
/// </para>
/// </summary>
public static class FileLinker
{
    /// <summary>
    /// Creates <paramref name="target"/> from <paramref name="source"/>. With
    /// <paramref name="preferHardlink"/> a hardlink is tried first and a copy is used when it
    /// fails. Throws whatever <see cref="File.Copy(string, string)"/> throws if the copy fails too.
    /// </summary>
    public static FilePlacement Place(string source, string target, bool preferHardlink)
    {
        if (preferHardlink && TryHardlink(source, target))
        {
            return FilePlacement.Hardlinked;
        }

        File.Copy(source, target);
        return FilePlacement.Copied;
    }

    /// <summary>
    /// Attempts a hardlink. False for every expected reason it can't be made (different volume,
    /// filesystem without hardlink support, permissions, target exists) — the caller copies instead.
    /// </summary>
    public static bool TryHardlink(string source, string target)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return CreateHardLink(target, source, IntPtr.Zero);
            }

            return link(source, target) == 0;
        }
        catch (Exception)
        {
            // DllImport resolution failure on an unusual platform: a copy still works.
            return false;
        }
    }

    // DllImport rather than LibraryImport for the same reason as DiskSpace: the source generator
    // requires AllowUnsafeBlocks across Maki.Core for what is one call.
    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    // CharSet.Ansi marshals as UTF-8 on Unix, which is what the syscall wants.
#pragma warning disable SYSLIB1054, IDE1006 // libc name kept as-is
    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int link(string oldPath, string newPath);
#pragma warning restore SYSLIB1054, IDE1006
}
