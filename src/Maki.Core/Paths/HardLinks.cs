using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Maki.Core.Paths;

/// <summary>
/// How many directory entries share a file's data. Rewriting an archive in place builds a new file
/// and swaps it over the name, which quietly turns a hardlinked (seeded) file into a second full
/// copy, so anything that rewrites adopted files checks this first.
/// </summary>
public static class HardLinks
{
    /// <summary>The link count, or null when the platform or filesystem cannot say.</summary>
    public static int? Count(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                return GetFileInformationByHandle(handle, out var info) ? (int)info.NumberOfLinks : null;
            }

            if (OperatingSystem.IsLinux())
            {
                // struct stat differs by architecture: x86_64 has a 64-bit st_nlink at offset 16,
                // the generic layout arm64 uses has a 32-bit one after st_mode at offset 20.
                var buffer = new byte[256];
                if (stat(path, buffer) != 0)
                {
                    return null;
                }

                return RuntimeInformation.ProcessArchitecture switch
                {
                    Architecture.X64 => (int)BitConverter.ToInt64(buffer, 16),
                    Architecture.Arm64 => BitConverter.ToInt32(buffer, 20),
                    _ => null,
                };
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DllNotFoundException
                                      or EntryPointNotFoundException)
        {
        }

        return null;
    }

    /// <summary>True only when the file is known to have more than one link.</summary>
    public static bool IsShared(string path) => Count(path) > 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    // DllImport for the same reason as FileLinker: LibraryImport needs AllowUnsafeBlocks.
#pragma warning disable SYSLIB1054, IDE1006
    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int stat(string path, [Out] byte[] buffer);
#pragma warning restore SYSLIB1054, IDE1006
}
