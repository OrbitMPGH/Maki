using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Maki.Core.Paths;
using Microsoft.Win32.SafeHandles;

namespace Maki.Core.Io;

public enum FileSameness
{
    Different,
    Same,
    Unknown,
}

/// <summary>
/// Whether two paths open one file, from the filesystem's own identity: device and inode on Linux,
/// volume serial and file index on Windows. Path strings cannot answer it: two roots can name one
/// share (<c>Z:\Manga</c> and <c>\\nas\manga</c>), and a case-insensitive mount opens two spellings
/// as one file. Anything that acts on "these are two copies" must treat <see cref="FileSameness.Unknown"/>
/// as the same file.
/// </summary>
public static class FileIdentity
{
    public static FileSameness Compare(string a, string b)
    {
        var fullA = Path.GetFullPath(a);
        var fullB = Path.GetFullPath(b);
        if (string.Equals(fullA, fullB, StringComparison.Ordinal))
        {
            return FileSameness.Same;
        }

        // CIFS without serverino and FUSE mounts invent an inode per name, so two spellings of one
        // file can carry two numbers. Only a listing that holds both spellings proves two files.
        if (string.Equals(fullA, fullB, StringComparison.OrdinalIgnoreCase) && File.Exists(fullA) && File.Exists(fullB))
        {
            try
            {
                return LibraryPaths.IsSameFile(fullA, fullB) ? FileSameness.Same : FileSameness.Different;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return FileSameness.Same;
            }
        }

        if (Of(a) is not { } first || Of(b) is not { } second)
        {
            return FileSameness.Unknown;
        }

        return first == second ? FileSameness.Same : FileSameness.Different;
    }

    private static (ulong Device, ulong Index)? Of(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return OfWindows(path);
            }

            if (OperatingSystem.IsLinux() && Environment.Is64BitProcess &&
                RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64)
            {
                // The first two fields of struct stat, 64-bit on both layouts.
                var buffer = new byte[256];
                return stat(path, buffer) == 0
                    ? (BitConverter.ToUInt64(buffer, 0), BitConverter.ToUInt64(buffer, 8))
                    : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EntryPointNotFoundException
                                       or DllNotFoundException or ArgumentException or NotSupportedException)
        {
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static (ulong, ulong)? OfWindows(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandle(handle, out var info))
        {
            return null;
        }

        var index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        // Some network filesystems report no index at all, which would make every file the same.
        return index == 0 ? null : (info.VolumeSerialNumber, index);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow, CreationTimeHigh;
        public uint LastAccessTimeLow, LastAccessTimeHigh;
        public uint LastWriteTimeLow, LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh, FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh, FileIndexLow;
    }

#pragma warning disable SYSLIB1054, IDE1006 // native names kept as-is, see FileLinker
    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int stat(string path, [Out] byte[] buffer);
#pragma warning restore SYSLIB1054, IDE1006
}
