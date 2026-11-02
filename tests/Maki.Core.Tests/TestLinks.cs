using System.Diagnostics;

namespace Maki.Core.Tests;

/// <summary>
/// Creates links for the symlink/junction escape tests. A directory link is a junction on Windows,
/// which mklink /J makes without elevation, and a symlink elsewhere, so every host the tests run on
/// can make one and a test asserts that it did. A file symlink is the exception: put such a test
/// under <see cref="FileSymlinkFactAttribute"/>. Also compiled into Maki.Api.Tests.
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

/// <summary>A fact about Windows behaviour. Elsewhere it reports as skipped rather than passing having checked nothing.</summary>
internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows only";
        }
    }
}

/// <inheritdoc cref="WindowsFactAttribute"/>
internal sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows only";
        }
    }
}

/// <summary>A fact that cannot be set up on Windows. There it reports as skipped rather than passing having checked nothing.</summary>
internal sealed class NotWindowsFactAttribute : FactAttribute
{
    public NotWindowsFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Not on Windows";
        }
    }
}

/// <summary>
/// A fact that needs a file symlink, which Windows only allows with Developer Mode or elevation.
/// Where the host refuses one it reports as skipped rather than passing having checked nothing.
/// </summary>
internal sealed class FileSymlinkFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> Supported = new(() =>
    {
        var dir = Directory.CreateTempSubdirectory("maki-symlink-probe-").FullName;
        try
        {
            var target = Path.Combine(dir, "target");
            File.WriteAllText(target, "");
            return TestLinks.TryLinkFile(Path.Combine(dir, "link"), target);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    });

    public FileSymlinkFactAttribute()
    {
        if (!Supported.Value)
        {
            Skip = "This host cannot create file symlinks (on Windows, enable Developer Mode)";
        }
    }
}
