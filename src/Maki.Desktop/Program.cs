using System.Diagnostics;
using Velopack;

namespace Maki.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var firstRun = false;

        // Velopack runs this executable with its own arguments during install, update and uninstall,
        // and exits from inside Run() for those. It has to come before anything else.
        // Start with Windows is set by the install hook, not on first run: a silent install never
        // launches the app, so a first-run callback would leave it unset there.
        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => StartWithWindows.Set(true))
            .OnFirstRun(_ => firstRun = true)
            .OnBeforeUninstallFastCallback(_ =>
            {
                StartWithWindows.RemoveForThisInstall();
                StopRunningCopies();
            })
            .Run();

        using var single = new Mutex(initiallyOwned: true, @"Local\Maki.Desktop", out var first);
        if (!first)
        {
            // Started again while running, from the Start menu or the Run key: show what is running.
            Process.Start(new ProcessStartInfo($"http://localhost:{DesktopPaths.Port()}/") { UseShellExecute = true });
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp(firstRun));
    }

    /// <summary>
    /// Uninstalling while Maki runs would leave the install folder behind, its files still in use.
    /// Ending the tray closes the server's standard input, which is the server's cue to stop; it
    /// gets a moment to finish its writes before anything still running is ended outright.
    /// </summary>
    private static void StopRunningCopies()
    {
        var folder = Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!;
        static bool Inside(Process process, string folder)
        {
            try
            {
                return process.MainModule?.FileName.StartsWith(folder, StringComparison.OrdinalIgnoreCase) == true;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return false;
            }
        }

        foreach (var tray in Process.GetProcessesByName("Maki").Where(p => p.Id != Environment.ProcessId && Inside(p, folder)))
            tray.Kill();

        var deadline = DateTime.UtcNow.AddSeconds(20);
        foreach (var server in Process.GetProcessesByName("Maki.Api").Where(p => Inside(p, folder)))
        {
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero || !server.WaitForExit(left))
                server.Kill();
        }
    }
}
