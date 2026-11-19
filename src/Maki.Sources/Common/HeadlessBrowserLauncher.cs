using Maki.Core.Io;
using PuppeteerSharp;

namespace Maki.Sources.Common;

/// <summary>
/// Starts the headless shell for both browser sources and talks to it over the DevTools protocol
/// from this process. There used to be a Playwright Node driver between the two (~96 MB fresh on a
/// NAS, 240 to 260 MB after a TopManhua burst, since every captured image crossed it as base64);
/// now there is only Chromium.
/// <para>
/// Counts the browsers it has running so the shell's files can leave the page cache once the last
/// one exits.
/// </para>
/// </summary>
public sealed class HeadlessBrowserLauncher(BrowserInstaller? installer = null)
{
    private const int LaunchTimeoutMs = 60_000;

    private int _running;

    /// <summary>How many browsers this launcher has up right now.</summary>
    public int Running => Volatile.Read(ref _running);

    /// <summary>Installs the browser first if this build shipped without it; see <see cref="BrowserInstaller"/>.</summary>
    public async Task<IBrowser> LaunchAsync(IEnumerable<string>? extraArgs = null, CancellationToken ct = default)
    {
        if (installer is not null)
        {
            await installer.EnsureInstalledAsync(ct);
        }

        var executable = BrowserInstaller.ExecutablePath();
        if (executable is null || !File.Exists(executable))
        {
            throw new BrowserUnavailableException("The headless browser is not installed.");
        }

        ct.ThrowIfCancellationRequested();
        var browser = await Puppeteer.LaunchAsync(new LaunchOptions
        {
            ExecutablePath = executable,
            Headless = true,
            IgnoreDefaultArgs = true,
            Args = [.. BrowserSupport.ChromiumArgs, .. extraArgs ?? []],
            DefaultViewport = null,
            // The shell starts with no window (--no-startup-window), so there is no first page to wait for.
            WaitForInitialPage = false,
            // A pipe rather than a localhost port: no other local process can reach the browser, and
            // Chromium exits when Maki does, even when Maki is killed. Over a port it outlived a hard
            // kill (checked on Windows, 2026-11-19), which Playwright's driver never let happen.
            Pipe = true,
            Timeout = LaunchTimeoutMs,
        });
        Interlocked.Increment(ref _running);
        return browser;
    }

    /// <summary>Closes a browser this launcher started. One that already died is still counted out.</summary>
    public async Task CloseAsync(IBrowser? browser)
    {
        if (browser is null)
        {
            return;
        }

        try
        {
            await browser.CloseAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }
        finally
        {
            try
            {
                if (browser.Process is { HasExited: false } process)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }

            browser.Dispose();
            if (Interlocked.Decrement(ref _running) == 0)
            {
                DropBinaryCache();
            }
        }
    }

    /// <summary>
    /// Drops the headless shell's files from the page cache once no browser runs. A blank page put
    /// over 100 MB of them into the container's <c>memory.current</c>, which is what dashboards show,
    /// and nothing reads them again until the next launch.
    /// </summary>
    private static void DropBinaryCache()
    {
        if (!OperatingSystem.IsLinux() || BrowserInstaller.BrowserDirectory() is not { } root || !Directory.Exists(root))
        {
            return;
        }

        try
        {
            foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if (file.Length >= 1024 * 1024)
                {
                    PageCache.DropAfterScan(file.FullName);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
