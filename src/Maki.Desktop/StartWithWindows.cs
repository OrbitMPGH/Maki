using Microsoft.Win32;

namespace Maki.Desktop;

/// <summary>The per-user Run key, so starting at sign-in needs no admin rights.</summary>
internal static class StartWithWindows
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Maki";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, $"\"{LauncherPath()}\"");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// Velopack installs each version under <c>current\</c> and keeps a stub of the same name in the
    /// folder above that always starts the installed version. Pointing the Run key at the stub keeps
    /// it valid across updates.
    /// </summary>
    private static string LauncherPath()
    {
        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Maki.exe");
        var folder = Path.GetDirectoryName(exe)!;
        if (Path.GetFileName(folder).Equals("current", StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(folder) is { } root
            && File.Exists(Path.Combine(root, Path.GetFileName(exe))))
        {
            return Path.Combine(root, Path.GetFileName(exe));
        }

        return exe;
    }
}
