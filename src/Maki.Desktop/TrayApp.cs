using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

namespace Maki.Desktop;

/// <summary>
/// The notification-area icon and everything behind it. This only supervises: it starts Maki,
/// brings it back after a restore, a crash or an update, and stops it cleanly on Quit. Settings,
/// status and errors all live in Maki's own web UI, so the desktop and Docker builds never drift.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private static readonly TimeSpan FirstUpdateCheck = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan CrashWindow = TimeSpan.FromMinutes(10);
    private const int MaxCrashesInWindow = 3;

    private readonly SynchronizationContext _ui;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _open;
    private readonly ToolStripMenuItem _start;
    private readonly ToolStripMenuItem _update;
    private readonly ToolStripMenuItem _startWithWindows;
    private readonly ServerProcess _server = new();
    private readonly Updater _updater = new();
    private readonly List<DateTimeOffset> _crashes = [];
    private readonly System.Windows.Forms.Timer _updateTimer = new();
    private bool _stopping;
    private bool _openWhenReady;
    private string? _announcedVersion;
    private int _port;

    public TrayApp(bool firstRun)
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _openWhenReady = firstRun;

        _open = new ToolStripMenuItem(Strings.Get("desktop.tray.open"), null, (_, _) => OpenInBrowser())
        {
            Font = new Font(SystemFonts.MenuFont ?? Control.DefaultFont, FontStyle.Bold),
        };
        _start = new ToolStripMenuItem(Strings.Get("desktop.tray.start"), null, (_, _) => StartServer()) { Visible = false };
        _update = new ToolStripMenuItem(string.Empty, null, async (_, _) => await ApplyUpdateAsync()) { Visible = false };
        _startWithWindows = new ToolStripMenuItem(Strings.Get("desktop.tray.startWithWindows"), null, (_, _) => ToggleStartWithWindows())
        {
            Checked = StartWithWindows.IsEnabled(),
        };

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(
        [
            _open,
            _start,
            _update,
            new ToolStripSeparator(),
            _startWithWindows,
            new ToolStripMenuItem(Strings.Get("desktop.tray.viewLogs"), null, (_, _) => OpenFolder(DesktopPaths.LogDir)),
            new ToolStripSeparator(),
            new ToolStripMenuItem(Strings.Get("desktop.tray.quit"), null, async (_, _) => await QuitAsync()),
        ]);

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            ContextMenuStrip = menu,
            Text = Strings.Get("desktop.tray.tooltipStarting"),
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) OpenInBrowser();
        };

        _server.Exited += code => _ui.Post(_ => OnServerExited(code), null);

        ClearStaleUpdateMarker();
        StartServer();

        if (_updater.Enabled)
        {
            _updateTimer.Interval = (int)FirstUpdateCheck.TotalMilliseconds;
            _updateTimer.Tick += async (_, _) =>
            {
                _updateTimer.Interval = (int)UpdateInterval.TotalMilliseconds;
                await CheckForUpdateAsync();
            };
            _updateTimer.Start();
        }
    }

    private void StartServer()
    {
        _port = DesktopPaths.Port();
        _start.Visible = false;

        if (!PortIsFree(_port))
        {
            SetState(running: false);
            Notify(Strings.Get("desktop.notice.portInUse", ("port", _port)), ToolTipIcon.Warning);
            return;
        }

        try
        {
            _server.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            SetState(running: false);
            Notify(Strings.Get("desktop.notice.stopped"), ToolTipIcon.Error);
            return;
        }

        _icon.Text = Strings.Get("desktop.tray.tooltipStarting");
        _ = WaitUntilReadyAsync();
    }

    private async Task WaitUntilReadyAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        // A first start migrates the database before it listens, which can take a while on a slow disk.
        for (var attempt = 0; attempt < 300 && _server.IsRunning; attempt++)
        {
            try
            {
                using var response = await http.GetAsync($"http://localhost:{_port}/initialize.json");
                if (response.IsSuccessStatusCode)
                {
                    SetState(running: true);
                    if (_openWhenReady)
                    {
                        _openWhenReady = false;
                        OpenInBrowser();
                        Notify(Strings.Get("desktop.notice.firstRun"), ToolTipIcon.Info);
                    }
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
            }

            await Task.Delay(1000);
        }
    }

    private void OnServerExited(int code)
    {
        SetState(running: false);
        if (_stopping)
            return;

        switch (code)
        {
            case ServerProcess.RestartExitCode:
                StartServer();
                return;
            case ServerProcess.ApplyUpdateExitCode when _updater.ReadyVersion is not null:
                _stopping = true;
                _updater.ApplyAndRestart();
                return;
            case ServerProcess.ApplyUpdateExitCode:
                StartServer();
                return;
        }

        var now = DateTimeOffset.UtcNow;
        _crashes.RemoveAll(at => now - at > CrashWindow);
        _crashes.Add(now);
        if (_crashes.Count > MaxCrashesInWindow)
        {
            Notify(Strings.Get("desktop.notice.stopped"), ToolTipIcon.Error);
            return;
        }

        _ = RestartAfterDelayAsync();
    }

    private async Task RestartAfterDelayAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(3));
        if (!_stopping && !_server.IsRunning)
            StartServer();
    }

    private async Task CheckForUpdateAsync()
    {
        string? version;
        try
        {
            version = await _updater.CheckAndDownloadAsync();
        }
        catch (Exception)
        {
            // Offline, rate-limited by GitHub, or a feed mid-upload. The next check tries again.
            return;
        }

        if (version is null)
            return;

        try
        {
            Directory.CreateDirectory(DesktopPaths.ConfigDir);
            await File.WriteAllTextAsync(DesktopPaths.UpdateReadyFile, version);
        }
        catch (IOException)
        {
        }

        _update.Text = Strings.Get("desktop.tray.restartToUpdate", ("version", version));
        _update.Visible = true;
        if (_announcedVersion != version)
        {
            _announcedVersion = version;
            Notify(Strings.Get("desktop.notice.updateReady", ("version", version)), ToolTipIcon.Info);
        }
    }

    private async Task ApplyUpdateAsync()
    {
        if (_updater.ReadyVersion is null)
            return;

        _stopping = true;
        await _server.StopAsync(TimeSpan.FromSeconds(30));
        _updater.ApplyAndRestart();
    }

    private async Task QuitAsync()
    {
        _stopping = true;
        _updateTimer.Stop();
        _icon.Text = Strings.Get("desktop.tray.tooltipStopping");
        await _server.StopAsync(TimeSpan.FromSeconds(30));
        _icon.Visible = false;
        _icon.Dispose();
        ExitThread();
    }

    private void ToggleStartWithWindows()
    {
        var enable = !_startWithWindows.Checked;
        StartWithWindows.Set(enable);
        _startWithWindows.Checked = enable;
    }

    private void SetState(bool running)
    {
        _icon.Text = Strings.Get(running ? "desktop.tray.tooltipRunning" : "desktop.tray.tooltipStopped");
        _open.Enabled = running;
        _start.Visible = !running && !_stopping;
    }

    /// <summary>A file left by an update that has since been applied, or by a downloaded update this
    /// build no longer has, would make Maki offer a restart that does nothing.</summary>
    private void ClearStaleUpdateMarker()
    {
        if (_updater.ReadyVersion is not null)
            return;

        try
        {
            File.Delete(DesktopPaths.UpdateReadyFile);
        }
        catch (IOException)
        {
        }
    }

    private void OpenInBrowser() =>
        Process.Start(new ProcessStartInfo($"http://localhost:{_port}/") { UseShellExecute = true });

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void Notify(string message, ToolTipIcon icon) =>
        _icon.ShowBalloonTip(10_000, "Maki", message, icon);

    /// <summary>Both the loopback and the wildcard address: Maki listens on one or the other
    /// depending on the network setting, and Windows lets another program hold either alone.</summary>
    private static bool PortIsFree(int port) =>
        CanBind(IPAddress.Loopback, port) && CanBind(IPAddress.Any, port);

    private static bool CanBind(IPAddress address, int port)
    {
        try
        {
            var listener = new TcpListener(address, port) { ExclusiveAddressUse = true };
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static Icon LoadIcon()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("maki.ico");
        return stream is null ? SystemIcons.Application : new Icon(stream, SystemInformation.SmallIconSize);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _updateTimer.Dispose();
            _icon.Dispose();
        }

        base.Dispose(disposing);
    }
}
