using System.Diagnostics;

namespace Maki.Desktop;

/// <summary>
/// Maki.Api as a child process with no window. Its standard input stays open for as long as this
/// tray wants it running: closing it is the shutdown request (<c>DesktopParentWatcher</c> on the
/// other side), and Windows closes it for us if the tray dies.
/// </summary>
internal sealed class ServerProcess
{
    /// <summary>Must match <c>HostRuntime</c> in Maki.Api.</summary>
    public const int RestartExitCode = 10;
    public const int ApplyUpdateExitCode = 11;

    private Process? _process;

    /// <summary>Raised on a thread-pool thread with the exit code.</summary>
    public event Action<int>? Exited;

    public bool IsRunning => _process is { HasExited: false };

    public void Start()
    {
        var executable = DesktopPaths.ServerExecutable;
        var start = executable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? new ProcessStartInfo("dotnet") { ArgumentList = { executable } }
            : new ProcessStartInfo(executable);
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardInput = true;
        start.WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!;
        start.Environment["MAKI_CONFIG_DIR"] = DesktopPaths.ConfigDir;
        start.Environment["MAKI_RUNTIME"] = "desktop";
        start.Environment["MAKI_DESKTOP_PARENT"] = "1";

        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.Exited += (_, _) =>
        {
            var code = process.ExitCode;
            process.Dispose();
            Exited?.Invoke(code);
        };
        process.Start();
        _process = process;
    }

    /// <summary>Asks Maki to stop, and kills it if it has not within <paramref name="timeout"/>.</summary>
    public async Task StopAsync(TimeSpan timeout)
    {
        if (_process is not { } process)
            return;

        try
        {
            if (process.HasExited)
                return;

            process.StandardInput.Close();
            using var wait = new CancellationTokenSource(timeout);
            try
            {
                await process.WaitForExitAsync(wait.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone, disposed by the Exited handler.
        }
    }
}
