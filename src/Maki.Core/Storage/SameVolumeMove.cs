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

public readonly record struct MoveOutcome(MoveResult Result, Exception? Error = null);

/// <summary>
/// A rename that never becomes a copy. <see cref="File.Move(string, string)"/> quietly copies and
/// deletes when the two paths are on different volumes, which would turn a hardlinked torrent import
/// into a second full copy and leave a half-written file behind if the copy fails. Here a move across
/// volumes is refused, the source untouched, and the caller decides what to do.
/// </summary>
public static class SameVolumeMove
{
    private const int ErrorNotSameDevice = 17;
    private const int ErrorFileExists = 80;
    private const int ErrorAlreadyExists = 183;
    private const int Exdev = 18;
    private const uint MoveFileWriteThrough = 0x8;

    public static MoveOutcome Move(string source, string target)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (MoveFileEx(source, target, MoveFileWriteThrough))
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

            // rename() replaces an existing target, so the check has to come first.
            if (Occupied(target))
            {
                return new MoveOutcome(MoveResult.TargetExists);
            }

            if (rename(source, target) == 0)
            {
                return new MoveOutcome(MoveResult.Moved);
            }

            var errno = Marshal.GetLastPInvokeError();
            return errno == Exdev
                ? new MoveOutcome(MoveResult.CrossVolume)
                : new MoveOutcome(MoveResult.Failed, new IOException($"rename failed with errno {errno}"));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return new MoveOutcome(MoveResult.Failed, ex);
        }
    }

    public static bool Occupied(string path) =>
        File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null;

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existingFileName, string newFileName, uint flags);

#pragma warning disable SYSLIB1054, IDE1006 // libc name kept as-is, see FileLinker
    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int rename(string oldPath, string newPath);
#pragma warning restore SYSLIB1054, IDE1006
}
