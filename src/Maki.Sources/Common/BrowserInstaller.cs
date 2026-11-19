using System.IO.Compression;
using System.Runtime.InteropServices;
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
/// The build and the folder layout are the ones Playwright used (Chrome Headless Shell from Chrome for
/// Testing, Playwright's own build on Linux arm64, which Chrome for Testing does not publish), in
/// Playwright's default location (<c>%LOCALAPPDATA%\ms-playwright</c>, <c>~/.cache/ms-playwright</c>),
/// so an install made before Maki dropped Playwright is found and reused, and a development checkout
/// and an installed build share one copy.
/// </para>
/// </summary>
public sealed class BrowserInstaller(IHttpClientFactory? httpClientFactory, ILogger<BrowserInstaller> logger) : IDisposable
{
    public const string HttpClientName = "headless-browser";

    /// <summary>Chrome Headless Shell's version. <c>distribution/docker/Dockerfile</c> carries the same
    /// two values, and a test holds them together.</summary>
    public const string Version = "149.0.7827.55";

    /// <summary>Playwright's number for the same build, which names its folder and its arm64 download.</summary>
    public const string Revision = "1228";

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

        if (InstallMarker() is not { } marker || !File.Exists(marker) || ExecutablePath() is not { } exe || !File.Exists(exe))
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
            if (Build() is not { } build || BrowserDirectory() is not { } target)
                throw new BrowserUnavailableException($"No headless browser build exists for {RuntimeInformation.RuntimeIdentifier}.");

            var root = Path.GetDirectoryName(target)!;
            if (DiskSpace.AvailableFor(ExistingAncestor(root)) is { } free && free < RequiredFreeBytes)
            {
                throw new BrowserUnavailableException(
                    $"Not enough free space to install it: {free / 1048576} MB free at {root}, " +
                    $"{RequiredFreeBytes / 1048576} MB needed.");
            }

            if (httpClientFactory is null)
                throw new BrowserUnavailableException("The headless browser is missing and this build cannot download it.");

            logger.LogInformation("Installing the headless browser, about 100 MB");
            Directory.CreateDirectory(root);
            var archive = Path.Combine(root, $"{Path.GetFileName(target)}.{Environment.ProcessId}.zip");
            var staging = Path.Combine(root, $"{Path.GetFileName(target)}.{Environment.ProcessId}.tmp");
            try
            {
                await DownloadAsync(build.Urls, archive, ct);
                Percent = 100;
                Unpack(archive, staging, build.Executable);
                if (Directory.Exists(target))
                    Directory.Delete(target, recursive: true);
                Directory.Move(staging, target);
                await File.WriteAllTextAsync(InstallMarker()!, string.Empty, CancellationToken.None);
            }
            finally
            {
                TryDelete(archive);
                if (Directory.Exists(staging))
                {
                    try { Directory.Delete(staging, recursive: true); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }

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

    /// <summary>Tries each mirror in turn; the first one to deliver the whole file wins.</summary>
    private async Task DownloadAsync(IReadOnlyList<string> urls, string path, CancellationToken ct)
    {
        var client = httpClientFactory!.CreateClient(HttpClientName);
        Exception? last = null;
        foreach (var url in urls)
        {
            try
            {
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var file = File.Create(path);
                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, n), ct);
                    read += n;
                    if (total is > 0)
                        Percent = (int)Math.Min(99, read * 100 / total.Value);
                }

                if (total is { } expected && read != expected)
                    throw new IOException($"{url} ended after {read} of {expected} bytes");
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException
                                           || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                logger.LogInformation("Headless browser download from {Url} failed: {Error}", url, ex.Message);
                last = ex;
            }
        }

        throw new BrowserUnavailableException($"The browser download failed: {last?.Message}", last);
    }

    /// <summary>
    /// Unzips the archive. .NET applies the zip's Unix permission bits on Linux and macOS; the
    /// executable is made executable again anyway, since a zip made without them would otherwise
    /// install a browser that cannot start.
    /// </summary>
    internal static void Unpack(string archive, string staging, string executable)
    {
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        ZipFile.ExtractToDirectory(archive, staging);

        foreach (var locale in Directory.EnumerateFiles(staging, "*.pak", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(Path.GetDirectoryName(locale)) == "locales" && Path.GetFileName(locale) != "en-US.pak")
                File.Delete(locale);
        }

        var exe = Path.Combine(staging, executable);
        if (!File.Exists(exe))
            throw new BrowserUnavailableException($"The browser download did not contain {executable}.");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(exe, File.GetUnixFileMode(exe)
                | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute
                | UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    internal sealed record BrowserBuild(IReadOnlyList<string> Urls, string Executable);

    /// <summary>Where this platform's build comes from and the executable inside the archive, or
    /// null on a platform no build exists for.</summary>
    internal static BrowserBuild? Build() => Build(
        OperatingSystem.IsWindows() ? OSPlatform.Windows : OperatingSystem.IsMacOS() ? OSPlatform.OSX : OSPlatform.Linux,
        RuntimeInformation.ProcessArchitecture);

    internal static BrowserBuild? Build(OSPlatform os, Architecture arch)
    {
        static BrowserBuild Cft(string platform, string executable)
        {
            var file = $"{platform}/chrome-headless-shell-{platform}.zip";
            return new(
                [
                    $"https://cdn.playwright.dev/builds/cft/{Version}/{file}",
                    $"https://storage.googleapis.com/chrome-for-testing-public/{Version}/{file}",
                ],
                Path.Combine($"chrome-headless-shell-{platform}", executable));
        }

        if (os == OSPlatform.Windows && arch == Architecture.X64)
            return Cft("win64", "chrome-headless-shell.exe");
        if (os == OSPlatform.OSX && arch == Architecture.X64)
            return Cft("mac-x64", "chrome-headless-shell");
        if (os == OSPlatform.OSX && arch == Architecture.Arm64)
            return Cft("mac-arm64", "chrome-headless-shell");
        if (os == OSPlatform.Linux && arch == Architecture.X64)
            return Cft("linux64", "chrome-headless-shell");
        if (os == OSPlatform.Linux && arch == Architecture.Arm64)
        {
            var path = $"builds/chromium/{Revision}/chromium-headless-shell-linux-arm64.zip";
            return new(
                [
                    $"https://cdn.playwright.dev/dbazure/download/playwright/{path}",
                    $"https://playwright.download.prss.microsoft.com/dbazure/download/playwright/{path}",
                    $"https://cdn.playwright.dev/{path}",
                ],
                Path.Combine("chrome-linux", "headless_shell"));
        }

        return null;
    }

    /// <summary>The first address this platform's build downloads from, or null where there is none.</summary>
    public static string? DownloadUrl() => Build()?.Urls[0];

    /// <summary>The browser's executable for this platform, whether or not it is installed yet.</summary>
    public static string? ExecutablePath() =>
        BrowserDirectory() is { } dir && Build() is { } build ? Path.Combine(dir, build.Executable) : null;

    /// <summary>The marker written once the browser is fully unpacked. An unfinished install has none.</summary>
    internal static string? InstallMarker() =>
        BrowserDirectory() is { } dir ? Path.Combine(dir, "INSTALLATION_COMPLETE") : null;

    internal static string? BrowserDirectory() =>
        BrowsersPath() is { } root ? Path.Combine(root, $"chromium_headless_shell-{Revision}") : null;

    /// <summary>The Docker image sets <c>PLAYWRIGHT_BROWSERS_PATH</c>; the name is kept so an
    /// admin's override from before still applies.</summary>
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

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string ExistingAncestor(string path)
    {
        var current = path;
        while (!Directory.Exists(current) && Path.GetDirectoryName(current) is { } parent)
            current = parent;
        return current;
    }
}
