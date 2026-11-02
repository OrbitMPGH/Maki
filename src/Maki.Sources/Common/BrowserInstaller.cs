using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Maki.Core.Storage;
using Microsoft.Extensions.Logging;

namespace Maki.Sources.Common;

public enum BrowserInstallState
{
    NotInstalled,
    Installing,
    Installed,
    Failed,
}

/// <summary>Thrown instead of launching when the headless browser is missing and could not be installed.</summary>
public sealed class BrowserUnavailableException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner);

/// <summary>
/// Installs the headless browser the first time a source needs one. The Docker image installs it at
/// build time, so there this only ever confirms it is present. A native build ships without it: it is
/// about 100 MB, and only the sources that declare <c>NeedsBrowser</c> use it.
/// <para>
/// Installs into Playwright's own default location (<c>%LOCALAPPDATA%\ms-playwright</c>,
/// <c>~/.cache/ms-playwright</c>) rather than the config directory, so a development checkout and
/// an installed build share one copy, and Playwright finds it with no path to pass around.
/// </para>
/// </summary>
public sealed partial class BrowserInstaller(ILogger<BrowserInstaller> logger) : IDisposable
{
    public const string BrowserName = "chromium-headless-shell";

    /// <summary>The download plus the unpacked browser, with room to spare.</summary>
    public const long RequiredFreeBytes = 600L * 1024 * 1024;

    /// <summary>A failed install is not retried on every source call, which would hammer the CDN
    /// from every sync while a disk stays full or a proxy blocks the download.</summary>
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(15);

    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _install;
    private volatile bool _confirmed;
    private DateTimeOffset _failedAt;

    public BrowserInstallState State { get; private set; } = BrowserInstallState.NotInstalled;
    public int? Percent { get; private set; }
    public string? Error { get; private set; }

    /// <summary>Checks the disk only, so it is cheap enough for a health check.</summary>
    public bool IsInstalled()
    {
        if (_confirmed)
            return true;

        if (InstallMarker() is not { } marker || !File.Exists(marker))
            return false;

        _confirmed = true;
        State = BrowserInstallState.Installed;
        return true;
    }

    /// <summary>
    /// Waits for the browser, installing it if needed. Every caller waits on one install, which runs
    /// on its own token: a sync that gives up should not kill a download another source is waiting
    /// for. Shutting Maki down is what cancels it.
    /// </summary>
    public Task EnsureInstalledAsync(CancellationToken ct = default)
    {
        if (IsInstalled())
            return Task.CompletedTask;

        lock (_gate)
        {
            if (_install is null || _install.IsCompleted)
            {
                if (State == BrowserInstallState.Failed && DateTimeOffset.UtcNow - _failedAt < RetryAfterFailure)
                    throw new BrowserUnavailableException($"The headless browser is not installed: {Error}");

                _install = InstallAsync(_shutdown.Token);
            }

            return _install.WaitAsync(ct);
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    private async Task InstallAsync(CancellationToken ct)
    {
        State = BrowserInstallState.Installing;
        Percent = 0;
        Error = null;
        try
        {
            if (BrowsersPath() is { } target && DiskSpace.AvailableFor(ExistingAncestor(target)) is { } free
                && free < RequiredFreeBytes)
            {
                throw new BrowserUnavailableException(
                    $"Not enough free space to install it: {free / 1048576} MB free at {target}, " +
                    $"{RequiredFreeBytes / 1048576} MB needed.");
            }

            if (Driver() is not var (node, cli))
                throw new BrowserUnavailableException("The Playwright driver is missing from the Maki install.");

            logger.LogInformation("Installing the headless browser, about 100 MB");
            await RunDriverAsync(node, cli, ct);

            if (!IsInstalled())
                throw new BrowserUnavailableException("The browser install finished but the browser is still missing.");

            Percent = null;
            logger.LogInformation("Headless browser installed");
        }
        catch (OperationCanceledException)
        {
            State = BrowserInstallState.NotInstalled;
            Percent = null;
            throw;
        }
        catch (Exception ex)
        {
            State = BrowserInstallState.Failed;
            Percent = null;
            Error = ex.Message;
            _failedAt = DateTimeOffset.UtcNow;
            logger.LogWarning(ex, "Could not install the headless browser");
            throw ex as BrowserUnavailableException ?? new BrowserUnavailableException(ex.Message, ex);
        }
    }

    private async Task RunDriverAsync(string node, string cli, CancellationToken ct)
    {
        var start = new ProcessStartInfo(node)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(cli);
        start.ArgumentList.Add("install");
        start.ArgumentList.Add(BrowserName);

        using var process = Process.Start(start)
            ?? throw new BrowserUnavailableException("The Playwright driver did not start.");
        var errors = new List<string>();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is { } line && ProgressPattern().Match(line) is { Success: true } match)
                Percent = int.Parse(match.Groups[1].Value);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                lock (errors) errors.Add(e.Data);
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }

        if (process.ExitCode != 0)
        {
            string detail;
            lock (errors) detail = string.Join(' ', errors.TakeLast(3));
            throw new BrowserUnavailableException(
                $"The browser install exited with code {process.ExitCode}. {detail}".Trim());
        }
    }

    [GeneratedRegex(@"(\d{1,3})%\s+of")]
    private static partial Regex ProgressPattern();

    /// <summary>The marker Playwright writes once a browser is fully unpacked, for the revision this
    /// build's driver asks for. A browser left behind by another Playwright version does not count.</summary>
    internal static string? InstallMarker()
    {
        if (BrowsersPath() is not { } root || Revision() is not { } revision)
            return null;

        return Path.Combine(root, $"{BrowserName.Replace('-', '_')}-{revision}", "INSTALLATION_COMPLETE");
    }

    internal static string? BrowsersPath()
    {
        var configured = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && configured != "0")
            return configured;

        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ms-playwright");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
            return Path.Combine(home, "Library", "Caches", "ms-playwright");

        var cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        return Path.Combine(string.IsNullOrWhiteSpace(cache) ? Path.Combine(home, ".cache") : cache, "ms-playwright");
    }

    private static string? Revision()
    {
        try
        {
            var file = Path.Combine(AppContext.BaseDirectory, ".playwright", "package", "browsers.json");
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            return document.RootElement.GetProperty("browsers").EnumerateArray()
                .Where(b => b.GetProperty("name").GetString() == BrowserName)
                .Select(b => b.GetProperty("revision").GetString())
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The Node binary and CLI script the Microsoft.Playwright package copies next to the app.</summary>
    private static (string Node, string Cli)? Driver()
    {
        var root = Path.Combine(AppContext.BaseDirectory, ".playwright");
        var cli = Path.Combine(root, "package", "cli.js");
        var nodeRoot = Path.Combine(root, "node");
        if (!File.Exists(cli) || !Directory.Exists(nodeRoot))
            return null;

        var platform = (OperatingSystem.IsWindows() ? "win32_" : OperatingSystem.IsMacOS() ? "darwin-" : "linux-")
            + (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64");
        var executable = OperatingSystem.IsWindows() ? "node.exe" : "node";
        var node = Path.Combine(nodeRoot, platform, executable);
        return File.Exists(node) ? (node, cli) : null;
    }

    private static string ExistingAncestor(string path)
    {
        var current = path;
        while (!Directory.Exists(current) && Path.GetDirectoryName(current) is { } parent)
            current = parent;
        return current;
    }
}
