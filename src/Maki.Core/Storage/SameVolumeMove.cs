using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Maki.Core.Storage;

public enum MoveResult
{
    Moved,
    TargetExists,
    CrossVolume,
    Failed
}

/// <param name="Racy">Moved by a check followed by a plain rename, which could have replaced a file that appeared in between.</param>
public readonly record struct MoveOutcome(MoveResult Result, Exception? Error = null, bool Racy = false);

/// <summary>
/// A rename that never becomes a copy and never replaces the target. <see cref="File.Move(string, string)"/>
/// quietly copies and deletes when the two paths are on different volumes, which would turn a
/// hardlinked torrent import into a second full copy and leave a half-written file behind if the
/// copy fails. Here a move across volumes is refused, the source untouched, and the caller decides
/// what to do.
/// </summary>
public static class SameVolumeMove
{
    private const int ErrorNotSameDevice = 17;
    private const int ErrorFileExists = 80;
    private const int ErrorAlreadyExists = 183;
    private const uint MoveFileWriteThrough = 0x8;

    // Linux errno values.
    private const int Eperm = 1;
    private const int Eexist = 17;
    private const int Exdev = 18;
    private const int Einval = 22;
    private const int Enosys = 38;
    private const int Eopnotsupp = 95;

    private const int AtFdcwd = -100;
    private const uint RenameNoreplace = 1;

    public static MoveOutcome Move(string source, string target)
    {
        try
        {
            return OperatingSystem.IsWindows() ? MoveWindows(source, target) : MoveUnix(source, target);
        }
        catch (DllNotFoundException ex)
        {
            return new MoveOutcome(MoveResult.Failed, ex);
        }
    }

    /// <summary>The source is gone and the target is there: the move happened whatever was reported.</summary>
    public static bool Landed(string source, string target) => !Occupied(source) && Occupied(target);

    public static bool Occupied(string path) =>
        File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null;

    [SupportedOSPlatform("windows")]
    private static MoveOutcome MoveWindows(string source, string target)
    {
        // No MOVEFILE_REPLACE_EXISTING and no MOVEFILE_COPY_ALLOWED: an existing target and another
        // volume are both errors.
        if (MoveFileEx(Extended(source), Extended(target), MoveFileWriteThrough))
        {
            return new MoveOutcome(MoveResult.Moved);
        }

        var code = Marshal.GetLastPInvokeError();
        return code switch
        {
            ErrorNotSameDevice => new MoveOutcome(MoveResult.CrossVolume),
            ErrorFileExists or ErrorAlreadyExists => new MoveOutcome(MoveResult.TargetExists),
            _ => new MoveOutcome(MoveResult.Failed, new Win32Exception(code))
        };
    }

    /// <summary>The <c>\\?\</c> form of a path too long for the Win32 call without it.</summary>
    public static string Extended(string path)
    {
        if (path.Length < 240 || path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return path;
        }

        var full = Path.GetFullPath(path);
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    /// <summary>
    /// <c>renameat2</c> with <c>RENAME_NOREPLACE</c> where kernel and filesystem have it; then a hard
    /// link and an unlink of the source, which refuses an existing target just the same; and only on a
    /// filesystem without hard links, a check then a rename, which can race and says so.
    /// </summary>
    private static MoveOutcome MoveUnix(string source, string target) =>
        RenameNoReplace(source, target) ?? LinkThenUnlink(source, target) ?? CheckThenRename(source, target);

    private static MoveOutcome? RenameNoReplace(string source, string target)
    {
        try
        {
            if (renameat2(AtFdcwd, source, AtFdcwd, target, RenameNoreplace) == 0)
            {
                return new MoveOutcome(MoveResult.Moved);
            }
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }

        var errno = Marshal.GetLastPInvokeError();
        return errno is Einval or Enosys ? null : FromErrno(errno, "renameat2");
    }

    private static MoveOutcome? LinkThenUnlink(string source, string target)
    {
        try
        {
            if (link(source, target) != 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                if (errno is Eperm or Eopnotsupp)
                {
                    return null;
                }

                // An NFS retransmit can report EEXIST for a link that was made: then both names are
                // already one file, and only the source name is left to remove.
                if (errno != Eexist || !SameFile(source, target))
                {
                    return FromErrno(errno, "link");
                }
            }

            if (unlink(source) == 0)
            {
                return new MoveOutcome(MoveResult.Moved);
            }

            var unlinkErrno = Marshal.GetLastPInvokeError();
            unlink(target);
            return FromErrno(unlinkErrno, "unlink");
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static MoveOutcome CheckThenRename(string source, string target)
    {
        if (Occupied(target))
        {
            return new MoveOutcome(MoveResult.TargetExists);
        }

        return rename(source, target) == 0
            ? new MoveOutcome(MoveResult.Moved, Racy: true)
            : FromErrno(Marshal.GetLastPInvokeError(), "rename");
    }

    private static MoveOutcome FromErrno(int errno, string call) => errno switch
    {
        Eexist => new MoveOutcome(MoveResult.TargetExists),
        Exdev => new MoveOutcome(MoveResult.CrossVolume),
        _ => new MoveOutcome(MoveResult.Failed, new IOException($"{call} failed with errno {errno}"))
    };

    /// <summary>
    /// Same device and inode, read from the first two fields of <c>struct stat</c>, which are 64-bit
    /// on both 64-bit Linux layouts (x86-64 and the generic one arm64 uses). Anywhere else, false.
    /// </summary>
    private static bool SameFile(string a, string b)
    {
        if (!OperatingSystem.IsLinux() || !Environment.Is64BitProcess)
        {
            return false;
        }

        try
        {
            var first = new byte[256];
            var second = new byte[256];
            return lstat(a, first) == 0 && lstat(b, second) == 0 &&
                   first.AsSpan(0, 16).SequenceEqual(second.AsSpan(0, 16));
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existingFileName, string newFileName, uint flags);

#pragma warning disable SYSLIB1054, IDE1006 // libc names kept as-is, see FileLinker
    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int renameat2(int oldDirFd, string oldPath, int newDirFd, string newPath, uint flags);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int link(string oldPath, string newPath);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int unlink(string path);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int rename(string oldPath, string newPath);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int lstat(string path, [Out] byte[] buffer);
#pragma warning restore SYSLIB1054, IDE1006
}
