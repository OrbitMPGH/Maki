using System.Diagnostics;

namespace Maki.Core.Tests;

/// <summary>
/// Creates links for the symlink/junction escape tests. A directory link is a junction on Windows,
/// which mklink /J makes without elevation, and a symlink elsewhere. Every method returns false
/// when the host refuses, and the caller then returns early instead of passing vacuously on a
/// platform it never ran on. Also compiled into Maki.Api.Tests.
/// </summary>
internal static class TestLinks
{
    public static bool TryLinkDirectory(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(
                "cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            })!;
            process.WaitForExit(10_000);
            return process.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>A file symlink. Windows needs Developer Mode or elevation for one.</summary>
    public static bool TryLinkFile(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return File.Exists(link);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void UnlinkDirectory(string link)
    {
        if (Directory.Exists(link) || File.Exists(link))
        {
            Directory.Delete(link);
        }
    }
}
